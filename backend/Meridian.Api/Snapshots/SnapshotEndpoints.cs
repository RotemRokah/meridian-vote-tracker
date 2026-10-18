using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meridian.Api.Auth;
using Meridian.Api.Data;
using Meridian.Api.Imports;
using Meridian.Domain;
using Meridian.Domain.Scoring;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Meridian.Api.Snapshots;

public static class SnapshotEndpoints
{
    public const int MaxNameLength = 200;

    // filter/rules jsonb: enum names as text, like the API
    public static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    public static void MapSnapshotEndpoints(this RouteGroupBuilder api)
    {
        var snapshots = api.MapGroup("/snapshots").WithTags("Snapshots").RequireAuthorization(Policies.Analyst);

        snapshots.MapPost("", Create).WithName("CreateSnapshot")
            .Produces<SnapshotDto>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem();
        snapshots.MapGet("", List).WithName("GetSnapshots");
        snapshots.MapGet("/{id:long}", Get).WithName("GetSnapshot")
            .Produces<SnapshotDetailsDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        snapshots.MapGet("/{id:long}/countries/{code}", CountryBreakdown).WithName("GetSnapshotCountry")
            .Produces<CountryBreakdownDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        snapshots.MapGet("/{id:long}/inputs", Inputs).WithName("GetSnapshotInputs")
            .Produces<PagedResult<SnapshotInputDto>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        // SNP-05: Admin only
        snapshots.MapPost("/{id:long}/publish", Publish).WithName("PublishSnapshot")
            .RequireAuthorization(Policies.Admin)
            .Produces<SnapshotDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        snapshots.MapPost("/{id:long}/unpublish", Unpublish).WithName("UnpublishSnapshot")
            .RequireAuthorization(Policies.Admin)
            .Produces<SnapshotDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    // SNP-01 + POS-04. Resolves the selection now, then queues the computation (202).
    private static async Task<IResult> Create(CreateSnapshotRequest request, MeridianDbContext db,
        ClaimsPrincipal user, HttpContext http, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var requested = (request.VoteIds ?? new()).Select(v => v.Trim()).Where(v => v != "").Distinct().ToList();
        var hasFilter = request.From != null || request.To != null || request.Session != null;

        if (string.IsNullOrWhiteSpace(request.Name))
            errors["name"] = new[] { "Name is required." };
        else if (request.Name.Trim().Length > MaxNameLength)
            errors["name"] = new[] { $"Name can be at most {MaxNameLength} characters." };
        if (!hasFilter && requested.Count == 0)
            errors["selection"] = new[] { "Choose votes: a date range, a session, and/or a list of votes." };
        if (request.From > request.To)
            errors["from"] = new[] { "'from' must be on or before 'to'." };

        // explicit list: every id must exist
        var listed = await db.Votes.AsNoTracking().Where(v => requested.Contains(v.AssemblyVoteId))
            .Select(v => new { v.Id, v.AssemblyVoteId }).ToListAsync(ct);
        var unknown = requested.Except(listed.Select(v => v.AssemblyVoteId)).ToList();
        if (unknown.Count > 0)
            errors["voteIds"] = new[] { "Unknown votes: " + string.Join(", ", unknown) };
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        // selection = filter UNION explicit list
        var selected = listed.Select(v => v.Id).ToHashSet();
        if (hasFilter)
        {
            var query = db.Votes.AsNoTracking();
            if (request.From != null) query = query.Where(v => v.VoteDate >= request.From);
            if (request.To != null) query = query.Where(v => v.VoteDate <= request.To);
            if (request.Session != null) query = query.Where(v => v.Session == request.Session);
            selected.UnionWith(await query.Select(v => v.Id).ToListAsync(ct));
        }
        if (selected.Count == 0)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["selection"] = new[] { "No votes match this selection." } });

