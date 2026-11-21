using Microsoft.EntityFrameworkCore.Migrations;
using Zayra.Api.Data;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// ONE-SHOT, add-only grant in EXISTING tenants (new tenants get it from AuthSeeder.EnsureTenantRolesAsync):
    ///   - creates the catalogue key benefits.usage.approve: deciding a benefit usage request is Finance's, HR makes the request
    ///     (it used to need payroll.approve, which HR Manager also holds, so the requester side and the approver side were not separate);
    ///   - the built-in Finance, Finance Approver and Payroll Manager roles get it, the roles that already decide loans, advances
    ///     and cash allowances. HR Director, HR Manager and HR Officer do not.
    /// Admin is not touched here: the boot backfill gives Admin every catalogue key it is not withheld, so it holds the key from
    /// the first start after this deploy. Same pattern as GrantFinanceEntitlementsRead: only each tenant's own built-in role
    /// (is_system, that normalized name), never a custom or renamed role; nothing is removed, so a key a tenant took off its role
    /// stays off; re-runnable; no row is rewritten. Done here, not in the boot backfill, so a tenant that later removes the key
    /// from a Finance role is not overruled on the next deploy. Sorts after 20261118030000_AddBenefitUsageRequests.
    /// </summary>
    [Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof(ZayraDbContext))]
    [Migration("20261118040000_GrantBenefitUsageApprove")]
    public partial class GrantBenefitUsageApprove : Migration
    {
        public const string GrantSql = """
            INSERT INTO permissions (id, permission_key, module, description, created_at_utc) VALUES
                (gen_random_uuid(), 'benefits.usage.approve', 'Entitlements', 'Approve or reject benefit usage requests (Finance; never the requester or the employee it is for)', now())
            ON CONFLICT (permission_key) DO NOTHING;

            INSERT INTO role_permissions (role_id, permission_id)
            SELECT r.id, p.id
            FROM roles r
            JOIN permissions p ON p.permission_key = 'benefits.usage.approve'
            WHERE r.tenant_id IS NOT NULL AND r.is_system AND NOT r.is_deleted
              AND r.normalized_name IN ('FINANCE', 'FINANCE APPROVER', 'PAYROLL MANAGER')
            ON CONFLICT DO NOTHING;
            """;

        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(GrantSql);

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data-only and add-only: a grant is not taken back on rollback.
        }
    }
}
