using System.Globalization;

namespace Meridian.Domain.Ingestion;

// One problem with a row: which field, why, and the value exactly as it was in the file (ING-14)
public record RowIssue(string Field, string Reason, string OriginalValue);

// A row that passed every per-row check, with clean typed values
public record VoteRow(
    int LineNo,
    string AssemblyVoteId,
    int Session,
    DateOnly VoteDate,
    string Title,
    string CountryCode,
    string CountryName,
    Ballot Ballot,
    string PublishedAt,
    string? NormalizationNote = null)
{
    // what "identical row" means for Duplicate: every value, not the line number
    public object Content => (Session, VoteDate, Title, CountryCode, CountryName, Ballot, PublishedAt);
}

public record RowValidation(int LineNo, IReadOnlyList<string>? Fields, VoteRow? Row, IReadOnlyList<RowIssue> Issues)
{
    public bool IsValid => Row != null;

    public string Reason => string.Join("; ", Issues.Select(i => $"{i.Field}: {i.Reason} (value: '{i.OriginalValue}')"));
}

// ING-04 per-row checks. Runs before any cross-row check (ING-05),
// so a malformed row can never make a whole vote look inconsistent.
public static class RowValidator
{
    private const string DateFormat = "yyyy-MM-dd";

    public static RowValidation Validate(int lineNo, string rawLine, MemberList members)
    {
        if (!CsvFormat.TrySplit(rawLine, out var fields, out var csvError))
            return Invalid(lineNo, null, new RowIssue("row", $"Malformed CSV: {csvError}", rawLine));

        if (fields.Count != CsvFormat.Columns.Length)
            return Invalid(lineNo, fields, new RowIssue("row",
                $"Expected {CsvFormat.Columns.Length} columns, found {fields.Count}", rawLine));

        var issues = new List<RowIssue>();
        var notes = new List<string>();

        // Normalization (ING-13): trim whitespace; uppercase the country code. Each change is noted.
        string Clean(int index)
        {
            var value = fields[index].Trim();
            if (value != fields[index]) notes.Add($"{CsvFormat.Columns[index]}: trimmed whitespace");
            return value;
        }

        var voteId = Clean(0);
        var sessionText = Clean(1);
        var dateText = Clean(2);
        var title = Clean(3);
        var countryText = Clean(4);
        var countryName = Clean(5);
        var ballotText = Clean(6);
        var publishedAt = Clean(7);

        var country = countryText.ToUpperInvariant();
        if (country != countryText) notes.Add($"country_code: '{countryText}' normalized to '{country}'");

        // country_name and published_at are informational only, so not required
        void Required(string field, string value, int index)
        {
            if (value == "") issues.Add(new RowIssue(field, "Required field is empty", fields[index]));
        }
        Required("assembly_vote_id", voteId, 0);
        Required("session", sessionText, 1);
        Required("vote_date", dateText, 2);
        Required("resolution_title", title, 3);
        Required("country_code", country, 4);
        Required("ballot", ballotText, 6);

        int session = 0;
        if (sessionText != "" && !(int.TryParse(sessionText, NumberStyles.None, CultureInfo.InvariantCulture, out session) && session > 0))
            issues.Add(new RowIssue("session", "Session must be a positive whole number", fields[1]));

        DateOnly date = default;
        var dateOk = dateText != "" && DateOnly.TryParseExact(dateText, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
        if (dateText != "" && !dateOk)
            issues.Add(new RowIssue("vote_date", $"Date must be in {DateFormat} format", fields[2]));

        // exact values only: "Yes", "YES", "0", "?" are rejected, not guessed
        Ballot ballot = default;
        var ballotOk = ballotText is "Y" or "N" or "A" or "X";
        if (ballotOk)
            ballot = Enum.Parse<Ballot>(ballotText);
        else if (ballotText != "")
            issues.Add(new RowIssue("ballot", "Ballot must be one of Y, N, A, X", fields[6]));

        if (country != "")
        {
            if (!members.IsKnown(country))
                issues.Add(new RowIssue("country_code", "Unknown country code", fields[4]));
            else if (dateOk && !members.WasMemberOn(country, date))
                issues.Add(new RowIssue("country_code", $"{country} was not a member on {date.ToString(DateFormat, CultureInfo.InvariantCulture)}", fields[4]));
        }

        if (issues.Count > 0)
            return new RowValidation(lineNo, fields, null, issues);

        var row = new VoteRow(lineNo, voteId, session, date, title, country, countryName, ballot, publishedAt,
            notes.Count > 0 ? string.Join("; ", notes) : null);
        return new RowValidation(lineNo, fields, row, Array.Empty<RowIssue>());
    }

    private static RowValidation Invalid(int lineNo, IReadOnlyList<string>? fields, RowIssue issue) =>
        new(lineNo, fields, null, new[] { issue });
}
