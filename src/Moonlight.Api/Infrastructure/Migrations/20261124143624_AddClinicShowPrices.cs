using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Moonlight.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddClinicShowPrices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "show_prices",
                table: "clinics",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "show_prices",
                table: "clinics");
        }
    }
}
