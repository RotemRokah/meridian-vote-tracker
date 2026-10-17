using System.Diagnostics;
using System.Net.Http.Json;
using Meridian.Api.Data;
using Meridian.Api.Imports;
using Meridian.Api.Snapshots;
using Meridian.Api.Votes;
using Meridian.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Meridian.Tests.Integration;

// Opt-in volume test: assembly_historic.csv (962,171 rows, 103 MB) is not in git.
// Runs when data/assembly_historic.csv exists (or MERIDIAN_HISTORIC_CSV points to it); otherwise Skipped.
// Uses its own disposable database: the test positions it creates are thrown away with it.
[Collection(HistoricCollection.Name)]
public class HistoricImportTests
{
    private readonly ApiFactory _factory;
    private readonly ITestOutputHelper _out;

    public HistoricImportTests(ApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _out = output;
    }

    private static string HistoricPath() =>
        Environment.GetEnvironmentVariable("MERIDIAN_HISTORIC_CSV")
        ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "assembly_historic.csv"));

    [SkippableFact]
    public async Task Historic_file_imports_and_full_history_snapshot_computes()
    {
        var path = HistoricPath();
        Skip.IfNot(File.Exists(path), $"Historic file not found at {path} (it is not in git). Opt-in test.");

        var dana = _factory.ClientFor("dana");
        var lena = _factory.ClientFor("lena");

        // ---------- import ----------
        var timer = Stopwatch.StartNew();
        var import = await _factory.ImportAndWait(dana, "assembly_historic.csv", await File.ReadAllBytesAsync(path), TimeSpan.FromMinutes(10));
        var importTime = timer.Elapsed;
        _out.WriteLine($"import: {importTime.TotalSeconds:F1}s");

        Assert.Equal(ImportStatus.Completed, import.Import.Status);
        Assert.Equal(962_171, import.Import.TotalRows);
        Assert.Equal(962_171, import.Import.ProcessedRows);

        // expected from an independent count of the file (see CLAUDE.md "historic")
        var counts = import.Summary!.Counts;
        Assert.Equal(32, counts[RowClassification.Invalid]);
        Assert.Equal(30, counts[RowClassification.Duplicate]);
        Assert.Equal(0, counts[RowClassification.Contradiction]);   // malformed rows don't taint their vote
        Assert.Equal(962_171 - 32 - 30, counts[RowClassification.New]);
        Assert.Equal(6_480, import.Summary.VotesCreated);

        await using (var db = _factory.NewDbContext())
        {
            async Task<ImportRow> Line(int n) => await db.ImportRows.SingleAsync(r => r.ImportId == import.Import.Id && r.LineNo == n);
            Assert.Contains("found 7", (await Line(134746)).Reason);
            var shifted = await Line(521760);
            Assert.Equal(RowClassification.Invalid, shifted.Classification);
            Assert.Contains("' Including Capacity Building'", shifted.Reason);
            // the shifted row's vote is still created normally
            Assert.Equal(VoteDetailsOutcome.Created, import.Summary.Votes.Single(v => v.AssemblyVoteId == "MA/2018/131").Outcome);
        }

        // ---------- test positions (disposable DB only), through the API as the Admin ----------
        List<long> voteIds;
        await using (var db = _factory.NewDbContext())
            voteIds = await db.Votes.Select(v => v.Id).OrderBy(id => id).ToListAsync();
        timer.Restart();
        foreach (var chunk in voteIds.Chunk(16))
            await Task.WhenAll(chunk.Select(async id =>
            {
                var response = await lena.PutAsJsonAsync($"/api/votes/{id}/position",
                    new PositionRequest(id % 3 == 0 ? "Against" : "For", (int)(id % 5) + 1, null));
                response.EnsureSuccessStatusCode();
            }));
        _out.WriteLine($"positions for {voteIds.Count} votes: {timer.Elapsed.TotalSeconds:F1}s");

        // ---------- snapshot over the full history (SNP-02) ----------
        timer.Restart();
        var created = await dana.PostAsJsonAsync("/api/snapshots",
            new CreateSnapshotRequest("full history", new DateOnly(2015, 1, 1), new DateOnly(2023, 12, 31), null, null));
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<SnapshotDto>(ApiFactory.Json))!.Id;
        SnapshotDetailsDto snapshot;
        while (true)
        {
            snapshot = (await dana.GetFromJsonAsync<SnapshotDetailsDto>($"/api/snapshots/{id}", ApiFactory.Json))!;
            if (snapshot.Snapshot.Status is SnapshotStatus.Completed or SnapshotStatus.Failed) break;
            Assert.True(timer.Elapsed < TimeSpan.FromMinutes(5), "snapshot did not finish in 5 minutes");
            await Task.Delay(250);
        }
        var snapshotTime = timer.Elapsed;
        _out.WriteLine($"full-history snapshot: {snapshotTime.TotalSeconds:F1}s");

        Assert.Equal(SnapshotStatus.Completed, snapshot.Snapshot.Status);
        Assert.Equal(6_480, snapshot.Snapshot.VoteCount);
        var inputs = (await dana.GetFromJsonAsync<PagedResult<SnapshotInputDto>>($"/api/snapshots/{id}/inputs?pageSize=1", ApiFactory.Json))!;
        Assert.Equal(962_171 - 32 - 30, inputs.Total);   // every approved ballot, frozen
        Assert.True(snapshot.Scores.Count > 100);

        // loose budgets: catch a big regression, not machine noise (targets are in CLAUDE.md).
        // The same import measured 287 s and 350 s on this laptop depending on load.
        Assert.True(importTime < TimeSpan.FromMinutes(8), $"import took {importTime}");
        Assert.True(snapshotTime < TimeSpan.FromMinutes(1), $"snapshot took {snapshotTime}");
    }
}

[CollectionDefinition(Name)]
public class HistoricCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "historic";
}
