using Meridian.Domain;

namespace Meridian.Api.Snapshots;

// SNP-01: a date range and/or session, plus an explicit list; the selection is their union
public record CreateSnapshotRequest(
    string? Name, DateOnly? From, DateOnly? To, int? Session, List<string>? VoteIds,
    bool ExcludeVotesWithoutPosition = false);

public record PublishRequest(string? Quarter);

// What was asked for (stored as jsonb)
public record SnapshotFilter(DateOnly? From, DateOnly? To, int? Session, List<string> VoteIds, bool ExcludeVotesWithoutPosition);

// How this snapshot was computed (stored as jsonb, never changes): readable a year later
public record SnapshotRules(
    string Formula,
    string Counted,
    string Matched,
    string NoCountedVotes,
    string Ranking,
    List<long> VoteIds,                          // the resolved selection
    List<string> ExcludedVotesWithoutPosition,   // POS-04: listed, never silent
    List<string> PendingConflictsAtCreation)     // counted with the approved value; listed for transparency
{
    public static SnapshotRules For(List<long> voteIds, List<string> excluded, List<string> pending) => new(
        "score = 100 x matched weight / counted weight, rounded to 2 decimals",
        "Y and N ballots count; A (abstain) and X (absent) do not; a country with no row for a vote is not counted",
        "Y on a For vote or N on an Against vote",
        "no Y/N ballots at all: score N/A, unranked",
        "by score, ties share a rank and the next rank skips (1, 2, 2, 4)",
        voteIds, excluded, pending);
}

public record SnapshotDto(
    long Id, string Name, SnapshotStatus Status, string CreatedBy, DateTime CreatedAt, DateTime? CompletedAt,
    string? Error, string? Quarter, bool IsPublished, string? PublishedBy, DateTime? PublishedAt, int VoteCount);

public record SnapshotScoreDto(
    string CountryCode, string CountryName, decimal? Score, int? Rank,
    int MatchedWeight, int CountedWeight, int CountedVotes, int AbstainCount, int AbsentCount);

public record SnapshotEventDto(DateTime At, string EventType, string Actor, string? OldValue, string? NewValue);

public record SnapshotDetailsDto(
    SnapshotDto Snapshot, SnapshotFilter Filter, SnapshotRules Rules,
    List<SnapshotScoreDto> Scores, List<SnapshotEventDto> History);

// SNP-03: one country's breakdown, vote by vote
public record VoteContributionDto(
    long VoteId, string AssemblyVoteId, string Title, DateOnly VoteDate,
    string Ballot, Stance Stance, int Weight, bool Counted, bool Matched, int MatchedWeight, int CountedWeight);

public record CountryBreakdownDto(SnapshotScoreDto Score, List<VoteContributionDto> Votes);

// 7.4 "exactly which data the snapshot was based on"
public record SnapshotInputDto(string AssemblyVoteId, string CountryCode, string Ballot, Stance Stance, int Weight);
