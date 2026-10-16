using Meridian.Domain;
using Meridian.Domain.Ingestion;
using static Meridian.Tests.Domain.TestData;

namespace Meridian.Tests.Domain;

// Spec section 7.2, row by row. The conflict decision part is tested in step 5, snapshots in step 8.
public class WorkedExampleTests
{
    private static readonly string[] OriginalFile =
    {
        Header,
        Line("MA/2024/101", "BRA", "Y", title: "Title 101"),   // line 2: new
        Line("MA/2024/101", "IND", "Y", title: "Title 101"),   // line 3: new
        Line("MA/2024/102", "JPN", "Y", title: "Title 102"),   // line 4: new
        Line("MA/2024/102", "JPN", "Y", title: "Title 102"),   // line 5: duplicate
        Line("MA/2024/103", "FRA", "Y", title: "Title 103"),   // line 6: contradiction
        Line("MA/2024/103", "FRA", "N", title: "Title 103"),   // line 7: contradiction
        Line("MA/2024/104", "FRN", "Y", title: "Title 104"),   // line 8: invalid, unknown code
        Line("MA/2024/104", "NZL", "Y", title: "Title 104"),   // line 9: invalid, not a member in 2024
    };

    private static readonly string[] RevisedFile =
    {
        Header,
        Line("MA/2024/101", "BRA", "A", title: "Title 101 revised"),   // line 2: conflict
        Line("MA/2024/101", "IND", "Y", title: "Title 101 revised"),   // line 3: unchanged
        Line("MA/2024/103", "FRA", "N", title: "Title 103"),           // line 4: new
        Line("MA/2024/104", "FRA", "X", title: "Title 104"),           // line 5: new
        Line("MA/2024/105", "BRA", "Y", title: "Title 105"),           // line 6: new (new vote)
        // vote 102 is absent: no action (ING-09)
    };

    [Fact]
    public void Original_file()
    {
        var ledger = new InMemoryLedger();
        var run = ledger.Import(OriginalFile);

        Assert.Equal(RowClassification.New, run.Class(2));
        Assert.Equal(RowClassification.New, run.Class(3));
        Assert.Equal(RowClassification.New, run.Class(4));
        Assert.Equal(RowClassification.Duplicate, run.Class(5));
        Assert.Equal(RowClassification.Contradiction, run.Class(6));
        Assert.Equal(RowClassification.Contradiction, run.Class(7));
        Assert.Equal(RowClassification.Invalid, run.Class(8));
        Assert.Contains("Unknown country code", run.Reason(8));
        Assert.Equal(RowClassification.Invalid, run.Class(9));
        Assert.Contains("NZL was not a member", run.Reason(9));
        Assert.Equal(8, run.Rows.Count);   // every line has exactly one class

        Assert.Equal(Ballot.Y, ledger.BallotOf("MA/2024/101", "BRA"));
        Assert.Equal(Ballot.Y, ledger.BallotOf("MA/2024/102", "JPN"));
        Assert.False(ledger.HasVote("MA/2024/103"));   // neither FRA value picked
        Assert.False(ledger.HasVote("MA/2024/104"));
        Assert.Equal(0, run.NewConflicts);
    }

    [Fact]
    public void Revised_file_after_original()
    {
        var ledger = new InMemoryLedger();
        ledger.Import(OriginalFile);
        var run = ledger.Import(RevisedFile);

        Assert.Equal(RowClassification.Conflict, run.Class(2));
        Assert.Equal(RowClassification.Unchanged, run.Class(3));
        Assert.Equal(RowClassification.New, run.Class(4));
        Assert.Equal(RowClassification.New, run.Class(5));
        Assert.Equal(RowClassification.New, run.Class(6));

        // approved Y stays until a human decides (ING-08)
        Assert.Equal(Ballot.Y, ledger.BallotOf("MA/2024/101", "BRA"));
        Assert.Equal("Title 101", ledger.TitleOf("MA/2024/101"));

        // conflicts: BRA ballot and vote 101 title
        Assert.Equal(2, run.NewConflicts);
        var title = Assert.Single(ledger.Conflicts, c => c.Field == ConflictField.Title);
        Assert.Equal("Title 101 revised", title.ProposedValue);
        Assert.Equal(VoteDetailsOutcome.Conflict, run.Votes["MA/2024/101"].DetailsOutcome);

        Assert.Equal(Ballot.N, ledger.BallotOf("MA/2024/103", "FRA"));
        Assert.Equal(Ballot.X, ledger.BallotOf("MA/2024/104", "FRA"));
        Assert.Equal(Ballot.Y, ledger.BallotOf("MA/2024/105", "BRA"));

        // vote 102 absent from the file: untouched (ING-09)
        Assert.False(run.Votes.ContainsKey("MA/2024/102"));
        Assert.Equal(Ballot.Y, ledger.BallotOf("MA/2024/102", "JPN"));
    }

