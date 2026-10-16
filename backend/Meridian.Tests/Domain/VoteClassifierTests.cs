using Meridian.Domain;
using Meridian.Domain.Ingestion;
using static Meridian.Tests.Domain.TestData;

namespace Meridian.Tests.Domain;

// ING-05, ING-06, ING-10, ING-11, ING-12 for one vote
public class VoteClassifierTests
{
    private const string VoteId = "MA/2024/001";

    private static VoteResult Classify(ApprovedVote? approved, params VoteRow[] rows) =>
        VoteClassifier.Classify(VoteId, rows, approved);

    private static RowClassification ClassOf(VoteResult result, int line) =>
        result.Rows.Single(r => r.LineNo == line).Classification;

    // --- new vote ---

    [Fact]
    public void Unknown_vote_is_created_and_all_rows_are_New()
    {
        var result = Classify(null, Row(2, "BRA", Ballot.Y), Row(3, "IND", Ballot.N));

        Assert.Equal(VoteDetailsOutcome.Created, result.DetailsOutcome);
        Assert.Equal(new NewVoteDetails(79, new DateOnly(2024, 1, 12), "Test Resolution"), result.VoteToCreate);
        Assert.All(result.Rows, r => Assert.Equal(RowClassification.New, r.Classification));
        Assert.Equal(new[] { "BRA", "IND" }, result.BallotsToAdd.Select(b => b.CountryCode));
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void Vote_with_nothing_ingestable_is_not_created()
    {
        var result = Classify(null, Row(6, "FRA", Ballot.Y), Row(7, "FRA", Ballot.N));

        Assert.Equal(VoteDetailsOutcome.NotCreated, result.DetailsOutcome);
        Assert.Null(result.VoteToCreate);
        Assert.Empty(result.BallotsToAdd);
    }

    // --- duplicates ---

    [Fact]
    public void Identical_row_is_Duplicate_and_counted_once()
    {
        var result = Classify(null, Row(4, "JPN", Ballot.Y), Row(5, "JPN", Ballot.Y));

        Assert.Equal(RowClassification.New, ClassOf(result, 4));
        Assert.Equal(RowClassification.Duplicate, ClassOf(result, 5));
        Assert.Equal("Identical to line 4", result.Rows[1].Reason);
        Assert.Single(result.BallotsToAdd);
    }

    [Fact]
    public void Same_ballot_with_different_country_name_is_Duplicate_with_the_difference_explained()
    {
        // decision: same vote + country + ballot, rows differ only in informational fields (ING-11)
        var result = Classify(null, Row(2, "TUR", Ballot.Y, name: "Turkey"), Row(3, "TUR", Ballot.Y, name: "Türkiye"));

        Assert.Equal(RowClassification.New, ClassOf(result, 2));
        Assert.Equal(RowClassification.Duplicate, ClassOf(result, 3));
        Assert.Contains("country_name ('Turkey' vs 'Türkiye')", result.Rows[1].Reason);
    }

    // --- contradictions (ING-05) ---

    [Fact]
    public void Same_country_with_different_ballots_is_Contradiction_and_neither_is_ingested()
    {
        var result = Classify(null, Row(2, "BRA", Ballot.Y), Row(6, "FRA", Ballot.Y), Row(7, "FRA", Ballot.N));

        Assert.Equal(RowClassification.Contradiction, ClassOf(result, 6));
        Assert.Equal(RowClassification.Contradiction, ClassOf(result, 7));
        Assert.Equal("FRA has different ballots in this file: Y (line 6), N (line 7)", result.Rows[1].Reason);
        Assert.Equal(new[] { "BRA" }, result.BallotsToAdd.Select(b => b.CountryCode));
    }

    [Fact]
    public void Different_titles_inside_one_vote_make_every_row_a_Contradiction()
    {
        // q1 MA/2024/025: normal, double-space and "(draft)" variants
        var result = Classify(null,
            Row(2, "BRA", Ballot.Y, title: "Nuclear Non-Proliferation"),
            Row(3, "IND", Ballot.Y, title: "Nuclear Non-Proliferation"),
            Row(4, "JPN", Ballot.N, title: "Nuclear  Non-Proliferation"),
            Row(5, "FRA", Ballot.N, title: "Nuclear Non-Proliferation (draft)"));

        Assert.Equal(VoteDetailsOutcome.Contradiction, result.DetailsOutcome);
        Assert.All(result.Rows, r => Assert.Equal(RowClassification.Contradiction, r.Classification));
        Assert.Contains("resolution_title has 3 different values", result.DetailsReason);
        Assert.Contains("'Nuclear  Non-Proliferation' (line 4)", result.DetailsReason);
        Assert.Empty(result.BallotsToAdd);
        Assert.Null(result.VoteToCreate);
    }

    [Fact]
    public void Different_dates_inside_one_vote_are_a_Contradiction_even_for_an_existing_vote()
    {
        var approved = Approved(new() { ["BRA"] = Ballot.Y });
        var result = Classify(approved,
            Row(2, "BRA", Ballot.Y, date: new DateOnly(2024, 1, 12)),
            Row(3, "IND", Ballot.Y, date: new DateOnly(2024, 1, 13)));

        Assert.Equal(VoteDetailsOutcome.Contradiction, result.DetailsOutcome);
        Assert.Empty(result.BallotsToAdd);
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void Malformed_rows_never_reach_the_consistency_check()
    {
        // historic: a shifted row has a different "title"; it must be Invalid, not taint the vote
        var lines = new[]
        {
            Line("MA/2018/131", "BRA", "Y", title: "Ocean Plastic Pollution", date: "2018-02-25"),
            "MA/2018/131,73,2018-02-25,Ocean Plastic Pollution, Including Capacity Building,EST,,Y",
            Line("MA/2018/131", "IND", "N", title: "Ocean Plastic Pollution", date: "2018-02-25"),
        };
        var validated = lines.Select((l, i) => RowValidator.Validate(i + 2, l, Members)).ToList();
        Assert.False(validated[1].IsValid);

        var result = VoteClassifier.Classify("MA/2018/131", validated.Where(v => v.IsValid).Select(v => v.Row!), null);

        Assert.Equal(VoteDetailsOutcome.Created, result.DetailsOutcome);
        Assert.All(result.Rows, r => Assert.Equal(RowClassification.New, r.Classification));
    }

    // --- against approved data ---

    [Fact]
    public void Same_value_as_approved_is_Unchanged()
    {
        var result = Classify(Approved(new() { ["IND"] = Ballot.Y }), Row(3, "IND", Ballot.Y));

        Assert.Equal(VoteDetailsOutcome.Unchanged, result.DetailsOutcome);
        Assert.Equal(RowClassification.Unchanged, ClassOf(result, 3));
        Assert.Empty(result.BallotsToAdd);
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void Different_ballot_opens_a_Conflict_and_never_overwrites()
    {
        var result = Classify(Approved(new() { ["BRA"] = Ballot.Y }), Row(2, "BRA", Ballot.A));

        var row = Assert.Single(result.Rows);
        Assert.Equal(RowClassification.Conflict, row.Classification);
        Assert.Equal("Approved Y, file has A", row.Reason);
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal(new ConflictProposal("BRA", ConflictField.Ballot, "Y", "A", null), conflict);
        Assert.Same(conflict, row.Conflict);
        Assert.Empty(result.BallotsToAdd);   // ING-08
    }

    [Fact]
    public void Missing_country_in_existing_vote_is_New_not_Conflict()
    {
        // ING-10: votes can arrive in parts
        var result = Classify(Approved(new() { ["BRA"] = Ballot.Y }), Row(2, "BRA", Ballot.Y), Row(3, "IND", Ballot.N));

        Assert.Equal(RowClassification.Unchanged, ClassOf(result, 2));
        Assert.Equal(RowClassification.New, ClassOf(result, 3));
        Assert.Equal(new[] { "IND" }, result.BallotsToAdd.Select(b => b.CountryCode));
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void Countries_absent_from_the_file_are_left_alone()
    {
        // ING-09 at country level: nothing is deleted or proposed for JPN
        var result = Classify(Approved(new() { ["BRA"] = Ballot.Y, ["JPN"] = Ballot.N }), Row(2, "BRA", Ballot.Y));

        Assert.Single(result.Rows);
        Assert.Empty(result.BallotsToAdd);
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void Different_country_name_for_same_code_is_not_a_conflict()
    {
        // ING-11: identity is the code
        var result = Classify(Approved(new() { ["TUR"] = Ballot.Y }), Row(2, "TUR", Ballot.Y, name: "Türkiye"));
        Assert.Equal(RowClassification.Unchanged, ClassOf(result, 2));
    }

    [Theory]
    [InlineData(ConflictField.Title, "Old title", "Test Resolution")]
    [InlineData(ConflictField.Date, "2024-02-05", "2024-01-12")]
    [InlineData(ConflictField.Session, "78", "79")]
    public void Changed_vote_detail_opens_a_details_conflict(ConflictField field, string approvedValue, string fileValue)
    {
        var approved = Approved(new() { ["BRA"] = Ballot.Y, ["IND"] = Ballot.Y },
            title: field == ConflictField.Title ? approvedValue : "Test Resolution",
            session: field == ConflictField.Session ? int.Parse(approvedValue) : 79,
            date: field == ConflictField.Date ? DateOnly.Parse(approvedValue) : null);

        var result = Classify(approved, Row(2, "BRA", Ballot.A), Row(3, "IND", Ballot.Y), Row(4, "JPN", Ballot.N));

        Assert.Equal(VoteDetailsOutcome.Conflict, result.DetailsOutcome);
        Assert.Contains(new ConflictProposal(null, field, approvedValue, fileValue, null), result.Conflicts);
        // country rows are still classified on their own ballot (7.2: IND/101 Unchanged while title conflicts)
        Assert.Equal(RowClassification.Conflict, ClassOf(result, 2));
        Assert.Equal(RowClassification.Unchanged, ClassOf(result, 3));
        Assert.Equal(RowClassification.New, ClassOf(result, 4));
    }

    // --- existing conflicts (ING-12) ---

    [Fact]
    public void Same_proposal_already_pending_links_instead_of_opening_a_new_conflict()
    {
        var pending = new ExistingConflict(7, "BRA", ConflictField.Ballot, "A", ConflictStatus.Pending, null, null);
        var result = Classify(Approved(new() { ["BRA"] = Ballot.Y }, conflicts: pending), Row(2, "BRA", Ballot.A));

        var conflict = Assert.Single(result.Conflicts);
        Assert.False(conflict.IsNew);
        Assert.Equal(7, conflict.ExistingConflictId);
        Assert.Contains("already pending as conflict #7", result.Rows[0].Reason);
    }

    [Fact]
    public void Pending_vote_detail_conflict_is_linked_too()
    {
        var pending = new ExistingConflict(8, null, ConflictField.Title, "Test Resolution", ConflictStatus.Pending, null, null);
        var result = Classify(Approved(new() { ["BRA"] = Ballot.Y }, title: "Old title", conflicts: pending), Row(2, "BRA", Ballot.Y));

        Assert.Equal(8, Assert.Single(result.Conflicts).ExistingConflictId);
    }

    [Fact]
    public void Previously_rejected_proposal_is_linked_to_that_decision()
    {
        var rejected = new ExistingConflict(9, "BRA", ConflictField.Ballot, "A", ConflictStatus.Rejected,
            "dana", new DateTime(2024, 5, 1, 10, 0, 0, DateTimeKind.Utc));
        var result = Classify(Approved(new() { ["BRA"] = Ballot.Y }, conflicts: rejected), Row(2, "BRA", Ballot.A));

        Assert.Equal(RowClassification.Conflict, result.Rows[0].Classification);
        Assert.Equal(9, Assert.Single(result.Conflicts).ExistingConflictId);
        Assert.Contains("already rejected on 2024-05-01 by dana (conflict #9)", result.Rows[0].Reason);
    }

    [Fact]
    public void A_different_proposal_opens_its_own_conflict_even_when_another_is_pending()
    {
        var pending = new ExistingConflict(7, "BRA", ConflictField.Ballot, "A", ConflictStatus.Pending, null, null);
        var result = Classify(Approved(new() { ["BRA"] = Ballot.Y }, conflicts: pending), Row(2, "BRA", Ballot.N));

        Assert.True(Assert.Single(result.Conflicts).IsNew);
    }

    // --- every row gets exactly one class (ING-06) ---

    [Fact]
    public void Every_input_row_gets_exactly_one_outcome()
    {
        var rows = new[]
        {
            Row(2, "BRA", Ballot.A), Row(3, "IND", Ballot.Y), Row(4, "JPN", Ballot.Y), Row(5, "JPN", Ballot.Y),
            Row(6, "FRA", Ballot.Y), Row(7, "FRA", Ballot.N), Row(8, "TUR", Ballot.N, name: "Türkiye"), Row(9, "TUR", Ballot.N),
        };
        var result = Classify(Approved(new() { ["BRA"] = Ballot.Y, ["IND"] = Ballot.Y }), rows);

        Assert.Equal(rows.Select(r => r.LineNo), result.Rows.Select(r => r.LineNo));
    }
}
