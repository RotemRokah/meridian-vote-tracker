using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Meridian.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class PdfExtractions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pdf_extractions",
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
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true),
                    sheet = table.Column<string>(type: "jsonb", nullable: true),
                    correlation_id = table.Column<string>(type: "text", nullable: true),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    decided_by = table.Column<string>(type: "character varying(50)", nullable: true),
                    decided_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    import_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pdf_extractions", x => x.id);
                    table.CheckConstraint("ck_pdf_extractions_status", "status IN ('Queued','Running','ReadyForReview','Failed','Approved','Rejected')");
                    table.ForeignKey(
                        name: "fk_pdf_extractions_imports_import_id",
                        column: x => x.import_id,
                        principalTable: "imports",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pdf_extractions_users_decided_by",
                        column: x => x.decided_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pdf_extractions_users_uploaded_by",
                        column: x => x.uploaded_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_pdf_extractions_decided_by",
                table: "pdf_extractions",
                column: "decided_by");

            migrationBuilder.CreateIndex(
                name: "ix_pdf_extractions_import_id",
                table: "pdf_extractions",
                column: "import_id");

            migrationBuilder.CreateIndex(
                name: "ix_pdf_extractions_status_uploaded_at",
                table: "pdf_extractions",
                columns: new[] { "status", "uploaded_at" });

            migrationBuilder.CreateIndex(
                name: "ix_pdf_extractions_uploaded_by",
                table: "pdf_extractions",
                column: "uploaded_by");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pdf_extractions");
        }
    }
}
