using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Meridian.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropSnapshotInputsForeignKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_snapshot_inputs_snapshots_snapshot_id",
                table: "snapshot_inputs");

            migrationBuilder.DropForeignKey(
                name: "fk_snapshot_inputs_votes_vote_id",
                table: "snapshot_inputs");

            migrationBuilder.DropIndex(
                name: "ix_snapshot_inputs_vote_id",
                table: "snapshot_inputs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_snapshot_inputs_vote_id",
                table: "snapshot_inputs",
                column: "vote_id");

            migrationBuilder.AddForeignKey(
                name: "fk_snapshot_inputs_snapshots_snapshot_id",
                table: "snapshot_inputs",
                column: "snapshot_id",
                principalTable: "snapshots",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_snapshot_inputs_votes_vote_id",
                table: "snapshot_inputs",
                column: "vote_id",
                principalTable: "votes",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
