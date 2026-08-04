using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddGlEntryCompanyDimension : Migration
    {
        // POD-B1b — strictly additive. Two independent parts:
        //
        //  1. finance_gl_entries.company_id (nullable) + two indexes. A PLAIN DIMENSION, not
        //     ICompanyScoped — no global query filter is attached to it, so every existing row (all NULL)
        //     stays visible to every GL report exactly as today. It carries legal-entity attribution for
        //     per-company trial balances, per-company period close, and the exact bonus
        //     accrual→clearing match when a bonus batch spans companies.
        //
        //  2. NOT EXISTS-guarded seeds of the BONUS_PAYABLE / LOAN_RECEIVABLE / ADVANCE_RECEIVABLE
        //     chart-of-accounts rows, tenant-default mappings and system drivers, for the ~55 live
        //     tenants seeded before this pod — so the accounts appear in the CoA UI and are remappable
        //     per company via FinanceGlController without a manual seed-defaults re-run.
        //
        // Correctness does NOT depend on part 2: PayrollGlCatalog.Defaults already resolves the same
        // "2300 - Bonus Payable" / "1400 - Employee Loans Receivable" / "1410 - Employee Salary Advances"
        // labels via the compiled fallback — the exact strings those modules hard-coded before B1b.
        // The mapping insert matches on code AND name so a tenant that already owns a DIFFERENT 2300
        // (the demo seeders write "2300 - GOSI Payable" as ledger text) is never hijacked; that tenant
        // keeps resolving through the compiled fallback.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "finance_gl_entries",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_finance_gl_entries_tenant_id_company_id_period",
                table: "finance_gl_entries",
                columns: new[] { "tenant_id", "company_id", "period" });

            migrationBuilder.CreateIndex(
                name: "IX_finance_gl_entries_tenant_id_event_type_source_entity_ref",
                table: "finance_gl_entries",
                columns: new[] { "tenant_id", "event_type", "source_entity_ref" });

            // 1) Tenant-default accounts for every tenant that already holds a tenant-default CoA.
            migrationBuilder.Sql(@"
INSERT INTO gl_accounts (id, tenant_id, company_id, code, name, account_type, is_active, created_at_utc)
SELECT gen_random_uuid(), t.tenant_id, NULL, s.code, s.name, s.account_type, TRUE, now()
FROM (SELECT DISTINCT tenant_id FROM gl_accounts WHERE company_id IS NULL) t
CROSS JOIN (VALUES
    ('2300', 'Bonus Payable',             'Liability'),
    ('1400', 'Employee Loans Receivable', 'Asset'),
    ('1410', 'Employee Salary Advances',  'Asset')
) AS s(code, name, account_type)
WHERE NOT EXISTS (
    SELECT 1 FROM gl_accounts a
    WHERE a.tenant_id = t.tenant_id AND a.company_id IS NULL AND a.code = s.code
);");

            // 2) Tenant-default driver→account mappings, pointed at the accounts from (1). Matching on
            //    code AND name guarantees we never bind a driver to a same-coded foreign account.
            migrationBuilder.Sql(@"
INSERT INTO gl_account_mappings (id, tenant_id, company_id, driver_key, account_id, is_active, created_at_utc)
SELECT gen_random_uuid(), a.tenant_id, NULL, s.driver_key, a.id, TRUE, now()
FROM (VALUES
    ('BONUS_PAYABLE',      '2300', 'Bonus Payable'),
    ('LOAN_RECEIVABLE',    '1400', 'Employee Loans Receivable'),
    ('ADVANCE_RECEIVABLE', '1410', 'Employee Salary Advances')
) AS s(driver_key, code, name)
JOIN gl_accounts a ON a.company_id IS NULL AND a.code = s.code AND a.name = s.name
WHERE NOT EXISTS (
    SELECT 1 FROM gl_account_mappings m
    WHERE m.tenant_id = a.tenant_id AND m.company_id IS NULL AND m.driver_key = s.driver_key
);");

            // 3) Tenant-default system drivers — mirrors PayrollGlCatalog.SystemDriverSeeds. Category
            //    'Balancing' so component routing (Earning/Deduction only) can never select them.
            migrationBuilder.Sql(@"
INSERT INTO gl_drivers (
    id, tenant_id, company_id, key, label, category, posting_side, account_type,
    default_code, default_name, match_source, match_mode, match_component_code,
    emits_employer_expense_pair, paired_expense_driver_key, is_system, is_active, sort_order, created_at_utc)
SELECT gen_random_uuid(), t.tenant_id, NULL, s.key, s.label, 'Balancing', s.posting_side, s.account_type,
    s.default_code, s.default_name, NULL, 'Any', NULL, FALSE, NULL, TRUE, TRUE, s.sort_order, now()
FROM (SELECT DISTINCT tenant_id FROM gl_drivers WHERE company_id IS NULL AND is_system = TRUE) t
CROSS JOIN (VALUES
    ('BONUS_PAYABLE',      'Bonus Payable',             'CR', 'Liability', '2300', 'Bonus Payable',             103),
    ('LOAN_RECEIVABLE',    'Employee Loans Receivable', 'DR', 'Asset',     '1400', 'Employee Loans Receivable', 104),
    ('ADVANCE_RECEIVABLE', 'Employee Salary Advances',  'DR', 'Asset',     '1410', 'Employee Salary Advances',  105)
) AS s(key, label, posting_side, account_type, default_code, default_name, sort_order)
WHERE NOT EXISTS (
    SELECT 1 FROM gl_drivers d
    WHERE d.tenant_id = t.tenant_id AND d.company_id IS NULL AND d.key = s.key
);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // ── POD-B1b-FIX (P2-5) — Down() reverses the SCHEMA only. It deliberately does NOT delete the
            // seeded chart-of-accounts rows, mappings or drivers.
            //
            // Why: every seed in Up() is a NOT EXISTS-guarded insert, so for a tenant that ALREADY owned a
            // '2300 - Bonus Payable' account (or already had a BONUS_PAYABLE mapping) this migration
            // inserted nothing — and nothing in the schema records which rows were inserted by whom. The
            // previous Down() deleted by (code, name) / driver_key across EVERY tenant, so rolling back
            // would have destroyed the finance configuration of tenants that created those rows
            // themselves, silently repointing their bonus/loan/advance postings to the compiled fallback.
            // A rollback must never be able to lose tenant data it did not create.
            //
            // Leaving the rows is harmless and fully reversible in the other direction: they are ordinary,
            // inactive-if-unused reference rows; posting resolves the identical labels through
            // PayrollGlCatalog.Defaults with or without them; and re-running Up() is a no-op because the
            // NOT EXISTS guards match what is already there. A deployment that genuinely wants them gone
            // can remove them per tenant through FinanceGlController, which enforces the referential
            // checks this raw SQL cannot.

            migrationBuilder.DropIndex(
                name: "IX_finance_gl_entries_tenant_id_company_id_period",
                table: "finance_gl_entries");

            migrationBuilder.DropIndex(
                name: "IX_finance_gl_entries_tenant_id_event_type_source_entity_ref",
                table: "finance_gl_entries");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "finance_gl_entries");
        }
    }
}
