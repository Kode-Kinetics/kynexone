using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddEmployeeWelcomeCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Fail fast instead of queueing behind a long transaction: every statement below takes an ACCESS EXCLUSIVE
            // lock (ADD COLUMN on employee_user_accounts, ADD CONSTRAINT on companies), and a DDL waiting on a lock
            // blocks every later reader of that table. Transaction-scoped (EF runs each migration in one transaction),
            // so it ends with this migration. A timeout fails the deploy cleanly with 55P03; re-run when quiet.
            migrationBuilder.Sql(LockTimeoutSql);

            migrationBuilder.AddColumn<DateTime>(
                name: "welcome_code_expires_at_utc",
                table: "employee_user_accounts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "welcome_code_failed_attempts",
                table: "employee_user_accounts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "welcome_code_hash",
                table: "employee_user_accounts",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "welcome_code_issued_at_utc",
                table: "employee_user_accounts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "welcome_code_issued_by",
                table: "employee_user_accounts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "welcome_code_redeemed_at_utc",
                table: "employee_user_accounts",
                type: "timestamp with time zone",
                nullable: true);

            // A company email domain routes sign-in without a workspace, so an ACTIVE domain belongs to ONE tenant
            // (companies of the same tenant may share it). EF cannot model EXCLUDE; btree_gist gives uuid its <>.
            // Expand-only and re-runnable. Existing cross-tenant collisions stop the migration with a named error so
            // they are resolved deliberately (domain-ownership verification is backlog).
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS btree_gist;");
            migrationBuilder.Sql("""
                DO $$
                DECLARE clash text;
                BEGIN
                    SELECT string_agg(d, ', ') INTO clash FROM (
                        SELECT lower(email_domain) AS d FROM companies
                        WHERE is_active AND NOT is_deleted AND email_domain <> ''
                        GROUP BY lower(email_domain) HAVING count(DISTINCT tenant_id) > 1) x;
                    IF clash IS NOT NULL THEN
                        RAISE EXCEPTION 'EMAIL_DOMAIN_CLAIMED_BY_SEVERAL_TENANTS: %. Clear the domain on all but one tenant''s companies, then deploy again.', clash
                            USING ERRCODE = '23P01';
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ex_companies__email_domain_one_tenant') THEN
                        ALTER TABLE companies ADD CONSTRAINT ex_companies__email_domain_one_tenant
                            EXCLUDE USING gist (lower(email_domain) WITH =, tenant_id WITH <>)
                            WHERE (is_active AND NOT is_deleted AND email_domain <> '');
                    END IF;
                END
                $$;
                """);

            migrationBuilder.Sql(GrantAccessKeysSql);
        }

        /// <summary>Transaction-scoped lock wait ceiling for this migration's DDL.</summary>
        public const string LockTimeoutSql = "SET LOCAL lock_timeout = '5s';";

        /// <summary>
        /// ONE-SHOT grant of the two new keys in EXISTING tenants (new tenants get them from AuthSeeder.EnsureTenantRolesAsync;
        /// Admin roles from the boot backfill). Done here, not in the boot backfill, so it runs exactly once: an Admin who
        /// later removes a key from a built-in role is not overruled on the next deploy (the "revocation became escalation"
        /// class AuthSeeder documents). Add-only and re-runnable.
        /// <list type="bullet">
        ///   <item>built-in HR Manager: employees.access.issue + employees.access.reset; built-in HR Officer: issue only
        ///   (the PR #210 contract, Amendment 3 F1);</item>
        ///   <item>every tenant role that holds <c>security.manage</c> gets both keys too. PrivilegeCeiling lets a caller
        ///   assign a role, or act on its holders, only when the caller holds every key the role carries; without this, a
        ///   custom "Console Admin" role would silently lose its reach over HR Officer and HR Manager the moment those roles
        ///   gained the new keys. Backfilling keeps the ceiling's subset rule exact (no exemption for "new" keys, which an
        ///   override could then hand out unheld), and it widens nothing material: a security.manage holder can already
        ///   send any non-senior user a password-reset link.</item>
        /// </list>
        /// </summary>
        public const string GrantAccessKeysSql = """
            INSERT INTO permissions (id, permission_key, module, description, created_at_utc) VALUES
                (gen_random_uuid(), 'employees.access.issue', 'Employees', 'Give employees their KynexOne welcome code (sign-in slips)', now()),
                (gen_random_uuid(), 'employees.access.reset', 'Employees', 'Reset the sign-in of an employee who already uses KynexOne', now())
            ON CONFLICT (permission_key) DO NOTHING;

            INSERT INTO role_permissions (role_id, permission_id)
            SELECT r.id, p.id
            FROM roles r
            JOIN permissions p ON p.permission_key IN ('employees.access.issue', 'employees.access.reset')
            WHERE r.tenant_id IS NOT NULL
              AND (
                    (r.is_system AND r.normalized_name = 'HR MANAGER')
                 OR (r.is_system AND r.normalized_name = 'HR OFFICER' AND p.permission_key = 'employees.access.issue')
                 OR EXISTS (SELECT 1 FROM role_permissions sp JOIN permissions sk ON sk.id = sp.permission_id
                            WHERE sp.role_id = r.id AND sk.permission_key = 'security.manage'))
            ON CONFLICT DO NOTHING;
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE companies DROP CONSTRAINT IF EXISTS ex_companies__email_domain_one_tenant;");

            migrationBuilder.DropColumn(
                name: "welcome_code_expires_at_utc",
                table: "employee_user_accounts");

            migrationBuilder.DropColumn(
                name: "welcome_code_failed_attempts",
                table: "employee_user_accounts");

            migrationBuilder.DropColumn(
                name: "welcome_code_hash",
                table: "employee_user_accounts");

            migrationBuilder.DropColumn(
                name: "welcome_code_issued_at_utc",
                table: "employee_user_accounts");

            migrationBuilder.DropColumn(
                name: "welcome_code_issued_by",
                table: "employee_user_accounts");

            migrationBuilder.DropColumn(
                name: "welcome_code_redeemed_at_utc",
                table: "employee_user_accounts");
        }
    }
}
