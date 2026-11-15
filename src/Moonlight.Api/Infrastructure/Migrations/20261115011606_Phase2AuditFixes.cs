using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Moonlight.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Phase2AuditFixes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_appointments_family_members_family_member_id",
                table: "appointments");

            migrationBuilder.DropForeignKey(
                name: "fk_patient_medical_infos_family_members_family_member_id",
                table: "patient_medical_infos");

            migrationBuilder.DropForeignKey(
                name: "fk_patient_record_entries_family_members_family_member_id",
                table: "patient_record_entries");

            migrationBuilder.DropColumn(
                name: "attachment_url",
                table: "patient_record_entries");

            migrationBuilder.AddColumn<string>(
                name: "attachment_content_type",
                table: "patient_record_entries",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "attachment_file",
                table: "patient_record_entries",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "deleted_at",
                table: "family_members",
                type: "timestamp with time zone",
                nullable: true);

            // Keep only the newest pending checkout of an appointment before the unique index is created.
            migrationBuilder.Sql(
                """
                UPDATE payments SET status = 'Cancelled'
                WHERE status = 'Pending' AND id NOT IN (
                    SELECT DISTINCT ON (appointment_id) id FROM payments WHERE status = 'Pending' ORDER BY appointment_id, created_at DESC, id);
                """);

            migrationBuilder.CreateIndex(
                name: "ux_payments_live_checkout",
                table: "payments",
                column: "appointment_id",
                unique: true,
                filter: "status = 'Pending'");

            migrationBuilder.AddForeignKey(
                name: "fk_appointments_family_members_family_member_id",
                table: "appointments",
                column: "family_member_id",
                principalTable: "family_members",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_patient_medical_infos_family_members_family_member_id",
                table: "patient_medical_infos",
                column: "family_member_id",
                principalTable: "family_members",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_patient_record_entries_family_members_family_member_id",
                table: "patient_record_entries",
                column: "family_member_id",
                principalTable: "family_members",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_appointments_family_members_family_member_id",
                table: "appointments");

            migrationBuilder.DropForeignKey(
                name: "fk_patient_medical_infos_family_members_family_member_id",
                table: "patient_medical_infos");

            migrationBuilder.DropForeignKey(
                name: "fk_patient_record_entries_family_members_family_member_id",
                table: "patient_record_entries");

            migrationBuilder.DropIndex(
                name: "ux_payments_live_checkout",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "attachment_content_type",
                table: "patient_record_entries");

            migrationBuilder.DropColumn(
                name: "attachment_file",
                table: "patient_record_entries");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "family_members");

            migrationBuilder.AddColumn<string>(
                name: "attachment_url",
                table: "patient_record_entries",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddForeignKey(
                name: "fk_appointments_family_members_family_member_id",
                table: "appointments",
                column: "family_member_id",
                principalTable: "family_members",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_patient_medical_infos_family_members_family_member_id",
                table: "patient_medical_infos",
                column: "family_member_id",
                principalTable: "family_members",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_patient_record_entries_family_members_family_member_id",
                table: "patient_record_entries",
                column: "family_member_id",
                principalTable: "family_members",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
