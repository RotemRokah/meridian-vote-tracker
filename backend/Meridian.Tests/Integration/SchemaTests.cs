using Meridian.Api.Data;
using Meridian.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Meridian.Tests.Integration;

// The DB itself enforces the rules that must never break, even if app code has a bug.
[Collection(IntegrationCollection.Name)]
public class SchemaTests
{
    private readonly ApiFactory _factory;

    public SchemaTests(ApiFactory factory)
    {
        _factory = factory;
    }

    // --- seed ---

    [Fact]
    public async Task Seed_loads_all_member_state_periods()
    {
        await using var db = _factory.NewDbContext();

        Assert.Equal(151, await db.MemberStates.CountAsync());
        Assert.Equal(150, await db.MemberStates.Select(m => m.CountryCode).Distinct().CountAsync());

        // ISL left and rejoined: two periods
        var isl = await db.MemberStates.Where(m => m.CountryCode == "ISL").OrderBy(m => m.MemberFrom).ToListAsync();
        Assert.Equal(2, isl.Count);
        Assert.Equal(new DateOnly(2019, 12, 31), isl[0].MemberTo);
        Assert.Equal(new DateOnly(2022, 1, 1), isl[1].MemberFrom);
        Assert.Null(isl[1].MemberTo);
    }

    [Fact]
    public async Task Seed_is_idempotent()
    {
        await using var db = _factory.NewDbContext();

        DbSeeder.Seed(db, Path.Combine(AppContext.BaseDirectory, "Seed", "member_states.csv"),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        Assert.Equal(3, await db.Users.CountAsync());
        Assert.Equal(151, await db.MemberStates.CountAsync());
    }

    // --- CHECK constraints ---

    [Fact]
    public async Task Ballot_outside_YNAX_is_rejected()
    {
        await using var db = _factory.NewDbContext();
        var vote = await CreateVote(db);

        db.CountryVotes.Add(new CountryVote { VoteId = vote.Id, CountryCode = "BRA", Ballot = "Q", SourceImportId = vote.SourceImportId });

        await AssertViolates(() => db.SaveChangesAsync(), PostgresErrorCodes.CheckViolation, "ck_country_votes_ballot");
    }

    [Fact]
    public async Task Weight_outside_1_to_5_is_rejected()
    {
        await using var db = _factory.NewDbContext();
        var vote = await CreateVote(db);

        db.VotePositions.Add(new VotePosition { VoteId = vote.Id, Stance = Stance.For, Weight = 7, UpdatedBy = "lena", UpdatedAt = DateTime.UtcNow });

        await AssertViolates(() => db.SaveChangesAsync(), PostgresErrorCodes.CheckViolation, "ck_vote_positions_weight");
    }

    // --- append-only triggers ---

    [Fact]
    public async Task Audit_events_cannot_be_updated_or_deleted()
    {
        await using var db = _factory.NewDbContext();
        var audit = new AuditEvent { EventType = "Test", NewValue = "Y", Actor = "dana", At = DateTime.UtcNow };
        db.AuditEvents.Add(audit);
        await db.SaveChangesAsync();

        await AssertRaises(() => db.Database.ExecuteSqlAsync($"UPDATE audit_events SET new_value = 'N' WHERE id = {audit.Id}"));
        await AssertRaises(() => db.Database.ExecuteSqlAsync($"DELETE FROM audit_events WHERE id = {audit.Id}"));
    }

    [Fact]
    public async Task Snapshot_status_can_change_but_definition_cannot()
    {
        await using var db = _factory.NewDbContext();
        var snapshot = new Snapshot { Name = "Q1", Status = SnapshotStatus.Queued, CreatedBy = "dana", CreatedAt = DateTime.UtcNow };
        db.Snapshots.Add(snapshot);
        await db.SaveChangesAsync();

        // job progress is allowed
        await db.Database.ExecuteSqlAsync($"UPDATE snapshots SET status = 'Completed' WHERE id = {snapshot.Id}");

        await AssertRaises(() => db.Database.ExecuteSqlAsync($"UPDATE snapshots SET name = 'other' WHERE id = {snapshot.Id}"));
        await AssertRaises(() => db.Database.ExecuteSqlAsync($"UPDATE snapshots SET rules = '{{\"x\":1}}' WHERE id = {snapshot.Id}"));
        await AssertRaises(() => db.Database.ExecuteSqlAsync($"DELETE FROM snapshots WHERE id = {snapshot.Id}"));
    }

    // --- one pending conflict per proposal (ING-12) ---

    [Theory]
    [InlineData("BRA", ConflictField.Ballot, "A")]
    [InlineData(null, ConflictField.Title, "New title")]   // vote details: country is NULL
    public async Task Same_proposal_cannot_open_a_second_pending_conflict(string? country, ConflictField field, string proposed)
    {
        await using var db = _factory.NewDbContext();
        var vote = await CreateVote(db);
        db.Conflicts.Add(NewConflict(vote, country, field, proposed));
        await db.SaveChangesAsync();

        db.Conflicts.Add(NewConflict(vote, country, field, proposed));

        await AssertViolates(() => db.SaveChangesAsync(), PostgresErrorCodes.UniqueViolation, "ux_conflicts_one_pending_per_proposal");
    }

    [Fact]
    public async Task Same_proposal_can_be_pending_again_after_a_decision()
    {
        await using var db = _factory.NewDbContext();
        var vote = await CreateVote(db);
        var first = NewConflict(vote, "BRA", ConflictField.Ballot, "A");
        first.Status = ConflictStatus.Rejected;
        first.DecidedBy = "dana";
        first.DecidedAt = DateTime.UtcNow;
        db.Conflicts.Add(first);

        db.Conflicts.Add(NewConflict(vote, "BRA", ConflictField.Ballot, "A"));

        await db.SaveChangesAsync();  // no exception: the index only covers Pending rows
    }

    [Fact]
    public async Task Decided_conflict_must_record_who_and_when()
    {
        await using var db = _factory.NewDbContext();
        var vote = await CreateVote(db);
        var conflict = NewConflict(vote, "BRA", ConflictField.Ballot, "A");
        conflict.Status = ConflictStatus.Approved;   // but no DecidedBy / DecidedAt
        db.Conflicts.Add(conflict);

        await AssertViolates(() => db.SaveChangesAsync(), PostgresErrorCodes.CheckViolation, "ck_conflicts_decision");
    }

    // --- helpers ---

    private static async Task<Vote> CreateVote(MeridianDbContext db)
    {
        var import = new Import
        {
            FileName = "test.csv", FileSha256 = "x", StoragePath = "x",
            UploadedBy = "dana", UploadedAt = DateTime.UtcNow, Status = ImportStatus.Completed,
        };
        db.Imports.Add(import);
        await db.SaveChangesAsync();

        var vote = new Vote
        {
            AssemblyVoteId = $"TEST/{Guid.NewGuid():N}", Session = 79,
            VoteDate = new DateOnly(2024, 1, 12), Title = "Test vote", SourceImportId = import.Id,
        };
        db.Votes.Add(vote);
        await db.SaveChangesAsync();
        return vote;
    }

    private static Conflict NewConflict(Vote vote, string? country, ConflictField field, string proposed) => new()
    {
        VoteId = vote.Id, CountryCode = country, Field = field,
        ApprovedValue = "old", ApprovedSourceImportId = vote.SourceImportId,
        ProposedValue = proposed, ProposedImportId = vote.SourceImportId,
        Status = ConflictStatus.Pending, CreatedAt = DateTime.UtcNow,
    };

    private static async Task AssertViolates(Func<Task> save, string sqlState, string constraint)
    {
        var ex = await Assert.ThrowsAsync<DbUpdateException>(save);
        var pg = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal(sqlState, pg.SqlState);
        Assert.Equal(constraint, pg.ConstraintName);
    }

    // our triggers use RAISE EXCEPTION -> SQLSTATE P0001
    private static async Task AssertRaises(Func<Task> sql)
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(sql);
        Assert.Equal(PostgresErrorCodes.RaiseException, ex.SqlState);
    }
}
