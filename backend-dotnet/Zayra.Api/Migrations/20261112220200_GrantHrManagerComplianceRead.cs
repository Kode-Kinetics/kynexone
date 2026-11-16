using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class GrantHrManagerComplianceRead : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(GrantSql);
        }

        /// <summary>
        /// ONE-SHOT grant, in EXISTING tenants, of compliance.read to the built-in HR Manager (owner decision 2026-11-12): read the
        /// visa, passport, work-permit and contract registers, the expiry alerts and the compliance dashboard. New tenants get it
        /// from AuthSeeder.EnsureTenantRolesAsync. Deliberately narrow:
        ///   - only the tenant's own built-in HR Manager role (is_system, normalized name HR MANAGER), never a custom or renamed
        ///     role, HR Officer, HR Director or any other role;
        ///   - only compliance.read, never compliance.write or compliance.approve;
        ///   - add-only: nothing is removed, so a key the tenant took off its HR Manager role stays off.
        /// Done here, not in the boot backfill, so a tenant that later removes the key is not overruled on the next deploy.
        /// Re-runnable. People already signed in pick the key up when their session next refreshes.
        /// </summary>
        public const string GrantSql = """
            INSERT INTO role_permissions (role_id, permission_id)
            SELECT r.id, p.id
            FROM roles r
            JOIN permissions p ON p.permission_key = 'compliance.read'
            WHERE r.tenant_id IS NOT NULL
              AND r.is_system
              AND NOT r.is_deleted
              AND r.normalized_name = 'HR MANAGER'
            ON CONFLICT DO NOTHING;
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data-only and add-only: a grant is not taken back on rollback.
        }
    }
}
