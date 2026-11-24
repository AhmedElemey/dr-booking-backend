using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Moonlight.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddClinicFeatureOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "deposit_amount_egp",
                table: "clinics",
                type: "numeric",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "clinic_feature_overrides",
                columns: table => new
                {
                    clinic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clinic_feature_overrides", x => new { x.clinic_id, x.key });
                    table.ForeignKey(
                        name: "fk_clinic_feature_overrides_clinics_clinic_id",
                        column: x => x.clinic_id,
                        principalTable: "clinics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "clinic_feature_overrides");

            migrationBuilder.DropColumn(
                name: "deposit_amount_egp",
                table: "clinics");
        }
    }
}
