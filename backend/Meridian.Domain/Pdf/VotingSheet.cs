using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Meridian.Domain.Ingestion;

namespace Meridian.Domain.Pdf;

// One country row read from a scanned voting sheet. Ballot is null when the ticks don't say (0 or 2+ ticks).
public record SheetRow(string Code, string Name, string? Ballot, double? Confidence, List<string> Issues);

public record SheetTotals(int Yes, int No, int Abstain, int NotVoting);

// What Document Intelligence read from one PDF, plus every problem found (PDF-03).
// Nothing here is trusted: it all goes to a human before ingestion (PDF-04, PDF-05).
public record VotingSheet(
    string? AssemblyVoteId, int? Session, string? VoteDate, string? Title,
    List<SheetRow> Rows, SheetTotals? PrintedTotals, List<string> Issues);

public static class VotingSheetReader
{
    // A ticked box under this confidence goes to the analyst. Unticked boxes always come back
    // with 0.10 (seen on the real sheets), so only the ticked box's confidence means anything.
    public const double LowConfidence = 0.8;

    private static readonly string[] BallotColumns = { "Y", "N", "A" };   // YES, NO, ABST. columns
    private static readonly Regex TotalsLine = new(
        @"Yes:\s*(\d+)\s*No:\s*(\d+)\s*Abstentions:\s*(\d+)\s*Not voting:\s*(\d+)", RegexOptions.IgnoreCase);

    // analyzeResult from the layout model (all pages of one PDF)
    public static VotingSheet Read(string analyzeResultJson)
    {
        using var doc = JsonDocument.Parse(analyzeResultJson);
        var root = doc.RootElement;
        var issues = new List<string>();

        // confidence of each ticked box, by its position in the text
        var markConfidence = new Dictionary<int, double>();
        foreach (var page in root.GetProperty("pages").EnumerateArray())
            if (page.TryGetProperty("selectionMarks", out var marks))
                foreach (var m in marks.EnumerateArray())
                    markConfidence[m.GetProperty("span").GetProperty("offset").GetInt32()] = m.GetProperty("confidence").GetDouble();

        string? voteId = null, date = null, title = null;
        int? session = null;
        var rows = new List<SheetRow>();

        foreach (var table in root.GetProperty("tables").EnumerateArray())
        {
            var cells = new Dictionary<(int Row, int Col), JsonElement>();
            foreach (var c in table.GetProperty("cells").EnumerateArray())
                cells[(c.GetProperty("rowIndex").GetInt32(), c.GetProperty("columnIndex").GetInt32())] = c;
            string Text(int r, int c) => cells.TryGetValue((r, c), out var cell) ? cell.GetProperty("content").GetString()!.Trim() : "";

            var columns = table.GetProperty("columnCount").GetInt32();
            var rowCount = table.GetProperty("rowCount").GetInt32();

            if (columns == 2)   // the header box: "Vote No.:", "Session:", "Date of vote:", "Resolution:"
            {
                for (var r = 0; r < rowCount; r++)
                {
                    var label = Text(r, 0).ToLowerInvariant();
                    var value = Text(r, 1);
                    if (label.StartsWith("vote no")) voteId = value;
                    else if (label.StartsWith("session")) session = int.TryParse(value, out var s) ? s : null;
                    else if (label.StartsWith("date")) date = value;
                    else if (label.StartsWith("resolution")) title = value;
                }
                continue;
            }

            if (columns != 5) { issues.Add($"Unexpected table with {columns} columns was ignored"); continue; }

            for (var r = 1; r < rowCount; r++)   // row 0: Code, Member state, YES, NO, ABST.
            {
                var code = Text(r, 0);
                if (code == "" || code.StartsWith(":")) continue;   // empty rows at the end of the last page
                if (code.Equals("Code", StringComparison.OrdinalIgnoreCase)) continue;   // a header row read as data (seen on the corrigendum)
                var ticked = new List<(string Ballot, double Confidence)>();
                for (var i = 0; i < 3; i++)
                {
                    if (!cells.TryGetValue((r, 2 + i), out var cell) || cell.GetProperty("content").GetString() != ":selected:") continue;
                    var offset = cell.GetProperty("spans")[0].GetProperty("offset").GetInt32();
                    ticked.Add((BallotColumns[i], markConfidence.GetValueOrDefault(offset, 0)));
                }
                var rowIssues = new List<string>();
                string? ballot;
                double? confidence = null;
                if (ticked.Count == 0) ballot = "X";   // no tick: not voting
                else if (ticked.Count == 1) { ballot = ticked[0].Ballot; confidence = ticked[0].Confidence; }
                else { ballot = null; rowIssues.Add($"{ticked.Count} boxes ticked ({string.Join(", ", ticked.Select(t => t.Ballot))})"); }
                rows.Add(new SheetRow(code, Text(r, 1).TrimEnd(',', '.'), ballot, confidence, rowIssues));
            }
        }

        if (voteId != null && (voteId.EndsWith('.') || voteId.EndsWith(',')))   // scan noise: "MA/2009/117." (normalized, and reported)
        {
            issues.Add($"Vote number '{voteId}': trailing '{voteId[^1]}' removed");
            voteId = voteId.TrimEnd('.', ',');
        }

        SheetTotals? totals = null;
        var match = TotalsLine.Match(root.GetProperty("content").GetString() ?? "");
        if (match.Success)
            totals = new SheetTotals(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value),
                int.Parse(match.Groups[3].Value), int.Parse(match.Groups[4].Value));

