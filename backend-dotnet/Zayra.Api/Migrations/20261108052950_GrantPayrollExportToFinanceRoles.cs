using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class GrantPayrollExportToFinanceRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(GrantSql);
        }

        /// <summary>
        /// ONE-SHOT grant of payroll.export (owner decision 3) in EXISTING tenants: the seeded Payroll Manager and Finance Approver roles
        /// export the bulk payslip bundle and the bank / WPS file. New tenants get it from AuthSeeder; Admin already holds it from the boot
        /// backfill. Done here, not in the boot backfill, so an Admin who later removes the key from one of these roles is not overruled on
        /// the next deploy. Only the two built-in roles (system roles, by name, per tenant) are touched: a tenant-defined role that happens
        /// to share a name is not a system role and gets nothing. Add-only, expand-only and re-runnable.
        /// </summary>
        public const string GrantSql = """
            INSERT INTO permissions (id, permission_key, module, description, created_at_utc) VALUES
                (gen_random_uuid(), 'payroll.export', 'Payroll', 'Export payroll and WPS files', now())
            ON CONFLICT (permission_key) DO NOTHING;

            INSERT INTO role_permissions (role_id, permission_id)
            SELECT r.id, p.id
            FROM roles r
            JOIN permissions p ON p.permission_key = 'payroll.export'
            WHERE r.tenant_id IS NOT NULL AND r.is_system = TRUE AND r.is_deleted = FALSE
              AND r.normalized_name IN ('PAYROLL MANAGER', 'FINANCE APPROVER')
            ON CONFLICT DO NOTHING;
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data-only and add-only: a grant is not taken back on rollback.
        }
    }
}
