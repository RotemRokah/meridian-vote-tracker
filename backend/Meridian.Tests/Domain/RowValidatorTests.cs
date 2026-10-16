using Meridian.Domain;
using Meridian.Domain.Ingestion;
using static Meridian.Tests.Domain.TestData;

namespace Meridian.Tests.Domain;

// ING-04 per-row checks and ING-13 normalization
public class RowValidatorTests
{
    private static RowValidation Validate(string line) => RowValidator.Validate(10, line, Members);

    private static RowIssue SingleIssue(string line)
    {
        var result = Validate(line);
        Assert.False(result.IsValid);
        return Assert.Single(result.Issues);
    }

    [Fact]
    public void Valid_row_is_parsed_into_typed_values()
    {
        var result = Validate(Line("MA/2024/001", "BRA", "Y", title: "Ocean Policy", date: "2024-01-12"));

        Assert.True(result.IsValid);
        var row = result.Row!;
        Assert.Equal(10, row.LineNo);
        Assert.Equal("MA/2024/001", row.AssemblyVoteId);
        Assert.Equal(79, row.Session);
        Assert.Equal(new DateOnly(2024, 1, 12), row.VoteDate);
        Assert.Equal("Ocean Policy", row.Title);
        Assert.Equal("BRA", row.CountryCode);
        Assert.Equal(Ballot.Y, row.Ballot);
        Assert.Null(row.NormalizationNote);
    }

    // --- structure ---

    [Fact]
    public void Semicolon_row_with_5_columns_is_invalid_structure()
    {
        // q1 line 3139
        var issue = SingleIssue("MA/2024/022,79,2024-02-17,Arms Trade Transparency: Follow-up to the Summit,AGO;N");
        Assert.Equal("row", issue.Field);
        Assert.Contains("Expected 8 columns, found 5", issue.Reason);
        Assert.Contains("AGO;N", issue.OriginalValue);
    }

    [Fact]
    public void Row_with_7_columns_is_invalid_structure()
    {
        // historic line 134746
        var issue = SingleIssue("MA/2020/350,75,2020-07-16,Biological Diversity: Follow-up to the Summit,LVA,,Y");
        Assert.Contains("found 7", issue.Reason);
    }

    [Fact]
    public void Broken_quoting_is_invalid_structure()
    {
        var issue = SingleIssue("MA/2024/001,79,2024-01-12,\"Unclosed title,BRA,Brazil,Y,x");
        Assert.Contains("Malformed CSV", issue.Reason);
    }

    // --- required fields and formats ---

    [Fact]
    public void Empty_ballot_is_a_missing_required_field()
    {
        // q1 line 2846 (AZE)
        var issue = SingleIssue(Line("MA/2024/020", "ARG", ""));
        Assert.Equal("ballot", issue.Field);
        Assert.Equal("Required field is empty", issue.Reason);
    }

    [Theory]
    [InlineData("Yes")]   // q1 line 2842
    [InlineData("YES")]
    [InlineData("0")]
    [InlineData("?")]
    [InlineData("y")]
    public void Ballot_variants_are_rejected_not_guessed(string ballot)
    {
        var issue = SingleIssue(Line("MA/2024/020", "ARG", ballot));
        Assert.Equal("ballot", issue.Field);
        Assert.Equal(ballot, issue.OriginalValue);
    }

    [Theory]
    [InlineData("21/02/2024")]   // q1 lines 3880-3885
    [InlineData("2024-2-21")]
    [InlineData("2024-02-30")]
    public void Non_ISO_or_impossible_date_is_rejected(string date)
    {
        var issue = SingleIssue(Line("MA/2024/027", "BRA", "Y", date: date));
        Assert.Equal("vote_date", issue.Field);
        Assert.Equal(date, issue.OriginalValue);
    }

    [Fact]
    public void Non_numeric_session_is_rejected()
    {
        var issue = SingleIssue(Line("MA/2024/001", "BRA", "Y", session: "79th"));
        Assert.Equal("session", issue.Field);
    }

