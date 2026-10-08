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
        }

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
