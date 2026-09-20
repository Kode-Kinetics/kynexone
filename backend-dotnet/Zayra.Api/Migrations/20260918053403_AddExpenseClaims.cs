using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// W2-B — expense claims &amp; reimbursement.
    ///
    /// <para><b>Schema</b>: two NEW tables (expense_claims, expense_claim_lines). Nothing existing is
    /// altered or dropped.</para>
    ///
    /// <para><b>Data</b> (every statement NOT EXISTS-guarded, so re-running Up — or overlapping with
    /// TenantProvisioningBundle / GlDriverSeeder on a new tenant — is a no-op):</para>
    /// <list type="number">
    /// <item>A default <c>ExpenseClaim</c> approval workflow (one HR Manager step, final) for every
    /// existing tenant that has no active tenant-wide one — exactly what provisioning now installs. Without
    /// it the F1 router would (correctly) refuse every submission with approval_route_not_configured.</item>
    /// <item>The <c>ExpenseCategory</c> master-data type and six starter values, with NO policy (no cap,
    /// receipt optional) — the tenant sets limits.</item>
    /// <item>The <c>EARN:EXPENSE_REIMBURSEMENT</c> GL driver (Exact on ADJ_EXPENSE_REIMBURSEMENT, source
    /// Adjustment → 5120) plus its tenant-default account and mapping, for tenants that already have the
    /// system drivers. Without the driver row the seeded catch-all EARN:OTHER (`Any`) would claim the
    /// reimbursement and book it to 5099 Other Earnings.</item>
    /// </list>
    ///
    /// <para><b>Down</b> removes exactly the seeded rows and the two tables.</para>
    /// </summary>
    public partial class AddExpenseClaims : Migration
    {
        internal const string SeedExpenseWorkflowsSql = """
            WITH targets AS (
                SELECT t.id AS tenant_id, gen_random_uuid() AS workflow_id
                FROM tenants t
                WHERE NOT EXISTS (
                        SELECT 1 FROM approval_workflows w
                        WHERE w.tenant_id = t.id AND w.entity_name = 'ExpenseClaim' AND w.is_active
                          AND w.department_id IS NULL AND w.grade_id IS NULL)
                  AND NOT EXISTS (
                        SELECT 1 FROM approval_workflows w
                        WHERE w.tenant_id = t.id AND upper(w.code) = 'EXPENSE-DEFAULT')
            ), wf AS (
                INSERT INTO approval_workflows (id, tenant_id, code, name, entity_name, department_id, grade_id, is_default, is_active, created_at_utc)
                SELECT workflow_id, tenant_id, 'EXPENSE-DEFAULT', 'Default Expense Claim Approval', 'ExpenseClaim', NULL, NULL, TRUE, TRUE, now()
                FROM targets
                RETURNING id, tenant_id
            )
            INSERT INTO approval_workflow_steps (id, tenant_id, workflow_id, step_order, step_name, approver_role, approver_type, specific_employee_id, escalation_after_hours, is_final_step)
            SELECT gen_random_uuid(), wf.tenant_id, wf.id, 1, 'HR Approval', 'HR Manager', 'HR', NULL, NULL, TRUE
            FROM wf;
            """;

        internal const string SeedExpenseCategoriesSql = """
            INSERT INTO master_data_types (id, tenant_id, code, name_en, name_ar, description, is_system_defined, allow_custom_values, is_active, is_deleted, created_at_utc)
            SELECT gen_random_uuid(), t.id, 'ExpenseCategory', 'Expense Category', 'فئة المصروفات', '', TRUE, TRUE, TRUE, FALSE, now()
            FROM tenants t
            WHERE NOT EXISTS (SELECT 1 FROM master_data_types x WHERE x.tenant_id = t.id AND lower(x.code) = 'expensecategory');

            INSERT INTO master_data_values (id, tenant_id, type_id, code, value_en, value_ar, extra_json, sort_order, is_default, is_system_defined, is_active, is_deleted, created_at_utc)
            SELECT gen_random_uuid(), mt.tenant_id, mt.id, v.code, v.value_en, v.value_ar, NULL, v.sort_order, v.sort_order = 1, TRUE, TRUE, FALSE, now()
            FROM master_data_types mt
            CROSS JOIN (VALUES
                ('TRAVEL', 'Travel', 'سفر', 1),
                ('ACCOMMODATION', 'Accommodation', 'إقامة', 2),
                ('MEALS', 'Meals & Entertainment', 'وجبات وضيافة', 3),
                ('TRANSPORT', 'Local Transport', 'مواصلات محلية', 4),
                ('OFFICE', 'Office Supplies', 'مستلزمات مكتبية', 5),
                ('OTHER', 'Other', 'أخرى', 6)
            ) AS v(code, value_en, value_ar, sort_order)
            WHERE mt.code = 'ExpenseCategory' AND NOT mt.is_deleted
              AND NOT EXISTS (SELECT 1 FROM master_data_values x WHERE x.type_id = mt.id AND upper(x.code) = v.code);
            """;

        internal const string SeedExpenseGlSql = """
            INSERT INTO gl_drivers (
                id, tenant_id, company_id, key, label, category, posting_side, account_type,
                default_code, default_name, match_source, match_mode, match_component_code,
                emits_employer_expense_pair, paired_expense_driver_key, is_system, is_active, sort_order, created_at_utc)
            SELECT gen_random_uuid(), t.tenant_id, NULL, 'EARN:EXPENSE_REIMBURSEMENT', 'Earning — Expense Reimbursement',
                'Earning', 'DR', 'Expense', '5120', 'Employee Expense Reimbursements',
                'Adjustment', 'Exact', 'ADJ_EXPENSE_REIMBURSEMENT', FALSE, NULL, TRUE, TRUE, 18, now()
            FROM (SELECT DISTINCT tenant_id FROM gl_drivers WHERE company_id IS NULL AND is_system = TRUE) t
            WHERE NOT EXISTS (
                SELECT 1 FROM gl_drivers d
                WHERE d.tenant_id = t.tenant_id AND d.company_id IS NULL AND d.key = 'EARN:EXPENSE_REIMBURSEMENT');

            INSERT INTO gl_accounts (id, tenant_id, company_id, code, name, account_type, is_active, created_at_utc)
            SELECT gen_random_uuid(), t.tenant_id, NULL, '5120', 'Employee Expense Reimbursements', 'Expense', TRUE, now()
            FROM (SELECT DISTINCT tenant_id FROM gl_drivers WHERE company_id IS NULL AND is_system = TRUE) t
            WHERE NOT EXISTS (
                SELECT 1 FROM gl_accounts a WHERE a.tenant_id = t.tenant_id AND a.company_id IS NULL AND a.code = '5120');

            INSERT INTO gl_account_mappings (id, tenant_id, company_id, driver_key, account_id, is_active, created_at_utc)
            SELECT gen_random_uuid(), a.tenant_id, NULL, 'EARN:EXPENSE_REIMBURSEMENT', a.id, TRUE, now()
            FROM gl_accounts a
            WHERE a.company_id IS NULL AND a.code = '5120' AND a.name = 'Employee Expense Reimbursements'
              AND EXISTS (SELECT 1 FROM gl_drivers d WHERE d.tenant_id = a.tenant_id AND d.company_id IS NULL AND d.key = 'EARN:EXPENSE_REIMBURSEMENT')
              AND NOT EXISTS (
                SELECT 1 FROM gl_account_mappings m
                WHERE m.tenant_id = a.tenant_id AND m.company_id IS NULL AND m.driver_key = 'EARN:EXPENSE_REIMBURSEMENT');
            """;

        internal const string RevertSeedsSql = """
            DELETE FROM gl_account_mappings WHERE company_id IS NULL AND driver_key = 'EARN:EXPENSE_REIMBURSEMENT';
            DELETE FROM gl_accounts a WHERE a.company_id IS NULL AND a.code = '5120' AND a.name = 'Employee Expense Reimbursements'
                AND NOT EXISTS (SELECT 1 FROM gl_account_mappings m WHERE m.account_id = a.id);
            DELETE FROM gl_drivers WHERE company_id IS NULL AND is_system = TRUE AND key = 'EARN:EXPENSE_REIMBURSEMENT';
            DELETE FROM master_data_values v USING master_data_types t
                WHERE v.type_id = t.id AND t.code = 'ExpenseCategory' AND t.is_system_defined;
            DELETE FROM master_data_types WHERE code = 'ExpenseCategory' AND is_system_defined;
            DELETE FROM approval_workflow_steps s USING approval_workflows w
                WHERE s.workflow_id = w.id AND w.code = 'EXPENSE-DEFAULT' AND w.entity_name = 'ExpenseClaim';
            DELETE FROM approval_workflows WHERE code = 'EXPENSE-DEFAULT' AND entity_name = 'ExpenseClaim';
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "expense_claims",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    employee_name = table.Column<string>(type: "text", nullable: false),
                    claim_number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    total_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    approval_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    submitted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    submitted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    rejection_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    payroll_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payroll_adjustment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    scheduled_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    scheduled_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    paid_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_expense_claims", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "expense_claim_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    claim_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_number = table.Column<int>(type: "integer", nullable: false),
                    expense_date = table.Column<DateOnly>(type: "date", nullable: false),
                    category_code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    category_name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    receipt_storage_key = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    receipt_file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    receipt_content_type = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    receipt_size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    receipt_uploaded_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_expense_claim_lines", x => x.id);
                    table.ForeignKey(
                        name: "FK_expense_claim_lines_expense_claims_claim_id",
                        column: x => x.claim_id,
                        principalTable: "expense_claims",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_expense_claim_lines_claim_id",
                table: "expense_claim_lines",
                column: "claim_id");

            migrationBuilder.CreateIndex(
                name: "IX_expense_claim_lines_tenant_id_category_code_expense_date",
                table: "expense_claim_lines",
                columns: new[] { "tenant_id", "category_code", "expense_date" });

            migrationBuilder.CreateIndex(
                name: "IX_expense_claim_lines_tenant_id_claim_id_line_number",
                table: "expense_claim_lines",
                columns: new[] { "tenant_id", "claim_id", "line_number" });

            migrationBuilder.CreateIndex(
                name: "IX_expense_claim_lines_tenant_id_company_id",
                table: "expense_claim_lines",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_expense_claims_tenant_id_approval_request_id",
                table: "expense_claims",
                columns: new[] { "tenant_id", "approval_request_id" });

            migrationBuilder.CreateIndex(
                name: "IX_expense_claims_tenant_id_claim_number",
                table: "expense_claims",
                columns: new[] { "tenant_id", "claim_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_expense_claims_tenant_id_company_id",
                table: "expense_claims",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_expense_claims_tenant_id_company_id_status",
                table: "expense_claims",
                columns: new[] { "tenant_id", "company_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_expense_claims_tenant_id_employee_id_status",
                table: "expense_claims",
                columns: new[] { "tenant_id", "employee_id", "status" });

            migrationBuilder.Sql(SeedExpenseWorkflowsSql);
            migrationBuilder.Sql(SeedExpenseCategoriesSql);
            migrationBuilder.Sql(SeedExpenseGlSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RevertSeedsSql);

            migrationBuilder.DropTable(
                name: "expense_claim_lines");

            migrationBuilder.DropTable(
                name: "expense_claims");
        }
    }
}
