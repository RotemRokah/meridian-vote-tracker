using Meridian.Api.Data;
using Meridian.Domain;
using Microsoft.EntityFrameworkCore;

namespace Meridian.Api.Imports;

// Background job runner for imports (ING-01). The imports table is the queue:
// status and progress live in the DB, so they survive a page refresh (ING-02).
// One import at a time, so two imports never touch the same vote concurrently.
public class ImportWorker : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ImportWorker> _logger;

    public ImportWorker(IServiceScopeFactory scopes, ILogger<ImportWorker> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await FailInterruptedImports(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var next = await ClaimNext(stoppingToken);
                if (next == null)
                    await Task.Delay(IdleDelay, stoppingToken);
                else
                    await Run(next, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;   // app is shutting down
            }
            catch (Exception ex)
            {
                // e.g. DB briefly unreachable: log, wait, keep the loop alive
                _logger.LogError(ex, "Import worker loop error");
                await Task.Delay(IdleDelay, stoppingToken);
            }
        }
    }

    // Takes the oldest queued import and marks it Running in one statement.
    // SKIP LOCKED: safe even if a second worker ever ran against the same DB.
    private async Task<ClaimedImport?> ClaimNext(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MeridianDbContext>();

        var claimed = await db.Database.SqlQuery<ClaimedImport>($@"
            UPDATE imports SET status = 'Running', started_at = now()
            WHERE id = (SELECT id FROM imports WHERE status = 'Queued'
                        ORDER BY uploaded_at, id LIMIT 1 FOR UPDATE SKIP LOCKED)
            RETURNING id, correlation_id").ToListAsync(ct);

        return claimed.FirstOrDefault();
    }

    private record ClaimedImport(long Id, string? CorrelationId);

    private async Task Run(ClaimedImport import, CancellationToken ct)
    {
        // same correlation id as the upload request, so its logs can be followed end to end
        using var logScope = _logger.BeginScope("CorrelationId:{CorrelationId} ImportId:{ImportId}",
            import.CorrelationId ?? "", import.Id);

        try
        {
            using var scope = _scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ImportProcessor>().ProcessAsync(import.Id, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // shutdown: left Running, marked Failed on next start
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Import {ImportId} failed", import.Id);
            await MarkFailed(import.Id, ex.Message, ct);
        }
    }

    private async Task MarkFailed(long importId, string error, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();   // fresh context: the failed one may be in a bad state
        var db = scope.ServiceProvider.GetRequiredService<MeridianDbContext>();
        await db.Imports.Where(i => i.Id == importId).ExecuteUpdateAsync(s => s
            .SetProperty(i => i.Status, ImportStatus.Failed)
            .SetProperty(i => i.CompletedAt, DateTime.UtcNow)
            .SetProperty(i => i.Error, error), ct);
    }

    // An import still Running at startup was cut off by a restart. Fail it with a clear message;
    // re-uploading is safe because classification is idempotent (already ingested rows come back Unchanged).
    private async Task FailInterruptedImports(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MeridianDbContext>();
        var count = await db.Imports.Where(i => i.Status == ImportStatus.Running).ExecuteUpdateAsync(s => s
            .SetProperty(i => i.Status, ImportStatus.Failed)
            .SetProperty(i => i.CompletedAt, DateTime.UtcNow)
            .SetProperty(i => i.Error, "Interrupted by an API restart. Upload the file again; rows already applied will show as Unchanged."), ct);
        if (count > 0)
            _logger.LogWarning("Marked {Count} interrupted import(s) as Failed", count);
    }
}
