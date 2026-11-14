using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// The write-off of an employee receivable (the loan balance a final settlement could not recover): one additive nullable column, the
    /// request kind added to the existing CHECK, and an idempotent seed of account 5114 and its posting driver.
    ///
    /// <para><b>Schema.</b> <c>gl_adjustment_requests.subject_id</c> names the record a request acts on (the receivable being written off).
    /// The kind CHECK is widened, never narrowed: every existing row still satisfies it.</para>
    ///
    /// <para><b>Seed (idempotent, NOT EXISTS-guarded).</b> Tenant-default chart account 5114 "Employee Receivable Write-off Expense"
    /// for tenants that already have a tenant-default chart, the SYSTEM driver RECEIVABLE_WRITE_OFF_EXPENSE (Balancing, so component
    /// routing never selects it) and its tenant-default mapping. Correctness does not depend on the seed for a tenant with no chart (the
    /// compiled default resolves the same label), but a tenant WITH a chart would otherwise have its first approval refused as
    /// "account not in the chart". A tenant whose chart already uses 5114 for a DIFFERENT account gets nothing pointed at that code;
    /// list them before deploying with
    /// <c>SELECT tenant_id, code, name, account_type FROM gl_accounts WHERE company_id IS NULL AND code = '5114'
    /// AND (name &lt;&gt; 'Employee Receivable Write-off Expense' OR account_type &lt;&gt; 'Expense');</c></para>
    ///
    /// <para><b>Down</b> removes the column, restores the narrower CHECK (after removing any write-off requests, which could not satisfy
    /// it) and removes only seeded rows nothing references. Posted ledger rows are never touched.</para>
    /// </summary>
    public partial class AddReceivableWriteOff : Migration
    {
        internal const string AccountName = "Employee Receivable Write-off Expense";
        internal const string DriverKey = "RECEIVABLE_WRITE_OFF_EXPENSE";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_gl_adjustment_requests__kind",
                table: "gl_adjustment_requests");

            migrationBuilder.AddColumn<Guid>(
                name: "subject_id",
                table: "gl_adjustment_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_gl_adjustment_requests__kind",
                table: "gl_adjustment_requests",
                sql: "kind IN ('EosbTrueUp','LegacyAbsenceReclass','ReceivableWriteOff')");

            migrationBuilder.Sql($@"
INSERT INTO gl_accounts (id, tenant_id, company_id, code, name, account_type, is_active, created_at_utc)
SELECT gen_random_uuid(), t.tenant_id, NULL, '5114', '{AccountName}', 'Expense', TRUE, now()
FROM (SELECT DISTINCT tenant_id FROM gl_accounts WHERE company_id IS NULL) t
WHERE NOT EXISTS (
    SELECT 1 FROM gl_accounts a
    WHERE a.tenant_id = t.tenant_id AND a.company_id IS NULL AND a.code = '5114'
);");

            migrationBuilder.Sql($@"
INSERT INTO gl_drivers (
    id, tenant_id, company_id, key, label, category, posting_side, account_type,
    default_code, default_name, match_source, match_mode, match_component_code,
    emits_employer_expense_pair, paired_expense_driver_key, is_system, is_active, sort_order, created_at_utc)
SELECT gen_random_uuid(), t.tenant_id, NULL, '{DriverKey}', '{AccountName}', 'Balancing', 'DR', 'Expense',
    '5114', '{AccountName}', NULL, 'Any', NULL, FALSE, NULL, TRUE, TRUE, 113, now()
FROM (SELECT DISTINCT tenant_id FROM gl_drivers WHERE company_id IS NULL AND is_system = TRUE) t
WHERE NOT EXISTS (
    SELECT 1 FROM gl_drivers d
    WHERE d.tenant_id = t.tenant_id AND d.company_id IS NULL AND d.key = '{DriverKey}'
)
  AND NOT EXISTS (
    SELECT 1 FROM gl_accounts c
    WHERE c.tenant_id = t.tenant_id AND c.company_id IS NULL AND c.code = '5114'
      AND (c.name <> '{AccountName}' OR c.account_type <> 'Expense'));");

            migrationBuilder.Sql($@"
INSERT INTO gl_account_mappings (id, tenant_id, company_id, driver_key, account_id, is_active, created_at_utc)
SELECT gen_random_uuid(), a.tenant_id, NULL, '{DriverKey}', a.id, TRUE, now()
FROM gl_accounts a
WHERE a.company_id IS NULL AND a.code = '5114' AND a.name = '{AccountName}' AND a.account_type = 'Expense'
  AND EXISTS (SELECT 1 FROM gl_drivers d WHERE d.tenant_id = a.tenant_id AND d.company_id IS NULL AND d.key = '{DriverKey}')
  AND NOT EXISTS (
    SELECT 1 FROM gl_account_mappings m
    WHERE m.tenant_id = a.tenant_id AND m.company_id IS NULL AND m.driver_key = '{DriverKey}'
  );");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Mappings first (FK to gl_accounts is Restrict), then the driver, then the account if nothing maps to it any more.
            migrationBuilder.Sql($@"
DELETE FROM gl_account_mappings m
USING gl_accounts a
WHERE m.account_id = a.id AND m.company_id IS NULL AND m.driver_key = '{DriverKey}'
  AND a.company_id IS NULL AND a.code = '5114';");
            migrationBuilder.Sql($"DELETE FROM gl_drivers WHERE company_id IS NULL AND is_system = TRUE AND key = '{DriverKey}';");
            migrationBuilder.Sql($@"
DELETE FROM gl_accounts a
WHERE a.company_id IS NULL AND a.code = '5114' AND a.name = '{AccountName}'
  AND NOT EXISTS (SELECT 1 FROM gl_account_mappings m WHERE m.account_id = a.id);");
            migrationBuilder.Sql("DELETE FROM gl_adjustment_requests WHERE kind = 'ReceivableWriteOff';");

            migrationBuilder.DropCheckConstraint(
                name: "ck_gl_adjustment_requests__kind",
                table: "gl_adjustment_requests");

            migrationBuilder.DropColumn(
                name: "subject_id",
                table: "gl_adjustment_requests");

            migrationBuilder.AddCheckConstraint(
                name: "ck_gl_adjustment_requests__kind",
                table: "gl_adjustment_requests",
                sql: "kind IN ('EosbTrueUp','LegacyAbsenceReclass')");
        }
    }
}
