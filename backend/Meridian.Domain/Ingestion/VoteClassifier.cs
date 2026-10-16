using System.Globalization;

namespace Meridian.Domain.Ingestion;

// --- input: what is already approved for this vote ---

public record ExistingConflict(
    long Id, string? CountryCode, ConflictField Field, string ProposedValue,
    ConflictStatus Status, string? DecidedBy, DateTime? DecidedAt);

public record ApprovedVote(
    int Session, DateOnly VoteDate, string Title,
    IReadOnlyDictionary<string, Ballot> Ballots,
    IReadOnlyList<ExistingConflict> Conflicts);

// --- output: what to do ---

// ExistingConflictId set = link to that conflict, don't open a new one (ING-12)
public record ConflictProposal(
    string? CountryCode, ConflictField Field, string ApprovedValue, string ProposedValue, long? ExistingConflictId)
{
    public bool IsNew => ExistingConflictId == null;
}

public record RowOutcome(int LineNo, RowClassification Classification, string? Reason, ConflictProposal? Conflict = null);

public record NewVoteDetails(int Session, DateOnly VoteDate, string Title);

public record VoteResult(
    string AssemblyVoteId,
    VoteDetailsOutcome DetailsOutcome,
    string? DetailsReason,
    NewVoteDetails? VoteToCreate,          // set only when the vote is new and something is ingested
    IReadOnlyList<RowOutcome> Rows,        // one per input row, by line number
    IReadOnlyList<VoteRow> BallotsToAdd,   // New rows: become approved (never overwrites, ING-08)
    IReadOnlyList<ConflictProposal> Conflicts);

// Classifies the valid rows of ONE vote from ONE file against what is approved (ING-05, 06, 10, 11, 12).
// Pure function: no DB. Never proposes to change approved data, only to add or to open a conflict.
public static class VoteClassifier
{
    public static VoteResult Classify(string assemblyVoteId, IEnumerable<VoteRow> validRows, ApprovedVote? approved)
    {
        var rows = validRows.OrderBy(r => r.LineNo).ToList();
        if (rows.Count == 0)
            throw new ArgumentException("Classify needs at least one valid row", nameof(validRows));
        if (rows.Any(r => r.AssemblyVoteId != assemblyVoteId))
            throw new ArgumentException("All rows must belong to the same vote", nameof(validRows));

        var outcomes = new List<RowOutcome>();

        // 1. Exact copies of an earlier row: keep the first, count the rest (Duplicate)
        var firstLineByContent = new Dictionary<object, int>();
        var remaining = new List<VoteRow>();
        foreach (var row in rows)
        {
            if (firstLineByContent.TryGetValue(row.Content, out var firstLine))
                outcomes.Add(new RowOutcome(row.LineNo, RowClassification.Duplicate, $"Identical to line {firstLine}"));
            else
            {
                firstLineByContent[row.Content] = row.LineNo;
                remaining.Add(row);
            }
        }

        // 2. Vote details must be the same on every row (ING-05). If not, we can't know which is right:
        //    every row is a Contradiction and nothing of this vote is ingested from this file.
        var detailsProblem = DescribeDetailsDisagreement(remaining);
        if (detailsProblem != null)
        {
            outcomes.AddRange(remaining.Select(r => new RowOutcome(r.LineNo, RowClassification.Contradiction, detailsProblem)));
            return Result(assemblyVoteId, VoteDetailsOutcome.Contradiction, detailsProblem, null, outcomes, new(), new());
        }

        // 3. One value per country (ING-05)
        var candidates = new List<VoteRow>();
        foreach (var group in remaining.GroupBy(r => r.CountryCode))
        {
            var countryRows = group.ToList();
            if (countryRows.Select(r => r.Ballot).Distinct().Count() > 1)
            {
                var reason = $"{group.Key} has different ballots in this file: "
                             + string.Join(", ", countryRows.Select(r => $"{r.Ballot} (line {r.LineNo})"));
                outcomes.AddRange(countryRows.Select(r => new RowOutcome(r.LineNo, RowClassification.Contradiction, reason)));
                continue;
            }

            // same ballot, rows differ only in name/published_at (e.g. Turkey vs Türkiye, ING-11): same information
            var first = countryRows[0];
            candidates.Add(first);
            foreach (var other in countryRows.Skip(1))
                outcomes.Add(new RowOutcome(other.LineNo, RowClassification.Duplicate,
                    $"Same vote, country and ballot as line {first.LineNo}; differs only in {DescribeInfoDifference(first, other)}"));
        }

        // 4a. Vote doesn't exist yet: everything left is New
        if (approved == null)
        {
            if (candidates.Count == 0)
                return Result(assemblyVoteId, VoteDetailsOutcome.NotCreated,
                    "No row of this vote could be ingested from this file", null, outcomes, new(), new());

            outcomes.AddRange(candidates.Select(r => new RowOutcome(r.LineNo, RowClassification.New, "New vote")));
            var d = candidates[0];
            return Result(assemblyVoteId, VoteDetailsOutcome.Created, null,
                new NewVoteDetails(d.Session, d.VoteDate, d.Title), outcomes, candidates, new());
        }

        // 4b. Vote exists: compare details, then each country's ballot
        var conflicts = new List<ConflictProposal>();
        var fileDetails = remaining[0];
        var detailReasons = new List<string>();

        void CompareDetail(ConflictField field, string approvedValue, string fileValue)
        {
            if (approvedValue == fileValue) return;
            conflicts.Add(Propose(null, field, approvedValue, fileValue, approved.Conflicts, out var reason));
            detailReasons.Add($"{field}: {reason}");
        }
        CompareDetail(ConflictField.Session, approved.Session.ToString(CultureInfo.InvariantCulture), fileDetails.Session.ToString(CultureInfo.InvariantCulture));
        CompareDetail(ConflictField.Date, FormatDate(approved.VoteDate), FormatDate(fileDetails.VoteDate));
        CompareDetail(ConflictField.Title, approved.Title, fileDetails.Title);

        var ballotsToAdd = new List<VoteRow>();
        foreach (var row in candidates)
        {
            if (!approved.Ballots.TryGetValue(row.CountryCode, out var current))
            {
                // vote arriving in parts: a missing country is New, not a conflict (ING-10)
                outcomes.Add(new RowOutcome(row.LineNo, RowClassification.New, "Country not yet recorded for this vote"));
                ballotsToAdd.Add(row);
            }
            else if (current == row.Ballot)
                outcomes.Add(new RowOutcome(row.LineNo, RowClassification.Unchanged, null));
            else
            {
                // never overwrite approved data (ING-08): a human decides
                var proposal = Propose(row.CountryCode, ConflictField.Ballot, current.ToString(), row.Ballot.ToString(), approved.Conflicts, out var reason);
                conflicts.Add(proposal);
                outcomes.Add(new RowOutcome(row.LineNo, RowClassification.Conflict, reason, proposal));
            }
        }

        var detailsOutcome = detailReasons.Count > 0 ? VoteDetailsOutcome.Conflict : VoteDetailsOutcome.Unchanged;
        var detailsReason = detailReasons.Count > 0 ? string.Join("; ", detailReasons) : null;
        return Result(assemblyVoteId, detailsOutcome, detailsReason, null, outcomes, ballotsToAdd, conflicts);
    }

