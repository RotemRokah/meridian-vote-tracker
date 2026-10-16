using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Meridian.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class VotePositionRowVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Nothing to create: xmin is a PostgreSQL system column that every table already has.
            // This migration only records in EF's model that vote_positions.Version maps to it.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // nothing to drop (see Up)
        }
    }
}
