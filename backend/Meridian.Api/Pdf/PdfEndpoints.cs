using System.Security.Claims;
using System.Text.Json;
using Meridian.Api.Auth;
using Meridian.Api.Data;
using Meridian.Api.Storage;
using Meridian.Domain;
using Meridian.Domain.Pdf;
using Microsoft.EntityFrameworkCore;

namespace Meridian.Api.Pdf;

public record PdfExtractionDto(long Id, string FileName, string UploadedBy, DateTime UploadedAt, PdfExtractionStatus Status,
    int Attempts, string? Error, DateTime? CompletedAt, string? DecidedBy, DateTime? DecidedAt, long? ImportId);

public record PdfExtractionDetailsDto(PdfExtractionDto Extraction, VotingSheet? Sheet, List<string> BlockingProblems);

public record PdfFeatureDto(bool Enabled);

public static class PdfEndpoints
{
    public const long MaxPdfBytes = 20 * 1024 * 1024;

    public static void MapPdfEndpoints(this RouteGroupBuilder api)
    {
        var pdfs = api.MapGroup("/pdf-extractions").WithTags("PdfExtractions").RequireAuthorization(Policies.Analyst);

        pdfs.MapGet("/feature", (IDocumentAnalyzer analyzer) => new PdfFeatureDto(analyzer.IsConfigured))
            .WithName("GetPdfFeature");

        pdfs.MapPost("", Upload).WithName("UploadPdf").DisableAntiforgery()
            .Produces<PdfExtractionDto>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        pdfs.MapGet("", List).WithName("GetPdfExtractions");

        pdfs.MapGet("/{id:long}", Get).WithName("GetPdfExtraction")
            .Produces<PdfExtractionDetailsDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        pdfs.MapPost("/{id:long}/retry", Retry).WithName("RetryPdfExtraction")
            .Produces<PdfExtractionDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    // PDF-01: store the file, queue it, 202 right away
    private static async Task<IResult> Upload(IFormFile? file, MeridianDbContext db, IFileStore files, IDocumentAnalyzer analyzer,
        ClaimsPrincipal user, HttpContext http, CancellationToken ct)
    {
        if (!analyzer.IsConfigured)
            return Results.Problem(statusCode: 503, title: "PDF extraction is not configured",
                detail: "Set AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT and AZURE_DOCUMENT_INTELLIGENCE_KEY to enable it.");
        if (file == null || file.Length == 0) return FileError("A non-empty PDF file is required.");
        if (file.Length > MaxPdfBytes) return FileError("The PDF is larger than 20 MB.");

        var magic = new byte[5];
        await using (var head = file.OpenReadStream())
            await head.ReadAtLeastAsync(magic, 5, throwOnEndOfStream: false, ct);
        if (System.Text.Encoding.ASCII.GetString(magic) != "%PDF-") return FileError("The file is not a PDF.");

        StoredFile stored;
        await using (var content = file.OpenReadStream())
            stored = await files.SaveAsync(content, ct);

        var extraction = new PdfExtraction
        {
            FileName = Path.GetFileName(file.FileName),
            FileSha256 = stored.Sha256,
            StoragePath = stored.StoragePath,
            UploadedBy = user.FindFirstValue(ClaimTypes.NameIdentifier)!,
            UploadedAt = DateTime.UtcNow,
            Status = PdfExtractionStatus.Queued,
            CorrelationId = http.TraceIdentifier,
        };
        db.PdfExtractions.Add(extraction);
        await db.SaveChangesAsync(ct);
        return Results.Accepted($"/api/pdf-extractions/{extraction.Id}", ToDto(extraction));
    }

    private static async Task<List<PdfExtractionDto>> List(MeridianDbContext db, CancellationToken ct) =>
        (await db.PdfExtractions.AsNoTracking().OrderByDescending(x => x.Id).ToListAsync(ct)).Select(ToDto).ToList();

    private static async Task<IResult> Get(long id, MeridianDbContext db, CancellationToken ct)
    {
        var x = await db.PdfExtractions.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id, ct);
        if (x == null) return NotFound(id);
        var sheet = SheetOf(x);
        return Results.Ok(new PdfExtractionDetailsDto(ToDto(x), sheet, sheet == null ? new() : VotingSheetReader.BlockingProblems(sheet)));
    }

    // PDF-06: a failed read can be tried again
    private static async Task<IResult> Retry(long id, MeridianDbContext db, CancellationToken ct)
    {
        var updated = await db.PdfExtractions.Where(x => x.Id == id && x.Status == PdfExtractionStatus.Failed)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, PdfExtractionStatus.Queued).SetProperty(x => x.Error, (string?)null), ct);
        var x = await db.PdfExtractions.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id, ct);
        if (x == null) return NotFound(id);
        if (updated == 0)
            return Results.Problem(statusCode: 409, title: "Only a failed extraction can be retried",
                detail: $"Extraction {id} is {x.Status}.");
        return Results.Accepted($"/api/pdf-extractions/{id}", ToDto(x));
    }

    internal static VotingSheet? SheetOf(PdfExtraction x) =>
        x.Sheet == null ? null : JsonSerializer.Deserialize<VotingSheet>(x.Sheet, PdfExtractionWorker.Json);

    internal static IResult NotFound(long id) =>
        Results.Problem(statusCode: 404, title: "PDF extraction not found", detail: $"No PDF extraction with id {id}.");

    private static IResult FileError(string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = new[] { message } });

    internal static PdfExtractionDto ToDto(PdfExtraction x) => new(x.Id, x.FileName, x.UploadedBy, x.UploadedAt, x.Status,
        x.Attempts, x.Error, x.CompletedAt, x.DecidedBy, x.DecidedAt, x.ImportId);
}
