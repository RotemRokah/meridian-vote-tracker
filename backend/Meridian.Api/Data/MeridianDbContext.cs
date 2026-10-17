using Meridian.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Meridian.Api.Data;

public class MeridianDbContext : DbContext
{
    public MeridianDbContext(DbContextOptions<MeridianDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<MemberState> MemberStates => Set<MemberState>();
    public DbSet<Import> Imports => Set<Import>();
    public DbSet<ImportRow> ImportRows => Set<ImportRow>();
    public DbSet<Vote> Votes => Set<Vote>();
    public DbSet<CountryVote> CountryVotes => Set<CountryVote>();
    public DbSet<Conflict> Conflicts => Set<Conflict>();
    public DbSet<VotePosition> VotePositions => Set<VotePosition>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<Snapshot> Snapshots => Set<Snapshot>();
    public DbSet<SnapshotInput> SnapshotInputs => Set<SnapshotInput>();
    public DbSet<SnapshotScore> SnapshotScores => Set<SnapshotScore>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Nothing in this system is ever deleted, so no cascading deletes anywhere.
        // Foreign keys have no navigation properties on purpose: queries stay explicit.

        b.Entity<User>(e =>
        {
            e.Property(x => x.Id).HasMaxLength(50);
            EnumAsText(e, x => x.Role, "role");
        });

        b.Entity<MemberState>(e =>
        {
            e.HasKey(x => new { x.CountryCode, x.MemberFrom });
            e.Property(x => x.CountryCode).HasMaxLength(3);
            e.ToTable(t => t.HasCheckConstraint("ck_member_states_period",
                "member_to IS NULL OR member_to >= member_from"));
        });

        b.Entity<Import>(e =>
        {
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UploadedBy).OnDelete(DeleteBehavior.Restrict);
            EnumAsText(e, x => x.Status, "status");
            EnumAsText(e, x => x.Stage, "stage");
            e.Property(x => x.Summary).HasColumnType("jsonb");
            // the worker picks the oldest queued import
            e.HasIndex(x => new { x.Status, x.UploadedAt });
        });

