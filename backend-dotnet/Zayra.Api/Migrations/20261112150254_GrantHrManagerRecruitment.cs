using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class GrantHrManagerRecruitment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(GrantSql);
        }

        /// <summary>
        /// ONE-SHOT grant, in EXISTING tenants, of the three recruitment keys the built-in HR Manager needs to run the recruitment
        /// pipeline and the offer lifecycle (owner decision 2026-11-12): recruitment.read, recruitment.write, recruitment.approve.
        /// New tenants get them from AuthSeeder.EnsureTenantRolesAsync. Deliberately narrow:
        ///   - only the tenant's own built-in HR Manager role (is_system, normalized name HR MANAGER), never a custom or renamed
        ///     role, HR Director, Recruiter, HR Officer or Admin;
        ///   - never recruitment.delete or any other key;
        ///   - add-only: nothing is removed, so a key the tenant took off its HR Manager role stays off.
        /// Done here, not in the boot backfill, so a tenant that later removes one of the keys is not overruled on the next
        /// deploy. Re-runnable. People already signed in pick the keys up when their session next refreshes.
        /// </summary>
        public const string GrantSql = """
            INSERT INTO role_permissions (role_id, permission_id)
            SELECT r.id, p.id
            FROM roles r
            JOIN permissions p ON p.permission_key IN ('recruitment.read', 'recruitment.write', 'recruitment.approve')
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
