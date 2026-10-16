using Meridian.Domain;

namespace Meridian.Api.Conflicts;

// Where a value came from (CON-02): which file, who uploaded it, when
public record ValueSourceDto(long ImportId, string FileName, string UploadedBy, DateTime UploadedAt);

public record ConflictDto(
    long Id,
    string AssemblyVoteId,
    string VoteTitle,
    string? CountryCode,          // null = conflict on vote details (CON-03)
    ConflictField Field,
    ConflictStatus Status,
    string ApprovedValue,         // approved when the conflict was opened
    ValueSourceDto ApprovedSource,
    string ProposedValue,
    ValueSourceDto ProposedSource,
    string? CurrentApprovedValue, // approved right now; differs if it changed since
    DateTime CreatedAt,
    string? DecidedBy,
    DateTime? DecidedAt,
    string? Comment);

// "Approve" or "Reject". Kept as text so a wrong value is a field error, not a parse error.
public record DecisionRequest(string? Decision, string? Comment);