    // Same proposal already pending -> link to it (ING-12). Already rejected -> link to that decision.
    // Otherwise a new conflict.
    private static ConflictProposal Propose(string? country, ConflictField field, string approvedValue, string proposedValue,
        IReadOnlyList<ExistingConflict> existing, out string reason)
    {
        var sameProposal = existing
            .Where(c => c.CountryCode == country && c.Field == field && c.ProposedValue == proposedValue)
            .ToList();

        var pending = sameProposal.FirstOrDefault(c => c.Status == ConflictStatus.Pending);
        if (pending != null)
        {
            reason = $"Approved {approvedValue}, file has {proposedValue}; already pending as conflict #{pending.Id}";
            return new ConflictProposal(country, field, approvedValue, proposedValue, pending.Id);
        }

        var rejected = sameProposal
            .Where(c => c.Status == ConflictStatus.Rejected)
            .OrderByDescending(c => c.DecidedAt)
            .FirstOrDefault();
        if (rejected != null)
        {
            reason = $"Approved {approvedValue}, file has {proposedValue}; this proposal was already rejected"
                     + $" on {rejected.DecidedAt:yyyy-MM-dd} by {rejected.DecidedBy} (conflict #{rejected.Id})";
            return new ConflictProposal(country, field, approvedValue, proposedValue, rejected.Id);
        }

        reason = $"Approved {approvedValue}, file has {proposedValue}";
        return new ConflictProposal(country, field, approvedValue, proposedValue, null);
    }

    // null when all rows agree on session, date and title
    private static string? DescribeDetailsDisagreement(IReadOnlyList<VoteRow> rows)
    {
        var problems = new List<string>();

        void Check(string field, Func<VoteRow, string> value)
        {
            var variants = rows.GroupBy(value).ToList();
            if (variants.Count < 2) return;
            problems.Add($"{field} has {variants.Count} different values: "
                         + string.Join(", ", variants.Select(v => $"'{v.Key}' ({Lines(v)})")));
        }
        Check("session", r => r.Session.ToString(CultureInfo.InvariantCulture));
        Check("vote_date", r => FormatDate(r.VoteDate));
        Check("resolution_title", r => r.Title);

        return problems.Count == 0 ? null : "Vote details differ between rows of this vote: " + string.Join("; ", problems);
    }

    private static string Lines(IEnumerable<VoteRow> rows)
    {
        var lines = rows.Select(r => r.LineNo).ToList();
        var shown = string.Join(", ", lines.Take(5));
        return lines.Count > 5 ? $"lines {shown}, ... {lines.Count} rows" : $"line{(lines.Count > 1 ? "s" : "")} {shown}";
    }

    private static string DescribeInfoDifference(VoteRow a, VoteRow b)
    {
        var fields = new List<string>();
        if (a.CountryName != b.CountryName) fields.Add($"country_name ('{a.CountryName}' vs '{b.CountryName}')");
        if (a.PublishedAt != b.PublishedAt) fields.Add($"published_at ('{a.PublishedAt}' vs '{b.PublishedAt}')");
        return string.Join(" and ", fields);
    }

    private static string FormatDate(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static VoteResult Result(string voteId, VoteDetailsOutcome outcome, string? reason, NewVoteDetails? create,
        List<RowOutcome> rows, List<VoteRow> ballots, List<ConflictProposal> conflicts) =>
        new(voteId, outcome, reason, create, rows.OrderBy(r => r.LineNo).ToList(), ballots, conflicts);
}
