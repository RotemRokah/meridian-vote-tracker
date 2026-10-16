using System.Net.Http.Json;
using Meridian.Api.Data;
using Meridian.Api.Imports;
using Meridian.Api.Votes;
using Meridian.Domain;
using Microsoft.EntityFrameworkCore;

namespace Meridian.Tests.Integration;

// The real assignment files, through the whole pipeline: HTTP upload -> worker -> DB -> report.
// Expected outcomes come from CLAUDE.md "Known data problems". Where a number is derived
// rather than listed there, the comment says how it was derived.
[Collection(RealFilesCollection.Name)]
public class RealFileImportTests
{
    private const string Q1 = "assembly_export_2024_q1.csv";
    private const string Rev2 = "assembly_export_2024_q1_rev2.csv";

    private readonly ApiFactory _factory;

    public RealFileImportTests(ApiFactory factory)
    {
        _factory = factory;
    }

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", name));

    [Fact]
    public async Task Q1_then_rev2_then_rev2_again()
    {
        var client = _factory.ClientFor("dana");

        // ---------- q1 ----------
        var q1 = await _factory.ImportAndWait(client, Q1, Fixture(Q1));
        Assert.Equal(ImportStatus.Completed, q1.Import.Status);
        Assert.Equal(5896, q1.Import.TotalRows);
        Assert.Equal(5896, q1.Import.ProcessedRows);
        var s1 = q1.Summary!;
        Assert.Contains(s1.FileNotes, n => n.Contains("BOM"));

        await using (var db = _factory.NewDbContext())
        {
            var id = q1.Import.Id;
            async Task<ImportRow> Line(int line) => await db.ImportRows.SingleAsync(r => r.ImportId == id && r.LineNo == line);

            foreach (var line in new[] { 1045, 1046, 1047 })
                Assert.Equal(RowClassification.Duplicate, (await Line(line)).Classification);

            var invalidLines = new Dictionary<int, string>
            {
                [2143] = "Unknown country code (value: 'FRN')",
                [2842] = "Ballot must be one of Y, N, A, X (value: 'Yes')",
                [2846] = "ballot: Required field is empty",
                [3139] = "Expected 8 columns, found 5",
                [3880] = "vote_date: Date must be in yyyy-MM-dd format (value: '21/02/2024')",
            };
            foreach (var (line, reason) in invalidLines)
            {
                var row = await Line(line);
                Assert.Equal(RowClassification.Invalid, row.Classification);
                Assert.Contains(reason, row.Reason);
            }
            foreach (var line in Enumerable.Range(3880, 6))
                Assert.Equal(RowClassification.Invalid, (await Line(line)).Classification);

            var nzl = await db.ImportRows.SingleAsync(r => r.ImportId == id && r.AssemblyVoteId == "MA/2024/033" && r.CountryCode == "NZL");
            Assert.Equal(RowClassification.Invalid, nzl.Classification);
            Assert.Contains("NZL was not a member", nzl.Reason);

            var deu = await Line(2594);
            Assert.Equal(RowClassification.New, deu.Classification);
            Assert.Equal("country_code: 'deu' normalized to 'DEU'", deu.NormalizationNote);

            Assert.Equal(RowClassification.Contradiction, (await Line(1695)).Classification);
            Assert.Equal(RowClassification.Contradiction, (await Line(1793)).Classification);
            Assert.Equal(VoteDetailsOutcome.Contradiction, s1.Votes.Single(v => v.AssemblyVoteId == "MA/2024/025").Outcome);
            Assert.False(await db.Votes.AnyAsync(v => v.AssemblyVoteId == "MA/2024/025"));
            Assert.Equal(39, await db.Votes.CountAsync());   // 40 votes in q1, 025 not created
        }

        // derived totals: listed problems above; vote 025 rows counted from the file itself
        var vote025Rows = CountRows(Q1, "MA/2024/025");
        Assert.Equal(3, s1.Counts[RowClassification.Duplicate]);
        Assert.Equal(11, s1.Counts[RowClassification.Invalid]);   // FRN, Yes, empty, AGO;N, 6 dates, NZL
        Assert.Equal(2 + vote025Rows, s1.Counts[RowClassification.Contradiction]);
        Assert.Equal(5896 - 3 - 11 - (2 + vote025Rows), s1.Counts[RowClassification.New]);
        Assert.Equal(0, s1.NewConflicts);
        Assert.Equal(1, s1.NormalizedRows);

        int vote040Ballots;
        await using (var db = _factory.NewDbContext())
            vote040Ballots = await BallotCount(db, "MA/2024/040");

        // ---------- rev2 ----------
        var r2 = await _factory.ImportAndWait(client, Rev2, Fixture(Rev2));
        Assert.Equal(ImportStatus.Completed, r2.Import.Status);
        Assert.Equal(5960, r2.Import.ProcessedRows);
        var s2 = r2.Summary!;

        await using (var db = _factory.NewDbContext())
        {
            // ballot conflicts: approved value stays until a human decides (ING-08)
            var expectedBallotConflicts = new (string, string?, string, string)[]
            {
                ("MA/2024/003", "BRA", "Y", "A"), ("MA/2024/003", "IND", "Y", "N"), ("MA/2024/010", "JPN", "Y", "X"),
                ("MA/2024/019", "ZAF", "Y", "N"), ("MA/2024/028", "CAN", "Y", "N"), ("MA/2024/028", "MEX", "Y", "A"),
            };
            var ballotConflicts = await ConflictsOf(db, ConflictField.Ballot);
            Assert.Equal(expectedBallotConflicts.OrderBy(c => c), ballotConflicts.OrderBy(c => c));
            foreach (var (vote, country, approved, _) in expectedBallotConflicts)
                Assert.Equal(approved, await BallotOf(db, vote, country!));

            // every Conflict row points at its conflict
            Assert.Equal(6, await db.ImportRows.CountAsync(r =>
                r.ImportId == r2.Import.Id && r.Classification == RowClassification.Conflict && r.ConflictId != null));

            // vote-detail conflicts; approved details unchanged
            Assert.Equal(
                new[] { ("MA/2024/009", (string?)null, "Biological Diversity in Coastal Zones", "Biological Diversity in Coastal Zones: Revised") },
                await ConflictsOf(db, ConflictField.Title));
            Assert.Equal(new[] { ("MA/2024/014", (string?)null, "2024-02-05", "2024-02-06") }, await ConflictsOf(db, ConflictField.Date));
            Assert.Equal("Biological Diversity in Coastal Zones", (await db.Votes.SingleAsync(v => v.AssemblyVoteId == "MA/2024/009")).Title);
            Assert.Equal(new DateOnly(2024, 2, 5), (await db.Votes.SingleAsync(v => v.AssemblyVoteId == "MA/2024/014")).VoteDate);

            // not ingested from q1, clean now -> New, with rev2 as their source
            foreach (var (vote, country, ballot) in new[] { ("MA/2024/020", "ARG", "A"), ("MA/2024/020", "AZE", "A"), ("MA/2024/012", "FRA", "A") })
            {
                Assert.Equal(ballot, await BallotOf(db, vote, country));
                Assert.Equal(r2.Import.Id, await SourceOf(db, vote, country));
            }

            // vote 040 absent from rev2: untouched (ING-09); 041 is new; 025 is clean in rev2
            Assert.Equal(vote040Ballots, await BallotCount(db, "MA/2024/040"));
            Assert.Equal(CountRows(Rev2, "MA/2024/041"), await BallotCount(db, "MA/2024/041"));
            Assert.Equal(CountRows(Rev2, "MA/2024/025"), await BallotCount(db, "MA/2024/025"));

            // Turkey vs Türkiye: same code, not a conflict (ING-11)
            Assert.False(await db.Conflicts.AnyAsync(c => c.CountryCode == "TUR"));

            // audit: one ConflictOpened event per new conflict, by the uploader
            Assert.Equal(8, await db.AuditEvents.CountAsync(a => a.ImportId == r2.Import.Id && a.EventType == "ConflictOpened" && a.Actor == "dana"));
        }

        // derived totals (cross-checked by an independent count of the files:
        // New = 149 vote 041 + 149 vote 025 + 9 fixed rows + 71 missing countries)
        Assert.Equal(378, s2.Counts[RowClassification.New]);
        Assert.Equal(6, s2.Counts[RowClassification.Conflict]);
        Assert.Equal(5960 - 378 - 6, s2.Counts[RowClassification.Unchanged]);
        Assert.Equal(0, s2.Counts[RowClassification.Invalid] + s2.Counts[RowClassification.Duplicate] + s2.Counts[RowClassification.Contradiction]);
        Assert.Equal(8, s2.NewConflicts);
        Assert.Equal(2, s2.VotesCreated);   // 025 and 041

        // ---------- rev2 again (ING-12) ----------
        int ballotsBefore;
        await using (var db = _factory.NewDbContext())
            ballotsBefore = await db.CountryVotes.CountAsync();

        var r3 = await _factory.ImportAndWait(client, Rev2, Fixture(Rev2));
        var s3 = r3.Summary!;
        Assert.Equal(0, s3.Counts[RowClassification.New]);
        Assert.Equal(0, s3.NewConflicts);
        Assert.Equal(8, s3.LinkedConflicts);   // linked to the pending ones, not reopened
        Assert.Equal(6, s3.Counts[RowClassification.Conflict]);
        Assert.Equal(5960 - 6, s3.Counts[RowClassification.Unchanged]);
        Assert.Contains(s3.FileNotes, n => n.Contains($"Identical file to import #{r2.Import.Id}"));

        await using (var db = _factory.NewDbContext())
        {
            Assert.Equal(8, await db.Conflicts.CountAsync());
            Assert.Equal(ballotsBefore, await db.CountryVotes.CountAsync());
        }

        // reports stay retrievable after the fact (ING-15)
        var again = await ApiFactory.WaitForImport(client, q1.Import.Id);
        Assert.Equal(s1.Counts, again.Summary!.Counts);

        // HIS-04 on real data: vote 025 was a contradiction in q1, created from rev2, unchanged on re-upload
        var vote025 = (await client.GetFromJsonAsync<PagedResult<VoteListItemDto>>(
            "/api/votes?search=MA%2F2024%2F025", ApiFactory.Json))!.Items.Single();
        var timeline = (await client.GetFromJsonAsync<List<TimelineEntryDto>>(
            $"/api/votes/{vote025.Id}/timeline", ApiFactory.Json))!;
        Assert.Equal(new[] { "FileProcessed", "FileProcessed", "VoteCreated", "BallotsAdded", "FileProcessed" },
            timeline.Select(t => t.EventType));
        var rev2Rows = CountRows(Rev2, "MA/2024/025");
        Assert.Equal(($"{vote025Rows} Contradiction", q1.Import.Id), (timeline[0].Detail, timeline[0].Source!.ImportId));
        Assert.Equal(($"{rev2Rows} New", r2.Import.Id), (timeline[1].Detail, timeline[1].Source!.ImportId));
        Assert.Equal(($"{rev2Rows} Unchanged", r3.Import.Id), (timeline[4].Detail, timeline[4].Source!.ImportId));
    }