        return new VotingSheet(voteId, session, date, title, rows, totals, issues);
    }

    // Header values the importer can use, or the problem with each. "4 October 2011" -> 2011-10-04.
    public static string? IsoDate(string? printed) =>
        DateTime.TryParseExact(printed?.Trim(), new[] { "d MMMM yyyy", "dd MMMM yyyy" }, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var d) ? d.ToString("yyyy-MM-dd") : null;

    // PDF-03: the model output is untrusted. Checks structure, codes, duplicates, ballots and the printed totals.
    // Can run again after an edit: it keeps only what the reader or a person noted, and redoes its own checks.
    public static VotingSheet Validate(VotingSheet sheet, MemberList members)
    {
        var issues = sheet.Issues.Where(IsReadNote).ToList();
        if (sheet.AssemblyVoteId == null || !Regex.IsMatch(sheet.AssemblyVoteId, @"^MA/\d{4}/\d{3}$"))
            issues.Add($"Vote number '{sheet.AssemblyVoteId}' is not in the form MA/yyyy/nnn");
        if (sheet.Session == null) issues.Add("Session is missing or not a number");
        if (IsoDate(sheet.VoteDate) == null) issues.Add($"Date of vote '{sheet.VoteDate}' could not be read");
        if (string.IsNullOrWhiteSpace(sheet.Title)) issues.Add("Resolution title is missing");
        if (sheet.Rows.Count == 0) issues.Add("No country rows were found");

        var seen = new HashSet<string>();
        var rows = new List<SheetRow>();
        foreach (var row in sheet.Rows)
        {
            var rowIssues = row.Issues.Where(IsReadNote).ToList();
            if (row.Confidence < LowConfidence) rowIssues.Add($"Low confidence {row.Confidence:0.00} on the ticked box");
            if (!members.IsKnown(row.Code)) rowIssues.Add($"Unknown country code '{row.Code}'");
            if (!seen.Add(row.Code)) rowIssues.Add($"Country '{row.Code}' appears more than once");
            if (row.Ballot != null && !new[] { "Y", "N", "A", "X" }.Contains(row.Ballot)) rowIssues.Add($"Invalid ballot '{row.Ballot}'");
            rows.Add(row with { Issues = rowIssues });
        }

        if (sheet.PrintedTotals == null)
            issues.Add("Printed totals were not found on the sheet");
        else
        {
            var t = sheet.PrintedTotals;
            var counted = Counted(sheet with { Rows = rows });
            if (counted != t)
                issues.Add($"Extracted counts Yes {counted.Yes}, No {counted.No}, Abstentions {counted.Abstain}, Not voting {counted.NotVoting} "
                         + $"differ from the printed totals Yes {t.Yes}, No {t.No}, Abstentions {t.Abstain}, Not voting {t.NotVoting}");
        }

        return sheet with { Rows = rows, Issues = issues };
    }

    private static SheetTotals Counted(VotingSheet sheet) => new(sheet.Rows.Count(r => r.Ballot == "Y"),
        sheet.Rows.Count(r => r.Ballot == "N"), sheet.Rows.Count(r => r.Ballot == "A"), sheet.Rows.Count(r => r.Ballot == "X"));

    // Notes from reading the scan or from a person's edit; everything else is recomputed by Validate
    private static bool IsReadNote(string issue) =>
        issue.Contains("boxes ticked") || issue.StartsWith("Edited by") || issue.Contains("removed") || issue.StartsWith("Unexpected table");

    // Approval is blocked while a row has no usable ballot or the header can't be ingested.
    public static List<string> BlockingProblems(VotingSheet sheet)
    {
        var problems = new List<string>();
        if (sheet.AssemblyVoteId == null || !Regex.IsMatch(sheet.AssemblyVoteId, @"^MA/\d{4}/\d{3}$")) problems.Add("vote number");
        if (sheet.Session == null) problems.Add("session");
        if (IsoDate(sheet.VoteDate) == null) problems.Add("date");
        if (string.IsNullOrWhiteSpace(sheet.Title)) problems.Add("title");
        problems.AddRange(sheet.Rows.Where(r => r.Ballot == null).Select(r => $"ballot of {r.Code}"));
        // rows may be missing or misread: the analyst fixes the ballots so the counts match, or rejects the sheet
        if (sheet.PrintedTotals != null && Counted(sheet) != sheet.PrintedTotals) problems.Add("counts differ from the printed totals");
        return problems;
    }
}
