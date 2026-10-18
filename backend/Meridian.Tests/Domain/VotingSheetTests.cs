using Meridian.Domain.Ingestion;
using Meridian.Domain.Pdf;

namespace Meridian.Tests.Domain;

// PDF-03: what Document Intelligence returned for two real sheets (recorded once, no network here),
// and every check that runs before a human sees it.
public class VotingSheetTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "pdf", name));

    // every code on the sheet is a member, unless a test says otherwise
    private static MemberList MembersOf(VotingSheet sheet, params string[] except) =>
        new(sheet.Rows.Select(r => r.Code).Distinct().Except(except)
            .Select(c => new MembershipPeriod(c, new DateOnly(1945, 1, 1), null)));

    [Fact]
    public void Clean_sheet_is_read_completely_and_matches_its_printed_totals()
    {
        var sheet = VotingSheetReader.Read(Fixture("MA_2011_042.layout.json"));
        var checkedSheet = VotingSheetReader.Validate(sheet, MembersOf(sheet));

        Assert.Equal(("MA/2011/042", 66, "Protection of Cultural Heritage"), (sheet.AssemblyVoteId, sheet.Session, sheet.Title));
        Assert.Equal("2011-10-04", VotingSheetReader.IsoDate(sheet.VoteDate));
        Assert.Equal(new SheetTotals(70, 55, 7, 16), sheet.PrintedTotals);
        Assert.Equal(148, sheet.Rows.Count);
        Assert.Equal("N", sheet.Rows.Single(r => r.Code == "AFG").Ballot);
        Assert.Equal("X", sheet.Rows.Single(r => r.Code == "DZA").Ballot);   // no tick: not voting
        Assert.Empty(checkedSheet.Issues);
        Assert.Empty(VotingSheetReader.BlockingProblems(checkedSheet));
    }

    [Fact]
    public void Noisy_sheet_flags_double_ticks_and_blocks_approval_until_fixed()
    {
        var sheet = VotingSheetReader.Read(Fixture("MA_2009_117.layout.json"));
        var checkedSheet = VotingSheetReader.Validate(sheet, MembersOf(sheet));

        Assert.Equal("MA/2009/117", sheet.AssemblyVoteId);   // read as "MA/2009/117," from the scan
        Assert.Contains(sheet.Issues, i => i.Contains("removed"));
        var hun = checkedSheet.Rows.Single(r => r.Code == "HUN");
        Assert.Null(hun.Ballot);
        Assert.Contains(hun.Issues, i => i.Contains("2 boxes ticked"));
        Assert.Contains("ballot of HUN", VotingSheetReader.BlockingProblems(checkedSheet));
    }

    [Fact]
    public void Low_confidence_tick_is_flagged_and_a_confident_one_is_not()
    {
        var sheet = VotingSheetReader.Read(Fixture("MA_2011_042.layout.json"));
        Assert.True(sheet.Rows.Single(r => r.Code == "AFG").Confidence > 0.9);   // tick confidence found by its position
        var unsure = sheet with { Rows = sheet.Rows.Select(r => r.Code == "AFG" ? r with { Confidence = 0.42 } : r).ToList() };
        var checkedSheet = VotingSheetReader.Validate(unsure, MembersOf(sheet));

        Assert.Contains(checkedSheet.Rows.Single(r => r.Code == "AFG").Issues, i => i == "Low confidence 0.42 on the ticked box");
        Assert.Empty(checkedSheet.Rows.Single(r => r.Code == "ALB").Issues);
    }

    [Fact]
    public void Unknown_code_and_repeated_country_are_flagged()
    {
        var sheet = VotingSheetReader.Read(Fixture("MA_2011_042.layout.json"));
        var withProblems = sheet with
        {
            Rows = sheet.Rows.Append(sheet.Rows[0]).Append(new SheetRow("XXX", "Nowhere", "Y", 0.99, new())).ToList(),
        };
        var checkedSheet = VotingSheetReader.Validate(withProblems, MembersOf(sheet));

        Assert.Contains(checkedSheet.Rows.Last().Issues, i => i.Contains("Unknown country code 'XXX'"));
        Assert.Contains(checkedSheet.Rows[^2].Issues, i => i.Contains("appears more than once"));
    }

    [Fact]
    public void Counts_that_differ_from_the_printed_totals_are_flagged()
    {
        var sheet = VotingSheetReader.Read(Fixture("MA_2011_042.layout.json"));
        var oneChanged = sheet with
        {
            Rows = sheet.Rows.Select(r => r.Code == "AFG" ? r with { Ballot = "Y" } : r).ToList(),
        };
        var checkedSheet = VotingSheetReader.Validate(oneChanged, MembersOf(sheet));

        Assert.Contains(checkedSheet.Issues, i => i.Contains("differ from the printed totals"));
        Assert.Contains("counts differ from the printed totals", VotingSheetReader.BlockingProblems(checkedSheet));   // can't be approved
    }

    [Fact]
    public void Missing_rows_block_approval_through_the_totals()
    {
        var sheet = VotingSheetReader.Read(Fixture("MA_2011_042.layout.json"));
        var rowsLost = sheet with { Rows = sheet.Rows.Take(109).ToList() };   // e.g. a table that couldn't be read
        Assert.Contains("counts differ from the printed totals",
            VotingSheetReader.BlockingProblems(VotingSheetReader.Validate(rowsLost, MembersOf(sheet))));
    }

    [Fact]
    public void A_repeated_column_header_inside_the_table_is_not_a_country()
    {
        // the corrigendum's layout had "Code | Member state | YES | NO | ABST." as a data row
        const string layout = """
            {"content":"Recorded totals: Yes: 1 No: 0 Abstentions: 0 Not voting: 0",
             "pages":[{"selectionMarks":[{"state":"selected","confidence":0.99,"span":{"offset":7}}]}],
             "tables":[{"rowCount":3,"columnCount":5,"cells":[
               {"rowIndex":0,"columnIndex":0,"content":"Code"},
               {"rowIndex":1,"columnIndex":0,"content":"Code"},{"rowIndex":1,"columnIndex":1,"content":"Member state"},
               {"rowIndex":2,"columnIndex":0,"content":"AFG"},{"rowIndex":2,"columnIndex":1,"content":"Afghanistan"},
               {"rowIndex":2,"columnIndex":2,"content":":selected:","spans":[{"offset":7}]}]}]}
            """;
        var sheet = VotingSheetReader.Read(layout);
        Assert.Equal(("AFG", "Y"), (Assert.Single(sheet.Rows).Code, sheet.Rows[0].Ballot));
    }

    [Fact]
    public void Missing_header_and_totals_are_flagged()
    {
        var empty = new VotingSheet(null, null, "sometime", "", new(), null, new());
        var checkedSheet = VotingSheetReader.Validate(empty, MembersOf(empty));

        Assert.Contains(checkedSheet.Issues, i => i.Contains("Vote number"));
        Assert.Contains(checkedSheet.Issues, i => i.Contains("could not be read"));
        Assert.Contains(checkedSheet.Issues, i => i.Contains("No country rows"));
        Assert.Contains(checkedSheet.Issues, i => i.Contains("Printed totals were not found"));
    }
}