    [Fact]
    public void Re_uploading_the_revised_file_changes_nothing()
    {
        var ledger = new InMemoryLedger();
        ledger.Import(OriginalFile);
        ledger.Import(RevisedFile);
        var conflictsBefore = ledger.Conflicts.Count;

        var run = ledger.Import(RevisedFile);

        Assert.Equal(0, run.Rows.Values.Count(r => r.Classification == RowClassification.New));
        Assert.Equal(0, run.NewConflicts);
        Assert.Equal(conflictsBefore, ledger.Conflicts.Count);
        // still reported as Conflict, linked to the pending one (ING-12)
        Assert.Equal(RowClassification.Conflict, run.Class(2));
        Assert.Contains("already pending", run.Reason(2));
    }
}

// Test stand-in for the database: applies VoteResults the way the import worker will (step 4).
public class InMemoryLedger
{
    public record StoredConflict(long Id, string VoteId, string? CountryCode, ConflictField Field,
        string ProposedValue, ConflictStatus Status);

    private readonly Dictionary<string, (NewVoteDetails Details, Dictionary<string, Ballot> Ballots)> _votes = new();
    public List<StoredConflict> Conflicts { get; } = new();

    public bool HasVote(string voteId) => _votes.ContainsKey(voteId);
    public Ballot BallotOf(string voteId, string country) => _votes[voteId].Ballots[country];
    public string TitleOf(string voteId) => _votes[voteId].Details.Title;

    public ImportRun Import(string[] lines)
    {
        Assert.True(CsvFormat.CheckHeader(lines[0]).IsValid);
        var rows = new Dictionary<int, RowOutcome>();
        var valid = new List<VoteRow>();

        for (var i = 1; i < lines.Length; i++)
        {
            var validation = RowValidator.Validate(i + 1, lines[i], Members);   // line numbers are 1-based incl. header
            if (validation.IsValid) valid.Add(validation.Row!);
            else rows[i + 1] = new RowOutcome(i + 1, RowClassification.Invalid, validation.Reason);
        }

        var votes = new Dictionary<string, VoteResult>();
        var newConflicts = 0;
        foreach (var group in valid.GroupBy(r => r.AssemblyVoteId))   // ING-03
        {
            var result = VoteClassifier.Classify(group.Key, group, ApprovedFor(group.Key));
            votes[group.Key] = result;
            foreach (var outcome in result.Rows) rows[outcome.LineNo] = outcome;

            if (result.VoteToCreate != null)
                _votes[group.Key] = (result.VoteToCreate, new Dictionary<string, Ballot>());
            foreach (var row in result.BallotsToAdd)
                _votes[group.Key].Ballots.Add(row.CountryCode, row.Ballot);   // Add throws if it would overwrite
            foreach (var c in result.Conflicts.Where(c => c.IsNew))
            {
                Conflicts.Add(new StoredConflict(Conflicts.Count + 1, group.Key, c.CountryCode, c.Field, c.ProposedValue, ConflictStatus.Pending));
                newConflicts++;
            }
        }

        return new ImportRun(rows, votes, newConflicts);
    }

    private ApprovedVote? ApprovedFor(string voteId)
    {
        if (!_votes.TryGetValue(voteId, out var v)) return null;
        var conflicts = Conflicts.Where(c => c.VoteId == voteId)
            .Select(c => new ExistingConflict(c.Id, c.CountryCode, c.Field, c.ProposedValue, c.Status, null, null))
            .ToList();
        return new ApprovedVote(v.Details.Session, v.Details.VoteDate, v.Details.Title, v.Ballots, conflicts);
    }
}

public record ImportRun(Dictionary<int, RowOutcome> Rows, Dictionary<string, VoteResult> Votes, int NewConflicts)
{
    public RowClassification Class(int line) => Rows[line].Classification;
    public string? Reason(int line) => Rows[line].Reason;
}
