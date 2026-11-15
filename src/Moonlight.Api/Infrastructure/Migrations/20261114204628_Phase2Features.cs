using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Moonlight.Api.Domain;

#nullable disable

namespace Moonlight.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Phase2Features : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "marketing_opt_in",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "whats_app_opt_in",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "offers_video",
                table: "doctors",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "completed_at",
                table: "appointments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "coupon_id",
                table: "appointments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "discount_egp",
                table: "appointments",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<Guid>(
                name: "family_member_id",
                table: "appointments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "insurance_member_number",
                table: "appointments",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "insurance_note",
                table: "appointments",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "insurance_provider_id",
                table: "appointments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "insurance_status",
                table: "appointments",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "mode",
                table: "appointments",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "InPerson");

            migrationBuilder.AddColumn<decimal>(
                name: "price_egp",
                table: "appointments",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "review_prompt_sent_at",
                table: "appointments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "video_host_url",
                table: "appointments",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "video_patient_url",
                table: "appointments",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "video_room_name",
                table: "appointments",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "clinic_holidays",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    clinic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    name_ar = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    name_en = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clinic_holidays", x => x.id);
                    table.ForeignKey(
                        name: "fk_clinic_holidays_clinics_clinic_id",
                        column: x => x.clinic_id,
                        principalTable: "clinics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "coupons",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    clinic_id = table.Column<Guid>(type: "uuid", nullable: true),
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    title_ar = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    title_en = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description_ar = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    description_en = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    discount_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    value = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    max_discount_egp = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false),
                    valid_to = table.Column<DateOnly>(type: "date", nullable: false),
                    max_redemptions = table.Column<int>(type: "integer", nullable: true),
                    per_patient_limit = table.Column<int>(type: "integer", nullable: false),
                    doctor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    service_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_public = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    redemptions_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_coupons", x => x.id);
                    table.ForeignKey(
                        name: "fk_coupons_clinics_clinic_id",
                        column: x => x.clinic_id,
                        principalTable: "clinics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_coupons_doctors_doctor_id",
                        column: x => x.doctor_id,
                        principalTable: "doctors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_coupons_services_service_id",
                        column: x => x.service_id,
                        principalTable: "services",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "family_members",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    full_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    relation = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    date_of_birth = table.Column<DateOnly>(type: "date", nullable: true),
                    gender = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_family_members", x => x.id);
                    table.ForeignKey(
                        name: "fk_family_members_users_owner_user_id",
                        column: x => x.owner_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "insurance_providers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name_ar = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    name_en = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_insurance_providers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outbound_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    to = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    clinic_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    body = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    provider_ref = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbound_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    appointment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount_egp = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    method = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    provider = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    provider_ref = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    provider_transaction_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    return_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    paid_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    refunded_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payments", x => x.id);
                    table.ForeignKey(
                        name: "fk_payments_appointments_appointment_id",
                        column: x => x.appointment_id,
                        principalTable: "appointments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "recall_campaigns",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    clinic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    message_ar = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    message_en = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    months_since_last_visit = table.Column<int>(type: "integer", nullable: false),
                    doctor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    service_id = table.Column<Guid>(type: "uuid", nullable: true),
                    mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    last_run_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recall_campaigns", x => x.id);
                    table.ForeignKey(
                        name: "fk_recall_campaigns_clinics_clinic_id",
                        column: x => x.clinic_id,
                        principalTable: "clinics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_recall_campaigns_doctors_doctor_id",
                        column: x => x.doctor_id,
                        principalTable: "doctors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_recall_campaigns_services_service_id",
                        column: x => x.service_id,
                        principalTable: "services",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "reviews",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    appointment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    doctor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rating = table.Column<int>(type: "integer", nullable: false),
                    comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reply = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    replied_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reviews", x => x.id);
                    table.CheckConstraint("ck_reviews_rating", "rating BETWEEN 1 AND 5");
                    table.ForeignKey(
                        name: "fk_reviews_appointments_appointment_id",
                        column: x => x.appointment_id,
                        principalTable: "appointments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_reviews_doctors_doctor_id",
                        column: x => x.doctor_id,
                        principalTable: "doctors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_reviews_users_patient_user_id",
                        column: x => x.patient_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "working_hours_templates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    clinic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    slot_minutes = table.Column<int>(type: "integer", nullable: false),
                    rules = table.Column<List<WorkingHoursRule>>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_working_hours_templates", x => x.id);
                    table.ForeignKey(
                        name: "fk_working_hours_templates_clinics_clinic_id",
                        column: x => x.clinic_id,
                        principalTable: "clinics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "coupon_redemptions",
                columns: table => new
                {
                    coupon_id = table.Column<Guid>(type: "uuid", nullable: false),
                    appointment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    discount_egp = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_coupon_redemptions", x => new { x.coupon_id, x.appointment_id });
                    table.ForeignKey(
                        name: "fk_coupon_redemptions_appointments_appointment_id",
                        column: x => x.appointment_id,
                        principalTable: "appointments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_coupon_redemptions_coupons_coupon_id",
                        column: x => x.coupon_id,
                        principalTable: "coupons",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_coupon_redemptions_users_patient_user_id",
                        column: x => x.patient_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "patient_medical_infos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_member_id = table.Column<Guid>(type: "uuid", nullable: true),
                    blood_type = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    allergies = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    chronic_conditions = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    medications = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_patient_medical_infos", x => x.id);
                    table.ForeignKey(
                        name: "fk_patient_medical_infos_family_members_family_member_id",
                        column: x => x.family_member_id,
                        principalTable: "family_members",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_patient_medical_infos_users_patient_user_id",
                        column: x => x.patient_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "patient_record_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_member_id = table.Column<Guid>(type: "uuid", nullable: true),
                    appointment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    doctor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    clinic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    body = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    attachment_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    visible_to_patient = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_patient_record_entries", x => x.id);
                    table.ForeignKey(
                        name: "fk_patient_record_entries_appointments_appointment_id",
                        column: x => x.appointment_id,
                        principalTable: "appointments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_patient_record_entries_doctors_doctor_id",
                        column: x => x.doctor_id,
                        principalTable: "doctors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_patient_record_entries_family_members_family_member_id",
                        column: x => x.family_member_id,
                        principalTable: "family_members",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_patient_record_entries_users_author_user_id",
                        column: x => x.author_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_patient_record_entries_users_patient_user_id",
                        column: x => x.patient_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "waiting_list_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_member_id = table.Column<Guid>(type: "uuid", nullable: true),
                    doctor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    service_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    notified_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_waiting_list_entries", x => x.id);
                    table.ForeignKey(
                        name: "fk_waiting_list_entries_doctors_doctor_id",
                        column: x => x.doctor_id,
                        principalTable: "doctors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_waiting_list_entries_family_members_family_member_id",
                        column: x => x.family_member_id,
                        principalTable: "family_members",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_waiting_list_entries_users_patient_user_id",
                        column: x => x.patient_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "doctor_insurance_providers",
                columns: table => new
                {
                    doctor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    insurance_provider_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_doctor_insurance_providers", x => new { x.doctor_id, x.insurance_provider_id });
                    table.ForeignKey(
                        name: "fk_doctor_insurance_providers_doctors_doctor_id",
                        column: x => x.doctor_id,
                        principalTable: "doctors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_doctor_insurance_providers_insurance_providers_insurance_pr",
                        column: x => x.insurance_provider_id,
                        principalTable: "insurance_providers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "recall_sends",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    sent_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recall_sends", x => x.id);
                    table.ForeignKey(
                        name: "fk_recall_sends_recall_campaigns_campaign_id",
                        column: x => x.campaign_id,
                        principalTable: "recall_campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_recall_sends_users_patient_user_id",
                        column: x => x.patient_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_appointments_family_member_id",
                table: "appointments",
                column: "family_member_id");

            migrationBuilder.CreateIndex(
                name: "ix_appointments_insurance_provider_id",
                table: "appointments",
                column: "insurance_provider_id");

            migrationBuilder.CreateIndex(
                name: "ix_appointments_review_prompt_due",
                table: "appointments",
                column: "completed_at",
                filter: "status = 'Completed' AND review_prompt_sent_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_clinic_holidays_clinic_id_date",
                table: "clinic_holidays",
                columns: new[] { "clinic_id", "date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_coupon_redemptions_appointment_id",
                table: "coupon_redemptions",
                column: "appointment_id");

            migrationBuilder.CreateIndex(
                name: "ix_coupon_redemptions_coupon_id_patient_user_id",
                table: "coupon_redemptions",
                columns: new[] { "coupon_id", "patient_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_coupon_redemptions_patient_user_id",
                table: "coupon_redemptions",
                column: "patient_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_coupons_clinic_id",
                table: "coupons",
                column: "clinic_id");

            migrationBuilder.CreateIndex(
                name: "ix_coupons_code",
                table: "coupons",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_coupons_doctor_id",
                table: "coupons",
                column: "doctor_id");

            migrationBuilder.CreateIndex(
                name: "ix_coupons_service_id",
                table: "coupons",
                column: "service_id");

            migrationBuilder.CreateIndex(
                name: "ix_doctor_insurance_providers_insurance_provider_id",
                table: "doctor_insurance_providers",
                column: "insurance_provider_id");

            migrationBuilder.CreateIndex(
                name: "ix_family_members_owner_user_id",
                table: "family_members",
                column: "owner_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_outbound_messages_clinic_id",
                table: "outbound_messages",
                column: "clinic_id");

            migrationBuilder.CreateIndex(
                name: "ix_outbound_messages_created_at",
                table: "outbound_messages",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_patient_medical_infos_family_member_id",
                table: "patient_medical_infos",
                column: "family_member_id");

            migrationBuilder.CreateIndex(
                name: "ux_medical_info_member",
                table: "patient_medical_infos",
                columns: new[] { "patient_user_id", "family_member_id" },
                unique: true,
                filter: "family_member_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_medical_info_self",
                table: "patient_medical_infos",
                column: "patient_user_id",
                unique: true,
                filter: "family_member_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_patient_record_entries_appointment_id",
                table: "patient_record_entries",
                column: "appointment_id");

            migrationBuilder.CreateIndex(
                name: "ix_patient_record_entries_author_user_id",
                table: "patient_record_entries",
                column: "author_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_patient_record_entries_doctor_id",
                table: "patient_record_entries",
                column: "doctor_id");

            migrationBuilder.CreateIndex(
                name: "ix_patient_record_entries_family_member_id",
                table: "patient_record_entries",
                column: "family_member_id");

            migrationBuilder.CreateIndex(
                name: "ix_patient_record_entries_patient_user_id_clinic_id",
                table: "patient_record_entries",
                columns: new[] { "patient_user_id", "clinic_id" });

            migrationBuilder.CreateIndex(
                name: "ix_payments_appointment_id",
                table: "payments",
                column: "appointment_id");

            migrationBuilder.CreateIndex(
                name: "ix_payments_created_at",
                table: "payments",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_payments_provider_provider_ref",
                table: "payments",
                columns: new[] { "provider", "provider_ref" });

            migrationBuilder.CreateIndex(
                name: "ix_recall_campaigns_clinic_id",
                table: "recall_campaigns",
                column: "clinic_id");

            migrationBuilder.CreateIndex(
                name: "ix_recall_campaigns_doctor_id",
                table: "recall_campaigns",
                column: "doctor_id");

            migrationBuilder.CreateIndex(
                name: "ix_recall_campaigns_service_id",
                table: "recall_campaigns",
                column: "service_id");

            migrationBuilder.CreateIndex(
                name: "ix_recall_sends_campaign_id_patient_user_id_sent_at",
                table: "recall_sends",
                columns: new[] { "campaign_id", "patient_user_id", "sent_at" });

            migrationBuilder.CreateIndex(
                name: "ix_recall_sends_patient_user_id",
                table: "recall_sends",
                column: "patient_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_reviews_appointment_id",
                table: "reviews",
                column: "appointment_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_reviews_doctor_id_status_created_at",
                table: "reviews",
                columns: new[] { "doctor_id", "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_reviews_patient_user_id",
                table: "reviews",
                column: "patient_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_waiting_list_entries_doctor_id_date_status",
                table: "waiting_list_entries",
                columns: new[] { "doctor_id", "date", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_waiting_list_entries_family_member_id",
                table: "waiting_list_entries",
                column: "family_member_id");

            migrationBuilder.CreateIndex(
                name: "ix_waiting_list_entries_patient_user_id",
                table: "waiting_list_entries",
                column: "patient_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_waiting_list_active",
                table: "waiting_list_entries",
                columns: new[] { "patient_user_id", "doctor_id", "date", "family_member_id" },
                unique: true,
                filter: "status IN ('Waiting', 'Notified')")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_working_hours_templates_clinic_id",
                table: "working_hours_templates",
                column: "clinic_id");

            migrationBuilder.AddForeignKey(
                name: "fk_appointments_family_members_family_member_id",
                table: "appointments",
                column: "family_member_id",
                principalTable: "family_members",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_appointments_insurance_providers_insurance_provider_id",
                table: "appointments",
                column: "insurance_provider_id",
                principalTable: "insurance_providers",
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
                name: "fk_appointments_insurance_providers_insurance_provider_id",
                table: "appointments");

            migrationBuilder.DropTable(
                name: "clinic_holidays");

            migrationBuilder.DropTable(
                name: "coupon_redemptions");

            migrationBuilder.DropTable(
                name: "doctor_insurance_providers");

            migrationBuilder.DropTable(
                name: "outbound_messages");

            migrationBuilder.DropTable(
                name: "patient_medical_infos");

            migrationBuilder.DropTable(
                name: "patient_record_entries");

            migrationBuilder.DropTable(
                name: "payments");

            migrationBuilder.DropTable(
                name: "recall_sends");

            migrationBuilder.DropTable(
                name: "reviews");

            migrationBuilder.DropTable(
                name: "waiting_list_entries");

            migrationBuilder.DropTable(
                name: "working_hours_templates");

            migrationBuilder.DropTable(
                name: "coupons");

            migrationBuilder.DropTable(
                name: "insurance_providers");

            migrationBuilder.DropTable(
                name: "recall_campaigns");

            migrationBuilder.DropTable(
                name: "family_members");

            migrationBuilder.DropIndex(
                name: "ix_appointments_family_member_id",
                table: "appointments");

            migrationBuilder.DropIndex(
                name: "ix_appointments_insurance_provider_id",
                table: "appointments");

            migrationBuilder.DropIndex(
                name: "ix_appointments_review_prompt_due",
                table: "appointments");

            migrationBuilder.DropColumn(
                name: "marketing_opt_in",
                table: "users");

            migrationBuilder.DropColumn(
                name: "whats_app_opt_in",
                table: "users");

            migrationBuilder.DropColumn(
                name: "offers_video",
                table: "doctors");

            migrationBuilder.DropColumn(
                name: "completed_at",
                table: "appointments");

            migrationBuilder.DropColumn(
                name: "coupon_id",
                table: "appointments");

            migrationBuilder.DropColumn(
                name: "discount_egp",
                table: "appointments");

            migrationBuilder.DropColumn(
                name: "family_member_id",
                table: "appointments");

            migrationBuilder.DropColumn(
                name: "insurance_member_number",
                table: "appointments");

            migrationBuilder.DropColumn(
                name: "insurance_note",
                table: "appointments");

            migrationBuilder.DropColumn(
                name: "insurance_provider_id",
                table: "appointments");

            migrationBuilder.DropColumn(
                name: "insurance_status",
                table: "appointments");

            migrationBuilder.DropColumn(
                name: "mode",
                table: "appointments");

            migrationBuilder.DropColumn(
                name: "price_egp",
                table: "appointments");

            migrationBuilder.DropColumn(
                name: "review_prompt_sent_at",
                table: "appointments");

            migrationBuilder.DropColumn(
                name: "video_host_url",
                table: "appointments");

            migrationBuilder.DropColumn(
                name: "video_patient_url",
                table: "appointments");

            migrationBuilder.DropColumn(
                name: "video_room_name",
                table: "appointments");
        }
    }
}
