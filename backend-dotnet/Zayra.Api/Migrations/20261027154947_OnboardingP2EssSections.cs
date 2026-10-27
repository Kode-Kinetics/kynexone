using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class OnboardingP2EssSections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "notice_version",
                table: "employee_profile_change_requests",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "review_note",
                table: "employee_profile_change_requests",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "section",
                table: "employee_profile_change_requests",
                type: "character varying(24)",
                maxLength: 24,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "step_up_at_utc",
                table: "employee_profile_change_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "scan_status",
                table: "employee_documents",
                type: "character varying(12)",
                maxLength: 12,
                nullable: false,
                defaultValue: "Clean");

            migrationBuilder.AddColumn<string>(
                name: "source_section",
                table: "employee_documents",
                type: "character varying(24)",
                maxLength: 24,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "requested_sections",
                table: "employee_action_items",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_epcr_inbox",
                table: "employee_profile_change_requests",
                columns: new[] { "tenant_id", "status", "created_at_utc" },
                descending: new[] { false, false, true },
                filter: "status = 'PendingHR'");

            migrationBuilder.CreateIndex(
                name: "ix_epcr_section_pending",
                table: "employee_profile_change_requests",
                columns: new[] { "tenant_id", "employee_id", "section" },
                unique: true,
                filter: "status = 'PendingHR' AND section IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_epcr__section",
                table: "employee_profile_change_requests",
                sql: "section IS NULL OR section IN ('person','emergency','arabicName','id','bank','documents')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_employee_documents__scan_status",
                table: "employee_documents",
                sql: "scan_status IN ('Clean','PendingScan','Quarantined')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_epcr_inbox",
                table: "employee_profile_change_requests");

            migrationBuilder.DropIndex(
                name: "ix_epcr_section_pending",
                table: "employee_profile_change_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_epcr__section",
                table: "employee_profile_change_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employee_documents__scan_status",
                table: "employee_documents");

            migrationBuilder.DropColumn(
                name: "notice_version",
                table: "employee_profile_change_requests");

            migrationBuilder.DropColumn(
                name: "review_note",
                table: "employee_profile_change_requests");

            migrationBuilder.DropColumn(
                name: "section",
                table: "employee_profile_change_requests");

            migrationBuilder.DropColumn(
                name: "step_up_at_utc",
                table: "employee_profile_change_requests");

            migrationBuilder.DropColumn(
                name: "scan_status",
                table: "employee_documents");

            migrationBuilder.DropColumn(
                name: "source_section",
                table: "employee_documents");

            migrationBuilder.DropColumn(
                name: "requested_sections",
                table: "employee_action_items");
        }
    }
}
