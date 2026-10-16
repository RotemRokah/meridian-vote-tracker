using System.Text.RegularExpressions;

namespace Meridian.Domain.Scoring;

// Quarters as "2024-Q1". One published snapshot per quarter (SNP-05).
public static class Quarter
{
    private static readonly Regex Format = new(@"^\d{4}-Q[1-4]$");

    public static string Of(DateOnly date) => $"{date.Year}-Q{(date.Month - 1) / 3 + 1}";

    public static bool IsValid(string? quarter) => quarter != null && Format.IsMatch(quarter);

    // the quarter all dates fall in, or null if they span more than one
    public static string? CommonQuarter(IEnumerable<DateOnly> dates)
    {
        var quarters = dates.Select(Of).Distinct().ToList();
        return quarters.Count == 1 ? quarters[0] : null;
    }
}
