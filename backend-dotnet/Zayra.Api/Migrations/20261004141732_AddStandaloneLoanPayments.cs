using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddStandaloneLoanPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "currency",
                table: "employee_loans",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "repayment_method",
                table: "employee_loans",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "PayrollDeduction");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_employee_loans_tenant_id_id",
                table: "employee_loans",
                columns: new[] { "tenant_id", "id" });

            migrationBuilder.CreateTable(
                name: "loan_disbursement_batches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    batch_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    total_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    paid_by = table.Column<Guid>(type: "uuid", nullable: true),
                    paid_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    payment_reference = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    payment_method = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    paid_date = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_loan_disbursement_batches", x => x.id);
                    table.UniqueConstraint("AK_loan_disbursement_batches_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_loan_payment_batch_amount", "total_amount > 0");
                    table.CheckConstraint("ck_loan_payment_batch_paid_evidence", "status <> 'Paid' OR (paid_date IS NOT NULL AND paid_by IS NOT NULL AND payment_reference IS NOT NULL)");
                    table.CheckConstraint("ck_loan_payment_batch_status", "status IN ('Draft','Approved','Paid','Cancelled')");
                });

            migrationBuilder.CreateTable(
                name: "loan_repayments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    loan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    paid_date = table.Column<DateOnly>(type: "date", nullable: false),
                    reference = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    payment_method = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_loan_repayments", x => x.id);
                    table.CheckConstraint("ck_loan_receipt_amount", "amount > 0");
                    table.CheckConstraint("ck_loan_receipt_method", "payment_method IN ('BankTransfer','DirectDebit','Cash')");
                    table.ForeignKey(
                        name: "FK_loan_repayments_employee_loans_tenant_id_loan_id",
                        columns: x => new { x.tenant_id, x.loan_id },
                        principalTable: "employee_loans",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "loan_disbursement_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    loan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    is_cancelled = table.Column<bool>(type: "boolean", nullable: false),
                    employee_name = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    employee_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    iban = table.Column<string>(type: "character varying(34)", maxLength: 34, nullable: false),
                    bank_name = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_loan_disbursement_lines", x => x.id);
                    table.CheckConstraint("ck_loan_payment_line_amount", "amount > 0");
                    table.ForeignKey(
                        name: "FK_loan_disbursement_lines_employee_loans_tenant_id_loan_id",
                        columns: x => new { x.tenant_id, x.loan_id },
                        principalTable: "employee_loans",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_loan_disbursement_lines_loan_disbursement_batches_tenant_id~",
                        columns: x => new { x.tenant_id, x.batch_id },
                        principalTable: "loan_disbursement_batches",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_loan_disbursement_batches_tenant_id_batch_number",
                table: "loan_disbursement_batches",
                columns: new[] { "tenant_id", "batch_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_loan_disbursement_batches_tenant_id_company_id",
                table: "loan_disbursement_batches",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_loan_disbursement_batches_tenant_id_company_id_status",
                table: "loan_disbursement_batches",
                columns: new[] { "tenant_id", "company_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_loan_disbursement_lines_tenant_id_batch_id",
                table: "loan_disbursement_lines",
                columns: new[] { "tenant_id", "batch_id" });

            migrationBuilder.CreateIndex(
                name: "IX_loan_disbursement_lines_tenant_id_loan_id",
                table: "loan_disbursement_lines",
                columns: new[] { "tenant_id", "loan_id" },
                unique: true,
                filter: "NOT is_cancelled");

            migrationBuilder.CreateIndex(
                name: "IX_loan_repayments_tenant_id_company_id",
                table: "loan_repayments",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_loan_repayments_tenant_id_loan_id_reference",
                table: "loan_repayments",
                columns: new[] { "tenant_id", "loan_id", "reference" },
                unique: true);

            // Repair requests created before application submission added its Finance
            // step. This creates an undecided step only; no loan is approved or paid.
            // The deterministic ID makes the repair identifiable and reversible.
            migrationBuilder.Sql("""
                INSERT INTO loan_approvals
                    (id, tenant_id, loan_id, step_order, approver_role, approved_by_name, status, comments, created_at_utc)
                SELECT md5('standalone-loan-finance-step:' || l.id::text)::uuid,
                       l.tenant_id, l.id, 1, 'Finance', '', 'Pending', '', CURRENT_TIMESTAMP
                FROM employee_loans l
                WHERE l.status = 'Pending' AND NOT l.is_deleted
                  AND NOT EXISTS (SELECT 1 FROM loan_approvals a WHERE a.tenant_id = l.tenant_id AND a.loan_id = l.id);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM loan_disbursement_lines)
                        OR EXISTS (SELECT 1 FROM loan_repayments)
                        OR EXISTS (SELECT 1 FROM loan_disbursement_batches) THEN
                        RAISE EXCEPTION 'Loan payment history exists. Preserve financial records and use a forward corrective migration.';
                    END IF;
                END $$;
                """);
            migrationBuilder.Sql("""
                DELETE FROM loan_approvals
                WHERE id = md5('standalone-loan-finance-step:' || loan_id::text)::uuid
                  AND status = 'Pending' AND decided_at_utc IS NULL;
                """);
            migrationBuilder.DropTable(
                name: "loan_disbursement_lines");

            migrationBuilder.DropTable(
                name: "loan_repayments");

            migrationBuilder.DropTable(
                name: "loan_disbursement_batches");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_employee_loans_tenant_id_id",
                table: "employee_loans");

            migrationBuilder.DropColumn(
                name: "currency",
                table: "employee_loans");

            migrationBuilder.DropColumn(
                name: "repayment_method",
                table: "employee_loans");
        }
    }
}
