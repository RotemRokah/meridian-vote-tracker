using System.Text.Json;
using Meridian.Api.Data;
using Meridian.Api.Storage;
using Meridian.Domain;
using Meridian.Domain.Ingestion;
using Meridian.Domain.Pdf;
using Microsoft.EntityFrameworkCore;

namespace Meridian.Api.Pdf;

// Same queue pattern as the import and snapshot workers: the pdf_extractions table is the queue (PDF-01).
public class PdfExtractionWorker : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(1);
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<PdfExtractionWorker> _logger;

    public PdfExtractionWorker(IServiceScopeFactory scopes, ILogger<PdfExtractionWorker> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await FailInterrupted(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var next = await ClaimNext(stoppingToken);
                if (next == null) await Task.Delay(IdleDelay, stoppingToken);
                else await Run(next, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PDF worker loop error");
                await Task.Delay(IdleDelay, stoppingToken);
            }
        }
    }

    private record Claimed(long Id, string? CorrelationId);

    private async Task<Claimed?> ClaimNext(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MeridianDbContext>();
        var claimed = await db.Database.SqlQuery<Claimed>($@"
            UPDATE pdf_extractions SET status = 'Running', attempts = attempts + 1, error = NULL
            WHERE id = (SELECT id FROM pdf_extractions WHERE status = 'Queued'
                        ORDER BY uploaded_at, id LIMIT 1 FOR UPDATE SKIP LOCKED)
            RETURNING id, correlation_id").ToListAsync(ct);
        return claimed.FirstOrDefault();
    }

    private async Task Run(Claimed job, CancellationToken ct)
    {
        var correlationId = job.CorrelationId ?? Guid.NewGuid().ToString();
        using var logScope = _logger.BeginScope("CorrelationId:{CorrelationId} PdfExtractionId:{PdfExtractionId}", correlationId, job.Id);
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MeridianDbContext>();
        var extraction = await db.PdfExtractions.SingleAsync(x => x.Id == job.Id, ct);
        try
        {
            string layout;
            await using (var pdf = scope.ServiceProvider.GetRequiredService<IFileStore>().OpenRead(extraction.StoragePath))
                layout = await scope.ServiceProvider.GetRequiredService<IDocumentAnalyzer>().AnalyzeLayoutAsync(pdf, correlationId, ct);

            var members = new MemberList(await db.MemberStates.AsNoTracking()
                .Select(m => new MembershipPeriod(m.CountryCode, m.MemberFrom, m.MemberTo)).ToListAsync(ct));
            var sheet = VotingSheetReader.Validate(VotingSheetReader.Read(layout), members);

            extraction.Sheet = JsonSerializer.Serialize(sheet, Json);
            extraction.Status = PdfExtractionStatus.ReadyForReview;
            extraction.CompletedAt = DateTime.UtcNow;
            _logger.LogInformation("PDF extraction {Id} read: {Rows} rows, {Issues} sheet issues, correlation {CorrelationId}",
                job.Id, sheet.Rows.Count, sheet.Issues.Count, correlationId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // PDF-06: shown to the user, who can retry
            _logger.LogError(ex, "PDF extraction {Id} failed, correlation {CorrelationId}", job.Id, correlationId);
            extraction.Status = PdfExtractionStatus.Failed;
            extraction.Error = ex is DocumentAnalysisException ? ex.Message : $"Reading the PDF failed: {ex.Message}";
            extraction.CompletedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task FailInterrupted(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MeridianDbContext>();
        await db.PdfExtractions.Where(x => x.Status == PdfExtractionStatus.Running).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Status, PdfExtractionStatus.Failed)
            .SetProperty(x => x.Error, "Interrupted by an API restart. Use Retry."), ct);
    }
}
