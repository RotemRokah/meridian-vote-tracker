using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Meridian.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "member_states",
                columns: table => new
                {
                    country_code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    member_from = table.Column<DateOnly>(type: "date", nullable: false),
                    member_to = table.Column<DateOnly>(type: "date", nullable: true),
                    country_name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_member_states", x => new { x.country_code, x.member_from });
                    table.CheckConstraint("ck_member_states_period", "member_to IS NULL OR member_to >= member_from");
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                    table.CheckConstraint("ck_users_role", "role IN ('Analyst','Admin')");
                });

            migrationBuilder.CreateTable(
                name: "imports",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    file_sha256 = table.Column<string>(type: "text", nullable: false),
                    storage_path = table.Column<string>(type: "text", nullable: false),
                    uploaded_by = table.Column<string>(type: "character varying(50)", nullable: false),
                    uploaded_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    total_rows = table.Column<int>(type: "integer", nullable: true),
                    processed_rows = table.Column<int>(type: "integer", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    summary = table.Column<string>(type: "jsonb", nullable: true),
                    correlation_id = table.Column<string>(type: "text", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_imports", x => x.id);
                    table.CheckConstraint("ck_imports_status", "status IN ('Queued','Running','Completed','Failed')");
                    table.ForeignKey(
                        name: "fk_imports_users_uploaded_by",
                        column: x => x.uploaded_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "snapshots",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    filter = table.Column<string>(type: "jsonb", nullable: false),
                    rules = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_by = table.Column<string>(type: "character varying(50)", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    quarter = table.Column<string>(type: "text", nullable: true),
                    is_published = table.Column<bool>(type: "boolean", nullable: false),
                    published_by = table.Column<string>(type: "character varying(50)", nullable: true),
                    published_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_snapshots", x => x.id);
                    table.CheckConstraint("ck_snapshots_published", "NOT is_published OR (quarter IS NOT NULL AND published_by IS NOT NULL AND published_at IS NOT NULL)");
                    table.CheckConstraint("ck_snapshots_status", "status IN ('Queued','Running','Completed','Failed')");
                    table.ForeignKey(
                        name: "fk_snapshots_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_snapshots_users_published_by",
                        column: x => x.published_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "votes",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    assembly_vote_id = table.Column<string>(type: "text", nullable: false),
                    session = table.Column<int>(type: "integer", nullable: false),
                    vote_date = table.Column<DateOnly>(type: "date", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    source_import_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_votes", x => x.id);
                    table.ForeignKey(
                        name: "fk_votes_imports_source_import_id",
                        column: x => x.source_import_id,
                        principalTable: "imports",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "snapshot_scores",
                columns: table => new
                {
                    snapshot_id = table.Column<long>(type: "bigint", nullable: false),
                    country_code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    score = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    rank = table.Column<int>(type: "integer", nullable: true),
                    matched_weight = table.Column<int>(type: "integer", nullable: false),
                    counted_weight = table.Column<int>(type: "integer", nullable: false),
                    counted_votes = table.Column<int>(type: "integer", nullable: false),
                    abstain_count = table.Column<int>(type: "integer", nullable: false),
                    absent_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_snapshot_scores", x => new { x.snapshot_id, x.country_code });
                    table.CheckConstraint("ck_snapshot_scores_score", "score IS NULL OR score BETWEEN 0 AND 100");
                    table.ForeignKey(
                        name: "fk_snapshot_scores_snapshots_snapshot_id",
                        column: x => x.snapshot_id,
                        principalTable: "snapshots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "conflicts",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    vote_id = table.Column<long>(type: "bigint", nullable: false),
                    country_code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    field = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    approved_value = table.Column<string>(type: "text", nullable: false),
                    approved_source_import_id = table.Column<long>(type: "bigint", nullable: false),
                    proposed_value = table.Column<string>(type: "text", nullable: false),
                    proposed_import_id = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    decided_by = table.Column<string>(type: "character varying(50)", nullable: true),
                    decided_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    comment = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_conflicts", x => x.id);
                    table.CheckConstraint("ck_conflicts_country_matches_field", "(field = 'Ballot') = (country_code IS NOT NULL)");
                    table.CheckConstraint("ck_conflicts_decision", "(status = 'Pending') = (decided_by IS NULL AND decided_at IS NULL)");
                    table.CheckConstraint("ck_conflicts_field", "field IN ('Ballot','Title','Date','Session')");
                    table.CheckConstraint("ck_conflicts_status", "status IN ('Pending','Approved','Rejected')");
                    table.ForeignKey(
                        name: "fk_conflicts_imports_approved_source_import_id",
                        column: x => x.approved_source_import_id,
                        principalTable: "imports",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_conflicts_imports_proposed_import_id",
                        column: x => x.proposed_import_id,
                        principalTable: "imports",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_conflicts_users_decided_by",
                        column: x => x.decided_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_conflicts_votes_vote_id",
                        column: x => x.vote_id,
                        principalTable: "votes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "country_votes",
                columns: table => new
                {
                    vote_id = table.Column<long>(type: "bigint", nullable: false),
                    country_code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    ballot = table.Column<string>(type: "character varying(1)", maxLength: 1, nullable: false),
                    source_import_id = table.Column<long>(type: "bigint", nullable: false),
                    approved_by = table.Column<string>(type: "character varying(50)", nullable: true),
                    approved_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_country_votes", x => new { x.vote_id, x.country_code });
                    table.CheckConstraint("ck_country_votes_ballot", "ballot IN ('Y','N','A','X')");
                    table.ForeignKey(
                        name: "fk_country_votes_imports_source_import_id",
                        column: x => x.source_import_id,
                        principalTable: "imports",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_country_votes_users_approved_by",
                        column: x => x.approved_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_country_votes_votes_vote_id",
                        column: x => x.vote_id,
                        principalTable: "votes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "snapshot_inputs",
                columns: table => new
                {
                    snapshot_id = table.Column<long>(type: "bigint", nullable: false),
                    vote_id = table.Column<long>(type: "bigint", nullable: false),
                    country_code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    ballot = table.Column<string>(type: "character varying(1)", maxLength: 1, nullable: false),
                    stance = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    weight = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_snapshot_inputs", x => new { x.snapshot_id, x.vote_id, x.country_code });
                    table.CheckConstraint("ck_snapshot_inputs_ballot", "ballot IN ('Y','N','A','X')");
                    table.CheckConstraint("ck_snapshot_inputs_stance", "stance IN ('For','Against')");
                    table.CheckConstraint("ck_snapshot_inputs_weight", "weight BETWEEN 1 AND 5");
                    table.ForeignKey(
                        name: "fk_snapshot_inputs_snapshots_snapshot_id",
                        column: x => x.snapshot_id,
                        principalTable: "snapshots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_snapshot_inputs_votes_vote_id",
                        column: x => x.vote_id,
                        principalTable: "votes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "vote_positions",
                columns: table => new
                {
                    vote_id = table.Column<long>(type: "bigint", nullable: false),
                    stance = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    weight = table.Column<int>(type: "integer", nullable: false),
                    updated_by = table.Column<string>(type: "character varying(50)", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vote_positions", x => x.vote_id);
                    table.CheckConstraint("ck_vote_positions_stance", "stance IN ('For','Against')");
                    table.CheckConstraint("ck_vote_positions_weight", "weight BETWEEN 1 AND 5");
                    table.ForeignKey(
                        name: "fk_vote_positions_users_updated_by",
                        column: x => x.updated_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_vote_positions_votes_vote_id",
                        column: x => x.vote_id,
                        principalTable: "votes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    vote_id = table.Column<long>(type: "bigint", nullable: true),
                    country_code = table.Column<string>(type: "text", nullable: true),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    old_value = table.Column<string>(type: "text", nullable: true),
                    new_value = table.Column<string>(type: "text", nullable: true),
                    import_id = table.Column<long>(type: "bigint", nullable: true),
                    conflict_id = table.Column<long>(type: "bigint", nullable: true),
                    actor = table.Column<string>(type: "character varying(50)", nullable: false),
                    at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    correlation_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_audit_events_conflicts_conflict_id",
                        column: x => x.conflict_id,
                        principalTable: "conflicts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_audit_events_imports_import_id",
                        column: x => x.import_id,
                        principalTable: "imports",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_audit_events_users_actor",
                        column: x => x.actor,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_audit_events_votes_vote_id",
                        column: x => x.vote_id,
                        principalTable: "votes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "import_rows",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    import_id = table.Column<long>(type: "bigint", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    raw_line = table.Column<string>(type: "text", nullable: false),
                    assembly_vote_id = table.Column<string>(type: "text", nullable: true),
                    session = table.Column<string>(type: "text", nullable: true),
                    vote_date = table.Column<string>(type: "text", nullable: true),
                    resolution_title = table.Column<string>(type: "text", nullable: true),
                    country_code = table.Column<string>(type: "text", nullable: true),
                    country_name = table.Column<string>(type: "text", nullable: true),
                    ballot = table.Column<string>(type: "text", nullable: true),
                    published_at = table.Column<string>(type: "text", nullable: true),
                    classification = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    reason = table.Column<string>(type: "text", nullable: true),
                    normalization_note = table.Column<string>(type: "text", nullable: true),
                    conflict_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_rows", x => x.id);
                    table.CheckConstraint("ck_import_rows_classification", "classification IN ('New','Unchanged','Duplicate','Invalid','Contradiction','Conflict')");
                    table.ForeignKey(
                        name: "fk_import_rows_conflicts_conflict_id",
                        column: x => x.conflict_id,
                        principalTable: "conflicts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_import_rows_imports_import_id",
                        column: x => x.import_id,
                        principalTable: "imports",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_actor",
                table: "audit_events",
                column: "actor");

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_conflict_id",
                table: "audit_events",
                column: "conflict_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_import_id",
                table: "audit_events",
                column: "import_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_vote_id_at",
                table: "audit_events",
                columns: new[] { "vote_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_conflicts_approved_source_import_id",
                table: "conflicts",
                column: "approved_source_import_id");

            migrationBuilder.CreateIndex(
                name: "ix_conflicts_decided_by",
                table: "conflicts",
                column: "decided_by");

            migrationBuilder.CreateIndex(
                name: "ix_conflicts_proposed_import_id",
                table: "conflicts",
                column: "proposed_import_id");

            migrationBuilder.CreateIndex(
                name: "ix_conflicts_status_vote_id",
                table: "conflicts",
                columns: new[] { "status", "vote_id" });

            migrationBuilder.CreateIndex(
                name: "ix_conflicts_vote_id",
                table: "conflicts",
                column: "vote_id");

            migrationBuilder.CreateIndex(
                name: "ix_country_votes_approved_by",
                table: "country_votes",
                column: "approved_by");

            migrationBuilder.CreateIndex(
                name: "ix_country_votes_source_import_id",
                table: "country_votes",
                column: "source_import_id");

            migrationBuilder.CreateIndex(
                name: "ix_import_rows_conflict_id",
                table: "import_rows",
                column: "conflict_id");

            migrationBuilder.CreateIndex(
                name: "ix_import_rows_import_id_classification",
                table: "import_rows",
                columns: new[] { "import_id", "classification" });

            migrationBuilder.CreateIndex(
                name: "ix_import_rows_import_id_line_no",
                table: "import_rows",
                columns: new[] { "import_id", "line_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_imports_status_uploaded_at",
                table: "imports",
                columns: new[] { "status", "uploaded_at" });

            migrationBuilder.CreateIndex(
                name: "ix_imports_uploaded_by",
                table: "imports",
                column: "uploaded_by");

            migrationBuilder.CreateIndex(
                name: "ix_snapshot_inputs_vote_id",
                table: "snapshot_inputs",
                column: "vote_id");

            migrationBuilder.CreateIndex(
                name: "ix_snapshots_created_by",
                table: "snapshots",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "ix_snapshots_published_by",
                table: "snapshots",
                column: "published_by");

            migrationBuilder.CreateIndex(
                name: "ix_snapshots_quarter",
                table: "snapshots",
                column: "quarter",
                unique: true,
                filter: "is_published");

            migrationBuilder.CreateIndex(
                name: "ix_vote_positions_updated_by",
                table: "vote_positions",
                column: "updated_by");

            migrationBuilder.CreateIndex(
                name: "ix_votes_assembly_vote_id",
                table: "votes",
                column: "assembly_vote_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_votes_source_import_id",
                table: "votes",
                column: "source_import_id");

            // --- Hand-written SQL: things EF can't express ---

            // ING-12: the same proposal never opens a second pending conflict.
            // coalesce: vote-details conflicts have no country, and NULLs never collide in a unique index.
            migrationBuilder.Sql(@"
                CREATE UNIQUE INDEX ux_conflicts_one_pending_per_proposal
                ON conflicts (vote_id, coalesce(country_code, ''), field, proposed_value)
                WHERE status = 'Pending';");

            // Append-only tables (HIS-02..04, SNP-04): any UPDATE or DELETE is an error.
            migrationBuilder.Sql(@"
                CREATE FUNCTION forbid_change() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION '% on % is not allowed: table is append-only', TG_OP, TG_TABLE_NAME;
                END $$;

                CREATE TRIGGER trg_audit_events_append_only BEFORE UPDATE OR DELETE ON audit_events
                    FOR EACH ROW EXECUTE FUNCTION forbid_change();
                CREATE TRIGGER trg_snapshot_inputs_append_only BEFORE UPDATE OR DELETE ON snapshot_inputs
                    FOR EACH ROW EXECUTE FUNCTION forbid_change();
                CREATE TRIGGER trg_snapshot_scores_append_only BEFORE UPDATE OR DELETE ON snapshot_scores
                    FOR EACH ROW EXECUTE FUNCTION forbid_change();");

            // Snapshots: never deleted; only job status and publish columns may change (SNP-04, SNP-05).
            migrationBuilder.Sql(@"
                CREATE FUNCTION snapshots_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'DELETE on snapshots is not allowed';
                    END IF;
                    IF (NEW.id, NEW.name, NEW.filter, NEW.rules, NEW.created_by, NEW.created_at)
                       IS DISTINCT FROM
                       (OLD.id, OLD.name, OLD.filter, OLD.rules, OLD.created_by, OLD.created_at) THEN
                        RAISE EXCEPTION 'snapshot % definition cannot change', OLD.id;
                    END IF;
                    RETURN NEW;
                END $$;

                CREATE TRIGGER trg_snapshots_guard BEFORE UPDATE OR DELETE ON snapshots
                    FOR EACH ROW EXECUTE FUNCTION snapshots_guard();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // triggers and the partial index go away with their tables; the functions don't
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_snapshots_guard ON snapshots;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_audit_events_append_only ON audit_events;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_snapshot_inputs_append_only ON snapshot_inputs;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_snapshot_scores_append_only ON snapshot_scores;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS snapshots_guard();");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS forbid_change();");

            migrationBuilder.DropTable(
                name: "audit_events");

            migrationBuilder.DropTable(
                name: "country_votes");

            migrationBuilder.DropTable(
                name: "import_rows");

            migrationBuilder.DropTable(
                name: "member_states");

            migrationBuilder.DropTable(
                name: "snapshot_inputs");

            migrationBuilder.DropTable(
                name: "snapshot_scores");

            migrationBuilder.DropTable(
                name: "vote_positions");

            migrationBuilder.DropTable(
                name: "conflicts");

            migrationBuilder.DropTable(
                name: "snapshots");

            migrationBuilder.DropTable(
                name: "votes");

            migrationBuilder.DropTable(
                name: "imports");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
