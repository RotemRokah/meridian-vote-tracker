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

public record EditBallotRequest(string? Ballot);

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

        pdfs.MapPost("/{id:long}/approve", Approve).WithName("ApprovePdfExtraction")
            .Produces<PdfExtractionDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        pdfs.MapPost("/{id:long}/reject", Reject).WithName("RejectPdfExtraction")
            .Produces<PdfExtractionDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        pdfs.MapPut("/{id:long}/rows/{code}", EditBallot).WithName("EditPdfBallot")
            .Produces<PdfExtractionDetailsDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        pdfs.MapPost("/{id:long}/retry", Retry).WithName("RetryPdfExtraction")
            .Produces<PdfExtractionDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    // PDF-05: an approved sheet becomes a normal import (CSV built from the reviewed rows, named after the PDF),
    // so it goes through the same ingestion rules: New / Unchanged / Conflict, never overwriting approved data.
    private static async Task<IResult> Approve(long id, MeridianDbContext db, IFileStore files,
        ClaimsPrincipal user, HttpContext http, CancellationToken ct)
    {
        var x = await db.PdfExtractions.SingleOrDefaultAsync(e => e.Id == id, ct);
        if (x == null) return NotFound(id);
        if (x.Status != PdfExtractionStatus.ReadyForReview) return NotReviewable(x);
        var sheet = SheetOf(x)!;
        var blocking = VotingSheetReader.BlockingProblems(sheet);
        if (blocking.Count > 0)
            return Results.ValidationProblem(new Dictionary<string, string[]>
                { ["sheet"] = new[] { "Fix before approving: " + string.Join(", ", blocking) } });

        StoredFile stored;
        using (var csv = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(ToCsv(sheet))))
            stored = await files.SaveAsync(csv, ct);

        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var import = new Import
        {
            FileName = x.FileName, FileSha256 = stored.Sha256, StoragePath = stored.StoragePath,
            UploadedBy = userId, UploadedAt = DateTime.UtcNow, Status = ImportStatus.Queued, CorrelationId = http.TraceIdentifier,
        };
        db.Imports.Add(import);
        await db.SaveChangesAsync(ct);

        // only one decision lands, even if two analysts click at once (same idea as CON-05)
        var claimed = await db.PdfExtractions.Where(e => e.Id == id && e.Status == PdfExtractionStatus.ReadyForReview)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, PdfExtractionStatus.Approved)
                .SetProperty(e => e.DecidedBy, userId).SetProperty(e => e.DecidedAt, DateTime.UtcNow)
                .SetProperty(e => e.ImportId, import.Id), ct);
        if (claimed == 0) return NotReviewable(await db.PdfExtractions.AsNoTracking().SingleAsync(e => e.Id == id, ct));
        await tx.CommitAsync(ct);
        return Results.Ok(ToDto(await db.PdfExtractions.AsNoTracking().SingleAsync(e => e.Id == id, ct)));
    }

    // PDF-04: the analyst corrects a ballot before approving. The edit is kept on the row, so it stays visible.
    private static async Task<IResult> EditBallot(long id, string code, EditBallotRequest request, MeridianDbContext db,
        ClaimsPrincipal user, CancellationToken ct)
    {
        var ballot = request.Ballot?.Trim().ToUpperInvariant();
        if (ballot is not ("Y" or "N" or "A" or "X"))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["ballot"] = new[] { "Ballot must be Y, N, A or X." } });

        var x = await db.PdfExtractions.SingleOrDefaultAsync(e => e.Id == id, ct);
        if (x == null) return NotFound(id);
        if (x.Status != PdfExtractionStatus.ReadyForReview) return NotReviewable(x);
        var sheet = SheetOf(x)!;
        var row = sheet.Rows.FirstOrDefault(r => r.Code == code.ToUpperInvariant());
        if (row == null) return Results.Problem(statusCode: 404, title: "Row not found", detail: $"No row for {code} on this sheet.");

        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        // a person decided: the tick-based notes no longer apply; what the scan said stays visible in the note
        var edited = row with
        {
            Ballot = ballot, Confidence = null,
            Issues = new() { $"Edited by {userId}: {row.Ballot ?? "unreadable"} → {ballot}" },
        };
        var members = new Meridian.Domain.Ingestion.MemberList(await db.MemberStates.AsNoTracking()
            .Select(m => new Meridian.Domain.Ingestion.MembershipPeriod(m.CountryCode, m.MemberFrom, m.MemberTo)).ToListAsync(ct));
        // all checks run again: totals, duplicates, codes
        var revalidated = VotingSheetReader.Validate(
            sheet with { Rows = sheet.Rows.Select(r => ReferenceEquals(r, row) ? edited : r).ToList() }, members);

        x.Sheet = JsonSerializer.Serialize(revalidated, PdfExtractionWorker.Json);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new PdfExtractionDetailsDto(ToDto(x), revalidated, VotingSheetReader.BlockingProblems(revalidated)));
    }

    private static async Task<IResult> Reject(long id, MeridianDbContext db, ClaimsPrincipal user, CancellationToken ct)
    {
        var claimed = await db.PdfExtractions.Where(e => e.Id == id && e.Status == PdfExtractionStatus.ReadyForReview)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, PdfExtractionStatus.Rejected)
                .SetProperty(e => e.DecidedBy, user.FindFirstValue(ClaimTypes.NameIdentifier))
                .SetProperty(e => e.DecidedAt, DateTime.UtcNow), ct);
        var x = await db.PdfExtractions.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id, ct);
        if (x == null) return NotFound(id);
        return claimed == 0 ? NotReviewable(x) : Results.Ok(ToDto(x));
    }

    private static IResult NotReviewable(PdfExtraction x) =>
        Results.Problem(statusCode: 409, title: "Extraction is not waiting for review",
            detail: x.DecidedBy == null ? $"Extraction {x.Id} is {x.Status}." : $"Extraction {x.Id} was {x.Status.ToString().ToLowerInvariant()} by {x.DecidedBy} at {x.DecidedAt:yyyy-MM-dd HH:mm:ss} UTC.",
            extensions: new Dictionary<string, object?> { ["current"] = ToDto(x) });

    internal static string ToCsv(VotingSheet sheet)
    {
        static string Q(string? v) => "\"" + (v ?? "").Replace("\"", "\"\"") + "\"";
        var lines = new List<string> { string.Join(",", Meridian.Domain.Ingestion.CsvFormat.Columns) };
        var date = VotingSheetReader.IsoDate(sheet.VoteDate);
        foreach (var r in sheet.Rows)
            lines.Add(string.Join(",", Q(sheet.AssemblyVoteId), sheet.Session, date, Q(sheet.Title), Q(r.Code), Q(r.Name), r.Ballot, ""));
        return string.Join("\n", lines) + "\n";
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
