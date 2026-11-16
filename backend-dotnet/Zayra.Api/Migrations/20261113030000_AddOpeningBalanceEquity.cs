using Microsoft.EntityFrameworkCore.Migrations;
using Zayra.Api.Data;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// Data-only, idempotent, NOT EXISTS-guarded (M6). Seeds what the opening-balance route of the provision catch-up needs, for existing tenants:
    /// (1) the switch <c>payparameter.provision_catchup_route</c> in the client-rate allow-list (without it RatesController refuses the key; with no
    /// company_rate_policies row the route resolves Expense, so nobody's books change); (2) tenant-default account 3900 "Opening Balance Equity"
    /// (Equity) for tenants that already have a tenant-default chart; (3) the SYSTEM posting driver OPENING_BALANCE_EQUITY and its tenant-default mapping,
    /// for tenants that already carry the system driver set. Correctness does not depend on (2) and (3) for a tenant with no chart (the compiled catalog
    /// resolves the same label), but a tenant WITH a chart would otherwise have its first opening-route Lock refused as "account not in the chart".
    /// A tenant whose chart already uses 3900 for a different account gets nothing pointed at it (left for manual mapping).
    /// Down removes only the seeded rows that nothing references; posted history is never touched.
    /// </summary>
    [Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof(ZayraDbContext))]
    [Migration("20261113030000_AddOpeningBalanceEquity")]
    public partial class AddOpeningBalanceEquity : Migration
    {
        internal const string Name = "Opening Balance Equity";

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
INSERT INTO client_rate_definitions (id, tenant_id, rate_key, rate_category, data_type, unit, min_value, max_value, description, is_active, created_at_utc)
SELECT gen_random_uuid(), t.tenant_id, 'payparameter.provision_catchup_route', 'PayParameter', 'string', 'enum', NULL, NULL,
       'Where the go-live catch-up of the EOSB and leave provisions is booked: Expense (default) | OpeningBalance (opening balance equity, not profit and loss)', TRUE, now()
FROM (SELECT DISTINCT tenant_id FROM client_rate_definitions) t
WHERE NOT EXISTS (
    SELECT 1 FROM client_rate_definitions c
    WHERE c.tenant_id = t.tenant_id AND c.rate_key = 'payparameter.provision_catchup_route'
);");
            migrationBuilder.Sql($@"
INSERT INTO gl_accounts (id, tenant_id, company_id, code, name, account_type, is_active, created_at_utc)
SELECT gen_random_uuid(), t.tenant_id, NULL, '3900', '{Name}', 'Equity', TRUE, now()
FROM (SELECT DISTINCT tenant_id FROM gl_accounts WHERE company_id IS NULL) t
WHERE NOT EXISTS (
    SELECT 1 FROM gl_accounts a
    WHERE a.tenant_id = t.tenant_id AND a.company_id IS NULL AND a.code = '3900'
);");
            migrationBuilder.Sql($@"
INSERT INTO gl_drivers (
    id, tenant_id, company_id, key, label, category, posting_side, account_type,
    default_code, default_name, match_source, match_mode, match_component_code,
    emits_employer_expense_pair, paired_expense_driver_key, is_system, is_active, sort_order, created_at_utc)
SELECT gen_random_uuid(), t.tenant_id, NULL, 'OPENING_BALANCE_EQUITY', '{Name}', 'Balancing', 'DR', 'Equity',
    '3900', '{Name}', NULL, 'Any', NULL, FALSE, NULL, TRUE, TRUE, 113, now()
FROM (SELECT DISTINCT tenant_id FROM gl_drivers WHERE company_id IS NULL AND is_system = TRUE) t
WHERE NOT EXISTS (
    SELECT 1 FROM gl_drivers d
    WHERE d.tenant_id = t.tenant_id AND d.company_id IS NULL AND d.key = 'OPENING_BALANCE_EQUITY'
)
  AND NOT EXISTS (
    SELECT 1 FROM gl_accounts c
    WHERE c.tenant_id = t.tenant_id AND c.company_id IS NULL AND c.code = '3900'
      AND (c.name <> '{Name}' OR c.account_type <> 'Equity'));");
            migrationBuilder.Sql($@"
INSERT INTO gl_account_mappings (id, tenant_id, company_id, driver_key, account_id, is_active, created_at_utc)
SELECT gen_random_uuid(), a.tenant_id, NULL, 'OPENING_BALANCE_EQUITY', a.id, TRUE, now()
FROM gl_accounts a
WHERE a.company_id IS NULL AND a.code = '3900' AND a.name = '{Name}' AND a.account_type = 'Equity'
  AND EXISTS (SELECT 1 FROM gl_drivers d WHERE d.tenant_id = a.tenant_id AND d.company_id IS NULL AND d.key = 'OPENING_BALANCE_EQUITY')
  AND NOT EXISTS (
    SELECT 1 FROM gl_account_mappings m
    WHERE m.tenant_id = a.tenant_id AND m.company_id IS NULL AND m.driver_key = 'OPENING_BALANCE_EQUITY'
  );");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM company_rate_policies WHERE rate_key = 'payparameter.provision_catchup_route';");
            migrationBuilder.Sql("DELETE FROM client_rate_definitions WHERE rate_key = 'payparameter.provision_catchup_route';");
            migrationBuilder.Sql(@"
DELETE FROM gl_account_mappings m
USING gl_accounts a
WHERE m.account_id = a.id AND m.company_id IS NULL AND m.driver_key = 'OPENING_BALANCE_EQUITY' AND a.company_id IS NULL AND a.code = '3900';");
            migrationBuilder.Sql("DELETE FROM gl_drivers WHERE company_id IS NULL AND is_system = TRUE AND key = 'OPENING_BALANCE_EQUITY';");
            migrationBuilder.Sql($@"
DELETE FROM gl_accounts a
WHERE a.company_id IS NULL AND a.code = '3900' AND a.name = '{Name}'
  AND NOT EXISTS (SELECT 1 FROM gl_account_mappings m WHERE m.account_id = a.id);");
        }
    }
}
