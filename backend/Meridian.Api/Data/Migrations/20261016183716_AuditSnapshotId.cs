using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Meridian.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AuditSnapshotId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "snapshot_id",
                table: "audit_events",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_snapshot_id",
                table: "audit_events",
                column: "snapshot_id");

            migrationBuilder.AddForeignKey(
                name: "fk_audit_events_snapshots_snapshot_id",
                table: "audit_events",
                column: "snapshot_id",
                principalTable: "snapshots",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_audit_events_snapshots_snapshot_id",
                table: "audit_events");

            migrationBuilder.DropIndex(
                name: "ix_audit_events_snapshot_id",
                table: "audit_events");

            migrationBuilder.DropColumn(
                name: "snapshot_id",
                table: "audit_events");
        }
    }
}
