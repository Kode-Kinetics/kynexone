using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Zayra.Api.Data;

#nullable disable

namespace Zayra.Api.Migrations
{
    [Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof(ZayraDbContext))]
    [Migration("20260713043000_AddStatutoryFilingAndErpPostingLifecycle")]
    public partial class AddStatutoryFilingAndErpPostingLifecycle : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "wps_submission_reference",
                table: "payroll_payment_batches",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "wps_rejection_reason",
                table: "payroll_payment_batches",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);
            migrationBuilder.AddColumn<DateTime>(
                name: "wps_status_changed_at_utc",
                table: "payroll_payment_batches",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "filing_status",
                table: "wps_file_batches",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "Generated");
            migrationBuilder.AddColumn<Guid>(
                name: "resubmission_of_wps_file_batch_id",
                table: "wps_file_batches",
                type: "uuid",
                nullable: true);
            migrationBuilder.AddColumn<int>(
                name: "resubmission_number",
                table: "wps_file_batches",
                type: "integer",
                nullable: false,
                defaultValue: 0);
            migrationBuilder.AddColumn<string>(
                name: "submission_reference",
                table: "wps_file_batches",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);
            migrationBuilder.AddColumn<DateTime>(
                name: "submitted_at_utc",
                table: "wps_file_batches",
                type: "timestamp with time zone",
                nullable: true);
            migrationBuilder.AddColumn<DateTime>(
                name: "acknowledged_at_utc",
                table: "wps_file_batches",
                type: "timestamp with time zone",
                nullable: true);
            migrationBuilder.AddColumn<DateTime>(
                name: "rejected_at_utc",
                table: "wps_file_batches",
                type: "timestamp with time zone",
                nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "rejection_reason",
                table: "wps_file_batches",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "erp_posting_status",
                table: "payroll_runs",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "NotReady");
            migrationBuilder.AddColumn<DateTime>(
                name: "erp_posting_status_changed_at_utc",
                table: "payroll_runs",
                type: "timestamp with time zone",
                nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "erp_posting_reference",
                table: "payroll_runs",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "erp_posting_failure_reason",
                table: "payroll_runs",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "erp_posting_status",
                table: "finance_gl_entries",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "NotReady");
            migrationBuilder.AddColumn<string>(
                name: "erp_document_number",
                table: "finance_gl_entries",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);
            migrationBuilder.AddColumn<DateTime>(
                name: "erp_status_changed_at_utc",
                table: "finance_gl_entries",
                type: "timestamp with time zone",
                nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "erp_rejection_reason",
                table: "finance_gl_entries",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_wps_file_batches_tenant_id_filing_status",
                table: "wps_file_batches",
                columns: new[] { "tenant_id", "filing_status" });
            migrationBuilder.CreateIndex(
                name: "ix_payroll_runs_tenant_id_erp_posting_status",
                table: "payroll_runs",
                columns: new[] { "tenant_id", "erp_posting_status" });
            migrationBuilder.CreateIndex(
                name: "ix_finance_gl_entries_tenant_id_erp_posting_status",
                table: "finance_gl_entries",
                columns: new[] { "tenant_id", "erp_posting_status" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "ix_wps_file_batches_tenant_id_filing_status", table: "wps_file_batches");
            migrationBuilder.DropIndex(name: "ix_payroll_runs_tenant_id_erp_posting_status", table: "payroll_runs");
            migrationBuilder.DropIndex(name: "ix_finance_gl_entries_tenant_id_erp_posting_status", table: "finance_gl_entries");

            migrationBuilder.DropColumn(name: "wps_submission_reference", table: "payroll_payment_batches");
            migrationBuilder.DropColumn(name: "wps_rejection_reason", table: "payroll_payment_batches");
            migrationBuilder.DropColumn(name: "wps_status_changed_at_utc", table: "payroll_payment_batches");
            migrationBuilder.DropColumn(name: "filing_status", table: "wps_file_batches");
            migrationBuilder.DropColumn(name: "resubmission_of_wps_file_batch_id", table: "wps_file_batches");
            migrationBuilder.DropColumn(name: "resubmission_number", table: "wps_file_batches");
            migrationBuilder.DropColumn(name: "submission_reference", table: "wps_file_batches");
            migrationBuilder.DropColumn(name: "submitted_at_utc", table: "wps_file_batches");
            migrationBuilder.DropColumn(name: "acknowledged_at_utc", table: "wps_file_batches");
            migrationBuilder.DropColumn(name: "rejected_at_utc", table: "wps_file_batches");
            migrationBuilder.DropColumn(name: "rejection_reason", table: "wps_file_batches");
            migrationBuilder.DropColumn(name: "erp_posting_status", table: "payroll_runs");
            migrationBuilder.DropColumn(name: "erp_posting_status_changed_at_utc", table: "payroll_runs");
            migrationBuilder.DropColumn(name: "erp_posting_reference", table: "payroll_runs");
            migrationBuilder.DropColumn(name: "erp_posting_failure_reason", table: "payroll_runs");
            migrationBuilder.DropColumn(name: "erp_posting_status", table: "finance_gl_entries");
            migrationBuilder.DropColumn(name: "erp_document_number", table: "finance_gl_entries");
            migrationBuilder.DropColumn(name: "erp_status_changed_at_utc", table: "finance_gl_entries");
            migrationBuilder.DropColumn(name: "erp_rejection_reason", table: "finance_gl_entries");
        }
    }
}
