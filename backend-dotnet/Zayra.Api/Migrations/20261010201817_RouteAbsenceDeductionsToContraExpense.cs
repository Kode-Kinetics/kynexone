using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// Data-only (no schema or model change). Re-types the attendance and leave deduction posting rules
    /// from LIABILITIES to CONTRA-EXPENSES for existing tenants, and adds the payroll rounding rule.
    ///
    /// <para><b>Why.</b> Late/early, loss-of-pay and unpaid-leave deductions credited 2104 "Attendance
    /// Adjustment Payable" and 2105 "Leave Deduction Payable". Nobody is owed that money: the employer
    /// simply pays less for time not worked, and no remittance path exists for those sources
    /// (<c>RemitGroups.ForSource</c> returns null for "Attendance"/"Leave"), so the two payables grew every
    /// month with nothing able to clear them. They now credit contra-expense accounts (5008 "Attendance
    /// &amp; Late Deductions (contra)" and 5009 "Unpaid Leave &amp; LOP Deductions (contra)"), exactly the
    /// treatment DED:SETTLEMENT_RECOVERY → 5113 already has, so net payroll cost is right and no
    /// balance-sheet account is left orphaned.</para>
    ///
    /// <para><b>What it changes, and only where the tenant has not customised it.</b></para>
    /// <list type="number">
    /// <item>gl_accounts: adds the tenant-default 5008 / 5009 / 5199 accounts to every tenant that already
    /// has a tenant-default chart, unless that code already exists.</item>
    /// <item>gl_account_mappings: a TENANT-DEFAULT mapping for DED:ATTENDANCE / DED:LEAVE is repointed only
    /// if it still references the seeded tenant-default 2104 / 2105 account with its seeded name and type,
    /// and only onto the seeded 5008 / 5009 account. Company-level mappings are deliberate per-company
    /// choices and are left alone.</item>
    /// <item>gl_drivers: SYSTEM rows for DED:ATTENDANCE / DED:LEAVE whose default_code is still 2104 / 2105
    /// get the new default account and account type Expense.</item>
    /// <item>The ROUNDING system driver (5199 Payroll Rounding Differences) and its tenant-default mapping,
    /// for tenants that already carry the system driver set.</item>
    /// <item>gl_drivers: every SYSTEM row that was switched off is switched back on. System rules can no
    /// longer be switched off (the API refuses it), and a switched-off one blocks every payroll Lock that
    /// would have used it.</item>
    /// </list>
    ///
    /// <para><b>Code collisions are left for manual mapping.</b> A tenant whose chart already uses 5008,
    /// 5009 or 5199 (tenant default) for a DIFFERENT account — another name or a non-Expense type — gets
    /// nothing pointed at that code: its DED:ATTENDANCE / DED:LEAVE mapping and driver default stay on
    /// 2104 / 2105, and no ROUNDING driver or mapping is added (posting falls back to the built-in rule).
    /// Finance must map those rules by hand. List them before deploying with:
    /// <c>SELECT tenant_id, code, name, account_type FROM gl_accounts WHERE company_id IS NULL AND
    /// ((code = '5008' AND (name &lt;&gt; 'Attendance &amp; Late Deductions (contra)' OR account_type &lt;&gt; 'Expense'))
    /// OR (code = '5009' AND (name &lt;&gt; 'Unpaid Leave &amp; LOP Deductions (contra)' OR account_type &lt;&gt; 'Expense'))
    /// OR (code = '5199' AND (name &lt;&gt; 'Payroll Rounding Differences' OR account_type &lt;&gt; 'Expense')));</c></para>
    ///
    /// <para><b>What it never touches.</b> finance_gl_entries: posted history stays exactly as posted.
    /// Balances already sitting in 2104 / 2105 are real ledger history; reclassifying them is an
    /// accountant's decision, not a migration's. The 2104 / 2105 accounts themselves stay in the chart.</para>
    ///
    /// <para><b>Down</b> restores the seeded driver defaults and tenant-default mappings, and removes the
    /// rows Up added once nothing references them. It does NOT switch system drivers back off: which rows
    /// were off is not recorded, and an off system rule is the fault Up repairs.</para>
    /// </summary>
    public partial class RouteAbsenceDeductionsToContraExpense : Migration
    {
        internal const string AttendanceOldName = "Attendance Adjustment Payable";
        internal const string LeaveOldName = "Leave Deduction Payable";
        internal const string AttendanceNewName = "Attendance & Late Deductions (contra)";
        internal const string LeaveNewName = "Unpaid Leave & LOP Deductions (contra)";
        internal const string RoundingName = "Payroll Rounding Differences";

        private static readonly (string Code, string Name)[] AddedAccounts =
        {
            ("5008", AttendanceNewName), ("5009", LeaveNewName), ("5199", RoundingName),
        };

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1) The new tenant-default accounts, for tenants that already have a tenant-default chart.
            foreach (var (code, name) in AddedAccounts)
                migrationBuilder.Sql($@"
INSERT INTO gl_accounts (id, tenant_id, company_id, code, name, account_type, is_active, created_at_utc)
SELECT gen_random_uuid(), t.tenant_id, NULL, '{code}', '{name}', 'Expense', TRUE, now()
FROM (SELECT DISTINCT tenant_id FROM gl_accounts WHERE company_id IS NULL) t
WHERE NOT EXISTS (
    SELECT 1 FROM gl_accounts a
    WHERE a.tenant_id = t.tenant_id AND a.company_id IS NULL AND a.code = '{code}'
);");

            // 2) Repoint the untouched tenant-default mappings (seeded account, seeded name and type).
            RepointMapping(migrationBuilder, "DED:ATTENDANCE", "2104", AttendanceOldName, "Liability", "5008", AttendanceNewName, "Expense");
            RepointMapping(migrationBuilder, "DED:LEAVE", "2105", LeaveOldName, "Liability", "5009", LeaveNewName, "Expense");

            // 3) System driver defaults still on the old seeded code — except in a tenant whose chart uses
            //    the new code for a different account (left for manual mapping; see the summary).
            migrationBuilder.Sql($@"
UPDATE gl_drivers SET default_code = '5008', default_name = '{AttendanceNewName}', account_type = 'Expense', updated_at_utc = now()
WHERE key = 'DED:ATTENDANCE' AND is_system = TRUE AND default_code = '2104'
  AND {NoCollision("gl_drivers.tenant_id", "5008", AttendanceNewName)};");
            migrationBuilder.Sql($@"
UPDATE gl_drivers SET default_code = '5009', default_name = '{LeaveNewName}', account_type = 'Expense', updated_at_utc = now()
WHERE key = 'DED:LEAVE' AND is_system = TRUE AND default_code = '2105'
  AND {NoCollision("gl_drivers.tenant_id", "5009", LeaveNewName)};");

            // 4) ROUNDING system driver (Balancing / DR / Any), mirroring PayrollGlCatalog.SystemDriverSeeds,
            //    and its tenant-default mapping onto the seeded 5199 account.
            migrationBuilder.Sql($@"
INSERT INTO gl_drivers (
    id, tenant_id, company_id, key, label, category, posting_side, account_type,
    default_code, default_name, match_source, match_mode, match_component_code,
    emits_employer_expense_pair, paired_expense_driver_key, is_system, is_active, sort_order, created_at_utc)
SELECT gen_random_uuid(), t.tenant_id, NULL, 'ROUNDING', '{RoundingName}', 'Balancing', 'DR', 'Expense',
    '5199', '{RoundingName}', NULL, 'Any', NULL, FALSE, NULL, TRUE, TRUE, 110, now()
FROM (SELECT DISTINCT tenant_id FROM gl_drivers WHERE company_id IS NULL AND is_system = TRUE) t
WHERE NOT EXISTS (
    SELECT 1 FROM gl_drivers d
    WHERE d.tenant_id = t.tenant_id AND d.company_id IS NULL AND d.key = 'ROUNDING'
)
  AND {NoCollision("t.tenant_id", "5199", RoundingName)};");
            migrationBuilder.Sql($@"
INSERT INTO gl_account_mappings (id, tenant_id, company_id, driver_key, account_id, is_active, created_at_utc)
SELECT gen_random_uuid(), a.tenant_id, NULL, 'ROUNDING', a.id, TRUE, now()
FROM gl_accounts a
WHERE a.company_id IS NULL AND a.code = '5199' AND a.name = '{RoundingName}' AND a.account_type = 'Expense'
  AND NOT EXISTS (
    SELECT 1 FROM gl_account_mappings m
    WHERE m.tenant_id = a.tenant_id AND m.company_id IS NULL AND m.driver_key = 'ROUNDING'
  );");

            // 5) Switch every system driver back on (Down leaves them on; see the summary).
            migrationBuilder.Sql(@"
UPDATE gl_drivers SET is_active = TRUE, updated_at_utc = now()
WHERE is_system = TRUE AND is_active = FALSE;");
        }

        /// <summary>SQL predicate: the tenant does NOT already use <paramref name="code"/> (tenant default)
        /// for an account other than the seeded <paramref name="name"/> Expense account.</summary>
        private static string NoCollision(string tenantColumn, string code, string name) => $@"NOT EXISTS (
    SELECT 1 FROM gl_accounts c
    WHERE c.tenant_id = {tenantColumn} AND c.company_id IS NULL AND c.code = '{code}'
      AND (c.name <> '{name}' OR c.account_type <> 'Expense'))";

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Mappings first (FK → gl_accounts is Restrict), then drivers, then the accounts Up added.
            migrationBuilder.Sql($@"
DELETE FROM gl_account_mappings m
USING gl_accounts a
WHERE m.account_id = a.id AND m.company_id IS NULL AND m.driver_key = 'ROUNDING'
  AND a.company_id IS NULL AND a.code = '5199' AND a.name = '{RoundingName}';");
            migrationBuilder.Sql("DELETE FROM gl_drivers WHERE company_id IS NULL AND key = 'ROUNDING' AND is_system = TRUE;");

            RepointMapping(migrationBuilder, "DED:ATTENDANCE", "5008", AttendanceNewName, "Expense", "2104", AttendanceOldName, "Liability");
            RepointMapping(migrationBuilder, "DED:LEAVE", "5009", LeaveNewName, "Expense", "2105", LeaveOldName, "Liability");

            migrationBuilder.Sql($@"
UPDATE gl_drivers SET default_code = '2104', default_name = '{AttendanceOldName}', account_type = 'Liability', updated_at_utc = now()
WHERE key = 'DED:ATTENDANCE' AND is_system = TRUE AND default_code = '5008'
  AND {NoCollision("gl_drivers.tenant_id", "5008", AttendanceNewName)};");
            migrationBuilder.Sql($@"
UPDATE gl_drivers SET default_code = '2105', default_name = '{LeaveOldName}', account_type = 'Liability', updated_at_utc = now()
WHERE key = 'DED:LEAVE' AND is_system = TRUE AND default_code = '5009'
  AND {NoCollision("gl_drivers.tenant_id", "5009", LeaveNewName)};");

            // Only the seeded rows, and only once nothing maps to them (a tenant may have mapped a company
            // rule onto one since; that account then stays).
            foreach (var (code, name) in AddedAccounts)
                migrationBuilder.Sql($@"
DELETE FROM gl_accounts a
WHERE a.company_id IS NULL AND a.code = '{code}' AND a.name = '{name}'
  AND NOT EXISTS (SELECT 1 FROM gl_account_mappings m WHERE m.account_id = a.id);");
        }

        /// <summary>Repoints a tenant-default mapping from the seeded (fromCode, fromName, fromType) account
        /// onto the tenant's seeded (toCode, toName, toType) account, and nothing else.</summary>
        private static void RepointMapping(MigrationBuilder migrationBuilder, string driverKey,
            string fromCode, string fromName, string fromType, string toCode, string toName, string toType)
            => migrationBuilder.Sql($@"
UPDATE gl_account_mappings m
SET account_id = target.id, updated_at_utc = now()
FROM gl_accounts src, gl_accounts target
WHERE m.driver_key = '{driverKey}' AND m.company_id IS NULL
  AND src.id = m.account_id AND src.tenant_id = m.tenant_id AND src.company_id IS NULL
  AND src.code = '{fromCode}' AND src.name = '{fromName}' AND src.account_type = '{fromType}'
  AND target.tenant_id = m.tenant_id AND target.company_id IS NULL
  AND target.code = '{toCode}' AND target.name = '{toName}' AND target.account_type = '{toType}';");
    }
}
