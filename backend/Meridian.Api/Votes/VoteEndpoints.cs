using System.Security.Claims;
using Meridian.Api.Auth;
using Meridian.Api.Conflicts;
using Meridian.Api.Data;
using Meridian.Api.Imports;
using Meridian.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Meridian.Api.Votes;

public static class VoteEndpoints
{
    public static void MapVoteEndpoints(this RouteGroupBuilder api)
    {
        var votes = api.MapGroup("/votes").WithTags("Votes").RequireAuthorization(Policies.Analyst);

        votes.MapGet("", List).WithName("GetVotes")
            .Produces<PagedResult<VoteListItemDto>>()
            .ProducesValidationProblem();

        // numeric id: the assembly id (MA/2024/003) has slashes; find it with ?search= on the list
        votes.MapGet("/{id:long}", Get).WithName("GetVote")
            .Produces<VoteDetailsDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        votes.MapGet("/{id:long}/timeline", Timeline).WithName("GetVoteTimeline")
            .Produces<List<TimelineEntryDto>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        // POS-01: Admin only, enforced here (Analyst -> 403)
        votes.MapPut("/{id:long}/position", SetPosition).WithName("SetVotePosition")
            .RequireAuthorization(Policies.Admin)
            .Produces<PositionDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    // POS-01..03. Optimistic concurrency: the client sends the version it saw; the UPDATE only
    // matches that version (xmin), so a change made meanwhile by someone else is never overwritten.
    private static async Task<IResult> SetPosition(long id, PositionRequest request, MeridianDbContext db,
        ClaimsPrincipal user, HttpContext http, CancellationToken ct)
    {
        // POS-02: every field error at once, next to its field
        var errors = new Dictionary<string, string[]>();
        var stanceName = Enum.GetNames<Stance>()
            .FirstOrDefault(n => string.Equals(n, request.Stance?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(request.Stance))
            errors["stance"] = new[] { "Stance is required: For or Against." };
        else if (stanceName == null)
            errors["stance"] = new[] { "Stance must be For or Against." };
        if (request.Weight == null)
            errors["weight"] = new[] { "Weight is required: a whole number from 1 to 5." };
        else if (request.Weight is < 1 or > 5)
            errors["weight"] = new[] { "Weight must be between 1 and 5." };
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var stance = Enum.Parse<Stance>(stanceName!);
        var weight = request.Weight!.Value;

        if (!await db.Votes.AnyAsync(v => v.Id == id, ct)) return NotFound();

        var position = await db.VotePositions.FirstOrDefaultAsync(p => p.VoteId == id, ct);
        string? oldValue = null;
        if (position == null)
        {
            position = new VotePosition { VoteId = id };
            db.VotePositions.Add(position);
        }
        else
        {
            if (request.Version != position.Version)
                return await ChangedMeanwhile(db, id, ct);
            if (position.Stance == stance && position.Weight == weight)
                return Results.Ok(ToDto(position));   // nothing changed: no audit event
            oldValue = Describe(position.Stance, position.Weight);
        }

        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        // PostgreSQL keeps microseconds; .NET has 0.1 µs. Truncate so the response equals what is stored.
        var now = DateTime.UtcNow;
        now = new DateTime(now.Ticks - now.Ticks % 10, DateTimeKind.Utc);
        position.Stance = stance;
        position.Weight = weight;
        position.UpdatedBy = userId;
        position.UpdatedAt = now;

        // POS-03 / HIS-03: who, when, old, new. Saved in the same transaction as the change.
        db.AuditEvents.Add(new AuditEvent
        {
            VoteId = id,
            EventType = "PositionSet",
            OldValue = oldValue,
            NewValue = Describe(stance, weight),
            Actor = userId,
            At = now,
            CorrelationId = http.TraceIdentifier,
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return await ChangedMeanwhile(db, id, ct);   // updated by someone else since we read it
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return await ChangedMeanwhile(db, id, ct);   // someone else created the first position at the same moment
        }

        return Results.Ok(ToDto(position));
    }

    private static string Describe(Stance stance, int weight) => $"{stance}, weight {weight}";

    private static PositionDto ToDto(VotePosition p) => new(p.Stance, p.Weight, p.UpdatedBy, p.UpdatedAt, p.Version);

    private static async Task<IResult> ChangedMeanwhile(MeridianDbContext db, long voteId, CancellationToken ct)
    {
        var current = await db.VotePositions.AsNoTracking().Where(p => p.VoteId == voteId)
            .Select(p => new PositionDto(p.Stance, p.Weight, p.UpdatedBy, p.UpdatedAt, p.Version))
            .FirstAsync(ct);
        return Results.Problem(
            title: "Position was changed by someone else",
            detail: $"{current.UpdatedBy} set it to {Describe(current.Stance, current.Weight)} at {current.UpdatedAt:yyyy-MM-dd HH:mm:ss} UTC. " +
                    "Your change was not saved; review the current position and try again.",
            statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?> { ["current"] = current });
    }

    private static async Task<IResult> List(MeridianDbContext db, CancellationToken ct,
        string? search = null, int? session = null, DateOnly? from = null, DateOnly? to = null,
        int page = 1, int pageSize = 50)
    {
        var errors = new Dictionary<string, string[]>();
        if (page < 1) errors["page"] = new[] { "Page must be 1 or more." };
        if (pageSize is < 1 or > 500) errors["pageSize"] = new[] { "Page size must be between 1 and 500." };
        if (from > to) errors["from"] = new[] { "'from' must be on or before 'to'." };
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var query = db.Votes.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(v => EF.Functions.ILike(v.AssemblyVoteId, pattern) || EF.Functions.ILike(v.Title, pattern));
        }
        if (session != null) query = query.Where(v => v.Session == session);
        if (from != null) query = query.Where(v => v.VoteDate >= from);
        if (to != null) query = query.Where(v => v.VoteDate <= to);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(v => v.VoteDate).ThenBy(v => v.AssemblyVoteId)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(v => new VoteListItemDto(
                v.Id, v.AssemblyVoteId, v.Session, v.VoteDate, v.Title,
                db.CountryVotes.Count(cv => cv.VoteId == v.Id),
                db.Conflicts.Count(c => c.VoteId == v.Id && c.Status == ConflictStatus.Pending),
                db.VotePositions.Where(p => p.VoteId == v.Id).Select(p => (Stance?)p.Stance).FirstOrDefault(),
                db.VotePositions.Where(p => p.VoteId == v.Id).Select(p => (int?)p.Weight).FirstOrDefault()))
            .ToListAsync(ct);

        return Results.Ok(new PagedResult<VoteListItemDto>(items, total, page, pageSize));
    }

    private static async Task<IResult> Get(long id, MeridianDbContext db, CancellationToken ct)
    {
        var vote = await db.Votes.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id, ct);
        if (vote == null) return NotFound();

        var ballots = await db.CountryVotes.AsNoTracking().Where(cv => cv.VoteId == id)
            .OrderBy(cv => cv.CountryCode).ToListAsync(ct);
        var sources = await Sources(db, ballots.Select(b => b.SourceImportId).Append(vote.SourceImportId), ct);
        var names = await CountryNames(db, ct);

        var position = await db.VotePositions.AsNoTracking().Where(p => p.VoteId == id)
            .Select(p => new PositionDto(p.Stance, p.Weight, p.UpdatedBy, p.UpdatedAt, p.Version))
            .FirstOrDefaultAsync(ct);
        var openConflicts = await ConflictEndpoints.ToDtos(db,
            db.Conflicts.Where(c => c.VoteId == id && c.Status == ConflictStatus.Pending), ct);

        return Results.Ok(new VoteDetailsDto(
            vote.Id, vote.AssemblyVoteId, vote.Session, vote.VoteDate, vote.Title,
            sources[vote.SourceImportId],
            position,
            Enum.GetNames<Ballot>().ToDictionary(b => b, b => ballots.Count(x => x.Ballot == b)),
            ballots.Select(b => new CountryBallotDto(
                b.CountryCode, names.GetValueOrDefault(b.CountryCode, b.CountryCode), b.Ballot,
                sources[b.SourceImportId], b.ApprovedBy != null, b.ApprovedBy, b.ApprovedAt)).ToList(),
            openConflicts));
    }

    // HIS-04: everything that happened to a vote. Two sources, merged in time order:
    //   audit_events: what changed (created, ballots added, conflicts opened and decided, positions)
    //   import_rows:  every file that touched the vote, even when it changed nothing
    // With ?country=BRA: only BRA's rows and BRA's events ("why did BRA's ballot change?")
    private static async Task<IResult> Timeline(long id, MeridianDbContext db, CancellationToken ct, string? country = null)
    {
        var vote = await db.Votes.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id, ct);
        if (vote == null) return NotFound();
        country = string.IsNullOrWhiteSpace(country) ? null : country.Trim().ToUpperInvariant();

        // 1. audit events
        var events = await db.AuditEvents.AsNoTracking()
            .Where(a => a.VoteId == id && (country == null || a.CountryCode == country))
            .ToListAsync(ct);
        var conflictIds = events.Where(a => a.ConflictId != null).Select(a => a.ConflictId!.Value).Distinct().ToList();
        var comments = await db.Conflicts.AsNoTracking()
            .Where(c => conflictIds.Contains(c.Id) && c.Comment != null)
            .ToDictionaryAsync(c => c.Id, c => c.Comment, ct);

        // 2. file rows for this vote (or for one country in it)
        var fileRows = await db.ImportRows.AsNoTracking()
            .Where(r => r.AssemblyVoteId == vote.AssemblyVoteId && (country == null || r.CountryCode == country))
            .Select(r => new { r.ImportId, r.LineNo, r.Ballot, r.Classification, r.Reason, r.ConflictId })
            .ToListAsync(ct);

        var importIds = events.Where(a => a.ImportId != null).Select(a => a.ImportId!.Value)
            .Concat(fileRows.Select(r => r.ImportId)).Distinct().ToList();
        var imports = await db.Imports.AsNoTracking().Where(i => importIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        ValueSourceDto SourceOf(long importId) => ToSource(imports[importId]);
        // file entries sit at the start of the import, before the changes it made
        DateTime FileTime(long importId) => imports[importId].StartedAt ?? imports[importId].UploadedAt;

        var timeline = events.Select(a => new TimelineEntryDto(
            a.At, a.EventType, a.CountryCode, a.OldValue, a.NewValue, a.Actor,
            a.ImportId == null ? null : SourceOf(a.ImportId.Value),
            a.ConflictId,
            a.ConflictId != null && a.EventType != "ConflictOpened" ? comments.GetValueOrDefault(a.ConflictId.Value) : null,
            null)).ToList();

        if (country == null)
        {
            // one entry per file: how its rows for this vote were classified
            timeline.AddRange(fileRows.GroupBy(r => r.ImportId).Select(g => new TimelineEntryDto(
                FileTime(g.Key), "FileProcessed", null, null, null, imports[g.Key].UploadedBy, SourceOf(g.Key), null, null,
                string.Join(", ", g.GroupBy(r => r.Classification)
                    .OrderBy(c => c.Key)
                    .Select(c => $"{c.Count()} {c.Key?.ToString() ?? "not classified yet"}")))));
        }
        else
        {
            // one entry per row of that country, with its line number, value and outcome
            timeline.AddRange(fileRows.Select(r => new TimelineEntryDto(
                FileTime(r.ImportId), "FileRow", country, null, r.Ballot, imports[r.ImportId].UploadedBy, SourceOf(r.ImportId),
                r.ConflictId, null,
                $"line {r.LineNo}: {r.Classification?.ToString() ?? "not classified yet"}{(r.Reason == null ? "" : " - " + r.Reason)}")));
        }

        return Results.Ok(timeline
            .OrderBy(t => t.At)
            .ThenBy(t => t.EventType is "FileProcessed" or "FileRow" ? 0 : 1)   // same moment: the file first
            .ToList());
    }

    private static async Task<Dictionary<long, ValueSourceDto>> Sources(MeridianDbContext db, IEnumerable<long> importIds, CancellationToken ct)
    {
        var ids = importIds.Distinct().ToList();
        return await db.Imports.AsNoTracking().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ToSource, ct);
    }

    private static ValueSourceDto ToSource(Import i) => new(i.Id, i.FileName, i.UploadedBy, i.UploadedAt);

    // current name per code: the latest membership period's name
    private static async Task<Dictionary<string, string>> CountryNames(MeridianDbContext db, CancellationToken ct) =>
        (await db.MemberStates.AsNoTracking().ToListAsync(ct))
            .GroupBy(m => m.CountryCode)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.MemberFrom).First().CountryName);

    private static IResult NotFound() =>
        Results.Problem(title: "Vote not found", statusCode: StatusCodes.Status404NotFound);
}
