using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class GrantAttendancePolicyManage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(GrantSql);
        }

        /// <summary>
        /// ONE-SHOT grant of attendance.policy_manage in EXISTING tenants (new tenants get it from
        /// AuthSeeder.EnsureTenantRolesAsync; Admin roles from the boot backfill). Done here, not in the boot backfill, so it
        /// runs once and an Admin who later removes the key from a built-in role is not overruled on the next deploy.
        /// Mirrors the attendance.* prefix grant: built-in HR Manager and HR Director. Add-only and re-runnable.
        /// </summary>
        public const string GrantSql = """
            INSERT INTO permissions (id, permission_key, module, description, created_at_utc) VALUES
                (gen_random_uuid(), 'attendance.policy_manage', 'Attendance', 'Manage attendance rules (lateness, early exit, half day, working day)', now())
            ON CONFLICT (permission_key) DO NOTHING;

            INSERT INTO role_permissions (role_id, permission_id)
            SELECT r.id, p.id
            FROM roles r
            JOIN permissions p ON p.permission_key = 'attendance.policy_manage'
            WHERE r.tenant_id IS NOT NULL
              AND r.is_system
              AND r.normalized_name IN ('HR MANAGER', 'HR DIRECTOR')
            ON CONFLICT DO NOTHING;
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data-only and add-only: a grant is not taken back on rollback (the key is unused by older code).
        }
    }
}
