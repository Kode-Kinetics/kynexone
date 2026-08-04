using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class SeedCashBankGlDefault : Migration
    {
        // POD-B1 — strictly additive, idempotent data seed. Provisions the CASH_BANK chart-of-accounts
        // row (1000 - Cash/Bank), its tenant-default driver→account mapping, and the CASH_BANK system
        // driver for the ~55 live tenants that already have GL rows — so the account shows in the CoA UI
        // and is remappable per company via FinanceGlController WITHOUT a manual seed-defaults re-run.
        //
        // Correctness does NOT depend on this seed: the compiled PayrollGlCatalog.Defaults fallback
        // already resolves "1000 - Cash/Bank" for every tenant at posting time (settlement/remittance).
        // This migration only surfaces + makes-remappable the account for tenants seeded before POD-B1.
        // Every statement is NOT EXISTS-guarded, so re-running (or overlap with a seed-defaults call) is
        // a no-op. Targets only tenants that already hold tenant-default GL rows; brand-new tenants get
        // CASH_BANK from the (now-updated) GlDriverSeeder on creation.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1) Tenant-default Cash/Bank account (company_id NULL) for every tenant that already has a
            //    tenant-default chart of accounts.
            migrationBuilder.Sql(@"
INSERT INTO gl_accounts (id, tenant_id, company_id, code, name, account_type, is_active, created_at_utc)
SELECT gen_random_uuid(), t.tenant_id, NULL, '1000', 'Cash/Bank', 'Asset', TRUE, now()
FROM (SELECT DISTINCT tenant_id FROM gl_accounts WHERE company_id IS NULL) t
WHERE NOT EXISTS (
    SELECT 1 FROM gl_accounts a
    WHERE a.tenant_id = t.tenant_id AND a.company_id IS NULL AND a.code = '1000'
);");

            // 2) Tenant-default CASH_BANK → 1000 mapping, pointed at the account seeded in (1).
            migrationBuilder.Sql(@"
INSERT INTO gl_account_mappings (id, tenant_id, company_id, driver_key, account_id, is_active, created_at_utc)
SELECT gen_random_uuid(), a.tenant_id, NULL, 'CASH_BANK', a.id, TRUE, now()
FROM gl_accounts a
WHERE a.company_id IS NULL AND a.code = '1000'
  AND NOT EXISTS (
    SELECT 1 FROM gl_account_mappings m
    WHERE m.tenant_id = a.tenant_id AND m.company_id IS NULL AND m.driver_key = 'CASH_BANK'
  );");

            // 3) Tenant-default CASH_BANK system driver (Balancing / CR / Any) for every tenant that
            //    already has the system driver set seeded — mirrors PayrollGlCatalog.SystemDriverSeeds.
            migrationBuilder.Sql(@"
INSERT INTO gl_drivers (
    id, tenant_id, company_id, key, label, category, posting_side, account_type,
    default_code, default_name, match_source, match_mode, match_component_code,
    emits_employer_expense_pair, paired_expense_driver_key, is_system, is_active, sort_order, created_at_utc)
SELECT gen_random_uuid(), t.tenant_id, NULL, 'CASH_BANK', 'Cash / Bank', 'Balancing', 'CR', 'Asset',
    '1000', 'Cash/Bank', NULL, 'Any', NULL, FALSE, NULL, TRUE, TRUE, 102, now()
FROM (SELECT DISTINCT tenant_id FROM gl_drivers WHERE company_id IS NULL AND is_system = TRUE) t
WHERE NOT EXISTS (
    SELECT 1 FROM gl_drivers d
    WHERE d.tenant_id = t.tenant_id AND d.company_id IS NULL AND d.key = 'CASH_BANK'
);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Remove ONLY the system rows this migration seeds. Mappings first (FK → gl_accounts is
            // Restrict), then the driver, then the account.
            migrationBuilder.Sql("DELETE FROM gl_account_mappings WHERE company_id IS NULL AND driver_key = 'CASH_BANK';");
            migrationBuilder.Sql("DELETE FROM gl_drivers WHERE company_id IS NULL AND key = 'CASH_BANK' AND is_system = TRUE;");
            migrationBuilder.Sql("DELETE FROM gl_accounts WHERE company_id IS NULL AND code = '1000' AND name = 'Cash/Bank';");
        }
    }
}
