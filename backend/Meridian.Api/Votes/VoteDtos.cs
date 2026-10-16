using Meridian.Api.Conflicts;
using Meridian.Domain;

namespace Meridian.Api.Votes;

public record VoteListItemDto(
    long Id, string AssemblyVoteId, int Session, DateOnly VoteDate, string Title,
    int BallotCount, int PendingConflicts, Stance? Stance, int? Weight);

public record PositionDto(Stance Stance, int Weight, string UpdatedBy, DateTime UpdatedAt);

// One country's approved ballot and where it came from (HIS-02)
public record CountryBallotDto(
    string CountryCode,
    string CountryName,        // from the member list: the code is the identity (ING-11)
    string Ballot,
    ValueSourceDto Source,     // the file the current value came from
    bool ChangedByDecision,    // true = a human approved a conflict on it
    string? ApprovedBy,
    DateTime? ApprovedAt);

public record VoteDetailsDto(
    long Id, string AssemblyVoteId, int Session, DateOnly VoteDate, string Title,
    ValueSourceDto DetailsSource,
    PositionDto? Position,
    Dictionary<string, int> BallotCounts,   // Y / N / A / X
    List<CountryBallotDto> Ballots,
    List<ConflictDto> OpenConflicts);

// One line of the vote timeline (HIS-04). Two kinds:
//   audit events (VoteCreated, BallotsAdded, ConflictOpened, ConflictApproved, ...)
//   file entries: FileProcessed (counts per class) or, with ?country=, FileRow (that country's row in a file)
public record TimelineEntryDto(
    DateTime At,
    string EventType,
    string? CountryCode,
    string? OldValue,
    string? NewValue,
    string? Actor,
    ValueSourceDto? Source,
    long? ConflictId,
    string? Comment,
    string? Detail);
