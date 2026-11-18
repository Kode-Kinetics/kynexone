using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Zayra.Api.Data;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// ONE-SHOT grant, in EXISTING tenants, of entitlements.read to the built-in Finance Approver and Payroll Manager ("company Finance confirms"
    /// monthly cash allowances: both hold payroll.approve, and the confirm list lives on Benefits by grade). New tenants get it from AuthSeeder.
    /// Same pattern as GrantHrManagerRecruitment. Deliberately narrow:
    ///   - only each tenant's own built-in (is_system, not deleted) roles normalized FINANCE APPROVER and PAYROLL MANAGER, never a custom or renamed role;
    ///   - only entitlements.read, never entitlements.manage (editing and publishing the grid stays with HR);
    ///   - add-only: nothing is removed, so a key a tenant took off one of these roles stays off; done here, not in the boot backfill, so a tenant that
    ///     later removes it is not overruled on the next deploy. Re-runnable. People already signed in pick it up when their session next refreshes.
    /// </summary>
    [DbContext(typeof(ZayraDbContext))]
    [Migration("20261116230000_GrantFinanceEntitlementsRead")]
    public partial class GrantFinanceEntitlementsRead : Migration
    {
        public const string GrantSql = """
            INSERT INTO role_permissions (role_id, permission_id)
            SELECT r.id, p.id
            FROM roles r
            JOIN permissions p ON p.permission_key = 'entitlements.read'
            WHERE r.tenant_id IS NOT NULL
              AND r.is_system
              AND NOT r.is_deleted
              AND r.normalized_name IN ('FINANCE APPROVER', 'PAYROLL MANAGER')
            ON CONFLICT DO NOTHING;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(GrantSql);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data-only and add-only: a grant is not taken back on rollback.
        }
    }
}
