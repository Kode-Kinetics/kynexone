using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddLoanPolicyLifecycleControls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_loan_payment_batch_status",
                table: "loan_disbursement_batches");

            migrationBuilder.AddColumn<bool>(
                name: "is_reversed",
                table: "loan_repayments",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "reversed_at_utc",
                table: "loan_repayments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "reversed_by",
                table: "loan_repayments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "additional_approval_threshold",
                table: "loan_policies",
                type: "numeric(14,2)",
                precision: 14,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "additional_approver_role",
                table: "loan_policies",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "allow_exceptions",
                table: "loan_policies",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "allowed_contract_types_json",
                table: "loan_policies",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "allowed_employment_statuses_json",
                table: "loan_policies",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "allowed_repayment_frequencies_json",
                table: "loan_policies",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "allowed_repayment_methods_json",
                table: "loan_policies",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "block_during_notice",
                table: "loan_policies",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "block_on_overdue",
                table: "loan_policies",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "loan_policies",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "max_amount",
                table: "loan_policies",
                type: "numeric(14,2)",
                precision: 14,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "max_installment_percent_of_salary",
                table: "loan_policies",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "max_installments",
                table: "loan_policies",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "max_total_outstanding",
                table: "loan_policies",
                type: "numeric(14,2)",
                precision: 14,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "min_service_months",
                table: "loan_policies",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "require_probation_completed",
                table: "loan_policies",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "version",
                table: "loan_policies",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "failure_reason",
                table: "loan_disbursement_lines",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "paid_by",
                table: "loan_disbursement_lines",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "paid_date",
                table: "loan_disbursement_lines",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "payment_method",
                table: "loan_disbursement_lines",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "payment_reference",
                table: "loan_disbursement_lines",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "loan_disbursement_lines",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "collection_status",
                table: "employee_loans",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "eligibility_snapshot_json",
                table: "employee_loans",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "employment_snapshot_json",
                table: "employee_loans",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "policy_id",
                table: "employee_loans",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "policy_snapshot_json",
                table: "employee_loans",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "policy_version",
                table: "employee_loans",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "review_reason",
                table: "employee_loans",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "review_required",
                table: "employee_loans",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "loan_change_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    loan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    change_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    requested_installments = table.Column<int>(type: "integer", nullable: true),
                    requested_start_date = table.Column<DateOnly>(type: "date", nullable: true),
                    requested_exceptions_json = table.Column<string>(type: "text", nullable: false),
                    outstanding_balance_at_request = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    decided_by = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    decision_reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    repayment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    effective_date = table.Column<DateOnly>(type: "date", nullable: true),
                    reference = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_loan_change_requests", x => x.id);
                    table.ForeignKey(
                        name: "FK_loan_change_requests_employee_loans_tenant_id_loan_id",
                        columns: x => new { x.tenant_id, x.loan_id },
                        principalTable: "employee_loans",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_loan_policies_tenant_id_company_id",
                table: "loan_policies",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_loan_policies_tenant_id_company_id_loan_type_id",
                table: "loan_policies",
                columns: new[] { "tenant_id", "company_id", "loan_type_id" },
                unique: true,
                filter: "company_id IS NOT NULL AND is_active");

            migrationBuilder.CreateIndex(
                name: "IX_loan_policies_tenant_id_company_id_loan_type_id_version",
                table: "loan_policies",
                columns: new[] { "tenant_id", "company_id", "loan_type_id", "version" },
                unique: true,
                filter: "company_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_loan_payment_batch_status",
                table: "loan_disbursement_batches",
                sql: "status IN ('Draft','Approved','PartiallyPaid','Completed','Paid','Cancelled')");

            migrationBuilder.CreateIndex(
                name: "IX_loan_change_requests_tenant_id_company_id",
                table: "loan_change_requests",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_loan_change_requests_tenant_id_loan_id_reference",
                table: "loan_change_requests",
                columns: new[] { "tenant_id", "loan_id", "reference" },
                unique: true,
                filter: "reference <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_loan_change_requests_tenant_id_loan_id_status",
                table: "loan_change_requests",
                columns: new[] { "tenant_id", "loan_id", "status" });

            // Preserve existing policy limits and paid instruction evidence before activating the new flow.
            migrationBuilder.Sql("""
                UPDATE loan_policies p SET version = 1, additional_approver_role = 'HR Director',
                    allowed_contract_types_json = '[]', allowed_employment_statuses_json = '["Active"]',
                    allowed_repayment_methods_json = '["BankTransfer","DirectDebit","Cash","PayrollDeduction"]',
                    allowed_repayment_frequencies_json = '["Monthly","Weekly","BiWeekly","Quarterly"]',
                    block_during_notice = true, block_on_overdue = true,
                    max_amount = t.max_amount, max_installments = t.max_installments, min_service_months = t.min_service_months
                FROM loan_types t WHERE p.tenant_id = t.tenant_id AND p.loan_type_id = t.id;
                UPDATE employee_loans SET policy_snapshot_json = '{}', eligibility_snapshot_json = '{}',
                    employment_snapshot_json = '{}', collection_status = 'Normal';
                UPDATE loan_disbursement_lines l SET
                    status = CASE WHEN b.status = 'Paid' THEN 'Paid' WHEN l.is_cancelled THEN 'Cancelled' ELSE 'Pending' END,
                    payment_reference = CASE WHEN b.status = 'Paid' THEN b.payment_reference END,
                    paid_date = CASE WHEN b.status = 'Paid' THEN b.paid_date END,
                    paid_by = CASE WHEN b.status = 'Paid' THEN b.paid_by END,
                    payment_method = CASE WHEN b.status = 'Paid' THEN b.payment_method END
                FROM loan_disbursement_batches b WHERE b.tenant_id = l.tenant_id AND b.id = l.batch_id;

                UPDATE loan_approvals a SET approver_role = 'HR Manager'
                FROM employee_loans l WHERE l.tenant_id = a.tenant_id AND l.id = a.loan_id
                    AND l.status = 'Pending' AND NOT l.is_deleted AND a.status = 'Pending' AND a.approver_role = 'Finance';
                INSERT INTO loan_approvals (id, tenant_id, loan_id, step_order, approver_role, approved_by_name, status, comments, created_at_utc)
                SELECT md5('loan-hr-policy-routing:' || l.id::text)::uuid, l.tenant_id, l.id,
                    COALESCE((SELECT MAX(step_order) FROM loan_approvals a WHERE a.tenant_id = l.tenant_id AND a.loan_id = l.id),0) + 1,
                    'HR Manager', '', 'Pending', 'HR review required before separate disbursement.', CURRENT_TIMESTAMP
                FROM employee_loans l WHERE l.status IN ('Pending','Approved') AND l.disbursement_date IS NULL
                    AND l.outstanding_balance = 0 AND NOT l.is_deleted
                    AND NOT EXISTS (SELECT 1 FROM loan_approvals a WHERE a.tenant_id = l.tenant_id AND a.loan_id = l.id
                        AND a.approver_role IN ('HR Manager','HR Director') AND a.status IN ('Pending','Approved'));
                UPDATE employee_loans l SET status = 'Pending'
                WHERE l.status = 'Approved' AND l.disbursement_date IS NULL AND l.outstanding_balance = 0 AND NOT l.is_deleted
                    AND EXISTS (SELECT 1 FROM loan_approvals a WHERE a.tenant_id = l.tenant_id AND a.loan_id = l.id
                        AND a.approver_role IN ('HR Manager','HR Director') AND a.status = 'Pending');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM loan_change_requests)
                        OR EXISTS (SELECT 1 FROM loan_disbursement_lines)
                        OR EXISTS (SELECT 1 FROM loan_repayments)
                        OR EXISTS (SELECT 1 FROM loan_disbursement_batches) THEN
                        RAISE EXCEPTION 'Loan lifecycle records exist. Preserve financial history and apply a forward corrective migration.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropTable(
                name: "loan_change_requests");

            migrationBuilder.DropIndex(
                name: "IX_loan_policies_tenant_id_company_id",
                table: "loan_policies");

            migrationBuilder.DropIndex(
                name: "IX_loan_policies_tenant_id_company_id_loan_type_id",
                table: "loan_policies");

            migrationBuilder.DropIndex(
                name: "IX_loan_policies_tenant_id_company_id_loan_type_id_version",
                table: "loan_policies");

            migrationBuilder.DropCheckConstraint(
                name: "ck_loan_payment_batch_status",
                table: "loan_disbursement_batches");

            migrationBuilder.DropColumn(
                name: "is_reversed",
                table: "loan_repayments");

            migrationBuilder.DropColumn(
                name: "reversed_at_utc",
                table: "loan_repayments");

            migrationBuilder.DropColumn(
                name: "reversed_by",
                table: "loan_repayments");

            migrationBuilder.DropColumn(
                name: "additional_approval_threshold",
                table: "loan_policies");

            migrationBuilder.DropColumn(
                name: "additional_approver_role",
                table: "loan_policies");

            migrationBuilder.DropColumn(
                name: "allow_exceptions",
                table: "loan_policies");

            migrationBuilder.DropColumn(
                name: "allowed_contract_types_json",
                table: "loan_policies");

            migrationBuilder.DropColumn(
                name: "allowed_employment_statuses_json",
                table: "loan_policies");

            migrationBuilder.DropColumn(
                name: "allowed_repayment_frequencies_json",
                table: "loan_policies");

            migrationBuilder.DropColumn(
                name: "allowed_repayment_methods_json",
                table: "loan_policies");

            migrationBuilder.DropColumn(
                name: "block_during_notice",
                table: "loan_policies");

            migrationBuilder.DropColumn(
                name: "block_on_overdue",
                table: "loan_policies");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "loan_policies");

            migrationBuilder.DropColumn(
                name: "max_amount",
                table: "loan_policies");

            migrationBuilder.DropColumn(
                name: "max_installment_percent_of_salary",
                table: "loan_policies");

            migrationBuilder.DropColumn(
                name: "max_installments",
                table: "loan_policies");

            migrationBuilder.DropColumn(
                name: "max_total_outstanding",
                table: "loan_policies");

            migrationBuilder.DropColumn(
                name: "min_service_months",
                table: "loan_policies");

            migrationBuilder.DropColumn(
                name: "require_probation_completed",
                table: "loan_policies");

            migrationBuilder.DropColumn(
                name: "version",
                table: "loan_policies");

            migrationBuilder.DropColumn(
                name: "failure_reason",
                table: "loan_disbursement_lines");

            migrationBuilder.DropColumn(
                name: "paid_by",
                table: "loan_disbursement_lines");

            migrationBuilder.DropColumn(
                name: "paid_date",
                table: "loan_disbursement_lines");

            migrationBuilder.DropColumn(
                name: "payment_method",
                table: "loan_disbursement_lines");

            migrationBuilder.DropColumn(
                name: "payment_reference",
                table: "loan_disbursement_lines");

            migrationBuilder.DropColumn(
                name: "status",
                table: "loan_disbursement_lines");

            migrationBuilder.DropColumn(
                name: "collection_status",
                table: "employee_loans");

            migrationBuilder.DropColumn(
                name: "eligibility_snapshot_json",
                table: "employee_loans");

            migrationBuilder.DropColumn(
                name: "employment_snapshot_json",
                table: "employee_loans");

            migrationBuilder.DropColumn(
                name: "policy_id",
                table: "employee_loans");

            migrationBuilder.DropColumn(
                name: "policy_snapshot_json",
                table: "employee_loans");

            migrationBuilder.DropColumn(
                name: "policy_version",
                table: "employee_loans");

            migrationBuilder.DropColumn(
                name: "review_reason",
                table: "employee_loans");

            migrationBuilder.DropColumn(
                name: "review_required",
                table: "employee_loans");

            migrationBuilder.AddCheckConstraint(
                name: "ck_loan_payment_batch_status",
                table: "loan_disbursement_batches",
                sql: "status IN ('Draft','Approved','Paid','Cancelled')");
        }
    }
}
