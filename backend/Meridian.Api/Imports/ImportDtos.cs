using Meridian.Domain;

namespace Meridian.Api.Imports;

public record ImportDto(
    long Id, string FileName, string UploadedBy, DateTime UploadedAt, ImportStatus Status, ImportStage? Stage,
    int? TotalRows, int ProcessedRows, DateTime? StartedAt, DateTime? CompletedAt, string? Error);

// The full report (ING-14): stored as jsonb on the import, so it can be opened days later (ING-15)
public record ImportSummary(
    Dictionary<RowClassification, int> Counts,
    int VotesCreated,
    int BallotsAdded,
    int NewConflicts,
    int LinkedConflicts,      // proposals that matched an existing pending/rejected conflict (ING-12)
    int NormalizedRows,
    List<string> FileNotes,   // file-level facts: BOM removed, identical to an earlier import
    List<VoteOutcomeDto> Votes);

public record VoteOutcomeDto(
    string AssemblyVoteId, VoteDetailsOutcome Outcome, string? Reason, Dictionary<RowClassification, int> Rows);

public record ImportDetailsDto(ImportDto Import, ImportSummary? Summary);

public record ImportRowDto(
    int LineNo, string RawLine, RowClassification? Classification, string? Reason, string? NormalizationNote,
    long? ConflictId, string? AssemblyVoteId, string? CountryCode);

public record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);
