using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class GrantFinanceGlExport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(GrantSql);
        }

        /// <summary>
        /// ONE-SHOT grant of finance.gl.export (AM-19) in EXISTING tenants: taking a journal-export file moved from
        /// finance.gl.manage to this key, so every live role holding finance.gl.manage, built-in or tenant-defined, gets it and
        /// keeps the ability it had yesterday. New tenants get it from AuthSeeder (Payroll Manager bundle; Admin from the boot
        /// backfill). Done here, not in the boot backfill, so an Admin who later removes the key from a role is not overruled on
        /// the next deploy. Add-only and re-runnable.
        /// </summary>
        public const string GrantSql = """
            INSERT INTO permissions (id, permission_key, module, description, created_at_utc) VALUES
                (gen_random_uuid(), 'finance.gl.export', 'Finance', 'Download GL journal export files and see their line detail', now())
            ON CONFLICT (permission_key) DO NOTHING;

            INSERT INTO role_permissions (role_id, permission_id)
            SELECT DISTINCT rp.role_id, e.id
            FROM role_permissions rp
            JOIN permissions m ON m.id = rp.permission_id AND m.permission_key = 'finance.gl.manage'
            JOIN roles r ON r.id = rp.role_id AND r.tenant_id IS NOT NULL
            JOIN permissions e ON e.permission_key = 'finance.gl.export'
            ON CONFLICT DO NOTHING;
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data-only and add-only: a grant is not taken back on rollback.
        }
    }
}
