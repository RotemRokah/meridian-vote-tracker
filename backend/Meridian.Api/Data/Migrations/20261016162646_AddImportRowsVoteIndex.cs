using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Meridian.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddImportRowsVoteIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_import_rows_assembly_vote_id_country_code",
                table: "import_rows",
                columns: new[] { "assembly_vote_id", "country_code" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_import_rows_assembly_vote_id_country_code",
                table: "import_rows");
        }
    }
}
