using Meridian.Domain;

namespace Meridian.Api.Data;

// One class per table. Table/column names become snake_case (see MeridianDbContext).
// All timestamps are UTC (timestamptz).

public class User
{
    public string Id { get; set; } = "";          // "dana", "omer", "lena" (sent in X-User-Id)
    public string Name { get; set; } = "";
    public UserRole Role { get; set; }
}

// A country can have several membership periods (ISL left and rejoined)
public class MemberState
{
    public string CountryCode { get; set; } = "";
    public DateOnly MemberFrom { get; set; }
    public DateOnly? MemberTo { get; set; }        // null = still a member
    public string CountryName { get; set; } = "";
}

// An uploaded file. Also the job queue row for the import worker.
public class Import
{
    public long Id { get; set; }
    public string FileName { get; set; } = "";
    public string FileSha256 { get; set; } = "";
    public string StoragePath { get; set; } = "";
    public string UploadedBy { get; set; } = "";
    public DateTime UploadedAt { get; set; }
    public ImportStatus Status { get; set; }
    public int? TotalRows { get; set; }
    public int ProcessedRows { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? Summary { get; set; }           // jsonb: counts per class, per-vote outcome
    public string? CorrelationId { get; set; }
    public string? Error { get; set; }
}

// One line of an imported file and what happened to it (HIS-01, ING-14).
// Parsed fields are kept as the original text: invalid rows may not parse.
public class ImportRow
{
    public long Id { get; set; }
    public long ImportId { get; set; }
    public int LineNo { get; set; }
    public string RawLine { get; set; } = "";
    public string? AssemblyVoteId { get; set; }
    public string? Session { get; set; }
    public string? VoteDate { get; set; }
    public string? ResolutionTitle { get; set; }
    public string? CountryCode { get; set; }
    public string? CountryName { get; set; }
    public string? Ballot { get; set; }
    public string? PublishedAt { get; set; }
    public RowClassification? Classification { get; set; }   // null until processed
    public string? Reason { get; set; }
    public string? NormalizationNote { get; set; }
    public long? ConflictId { get; set; }
}

// Current approved vote details
public class Vote
{
    public long Id { get; set; }
    public string AssemblyVoteId { get; set; } = "";
    public int Session { get; set; }
    public DateOnly VoteDate { get; set; }
    public string Title { get; set; } = "";
    public long SourceImportId { get; set; }
}

// Current approved ballot only; history lives in audit_events
public class CountryVote
{
    public long VoteId { get; set; }
    public string CountryCode { get; set; } = "";
    public string Ballot { get; set; } = "";      // Y, N, A, X
    public long SourceImportId { get; set; }
    public string? ApprovedBy { get; set; }        // null = ingested automatically
    public DateTime? ApprovedAt { get; set; }
}

// Approved vs proposed value, waiting for (or decided by) a human.
// CountryCode null = conflict on vote details (Title/Date/Session).
public class Conflict
{
    public long Id { get; set; }
    public long VoteId { get; set; }
    public string? CountryCode { get; set; }
    public ConflictField Field { get; set; }
    public string ApprovedValue { get; set; } = "";
    public long ApprovedSourceImportId { get; set; }
    public string ProposedValue { get; set; } = "";
    public long ProposedImportId { get; set; }
    public ConflictStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? DecidedBy { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? Comment { get; set; }
}

// Institute's stance and weight per vote (POS-01)
public class VotePosition
{
    public long VoteId { get; set; }
    public Stance Stance { get; set; }
    public int Weight { get; set; }               // 1..5
    public string UpdatedBy { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
}

// Append-only log of every change (HIS-02..04). A DB trigger blocks UPDATE/DELETE.
public class AuditEvent
{
    public long Id { get; set; }
    public long? VoteId { get; set; }
    public string? CountryCode { get; set; }
    public string EventType { get; set; } = "";
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public long? ImportId { get; set; }
    public long? ConflictId { get; set; }
    public string Actor { get; set; } = "";
    public DateTime At { get; set; }
    public string? CorrelationId { get; set; }
}

public class Snapshot
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Filter { get; set; } = "{}";     // jsonb: date range / session / explicit vote list
    public string Rules { get; set; } = "{}";      // jsonb: scoring rules used, excluded votes
    public SnapshotStatus Status { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? Error { get; set; }
    public string? Quarter { get; set; }           // e.g. "2024-Q1"
    public bool IsPublished { get; set; }
    public string? PublishedBy { get; set; }
    public DateTime? PublishedAt { get; set; }
}

// Frozen copy of exactly the data a snapshot used (SNP-04). Insert only.
public class SnapshotInput
{
    public long SnapshotId { get; set; }
    public long VoteId { get; set; }
    public string CountryCode { get; set; } = "";
    public string Ballot { get; set; } = "";
    public Stance Stance { get; set; }
    public int Weight { get; set; }
}

// Result per country (SNP-03). Insert only.
public class SnapshotScore
{
    public long SnapshotId { get; set; }
    public string CountryCode { get; set; } = "";
    public decimal? Score { get; set; }            // null = no Y/N votes counted (N/A)
    public int? Rank { get; set; }
    public int MatchedWeight { get; set; }
    public int CountedWeight { get; set; }
    public int CountedVotes { get; set; }
    public int AbstainCount { get; set; }
    public int AbsentCount { get; set; }
}
