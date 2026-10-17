using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Meridian.Api.Auth;
using Meridian.Api.Data;
using Meridian.Api.Storage;
using Meridian.Domain;
using Meridian.Domain.Ingestion;
using Microsoft.EntityFrameworkCore;

namespace Meridian.Api.Imports;

public static class ImportEndpoints
{
    public const long MaxUploadBytes = 200 * 1024 * 1024;   // historic file is ~103 MB

    public static void MapImportEndpoints(this RouteGroupBuilder api)
    {
        var imports = api.MapGroup("/imports").WithTags("Imports").RequireAuthorization(Policies.Analyst);

        imports.MapPost("", Upload)
            .WithName("UploadImport")
            .DisableAntiforgery()   // header-based auth, no cookies: antiforgery doesn't apply
            .Produces<ImportDto>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem();

        imports.MapGet("", List).WithName("GetImports");

        imports.MapGet("/{id:long}", Get).WithName("GetImport")
            .ProducesProblem(StatusCodes.Status404NotFound);

        imports.MapGet("/{id:long}/rows", GetRows).WithName("GetImportRows")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    // ING-01: store the file, queue it, answer 202 right away. The worker does the rest.
    private static async Task<IResult> Upload(IFormFile? file, MeridianDbContext db, IFileStore files,
        ClaimsPrincipal user, HttpContext http, CancellationToken ct)
    {
        if (file == null || file.Length == 0)
            return FileError("A non-empty CSV file is required.");

        // Header is the first line: check it now so a wrong file is rejected next to the field
        string? firstLine;
        using (var reader = new StreamReader(file.OpenReadStream(), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false))
            firstLine = await reader.ReadLineAsync(ct);
        var header = CsvFormat.CheckHeader(firstLine ?? "");
        if (!header.IsValid)
            return FileError(header.Error!);

        StoredFile stored;
        await using (var content = file.OpenReadStream())
            stored = await files.SaveAsync(content, ct);

        var import = new Import
        {
            FileName = Path.GetFileName(file.FileName),
            FileSha256 = stored.Sha256,
            StoragePath = stored.StoragePath,
            UploadedBy = user.FindFirstValue(ClaimTypes.NameIdentifier)!,
            UploadedAt = DateTime.UtcNow,
            Status = ImportStatus.Queued,
            CorrelationId = http.TraceIdentifier,
        };
        db.Imports.Add(import);
        await db.SaveChangesAsync(ct);

        return Results.Accepted($"/api/imports/{import.Id}", ToDto(import));
    }

    private static async Task<List<ImportDto>> List(MeridianDbContext db, CancellationToken ct)
    {
        var imports = await db.Imports.AsNoTracking().OrderByDescending(i => i.Id).ToListAsync(ct);
        return imports.Select(ToDto).ToList();
    }

    private static async Task<IResult> Get(long id, MeridianDbContext db, CancellationToken ct)
    {
        var import = await db.Imports.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct);
        if (import == null)
            return Results.Problem(title: "Import not found", statusCode: StatusCodes.Status404NotFound);

        var summary = import.Summary == null ? null : JsonSerializer.Deserialize<ImportSummary>(import.Summary, SummaryJson);
        return Results.Ok(new ImportDetailsDto(ToDto(import), summary));
    }

    // ING-14: the rows behind the report, e.g. ?classification=Invalid
    private static async Task<IResult> GetRows(long id, MeridianDbContext db, CancellationToken ct,
        RowClassification? classification = null, int page = 1, int pageSize = 100)
    {
        var errors = new Dictionary<string, string[]>();
        if (page < 1) errors["page"] = new[] { "Page must be 1 or more." };
        if (pageSize is < 1 or > 1000) errors["pageSize"] = new[] { "Page size must be between 1 and 1000." };
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        if (!await db.Imports.AnyAsync(i => i.Id == id, ct))
            return Results.Problem(title: "Import not found", statusCode: StatusCodes.Status404NotFound);

        var query = db.ImportRows.AsNoTracking().Where(r => r.ImportId == id);
        if (classification != null) query = query.Where(r => r.Classification == classification);

        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(r => r.LineNo)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(r => new ImportRowDto(r.LineNo, r.RawLine, r.Classification, r.Reason, r.NormalizationNote,
                r.ConflictId, r.AssemblyVoteId, r.CountryCode))
            .ToListAsync(ct);

        return Results.Ok(new PagedResult<ImportRowDto>(items, total, page, pageSize));
    }

    // enum names as text in the stored report, same as in the API
    public static readonly JsonSerializerOptions SummaryJson = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static IResult FileError(string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = new[] { message } });

    private static ImportDto ToDto(Import i) => new(i.Id, i.FileName, i.UploadedBy, i.UploadedAt, i.Status, i.Stage,
        i.TotalRows, i.ProcessedRows, i.StartedAt, i.CompletedAt, i.Error);
}