        // POS-04: a vote without a position never enters silently. Block, or exclude only when asked to.
        var withoutPosition = await db.Votes.AsNoTracking()
            .Where(v => selected.Contains(v.Id) && !db.VotePositions.Any(p => p.VoteId == v.Id))
            .OrderBy(v => v.AssemblyVoteId)
            .Select(v => new { v.Id, v.AssemblyVoteId }).ToListAsync(ct);
        if (withoutPosition.Count > 0 && !request.ExcludeVotesWithoutPosition)
        {
            var shown = string.Join(", ", withoutPosition.Take(20).Select(v => v.AssemblyVoteId));
            var more = withoutPosition.Count > 20 ? $" and {withoutPosition.Count - 20} more" : "";
            return Results.ValidationProblem(
                new Dictionary<string, string[]>
                {
                    ["excludeVotesWithoutPosition"] = new[]
                    {
                        $"{withoutPosition.Count} selected votes have no position: {shown}{more}. " +
                        "Ask an Admin to set them, or resend with excludeVotesWithoutPosition = true to leave them out.",
                    },
                },
                extensions: new Dictionary<string, object?> { ["votesWithoutPosition"] = withoutPosition.Select(v => v.AssemblyVoteId).ToList() });
        }
        selected.ExceptWith(withoutPosition.Select(v => v.Id));
        if (selected.Count == 0)
            return Results.ValidationProblem(new Dictionary<string, string[]>
                { ["selection"] = new[] { "None of the selected votes has a position." } });

        // pending conflicts: counted with the approved value, listed for transparency
        var pending = await (from c in db.Conflicts.AsNoTracking()
                             join v in db.Votes on c.VoteId equals v.Id
                             where c.Status == ConflictStatus.Pending && selected.Contains(c.VoteId)
                             orderby v.AssemblyVoteId, c.CountryCode
                             select new { v.AssemblyVoteId, c.CountryCode, c.Field, c.ApprovedValue, c.ProposedValue })
            .ToListAsync(ct);

        var rules = SnapshotRules.For(
            selected.Order().ToList(),
            withoutPosition.Select(v => v.AssemblyVoteId).ToList(),
            pending.Select(c => $"{c.AssemblyVoteId} {(c.CountryCode == null ? "" : c.CountryCode + " ")}{c.Field}: {c.ApprovedValue} -> {c.ProposedValue}").ToList());
        var filter = new SnapshotFilter(request.From, request.To, request.Session, requested, request.ExcludeVotesWithoutPosition);

        var snapshot = new Snapshot
        {
            Name = request.Name!.Trim(),
            Filter = JsonSerializer.Serialize(filter, Json),
            Rules = JsonSerializer.Serialize(rules, Json),
            Status = SnapshotStatus.Queued,
            CreatedBy = user.FindFirstValue(ClaimTypes.NameIdentifier)!,
            CreatedAt = DateTime.UtcNow,
            CorrelationId = http.TraceIdentifier,   // the worker logs under the same id
        };
        db.Snapshots.Add(snapshot);
        await db.SaveChangesAsync(ct);

