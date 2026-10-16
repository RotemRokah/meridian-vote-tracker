using Meridian.Api.Data;
using Meridian.Domain;
using Microsoft.EntityFrameworkCore;

namespace Meridian.Api.Snapshots;

// Background runner for snapshots (SNP-02), same queue pattern as ImportWorker:
// the snapshots table is the queue, status lives in the DB, one at a time.
public class SnapshotWorker : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<SnapshotWorker> _logger;

    public SnapshotWorker(IServiceScopeFactory scopes, ILogger<SnapshotWorker> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RequeueInterrupted(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var next = await ClaimNext(stoppingToken);
                if (next == null)
                    await Task.Delay(IdleDelay, stoppingToken);
                else
                    await Run(next.Value, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Snapshot worker loop error");
                await Task.Delay(IdleDelay, stoppingToken);
            }
        }
    }

    private async Task<long?> ClaimNext(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MeridianDbContext>();
        var claimed = await db.Database.SqlQuery<long>($@"
            UPDATE snapshots SET status = 'Running'
            WHERE id = (SELECT id FROM snapshots WHERE status = 'Queued'
                        ORDER BY created_at, id LIMIT 1 FOR UPDATE SKIP LOCKED)
            RETURNING id AS ""Value""").ToListAsync(ct);
        return claimed.Count == 0 ? null : claimed[0];
    }

    private async Task Run(long snapshotId, CancellationToken ct)
    {
        using var logScope = _logger.BeginScope("SnapshotId:{SnapshotId}", snapshotId);
        try
        {
            using var scope = _scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<SnapshotCalculator>().ComputeAsync(snapshotId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // shutdown: transaction rolled back, re-queued on next start
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Snapshot {SnapshotId} failed", snapshotId);
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MeridianDbContext>();
            await db.Snapshots.Where(s => s.Id == snapshotId).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, SnapshotStatus.Failed)
                .SetProperty(x => x.CompletedAt, DateTime.UtcNow)
                .SetProperty(x => x.Error, ex.Message), ct);
        }
    }

    // Unlike an import, a snapshot is one transaction: if it was cut off, nothing of it was saved,
    // so running it again is safe. Back to the queue.
    private async Task RequeueInterrupted(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MeridianDbContext>();
        var count = await db.Snapshots.Where(s => s.Status == SnapshotStatus.Running)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, SnapshotStatus.Queued), ct);
        if (count > 0)
            _logger.LogWarning("Re-queued {Count} interrupted snapshot(s)", count);
    }
}
