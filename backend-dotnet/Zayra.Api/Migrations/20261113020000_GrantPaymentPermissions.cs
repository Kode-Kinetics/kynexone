using Microsoft.EntityFrameworkCore.Migrations;
using Zayra.Api.Data;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// Data-only, add-only (M2). Creates the four narrow payment permission keys in the catalog and grants them, once, to the two built-in roles
    /// that should hold them in EXISTING tenants (new tenants get them from AuthSeeder): the Finance Approver the WPS-status, settle and remit keys, the Payroll Manager the
    /// batch key only. Same pattern as GrantHrManagerRecruitment: only each tenant's own is_system role with that normalized name
    /// (never a custom or renamed role), nothing removed, re-runnable; done here rather than in the boot backfill so a tenant that later takes a key
    /// off a role is not overruled on the next deploy. Admin keeps payroll.export, which still opens every payment action; the boot backfill gives
    /// it the new keys too.
    ///
    /// <para><b>Segregation of duties.</b> The Payroll Manager prepares the bank file (batch key only); the Finance Approver pays and confirms
    /// (WPS status, settle, remit) and does NOT hold the batch key, so no built-in role both prepares and pays. The reverted payroll.export widening gave both roles the bank file's IBAN lines, the bulk payslip bundle
    /// and the bank-confirmation screens as well; these keys give exactly the four action families and nothing else.</para>
    /// </summary>
    [Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof(ZayraDbContext))]
    [Migration("20261113020000_GrantPaymentPermissions")]
    public partial class GrantPaymentPermissions : Migration
    {
        public const string GrantSql = """
            INSERT INTO permissions (id, permission_key, module, description, created_at_utc) VALUES
                (gen_random_uuid(), 'payroll.payment_batch.manage', 'Payroll', 'Create payment batches and generate or download their bank or WPS files', now()),
                (gen_random_uuid(), 'payroll.wps_status.update', 'Payroll', 'Record the WPS or bank file status (submitted, accepted, rejected) and attach bank evidence', now()),
                (gen_random_uuid(), 'payroll.payment.settle', 'Payroll', 'Settle net pay to the general ledger, record payments made outside the bank file, and reverse them', now()),
                (gen_random_uuid(), 'payroll.remit.manage', 'Payroll', 'Remit statutory and loan liabilities and reverse a remittance', now())
            ON CONFLICT (permission_key) DO NOTHING;

            INSERT INTO role_permissions (role_id, permission_id)
            SELECT r.id, p.id
            FROM roles r
            JOIN permissions p ON p.permission_key IN
                ('payroll.wps_status.update', 'payroll.payment.settle', 'payroll.remit.manage')
            WHERE r.tenant_id IS NOT NULL AND r.is_system AND NOT r.is_deleted AND r.normalized_name = 'FINANCE APPROVER'
            ON CONFLICT DO NOTHING;

            INSERT INTO role_permissions (role_id, permission_id)
            SELECT r.id, p.id
            FROM roles r
            JOIN permissions p ON p.permission_key = 'payroll.payment_batch.manage'
            WHERE r.tenant_id IS NOT NULL AND r.is_system AND NOT r.is_deleted AND r.normalized_name = 'PAYROLL MANAGER'
            ON CONFLICT DO NOTHING;
            """;

        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(GrantSql);

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data-only and add-only: a grant is not taken back on rollback.
        }
    }
}
