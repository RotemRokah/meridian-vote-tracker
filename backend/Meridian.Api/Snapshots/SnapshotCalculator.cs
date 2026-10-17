using System.Text.Json;
using Meridian.Api.Data;
using Meridian.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Meridian.Api.Snapshots;

// Computes one snapshot (SNP-02, SNP-03). Two set-based statements in one transaction:
//   1. freeze: copy the approved ballots + stance + weight of the selected votes into snapshot_inputs
//   2. score:  aggregate per country FROM THAT COPY, rank with RANK()
// Nothing is loaded into memory, so ~1M country-votes stay fast. The DB triggers then keep
// both tables unchangeable (SNP-04).
public class SnapshotCalculator
{
    private const string FreezeSql = @"
        INSERT INTO snapshot_inputs (snapshot_id, vote_id, country_code, ballot, stance, weight)
        SELECT @id, cv.vote_id, cv.country_code, cv.ballot, p.stance, p.weight
        FROM country_votes cv
        JOIN vote_positions p ON p.vote_id = cv.vote_id
        WHERE cv.vote_id = ANY(@voteIds)";

    // matched: Y on a For vote, N on an Against vote. counted: Y or N. A and X: counted nothing.
    // score NULL when nothing counted (N/A); NULLs rank last and get no rank.
    private const string ScoreSql = @"
        INSERT INTO snapshot_scores (snapshot_id, country_code, score, rank,
                                     matched_weight, counted_weight, counted_votes, abstain_count, absent_count)
        SELECT snapshot_id, country_code, score,
               CASE WHEN score IS NULL THEN NULL ELSE RANK() OVER (ORDER BY score DESC NULLS LAST) END,
               matched_weight, counted_weight, counted_votes, abstain_count, absent_count
        FROM (
            SELECT snapshot_id, country_code, matched_weight, counted_weight, counted_votes, abstain_count, absent_count,
                   ROUND(100.0 * matched_weight / NULLIF(counted_weight, 0), 2) AS score
            FROM (
                SELECT snapshot_id, country_code,
                       COALESCE(SUM(weight) FILTER (WHERE (ballot = 'Y' AND stance = 'For')
                                                     OR (ballot = 'N' AND stance = 'Against')), 0) AS matched_weight,
                       COALESCE(SUM(weight) FILTER (WHERE ballot IN ('Y', 'N')), 0) AS counted_weight,
                       COUNT(*) FILTER (WHERE ballot IN ('Y', 'N')) AS counted_votes,
                       COUNT(*) FILTER (WHERE ballot = 'A') AS abstain_count,
                       COUNT(*) FILTER (WHERE ballot = 'X') AS absent_count
                FROM snapshot_inputs
                WHERE snapshot_id = @id
                GROUP BY snapshot_id, country_code
            ) totals
        ) scored";

    private readonly MeridianDbContext _db;
    private readonly ILogger<SnapshotCalculator> _logger;

    public SnapshotCalculator(MeridianDbContext db, ILogger<SnapshotCalculator> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task ComputeAsync(long snapshotId, CancellationToken ct)
    {
        var snapshot = await _db.Snapshots.AsNoTracking().SingleAsync(s => s.Id == snapshotId, ct);
        var rules = JsonSerializer.Deserialize<SnapshotRules>(snapshot.Rules, SnapshotEndpoints.Json)!;
        var timer = System.Diagnostics.Stopwatch.StartNew();

        // Npgsql's default command timeout is 30 s; copying ~1M country-votes can take longer.
        // Background job, nobody is waiting on a request: allow minutes.
        _db.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var inputs = await _db.Database.ExecuteSqlRawAsync(FreezeSql, new[]
        {
            new NpgsqlParameter("id", snapshotId),
            new NpgsqlParameter("voteIds", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = rules.VoteIds.ToArray() },
        }, ct);
        var countries = await _db.Database.ExecuteSqlRawAsync(ScoreSql, new[] { new NpgsqlParameter("id", snapshotId) }, ct);

        await _db.Snapshots.Where(s => s.Id == snapshotId).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Status, SnapshotStatus.Completed)
            .SetProperty(x => x.CompletedAt, DateTime.UtcNow), ct);

        await tx.CommitAsync(ct);
        _logger.LogInformation("Snapshot {SnapshotId} computed in {Seconds:F1}s: {Inputs} country-votes, {Countries} countries",
            snapshotId, timer.Elapsed.TotalSeconds, inputs, countries);
    }
}
