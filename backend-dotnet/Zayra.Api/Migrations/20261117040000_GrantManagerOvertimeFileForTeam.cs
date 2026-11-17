using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class GrantManagerOvertimeFileForTeam : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(GrantSql);
        }

        /// <summary>
        /// ONE-SHOT grant, in EXISTING tenants, of overtime.file_for_team (file overtime for a member of the caller's own team,
        /// while the company setting overtime_manager_files_for_team allows it, default on) to the tenant's own built-in Manager
        /// role. New tenants get it from AuthSeeder; Admin gets it from the boot backfill like every key. The permission row is
        /// created here when missing, because this runs before the boot seeding that would otherwise create it. Deliberately
        /// narrow: only the built-in (is_system) role normalized MANAGER, never Supervisor, a custom or a renamed role. Add-only:
        /// nothing is removed, so a tenant that later takes the key off is not overruled. Re-runnable.
        /// </summary>
        public const string GrantSql = """
            INSERT INTO permissions (id, permission_key, module, description, created_at_utc) VALUES
                (gen_random_uuid(), 'overtime.file_for_team', 'Overtime',
                 'File overtime for a member of your own team, when the company allows line managers to', now())
            ON CONFLICT (permission_key) DO NOTHING;

            INSERT INTO role_permissions (role_id, permission_id)
            SELECT r.id, p.id
            FROM roles r
            JOIN permissions p ON p.permission_key = 'overtime.file_for_team'
            WHERE r.tenant_id IS NOT NULL
              AND r.is_system
              AND NOT r.is_deleted
              AND r.normalized_name = 'MANAGER'
            ON CONFLICT DO NOTHING;
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data-only and add-only: a grant is not taken back on rollback.
        }
    }
}
