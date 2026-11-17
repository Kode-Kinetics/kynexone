using Microsoft.EntityFrameworkCore.Migrations;
using Zayra.Api.Data;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// ONE-SHOT, add-only grant in EXISTING tenants (owner decision 2026-11-16; new tenants get it from AuthSeeder.EnsureTenantRolesAsync):
    ///   - creates the two narrow recruitment keys in the catalogue: recruitment.requisition.raise, recruitment.interview.feedback;
    ///   - the built-in Manager gets exactly those two (raise own-department requisitions, give feedback on assigned interviews);
    ///   - the built-in HR Officer gets recruitment.read and recruitment.write, nothing above (no approve, no delete).
    /// Deliberately narrow, the pattern of GrantHrManagerRecruitment: only each tenant's own built-in role (is_system, that normalized name),
    /// never a custom or renamed role; nothing is removed, so a key a tenant took off its role stays off; re-runnable. No row is rewritten.
    /// Done here, not in the boot backfill, so a tenant that later removes a key is not overruled on the next deploy.
    /// </summary>
    [Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof(ZayraDbContext))]
    [Migration("20261117040000_GrantManagerHrOfficerRecruitment")]
    public partial class GrantManagerHrOfficerRecruitment : Migration
    {
        public const string GrantSql = """
            INSERT INTO permissions (id, permission_key, module, description, created_at_utc) VALUES
                (gen_random_uuid(), 'recruitment.requisition.raise', 'Recruitment', 'Raise and submit your own manpower requisitions for your own department', now()),
                (gen_random_uuid(), 'recruitment.interview.feedback', 'Recruitment', 'See and give feedback on the interviews you are assigned to', now())
            ON CONFLICT (permission_key) DO NOTHING;

            INSERT INTO role_permissions (role_id, permission_id)
            SELECT r.id, p.id
            FROM roles r
            JOIN permissions p ON p.permission_key IN ('recruitment.requisition.raise', 'recruitment.interview.feedback')
            WHERE r.tenant_id IS NOT NULL AND r.is_system AND NOT r.is_deleted AND r.normalized_name = 'MANAGER'
            ON CONFLICT DO NOTHING;

            INSERT INTO role_permissions (role_id, permission_id)
            SELECT r.id, p.id
            FROM roles r
            JOIN permissions p ON p.permission_key IN ('recruitment.read', 'recruitment.write')
            WHERE r.tenant_id IS NOT NULL AND r.is_system AND NOT r.is_deleted AND r.normalized_name = 'HR OFFICER'
            ON CONFLICT DO NOTHING;
            """;

        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(GrantSql);

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data-only and add-only: a grant is not taken back on rollback.
        }
    }
}
