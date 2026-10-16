using System.Text;

namespace Meridian.Domain.Ingestion;

public record HeaderCheck(bool IsValid, bool HadBom, string? Error);

// The assembly export format: 8 comma-separated columns, RFC 4180 quoting.
public static class CsvFormat
{
    public static readonly string[] Columns =
    {
        "assembly_vote_id", "session", "vote_date", "resolution_title",
        "country_code", "country_name", "ballot", "published_at",
    };

    private const char Bom = '﻿';

    // A wrong header fails the whole import: we can't trust any row of the file.
    public static HeaderCheck CheckHeader(string firstLine)
    {
        var hadBom = firstLine.StartsWith(Bom);
        var line = hadBom ? firstLine[1..] : firstLine;

        var expected = string.Join(",", Columns);
        if (line.Trim() != expected)
            return new HeaderCheck(false, hadBom, $"Header must be '{expected}' but was '{line}'");

        return new HeaderCheck(true, hadBom, null);
    }

    // Splits one line into fields. Quoted fields may contain commas and "" (an escaped quote).
    // Returns false for broken quoting (e.g. an unterminated quote).
    public static bool TrySplit(string line, out List<string> fields, out string? error)
    {
        fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var justClosedQuote = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (inQuotes)
            {
                if (c != '"')
                    current.Append(c);
                else if (i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');   // "" inside quotes = one quote character
                    i++;
                }
                else
                {
                    inQuotes = false;
                    justClosedQuote = true;
                }
                continue;
            }

            if (c == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
                justClosedQuote = false;
            }
            else if (justClosedQuote)
            {
                error = $"Unexpected character after closing quote at position {i + 1}";
                return false;
            }
            else if (c == '"' && current.Length == 0)
                inQuotes = true;
            else if (c == '"')
            {
                error = $"Unexpected quote inside an unquoted field at position {i + 1}";
                return false;
            }
            else
                current.Append(c);
        }

        if (inQuotes)
        {
            error = "Unterminated quoted field";
            return false;
        }

        fields.Add(current.ToString());
        error = null;
        return true;
    }
}
