using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddLoanJournalEvidenceAndJawazatPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "gl_entry_id",
                table: "loan_repayments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "reversal_gl_entry_id",
                table: "loan_repayments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "gl_entry_id",
                table: "loan_disbursement_lines",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "requested_repayment_method",
                table: "loan_change_requests",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "approval_request_id",
                table: "hr_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "hr_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "jawazat_data_json",
                table: "hr_requests",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "workflow_version",
                table: "hr_requests",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "jawazat_policy_json",
                table: "company_compliance_profiles",
                type: "text",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_loan_repayments_tenant_id_loan_id_id",
                table: "loan_repayments",
                columns: new[] { "tenant_id", "loan_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_finance_gl_entries_tenant_id_id",
                table: "finance_gl_entries",
                columns: new[] { "tenant_id", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_loan_repayments_tenant_id_gl_entry_id",
                table: "loan_repayments",
                columns: new[] { "tenant_id", "gl_entry_id" },
                unique: true,
                filter: "gl_entry_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_loan_repayments_tenant_id_reversal_gl_entry_id",
                table: "loan_repayments",
                columns: new[] { "tenant_id", "reversal_gl_entry_id" },
                unique: true,
                filter: "reversal_gl_entry_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_loan_disbursement_lines_tenant_id_gl_entry_id",
                table: "loan_disbursement_lines",
                columns: new[] { "tenant_id", "gl_entry_id" },
                unique: true,
                filter: "gl_entry_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_loan_payment_line_evidence",
                table: "loan_disbursement_lines",
                sql: "status NOT IN ('Paid','Reversed') OR (paid_date IS NOT NULL AND paid_by IS NOT NULL AND payment_reference IS NOT NULL AND trim(payment_reference) <> '' AND payment_method IS NOT NULL AND payment_method IN ('BankTransfer','DirectDebit','Cash'))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_loan_payment_line_status",
                table: "loan_disbursement_lines",
                sql: "status IN ('Pending','Paid','Failed','Cancelled','Reversed')");

            migrationBuilder.CreateIndex(
                name: "IX_loan_change_requests_tenant_id_loan_id_repayment_id",
                table: "loan_change_requests",
                columns: new[] { "tenant_id", "loan_id", "repayment_id" });

            migrationBuilder.CreateIndex(
                name: "IX_hr_requests_tenant_id_company_id_status",
                table: "hr_requests",
                columns: new[] { "tenant_id", "company_id", "status" });

            migrationBuilder.AddForeignKey(
                name: "FK_loan_change_requests_loan_repayments_tenant_id_loan_id_repa~",
                table: "loan_change_requests",
                columns: new[] { "tenant_id", "loan_id", "repayment_id" },
                principalTable: "loan_repayments",
                principalColumns: new[] { "tenant_id", "loan_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_loan_disbursement_lines_finance_gl_entries_tenant_id_gl_ent~",
                table: "loan_disbursement_lines",
                columns: new[] { "tenant_id", "gl_entry_id" },
                principalTable: "finance_gl_entries",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_loan_repayments_finance_gl_entries_tenant_id_gl_entry_id",
                table: "loan_repayments",
                columns: new[] { "tenant_id", "gl_entry_id" },
                principalTable: "finance_gl_entries",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_loan_repayments_finance_gl_entries_tenant_id_reversal_gl_en~",
                table: "loan_repayments",
                columns: new[] { "tenant_id", "reversal_gl_entry_id" },
                principalTable: "finance_gl_entries",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM loan_repayments WHERE gl_entry_id IS NOT NULL OR reversal_gl_entry_id IS NOT NULL)
                        OR EXISTS (SELECT 1 FROM loan_disbursement_lines WHERE gl_entry_id IS NOT NULL)
                        OR EXISTS (SELECT 1 FROM loan_change_requests WHERE requested_repayment_method IS NOT NULL)
                        OR EXISTS (SELECT 1 FROM hr_requests WHERE jawazat_data_json IS NOT NULL)
                        OR EXISTS (SELECT 1 FROM company_compliance_profiles WHERE jawazat_policy_json IS NOT NULL) THEN
                        RAISE EXCEPTION 'Loan journal or Jawazat policy/workflow evidence exists. Preserve history and use a forward corrective migration.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_loan_change_requests_loan_repayments_tenant_id_loan_id_repa~",
                table: "loan_change_requests");

            migrationBuilder.DropForeignKey(
                name: "FK_loan_disbursement_lines_finance_gl_entries_tenant_id_gl_ent~",
                table: "loan_disbursement_lines");

            migrationBuilder.DropForeignKey(
                name: "FK_loan_repayments_finance_gl_entries_tenant_id_gl_entry_id",
                table: "loan_repayments");

            migrationBuilder.DropForeignKey(
                name: "FK_loan_repayments_finance_gl_entries_tenant_id_reversal_gl_en~",
                table: "loan_repayments");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_loan_repayments_tenant_id_loan_id_id",
                table: "loan_repayments");

            migrationBuilder.DropIndex(
                name: "IX_loan_repayments_tenant_id_gl_entry_id",
                table: "loan_repayments");

            migrationBuilder.DropIndex(
                name: "IX_loan_repayments_tenant_id_reversal_gl_entry_id",
                table: "loan_repayments");

            migrationBuilder.DropIndex(
                name: "IX_loan_disbursement_lines_tenant_id_gl_entry_id",
                table: "loan_disbursement_lines");

            migrationBuilder.DropCheckConstraint(
                name: "ck_loan_payment_line_evidence",
                table: "loan_disbursement_lines");

            migrationBuilder.DropCheckConstraint(
                name: "ck_loan_payment_line_status",
                table: "loan_disbursement_lines");

            migrationBuilder.DropIndex(
                name: "IX_loan_change_requests_tenant_id_loan_id_repayment_id",
                table: "loan_change_requests");

            migrationBuilder.DropIndex(
                name: "IX_hr_requests_tenant_id_company_id_status",
                table: "hr_requests");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_finance_gl_entries_tenant_id_id",
                table: "finance_gl_entries");

            migrationBuilder.DropColumn(
                name: "gl_entry_id",
                table: "loan_repayments");

            migrationBuilder.DropColumn(
                name: "reversal_gl_entry_id",
                table: "loan_repayments");

            migrationBuilder.DropColumn(
                name: "gl_entry_id",
                table: "loan_disbursement_lines");

            migrationBuilder.DropColumn(
                name: "requested_repayment_method",
                table: "loan_change_requests");

            migrationBuilder.DropColumn(
                name: "approval_request_id",
                table: "hr_requests");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "hr_requests");

            migrationBuilder.DropColumn(
                name: "jawazat_data_json",
                table: "hr_requests");

            migrationBuilder.DropColumn(
                name: "workflow_version",
                table: "hr_requests");

            migrationBuilder.DropColumn(
                name: "jawazat_policy_json",
                table: "company_compliance_profiles");
        }
    }
}