        b.Entity<ImportRow>(e =>
        {
            e.HasOne<Import>().WithMany().HasForeignKey(x => x.ImportId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Conflict>().WithMany().HasForeignKey(x => x.ConflictId).OnDelete(DeleteBehavior.Restrict);
            EnumAsText(e, x => x.Classification, "classification");
            e.HasIndex(x => new { x.ImportId, x.LineNo }).IsUnique();
            e.HasIndex(x => new { x.ImportId, x.Classification });
            // vote timeline: every file row that touched a vote (or one country in it)
            e.HasIndex(x => new { x.AssemblyVoteId, x.CountryCode });
        });

        b.Entity<Vote>(e =>
        {
            e.HasIndex(x => x.AssemblyVoteId).IsUnique();
            e.HasOne<Import>().WithMany().HasForeignKey(x => x.SourceImportId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<CountryVote>(e =>
        {
            e.HasKey(x => new { x.VoteId, x.CountryCode });
            e.Property(x => x.CountryCode).HasMaxLength(3);
            e.Property(x => x.Ballot).HasMaxLength(1);
            e.HasOne<Vote>().WithMany().HasForeignKey(x => x.VoteId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Import>().WithMany().HasForeignKey(x => x.SourceImportId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.ApprovedBy).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("ck_country_votes_ballot", "ballot IN ('Y','N','A','X')"));
        });

        b.Entity<Conflict>(e =>
        {
            e.Property(x => x.CountryCode).HasMaxLength(3);
            e.HasOne<Vote>().WithMany().HasForeignKey(x => x.VoteId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Import>().WithMany().HasForeignKey(x => x.ApprovedSourceImportId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Import>().WithMany().HasForeignKey(x => x.ProposedImportId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.DecidedBy).OnDelete(DeleteBehavior.Restrict);
            EnumAsText(e, x => x.Field, "field");
            EnumAsText(e, x => x.Status, "status");
            e.HasIndex(x => new { x.Status, x.VoteId });
            e.ToTable(t =>
            {
                // a ballot conflict has a country, a vote-details conflict doesn't
                t.HasCheckConstraint("ck_conflicts_country_matches_field",
                    "(field = 'Ballot') = (country_code IS NOT NULL)");
                // pending = undecided; decided = who and when are known
                t.HasCheckConstraint("ck_conflicts_decision",
                    "(status = 'Pending') = (decided_by IS NULL AND decided_at IS NULL)");
            });
            // Partial unique index "one pending conflict per proposal" is in the migration (raw SQL).
        });

        b.Entity<VotePosition>(e =>
        {
            e.HasKey(x => x.VoteId);
            e.HasOne<Vote>().WithOne().HasForeignKey<VotePosition>(x => x.VoteId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UpdatedBy).OnDelete(DeleteBehavior.Restrict);
            EnumAsText(e, x => x.Stance, "stance");
            e.ToTable(t => t.HasCheckConstraint("ck_vote_positions_weight", "weight BETWEEN 1 AND 5"));
            // maps to the system column xmin: an UPDATE whose version doesn't match changes 0 rows
            e.Property(x => x.Version).IsRowVersion();
        });

        b.Entity<AuditEvent>(e =>
        {
            e.HasOne<Vote>().WithMany().HasForeignKey(x => x.VoteId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Import>().WithMany().HasForeignKey(x => x.ImportId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Conflict>().WithMany().HasForeignKey(x => x.ConflictId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.Actor).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Snapshot>().WithMany().HasForeignKey(x => x.SnapshotId).OnDelete(DeleteBehavior.Restrict);
            // vote timeline (HIS-04) reads events of one vote in time order
            e.HasIndex(x => new { x.VoteId, x.At });
        });

        b.Entity<Snapshot>(e =>
        {
            e.Property(x => x.Filter).HasColumnType("jsonb");
            e.Property(x => x.Rules).HasColumnType("jsonb");
            e.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.PublishedBy).OnDelete(DeleteBehavior.Restrict);
            EnumAsText(e, x => x.Status, "status");
            // SNP-05: at most one published snapshot per quarter
            e.HasIndex(x => x.Quarter).IsUnique().HasFilter("is_published");
            e.ToTable(t => t.HasCheckConstraint("ck_snapshots_published",
                "NOT is_published OR (quarter IS NOT NULL AND published_by IS NOT NULL AND published_at IS NOT NULL)"));
        });

        b.Entity<SnapshotInput>(e =>
        {
            e.HasKey(x => new { x.SnapshotId, x.VoteId, x.CountryCode });
            e.Property(x => x.CountryCode).HasMaxLength(3);
            e.Property(x => x.Ballot).HasMaxLength(1);
            // No foreign keys here, on purpose (step 9): a full-history snapshot inserts ~1M rows and the
            // two per-row FK checks took 120 of 128 seconds. The rows are copied in one statement from
            // country_votes (already FK-checked), and a trigger forbids any later change, so the FKs added
            // no protection. Every business table keeps its FKs.
            EnumAsText(e, x => x.Stance, "stance");
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_snapshot_inputs_ballot", "ballot IN ('Y','N','A','X')");
                t.HasCheckConstraint("ck_snapshot_inputs_weight", "weight BETWEEN 1 AND 5");
            });
        });

        b.Entity<SnapshotScore>(e =>
        {
            e.HasKey(x => new { x.SnapshotId, x.CountryCode });
            e.Property(x => x.CountryCode).HasMaxLength(3);
            e.Property(x => x.Score).HasPrecision(5, 2);
            e.HasOne<Snapshot>().WithMany().HasForeignKey(x => x.SnapshotId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("ck_snapshot_scores_score", "score IS NULL OR score BETWEEN 0 AND 100"));
        });
    }

    // Store an enum as its name (e.g. 'Pending') and only allow known names.
    private static void EnumAsText<TEntity, TEnum>(EntityTypeBuilder<TEntity> e,
        System.Linq.Expressions.Expression<Func<TEntity, TEnum>> property, string column)
        where TEntity : class
    {
        e.Property(property).HasConversion<string>().HasMaxLength(20);

        var enumType = Nullable.GetUnderlyingType(typeof(TEnum)) ?? typeof(TEnum);
        var allowed = string.Join(",", Enum.GetNames(enumType).Select(n => $"'{n}'"));
        var table = e.Metadata.GetTableName();
        e.ToTable(t => t.HasCheckConstraint($"ck_{table}_{column}", $"{column} IN ({allowed})"));
    }
}
