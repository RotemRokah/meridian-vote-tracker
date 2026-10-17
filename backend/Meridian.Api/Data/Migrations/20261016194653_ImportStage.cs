using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Meridian.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ImportStage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "stage",
                table: "imports",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_imports_stage",
                table: "imports",
                sql: "stage IN ('Reading','Classifying')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_imports_stage",
                table: "imports");

            migrationBuilder.DropColumn(
                name: "stage",
                table: "imports");
        }
    }
}