        return Results.Accepted($"/api/snapshots/{snapshot.Id}", ToDto(snapshot, rules));
    }

    private static async Task<List<SnapshotDto>> List(MeridianDbContext db, CancellationToken ct)
    {
        var snapshots = await db.Snapshots.AsNoTracking().OrderByDescending(s => s.Id).ToListAsync(ct);
        return snapshots.Select(s => ToDto(s, RulesOf(s))).ToList();
    }

    // SNP-03: ranked scores, plus what the snapshot was made of and its publish history
    private static async Task<IResult> Get(long id, MeridianDbContext db, CancellationToken ct)
    {
        var snapshot = await db.Snapshots.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (snapshot == null) return NotFound("Snapshot not found");

        var names = await CountryNames.LoadAsync(db, ct);
        var scores = (await db.SnapshotScores.AsNoTracking().Where(s => s.SnapshotId == id).ToListAsync(ct))
            .OrderBy(s => s.Rank == null).ThenBy(s => s.Rank).ThenBy(s => s.CountryCode)
            .Select(s => ToScoreDto(s, names)).ToList();
        var history = await db.AuditEvents.AsNoTracking().Where(a => a.SnapshotId == id).OrderBy(a => a.At)
            .Select(a => new SnapshotEventDto(a.At, a.EventType, a.Actor, a.OldValue, a.NewValue)).ToListAsync(ct);

        var rules = RulesOf(snapshot);
        return Results.Ok(new SnapshotDetailsDto(ToDto(snapshot, rules),
            JsonSerializer.Deserialize<SnapshotFilter>(snapshot.Filter, Json)!, rules, scores, history));
    }

    // SNP-03: each vote's contribution to one country's score
    private static async Task<IResult> CountryBreakdown(long id, string code, MeridianDbContext db, CancellationToken ct)
    {
        code = code.Trim().ToUpperInvariant();
        var score = await db.SnapshotScores.AsNoTracking().FirstOrDefaultAsync(s => s.SnapshotId == id && s.CountryCode == code, ct);
        if (score == null) return NotFound("Country not found in this snapshot");

        var votes = await (from i in db.SnapshotInputs.AsNoTracking()
                           join v in db.Votes on i.VoteId equals v.Id
                           where i.SnapshotId == id && i.CountryCode == code
                           orderby v.VoteDate, v.AssemblyVoteId
                           select new { i, v.AssemblyVoteId, v.Title, v.VoteDate }).ToListAsync(ct);

        var names = await CountryNames.LoadAsync(db, ct);
        return Results.Ok(new CountryBreakdownDto(ToScoreDto(score, names), votes.Select(x =>
        {
            var counted = x.i.Ballot is "Y" or "N";
            var matched = (x.i.Ballot == "Y" && x.i.Stance == Stance.For) || (x.i.Ballot == "N" && x.i.Stance == Stance.Against);
            return new VoteContributionDto(x.i.VoteId, x.AssemblyVoteId, x.Title, x.VoteDate, x.i.Ballot, x.i.Stance, x.i.Weight,
                counted, matched, matched ? x.i.Weight : 0, counted ? x.i.Weight : 0);
        }).ToList()));
    }

    private static async Task<IResult> Inputs(long id, MeridianDbContext db, CancellationToken ct, int page = 1, int pageSize = 500)
    {
        var errors = new Dictionary<string, string[]>();
        if (page < 1) errors["page"] = new[] { "Page must be 1 or more." };
        if (pageSize is < 1 or > 1000) errors["pageSize"] = new[] { "Page size must be between 1 and 1000." };
        if (errors.Count > 0) return Results.ValidationProblem(errors);
        if (!await db.Snapshots.AnyAsync(s => s.Id == id, ct)) return NotFound("Snapshot not found");

        var query = from i in db.SnapshotInputs.AsNoTracking()
                    join v in db.Votes on i.VoteId equals v.Id
                    where i.SnapshotId == id
                    select new { v.AssemblyVoteId, i.CountryCode, i.Ballot, i.Stance, i.Weight };
        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(x => x.AssemblyVoteId).ThenBy(x => x.CountryCode)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new SnapshotInputDto(x.AssemblyVoteId, x.CountryCode, x.Ballot, x.Stance, x.Weight))
            .ToListAsync(ct);
        return Results.Ok(new PagedResult<SnapshotInputDto>(items, total, page, pageSize));
    }

    // SNP-05: one published snapshot per quarter. Explicit: an already published one must be unpublished first.
    private static async Task<IResult> Publish(long id, PublishRequest? request, MeridianDbContext db,
        ClaimsPrincipal user, HttpContext http, CancellationToken ct)
    {
        var snapshot = await db.Snapshots.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (snapshot == null) return NotFound("Snapshot not found");
        if (snapshot.Status != SnapshotStatus.Completed)
            return Conflict409("Snapshot is not completed", $"Status is {snapshot.Status}; only a completed snapshot can be published.", snapshot);
        if (snapshot.IsPublished)
            return Conflict409("Snapshot already published", $"It is published for {snapshot.Quarter}.", snapshot);

        // quarter: given, or derived when all votes fall in one quarter
        var quarter = request?.Quarter?.Trim();
        if (string.IsNullOrEmpty(quarter))
        {
            var voteIds = RulesOf(snapshot).VoteIds;
            var dates = await db.Votes.AsNoTracking().Where(v => voteIds.Contains(v.Id)).Select(v => v.VoteDate).ToListAsync(ct);
            quarter = Quarter.CommonQuarter(dates);
            if (quarter == null)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["quarter"] = new[] { $"The votes span {Quarter.Of(dates.Min())} to {Quarter.Of(dates.Max())}; choose the quarter (e.g. 2024-Q1)." },
                });
        }
        else if (!Quarter.IsValid(quarter))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["quarter"] = new[] { "Quarter must look like 2024-Q1." } });

        var published = await db.Snapshots.AsNoTracking().FirstOrDefaultAsync(s => s.IsPublished && s.Quarter == quarter, ct);
        if (published != null)
            return Conflict409("Quarter already has a published snapshot",
                $"Snapshot #{published.Id} '{published.Name}' is published for {quarter} by {published.PublishedBy}. Unpublish it first.", published);

        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var now = DateTime.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var changed = await db.Snapshots.Where(s => s.Id == id && !s.IsPublished).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsPublished, true)
                .SetProperty(x => x.Quarter, quarter)
                .SetProperty(x => x.PublishedBy, userId)
                .SetProperty(x => x.PublishedAt, now), ct);
            if (changed == 0)
                return await AlreadyChanged(db, id, ct);
        }
        catch (Exception ex) when ((ex as PostgresException ?? ex.InnerException as PostgresException)?.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // someone published another snapshot for this quarter at the same moment
            await tx.RollbackAsync(ct);
            var winner = await db.Snapshots.AsNoTracking().FirstAsync(s => s.IsPublished && s.Quarter == quarter, ct);
            return Conflict409("Quarter already has a published snapshot",
                $"Snapshot #{winner.Id} was just published for {quarter} by {winner.PublishedBy}.", winner);
        }

        db.AuditEvents.Add(Audit(id, "SnapshotPublished", null, quarter, userId, now, http));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var updated = await db.Snapshots.AsNoTracking().FirstAsync(s => s.Id == id, ct);
        return Results.Ok(ToDto(updated, RulesOf(updated)));
    }

    // The snapshot's numbers don't change; it just stops being the published one. Audited.
    private static async Task<IResult> Unpublish(long id, MeridianDbContext db, ClaimsPrincipal user, HttpContext http, CancellationToken ct)
    {
        var snapshot = await db.Snapshots.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (snapshot == null) return NotFound("Snapshot not found");

        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var now = DateTime.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var changed = await db.Snapshots.Where(s => s.Id == id && s.IsPublished).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.IsPublished, false)
            .SetProperty(x => x.PublishedBy, (string?)null)
            .SetProperty(x => x.PublishedAt, (DateTime?)null), ct);
        if (changed == 0)
            return Conflict409("Snapshot is not published", "There is nothing to unpublish.", snapshot);

        db.AuditEvents.Add(Audit(id, "SnapshotUnpublished",
            $"{snapshot.Quarter} (published by {snapshot.PublishedBy} at {snapshot.PublishedAt:yyyy-MM-dd HH:mm:ss} UTC)", null, userId, now, http));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var updated = await db.Snapshots.AsNoTracking().FirstAsync(s => s.Id == id, ct);
        return Results.Ok(ToDto(updated, RulesOf(updated)));
    }

    private static async Task<IResult> AlreadyChanged(MeridianDbContext db, long id, CancellationToken ct)
    {
        var current = await db.Snapshots.AsNoTracking().FirstAsync(s => s.Id == id, ct);
        return Conflict409("Snapshot already published", $"It was just published for {current.Quarter} by {current.PublishedBy}.", current);
    }

    private static AuditEvent Audit(long snapshotId, string type, string? oldValue, string? newValue, string actor, DateTime at, HttpContext http) => new()
    {
        SnapshotId = snapshotId,
        EventType = type,
        OldValue = oldValue,
        NewValue = newValue,
        Actor = actor,
        At = at,
        CorrelationId = http.TraceIdentifier,
    };

    private static SnapshotRules RulesOf(Snapshot s) => JsonSerializer.Deserialize<SnapshotRules>(s.Rules, Json)!;

    private static SnapshotDto ToDto(Snapshot s, SnapshotRules rules) => new(
        s.Id, s.Name, s.Status, s.CreatedBy, s.CreatedAt, s.CompletedAt, s.Error,
        s.Quarter, s.IsPublished, s.PublishedBy, s.PublishedAt, rules.VoteIds.Count);

    private static SnapshotScoreDto ToScoreDto(SnapshotScore s, Dictionary<string, string> names) => new(
        s.CountryCode, names.GetValueOrDefault(s.CountryCode, s.CountryCode), s.Score, s.Rank,
        s.MatchedWeight, s.CountedWeight, s.CountedVotes, s.AbstainCount, s.AbsentCount);

    private static IResult NotFound(string title) => Results.Problem(title: title, statusCode: StatusCodes.Status404NotFound);

    private static IResult Conflict409(string title, string detail, Snapshot current) =>
        Results.Problem(title: title, detail: detail, statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?> { ["current"] = ToDto(current, RulesOf(current)) });
}