    [Fact]
    public void Country_name_and_published_at_are_not_required()
    {
        Assert.True(Validate(Line("MA/2024/001", "BRA", "Y", name: "", publishedAt: "")).IsValid);
    }

    // --- member list ---

    [Theory]
    [InlineData("FRN")]   // q1 line 2143
    [InlineData("XXX")]   // historic
    public void Unknown_country_code_is_rejected(string code)
    {
        var issue = SingleIssue(Line("MA/2024/015", code, "Y"));
        Assert.Equal("Unknown country code", issue.Reason);
        Assert.Equal(code, issue.OriginalValue);
    }

    [Theory]
    [InlineData("NZL", "2024-03-01", false)]   // left 2021-06-30
    [InlineData("NZL", "2021-06-30", true)]    // last day is still a member
    [InlineData("NZL", "2021-07-01", false)]
    [InlineData("ISL", "2020-06-01", false)]   // gap between two periods
    [InlineData("ISL", "2022-01-01", true)]    // rejoined
    [InlineData("ISL", "2019-12-31", true)]
    public void Country_must_be_a_member_on_the_vote_date(string code, string date, bool valid)
    {
        var result = Validate(Line("MA/2024/033", code, "Y", date: date));

        Assert.Equal(valid, result.IsValid);
        if (!valid)
            Assert.Contains("was not a member on " + date, Assert.Single(result.Issues).Reason);
    }

    [Fact]
    public void Shifted_columns_from_unquoted_comma_are_invalid_with_original_values()
    {
        // historic line 521760: the comma in the title shifts every column to the right
        var result = Validate("MA/2018/131,73,2018-02-25,Ocean Plastic Pollution, Including Capacity Building,EST,,Y");

        Assert.False(result.IsValid);
        var country = Assert.Single(result.Issues, i => i.Field == "country_code");
        Assert.Equal(" Including Capacity Building", country.OriginalValue);
        Assert.Contains(result.Issues, i => i.Field == "ballot" && i.Reason == "Required field is empty");
    }

    [Fact]
    public void All_issues_of_a_row_are_reported_together()
    {
        var result = Validate(Line("MA/2024/001", "FRN", "Yes", date: "12/01/2024"));

        Assert.Equal(new[] { "vote_date", "ballot", "country_code" }, result.Issues.Select(i => i.Field));
        Assert.Contains("ballot: Ballot must be one of Y, N, A, X (value: 'Yes')", result.Reason);
    }

    // --- normalization is done and reported (ING-13) ---

    [Fact]
    public void Lowercase_country_code_is_uppercased_and_noted()
    {
        // q1 line 2594
        var result = Validate(Line("MA/2024/018", "deu", "X"));

        Assert.True(result.IsValid);
        Assert.Equal("DEU", result.Row!.CountryCode);
        Assert.Equal("country_code: 'deu' normalized to 'DEU'", result.Row.NormalizationNote);
    }

    [Fact]
    public void Surrounding_whitespace_is_trimmed_and_noted()
    {
        var result = Validate(Line("MA/2024/001", " BRA", "Y ", name: "Brazil"));

        Assert.True(result.IsValid);
        Assert.Equal("BRA", result.Row!.CountryCode);
        Assert.Equal(Ballot.Y, result.Row.Ballot);
        Assert.Equal("country_code: trimmed whitespace; ballot: trimmed whitespace", result.Row.NormalizationNote);
    }

    [Fact]
    public void Internal_double_space_in_title_is_kept()
    {
        // decision: titles are compared exactly after trim; "Nuclear  Non-..." stays different
        var result = Validate(Line("MA/2024/025", "BRA", "Y", title: "Nuclear  Non-Proliferation"));
        Assert.Equal("Nuclear  Non-Proliferation", result.Row!.Title);
        Assert.Null(result.Row.NormalizationNote);
    }
}