    private static int CountRows(string file, string voteId) =>
        File.ReadLines(Path.Combine(AppContext.BaseDirectory, "TestData", file)).Count(l => l.StartsWith(voteId + ","));

    private static Task<int> BallotCount(MeridianDbContext db, string voteId) =>
        db.CountryVotes.CountAsync(cv => db.Votes.Any(v => v.Id == cv.VoteId && v.AssemblyVoteId == voteId));

    private static Task<string?> BallotOf(MeridianDbContext db, string voteId, string country) =>
        (from cv in db.CountryVotes
         join v in db.Votes on cv.VoteId equals v.Id
         where v.AssemblyVoteId == voteId && cv.CountryCode == country
         select cv.Ballot).SingleOrDefaultAsync();

    private static Task<long> SourceOf(MeridianDbContext db, string voteId, string country) =>
        (from cv in db.CountryVotes
         join v in db.Votes on cv.VoteId equals v.Id
         where v.AssemblyVoteId == voteId && cv.CountryCode == country
         select cv.SourceImportId).SingleAsync();

    private static async Task<List<(string Vote, string? Country, string Approved, string Proposed)>> ConflictsOf(
        MeridianDbContext db, ConflictField field)
    {
        var rows = await (from c in db.Conflicts
                          join v in db.Votes on c.VoteId equals v.Id
                          where c.Field == field && c.Status == ConflictStatus.Pending
                          select new { v.AssemblyVoteId, c.CountryCode, c.ApprovedValue, c.ProposedValue }).ToListAsync();
        return rows.Select(r => (r.AssemblyVoteId, (string?)r.CountryCode, r.ApprovedValue, r.ProposedValue)).ToList();
    }
}
