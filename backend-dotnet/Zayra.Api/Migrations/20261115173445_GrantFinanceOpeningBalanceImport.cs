using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class GrantFinanceOpeningBalanceImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(GrantSql);
        }

        /// <summary>
        /// ONE-SHOT grant, in EXISTING tenants, of finance.opening_balances.import (import the opening-balance loans and advances,
        /// which post opening journals) to the tenant's own built-in Finance and Finance Approver roles. New tenants get it from
        /// AuthSeeder; Admin gets it from the boot backfill like every key. The permission row is created here when missing,
        /// because this runs before the boot seeding that would otherwise create it. Deliberately narrow: only the built-in
        /// (is_system) roles normalized FINANCE and FINANCE APPROVER, never a custom or renamed role. Add-only: nothing is removed,
        /// so a tenant that later takes the key off is not overruled; done here, not in the boot backfill, for the same reason.
        /// Re-runnable.
        /// </summary>
        public const string GrantSql = """
            INSERT INTO permissions (id, permission_key, module, description, created_at_utc) VALUES
                (gen_random_uuid(), 'finance.opening_balances.import', 'Finance',
                 'Import carried-in loan and advance opening balances (posts their opening journals); no other import section', now())
            ON CONFLICT (permission_key) DO NOTHING;

            INSERT INTO role_permissions (role_id, permission_id)
            SELECT r.id, p.id
            FROM roles r
            JOIN permissions p ON p.permission_key = 'finance.opening_balances.import'
            WHERE r.tenant_id IS NOT NULL
              AND r.is_system
              AND NOT r.is_deleted
              AND r.normalized_name IN ('FINANCE', 'FINANCE APPROVER')
            ON CONFLICT DO NOTHING;
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data-only and add-only: a grant is not taken back on rollback.
        }
    }
}
