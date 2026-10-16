using System.Globalization;
using System.Security.Claims;
using Meridian.Api.Auth;
using Meridian.Api.Data;
using Meridian.Domain;
using Microsoft.EntityFrameworkCore;

namespace Meridian.Api.Conflicts;

public static class ConflictEndpoints
{
    public const int MaxCommentLength = 1000;

    public static void MapConflictEndpoints(this RouteGroupBuilder api)
    {
        var conflicts = api.MapGroup("/conflicts").WithTags("Conflicts").RequireAuthorization(Policies.Analyst);

        conflicts.MapGet("", List).WithName("GetConflicts");

        conflicts.MapGet("/{id:long}", Get).WithName("GetConflict")
            .Produces<ConflictDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        conflicts.MapPost("/{id:long}/decision", Decide).WithName("DecideConflict")
            .Produces<ConflictDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    // CON-01: pending by default; decided ones with ?status=Approved / Rejected (CON-06)
    private static async Task<List<ConflictDto>> List(MeridianDbContext db, CancellationToken ct,
        ConflictStatus status = ConflictStatus.Pending, string? vote = null)
    {
        var query = db.Conflicts.Where(c => c.Status == status);
        if (!string.IsNullOrWhiteSpace(vote))
            query = query.Where(c => db.Votes.Any(v => v.Id == c.VoteId && v.AssemblyVoteId == vote.Trim()));

        return await ToDtos(db, query, ct);
    }

    private static async Task<IResult> Get(long id, MeridianDbContext db, CancellationToken ct)
    {
        var dto = await FindDto(db, id, ct);
        return dto == null ? NotFound() : Results.Ok(dto);
    }

    // CON-04 + CON-05. One transaction:
    //   1. claim the conflict: UPDATE ... WHERE status = 'Pending'. 0 rows = someone decided first -> 409
    //   2. on approve, change the approved value only if it is still the one this conflict was opened against -> else 409
    //   3. write the audit event
    // If any step fails, nothing changes.
    private static async Task<IResult> Decide(long id, DecisionRequest request, MeridianDbContext db,
        ClaimsPrincipal user, HttpContext http, CancellationToken ct)
    {
        var errors = Validate(request, out var approve);
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var conflict = await db.Conflicts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (conflict == null) return NotFound();

        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var now = DateTime.UtcNow;
        var comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim();

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // the row lock makes a second, simultaneous decision wait, then match 0 rows
        var claimed = await db.Conflicts
            .Where(c => c.Id == id && c.Status == ConflictStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, approve ? ConflictStatus.Approved : ConflictStatus.Rejected)
                .SetProperty(c => c.DecidedBy, userId)
                .SetProperty(c => c.DecidedAt, now)
                .SetProperty(c => c.Comment, comment), ct);
        if (claimed == 0)
        {
            await tx.RollbackAsync(ct);
            var current = (await FindDto(db, id, ct))!;
            return Conflict409("Conflict already decided",
                $"{current.DecidedBy} {current.Status.ToString().ToLowerInvariant()} this conflict at {current.DecidedAt:yyyy-MM-dd HH:mm:ss} UTC. Your decision was not applied.",
                current);
        }

        if (approve && await ApplyProposal(db, conflict, userId, now, ct) == 0)
        {
            await tx.RollbackAsync(ct);
            var current = (await FindDto(db, id, ct))!;
            return Conflict409("Approved value has changed",
                $"The approved value is now '{current.CurrentApprovedValue}', not '{conflict.ApprovedValue}' as when this conflict was opened. " +
                "Nothing was changed; review it again and reject it if it no longer applies.",
                current);
        }

        db.AuditEvents.Add(new AuditEvent
        {
            VoteId = conflict.VoteId,
            CountryCode = conflict.CountryCode,
            EventType = approve ? "ConflictApproved" : "ConflictRejected",
            OldValue = $"{conflict.Field}: {conflict.ApprovedValue}",
            NewValue = $"{conflict.Field}: {conflict.ProposedValue}",
            ImportId = conflict.ProposedImportId,   // the file the proposal came from
            ConflictId = conflict.Id,
            Actor = userId,
            At = now,
            CorrelationId = http.TraceIdentifier,
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return Results.Ok(await FindDto(db, id, ct));
    }

    // Writes the proposed value as approved. The WHERE on the old value makes this a no-op (0 rows)
    // if the approved value changed after the conflict was opened.
    private static Task<int> ApplyProposal(MeridianDbContext db, Conflict c, string userId, DateTime now, CancellationToken ct)
    {
        var votes = db.Votes.Where(v => v.Id == c.VoteId);
        switch (c.Field)
        {
            case ConflictField.Ballot:
                return db.CountryVotes
                    .Where(cv => cv.VoteId == c.VoteId && cv.CountryCode == c.CountryCode && cv.Ballot == c.ApprovedValue)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(cv => cv.Ballot, c.ProposedValue)
                        .SetProperty(cv => cv.SourceImportId, c.ProposedImportId)
                        .SetProperty(cv => cv.ApprovedBy, userId)
                        .SetProperty(cv => cv.ApprovedAt, now), ct);

            case ConflictField.Title:
                return votes.Where(v => v.Title == c.ApprovedValue).ExecuteUpdateAsync(s => s
                    .SetProperty(v => v.Title, c.ProposedValue)
                    .SetProperty(v => v.SourceImportId, c.ProposedImportId), ct);

            case ConflictField.Date:
                var oldDate = ParseDate(c.ApprovedValue);
                var newDate = ParseDate(c.ProposedValue);
                return votes.Where(v => v.VoteDate == oldDate).ExecuteUpdateAsync(s => s
                    .SetProperty(v => v.VoteDate, newDate)
                    .SetProperty(v => v.SourceImportId, c.ProposedImportId), ct);

            case ConflictField.Session:
                var oldSession = int.Parse(c.ApprovedValue, CultureInfo.InvariantCulture);
                var newSession = int.Parse(c.ProposedValue, CultureInfo.InvariantCulture);
                return votes.Where(v => v.Session == oldSession).ExecuteUpdateAsync(s => s
                    .SetProperty(v => v.Session, newSession)
                    .SetProperty(v => v.SourceImportId, c.ProposedImportId), ct);

            default:
                throw new InvalidOperationException($"Unknown conflict field {c.Field}");
        }
    }

    private static Dictionary<string, string[]> Validate(DecisionRequest request, out bool approve)
    {
        var errors = new Dictionary<string, string[]>();
        approve = false;

        if (string.IsNullOrWhiteSpace(request.Decision))
            errors["decision"] = new[] { "Decision is required: Approve or Reject." };
        else if (string.Equals(request.Decision.Trim(), "Approve", StringComparison.OrdinalIgnoreCase))
            approve = true;
        else if (!string.Equals(request.Decision.Trim(), "Reject", StringComparison.OrdinalIgnoreCase))
            errors["decision"] = new[] { "Decision must be Approve or Reject." };

        if (request.Comment?.Length > MaxCommentLength)
            errors["comment"] = new[] { $"Comment can be at most {MaxCommentLength} characters." };

        return errors;
    }

    // --- reading ---

    private static async Task<ConflictDto?> FindDto(MeridianDbContext db, long id, CancellationToken ct) =>
        (await ToDtos(db, db.Conflicts.Where(c => c.Id == id), ct)).SingleOrDefault();

    // also used by the vote screen (open conflicts of one vote)
    public static async Task<List<ConflictDto>> ToDtos(MeridianDbContext db, IQueryable<Conflict> query, CancellationToken ct)
    {
        var rows = await (from c in query.AsNoTracking()
                          join v in db.Votes on c.VoteId equals v.Id
                          orderby c.CreatedAt, c.Id   // oldest first
                          select new { Conflict = c, Vote = v }).ToListAsync(ct);

        var importIds = rows.SelectMany(r => new[] { r.Conflict.ApprovedSourceImportId, r.Conflict.ProposedImportId }).Distinct().ToList();
        var imports = await db.Imports.AsNoTracking().Where(i => importIds.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, i => new ValueSourceDto(i.Id, i.FileName, i.UploadedBy, i.UploadedAt), ct);

        // current approved ballots, for "has it changed since?"
        var ballotKeys = rows.Where(r => r.Conflict.CountryCode != null)
            .Select(r => new { r.Conflict.VoteId, r.Conflict.CountryCode }).Distinct().ToList();
        var ballotVoteIds = ballotKeys.Select(k => k.VoteId).Distinct().ToList();
        var ballotCountries = ballotKeys.Select(k => k.CountryCode).Distinct().ToList();
        var ballots = await db.CountryVotes.AsNoTracking()
            .Where(cv => ballotVoteIds.Contains(cv.VoteId) && ballotCountries.Contains(cv.CountryCode))
            .ToDictionaryAsync(cv => (cv.VoteId, cv.CountryCode), cv => cv.Ballot, ct);

        return rows.Select(r =>
        {
            var c = r.Conflict;
            var current = c.Field switch
            {
                ConflictField.Ballot => ballots.GetValueOrDefault((c.VoteId, c.CountryCode!)),
                ConflictField.Title => r.Vote.Title,
                ConflictField.Date => r.Vote.VoteDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ConflictField.Session => r.Vote.Session.ToString(CultureInfo.InvariantCulture),
                _ => null,
            };
            return new ConflictDto(c.Id, r.Vote.AssemblyVoteId, r.Vote.Title, c.CountryCode, c.Field, c.Status,
                c.ApprovedValue, imports[c.ApprovedSourceImportId], c.ProposedValue, imports[c.ProposedImportId],
                current, c.CreatedAt, c.DecidedBy, c.DecidedAt, c.Comment);
        }).ToList();
    }

    private static DateOnly ParseDate(string s) => DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static IResult NotFound() =>
        Results.Problem(title: "Conflict not found", statusCode: StatusCodes.Status404NotFound);

    // Rule 8: a concurrency conflict is a 409 that carries the current state
    private static IResult Conflict409(string title, string detail, ConflictDto current) =>
        Results.Problem(title: title, detail: detail, statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?> { ["current"] = current });
}
