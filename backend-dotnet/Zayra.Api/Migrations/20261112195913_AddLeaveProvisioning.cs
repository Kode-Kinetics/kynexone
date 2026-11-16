using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// The monthly ANNUAL-LEAVE provision: one additive table plus an idempotent data seed. Nothing is dropped or altered, and no
    /// tenant's books change until finance switches <c>payparameter.leave_provisioning</c> on for a company.
    ///
    /// <para><b>Schema.</b> <c>leave_provision_snapshots</c>, one row per employee per payroll month: the unused days, the day rate and
    /// the value the Lock journal's provision movement was measured on (the witness an auditor re-performs). Unique
    /// (tenant, employee, period) is what makes a retried Lock unable to provide twice. Cascades from its payroll run, so a tenant
    /// purge removes it with the run; a void deletes the run's rows in the same unit of work as the reversing journal.</para>
    ///
    /// <para><b>Data seed (idempotent, NOT EXISTS-guarded).</b> (1) the switch in each tenant's client-rate allow-list, without which
    /// RatesController refuses the key; (2) tenant-default chart accounts 2330 "Accrued Annual Leave Provision" (Liability) and 5115
    /// "Annual Leave Provision Expense" (Expense) for tenants that already have a tenant-default chart; (3) the two SYSTEM posting
    /// drivers LEAVE_PROVISION / LEAVE_PROVISION_EXPENSE and their tenant-default mappings, for tenants that already carry the system
    /// driver set. Correctness does not depend on (2) and (3) for a tenant with NO chart (the compiled catalog default resolves the same
    /// labels), but a tenant WITH a chart would otherwise have its first provisioning Lock refused as "account not in the chart".</para>
    ///
    /// <para><b>Code collisions are left for manual mapping.</b> A tenant whose tenant-default chart already uses 2330 or 5115 for a
    /// DIFFERENT account gets nothing pointed at that code. List them before deploying with:
    /// <c>SELECT tenant_id, code, name, account_type FROM gl_accounts WHERE company_id IS NULL AND
    /// ((code = '2330' AND (name &lt;&gt; 'Accrued Annual Leave Provision' OR account_type &lt;&gt; 'Liability'))
    /// OR (code = '5115' AND (name &lt;&gt; 'Annual Leave Provision Expense' OR account_type &lt;&gt; 'Expense')));</c></para>
    ///
    /// <para><b>Down</b> drops the table and removes only the seeded rows that nothing references (a mapping a tenant has since pointed at
    /// one of the accounts keeps that account). It does NOT remove ledger rows: posted history stays exactly as posted.</para>
    /// </summary>
    public partial class AddLeaveProvisioning : Migration
    {
        internal const string ProvisionName = "Accrued Annual Leave Provision";
        internal const string ExpenseName = "Annual Leave Provision Expense";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "leave_provision_snapshots",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    period = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    measured_at = table.Column<DateOnly>(type: "date", nullable: false),
                    payroll_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    days = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    daily_rate = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    basis = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    obligation = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    carried_before = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    movement = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    is_catch_up = table.Column<bool>(type: "boolean", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_leave_provision_snapshots", x => x.id);
                    table.ForeignKey(
                        name: "FK_leave_provision_snapshots_payroll_runs_payroll_run_id",
                        column: x => x.payroll_run_id,
                        principalTable: "payroll_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_leave_provision_snapshots_payroll_run_id",
                table: "leave_provision_snapshots",
                column: "payroll_run_id");

            migrationBuilder.CreateIndex(
                name: "IX_leave_provision_snapshots_tenant_id_company_id",
                table: "leave_provision_snapshots",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_leave_provision_snapshots_tenant_id_company_id_period",
                table: "leave_provision_snapshots",
                columns: new[] { "tenant_id", "company_id", "period" });

            migrationBuilder.CreateIndex(
                name: "IX_leave_provision_snapshots_tenant_id_payroll_run_id",
                table: "leave_provision_snapshots",
                columns: new[] { "tenant_id", "payroll_run_id" });

            migrationBuilder.CreateIndex(
                name: "ux_leave_provision_snapshots_tenant_employee_period",
                table: "leave_provision_snapshots",
                columns: new[] { "tenant_id", "employee_id", "period" },
                unique: true);

            // ── Idempotent data seed ─────────────────────────────────────────────────────────────────────
            // 1) The provisioning switch in the CLIENT-RATE ALLOW-LIST. Seeding the DEFINITION changes nobody's books: with no
            //    company_rate_policies row the mode resolves Off.
            migrationBuilder.Sql(@"
INSERT INTO client_rate_definitions (id, tenant_id, rate_key, rate_category, data_type, unit, min_value, max_value, description, is_active, created_at_utc)
SELECT gen_random_uuid(), t.tenant_id, 'payparameter.leave_provisioning', 'PayParameter', 'string', 'enum', NULL, NULL,
       'Monthly annual-leave provision at payroll Lock: Off (default) | BalanceMonthly', TRUE, now()
FROM (SELECT DISTINCT tenant_id FROM client_rate_definitions) t
WHERE NOT EXISTS (
    SELECT 1 FROM client_rate_definitions c
    WHERE c.tenant_id = t.tenant_id AND c.rate_key = 'payparameter.leave_provisioning'
);");

            // 2) The two tenant-default accounts, for tenants that already have a tenant-default chart.
            Account(migrationBuilder, "2330", ProvisionName, "Liability");
            Account(migrationBuilder, "5115", ExpenseName, "Expense");

            // 3) The two SYSTEM drivers (Balancing: never selected by component routing) and their tenant-default mappings.
            Driver(migrationBuilder, "LEAVE_PROVISION", ProvisionName, "CR", "Liability", "2330", 111);
            Driver(migrationBuilder, "LEAVE_PROVISION_EXPENSE", ExpenseName, "DR", "Expense", "5115", 112);
            Mapping(migrationBuilder, "LEAVE_PROVISION", "2330", ProvisionName, "Liability");
            Mapping(migrationBuilder, "LEAVE_PROVISION_EXPENSE", "5115", ExpenseName, "Expense");
        }

        private static void Account(MigrationBuilder migrationBuilder, string code, string name, string type)
            => migrationBuilder.Sql($@"
INSERT INTO gl_accounts (id, tenant_id, company_id, code, name, account_type, is_active, created_at_utc)
SELECT gen_random_uuid(), t.tenant_id, NULL, '{code}', '{name}', '{type}', TRUE, now()
FROM (SELECT DISTINCT tenant_id FROM gl_accounts WHERE company_id IS NULL) t
WHERE NOT EXISTS (
    SELECT 1 FROM gl_accounts a
    WHERE a.tenant_id = t.tenant_id AND a.company_id IS NULL AND a.code = '{code}'
);");

        private static void Driver(MigrationBuilder migrationBuilder, string key, string name, string side, string type, string code, int sort)
            => migrationBuilder.Sql($@"
INSERT INTO gl_drivers (
    id, tenant_id, company_id, key, label, category, posting_side, account_type,
    default_code, default_name, match_source, match_mode, match_component_code,
    emits_employer_expense_pair, paired_expense_driver_key, is_system, is_active, sort_order, created_at_utc)
SELECT gen_random_uuid(), t.tenant_id, NULL, '{key}', '{name}', 'Balancing', '{side}', '{type}',
    '{code}', '{name}', NULL, 'Any', NULL, FALSE, NULL, TRUE, TRUE, {sort}, now()
FROM (SELECT DISTINCT tenant_id FROM gl_drivers WHERE company_id IS NULL AND is_system = TRUE) t
WHERE NOT EXISTS (
    SELECT 1 FROM gl_drivers d
    WHERE d.tenant_id = t.tenant_id AND d.company_id IS NULL AND d.key = '{key}'
)
  AND NOT EXISTS (
    SELECT 1 FROM gl_accounts c
    WHERE c.tenant_id = t.tenant_id AND c.company_id IS NULL AND c.code = '{code}'
      AND (c.name <> '{name}' OR c.account_type <> '{type}'));");

        private static void Mapping(MigrationBuilder migrationBuilder, string key, string code, string name, string type)
            => migrationBuilder.Sql($@"
INSERT INTO gl_account_mappings (id, tenant_id, company_id, driver_key, account_id, is_active, created_at_utc)
SELECT gen_random_uuid(), a.tenant_id, NULL, '{key}', a.id, TRUE, now()
FROM gl_accounts a
WHERE a.company_id IS NULL AND a.code = '{code}' AND a.name = '{name}' AND a.account_type = '{type}'
  AND EXISTS (SELECT 1 FROM gl_drivers d WHERE d.tenant_id = a.tenant_id AND d.company_id IS NULL AND d.key = '{key}')
  AND NOT EXISTS (
    SELECT 1 FROM gl_account_mappings m
    WHERE m.tenant_id = a.tenant_id AND m.company_id IS NULL AND m.driver_key = '{key}'
  );");

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Remove ONLY what Up seeded. A company rate row for the key would be orphaned by the allow-list removal, so those go too
            // (they only ever switched provisioning on or off).
            migrationBuilder.Sql("DELETE FROM company_rate_policies WHERE rate_key = 'payparameter.leave_provisioning';");
            migrationBuilder.Sql("DELETE FROM client_rate_definitions WHERE rate_key = 'payparameter.leave_provisioning';");

            // Mappings first (FK to gl_accounts is Restrict), then drivers, then accounts nothing maps to any more.
            migrationBuilder.Sql(@"
DELETE FROM gl_account_mappings m
USING gl_accounts a
WHERE m.account_id = a.id AND m.company_id IS NULL AND m.driver_key IN ('LEAVE_PROVISION', 'LEAVE_PROVISION_EXPENSE')
  AND a.company_id IS NULL AND a.code IN ('2330', '5115');");
            migrationBuilder.Sql("DELETE FROM gl_drivers WHERE company_id IS NULL AND is_system = TRUE AND key IN ('LEAVE_PROVISION', 'LEAVE_PROVISION_EXPENSE');");
            foreach (var (code, name) in new[] { ("2330", ProvisionName), ("5115", ExpenseName) })
                migrationBuilder.Sql($@"
DELETE FROM gl_accounts a
WHERE a.company_id IS NULL AND a.code = '{code}' AND a.name = '{name}'
  AND NOT EXISTS (SELECT 1 FROM gl_account_mappings m WHERE m.account_id = a.id);");

            migrationBuilder.DropTable(
                name: "leave_provision_snapshots");
        }
    }
}
