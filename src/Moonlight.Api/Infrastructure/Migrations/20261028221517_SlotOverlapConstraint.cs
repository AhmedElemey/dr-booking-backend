using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Moonlight.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SlotOverlapConstraint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A doctor's slots may never overlap in time (half-open ranges, so back-to-back slots are fine).
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS btree_gist;");
            migrationBuilder.Sql(
                "ALTER TABLE slots ADD CONSTRAINT no_overlapping_slots " +
                "EXCLUDE USING gist (doctor_id WITH =, tstzrange(starts_at, ends_at) WITH &&);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE slots DROP CONSTRAINT no_overlapping_slots;");
        }
    }
}
