using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Zayra.Api.Data;

#nullable disable

namespace Zayra.Api.Migrations;

/// <summary>
/// Repairs columns and indexes that were present in the EF model/snapshot but were historically
/// created only by EnsureCreated. Real migration-built databases therefore lacked them even though
/// `has-pending-model-changes` reported clean. Every operation is idempotent so this also upgrades
/// older development databases that already received some or all of the EnsureCreated schema.
/// </summary>
[DbContext(typeof(ZayraDbContext))]
[Migration("20260816013100_RepairMigrationModelParity")]
public partial class RepairMigrationModelParity : AddRefreshTokenFamilies
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE approval_requests ADD COLUMN IF NOT EXISTS company_id uuid;
            ALTER TABLE approval_requests ADD COLUMN IF NOT EXISTS current_approver_employee_id integer;
            ALTER TABLE approval_requests ADD COLUMN IF NOT EXISTS current_approver_user_id uuid;
            ALTER TABLE approval_requests ADD COLUMN IF NOT EXISTS current_approver_name varchar(180) NOT NULL DEFAULT '';
            ALTER TABLE approval_requests ADD COLUMN IF NOT EXISTS current_approver_role varchar(80) NOT NULL DEFAULT '';
            ALTER TABLE approval_requests ADD COLUMN IF NOT EXISTS current_approver_type varchar(60) NOT NULL DEFAULT '';
            ALTER TABLE approval_requests ADD COLUMN IF NOT EXISTS current_queue varchar(180) NOT NULL DEFAULT '';
            ALTER TABLE approval_requests ADD COLUMN IF NOT EXISTS sla_hours integer NOT NULL DEFAULT 24;
            ALTER TABLE approval_requests ADD COLUMN IF NOT EXISTS due_at_utc timestamptz;
            ALTER TABLE approval_requests ADD COLUMN IF NOT EXISTS last_routed_at_utc timestamptz;
            ALTER TABLE approval_requests ADD COLUMN IF NOT EXISTS escalated_at_utc timestamptz;
            ALTER TABLE approval_requests ADD COLUMN IF NOT EXISTS escalated_to_role varchar(80) NOT NULL DEFAULT '';
            ALTER TABLE approval_requests ADD COLUMN IF NOT EXISTS priority varchar(40) NOT NULL DEFAULT 'Normal';
            ALTER TABLE approval_requests ADD COLUMN IF NOT EXISTS requested_for_employee_id integer;

            ALTER TABLE approval_requests ALTER COLUMN current_approver_name DROP DEFAULT;
            ALTER TABLE approval_requests ALTER COLUMN current_approver_role DROP DEFAULT;
            ALTER TABLE approval_requests ALTER COLUMN current_approver_type DROP DEFAULT;
            ALTER TABLE approval_requests ALTER COLUMN current_queue DROP DEFAULT;
            ALTER TABLE approval_requests ALTER COLUMN sla_hours DROP DEFAULT;
            ALTER TABLE approval_requests ALTER COLUMN escalated_to_role DROP DEFAULT;
            ALTER TABLE approval_requests ALTER COLUMN priority DROP DEFAULT;

            ALTER TABLE employee_change_requests ADD COLUMN IF NOT EXISTS approval_request_id uuid;

            ALTER TABLE salary_structures ADD COLUMN IF NOT EXISTS eligible_designation_ids_json json NOT NULL DEFAULT '[]'::json;
            ALTER TABLE salary_structures ADD COLUMN IF NOT EXISTS eligible_grade_ids_json json NOT NULL DEFAULT '[]'::json;
            ALTER TABLE salary_structures ADD COLUMN IF NOT EXISTS max_basic_salary numeric(14,2) NOT NULL DEFAULT 0;
            ALTER TABLE salary_structures ADD COLUMN IF NOT EXISTS max_gross_salary numeric(14,2) NOT NULL DEFAULT 0;
            ALTER TABLE salary_structures ADD COLUMN IF NOT EXISTS min_basic_salary numeric(14,2) NOT NULL DEFAULT 0;
            ALTER TABLE salary_structures ADD COLUMN IF NOT EXISTS min_gross_salary numeric(14,2) NOT NULL DEFAULT 0;
            ALTER TABLE salary_structures ADD COLUMN IF NOT EXISTS previous_version_id uuid;
            ALTER TABLE salary_structures ADD COLUMN IF NOT EXISTS version_number integer NOT NULL DEFAULT 1;

            ALTER TABLE salary_structures ALTER COLUMN eligible_designation_ids_json DROP DEFAULT;
            ALTER TABLE salary_structures ALTER COLUMN eligible_grade_ids_json DROP DEFAULT;
            ALTER TABLE salary_structures ALTER COLUMN max_basic_salary DROP DEFAULT;
            ALTER TABLE salary_structures ALTER COLUMN max_gross_salary DROP DEFAULT;
            ALTER TABLE salary_structures ALTER COLUMN min_basic_salary DROP DEFAULT;
            ALTER TABLE salary_structures ALTER COLUMN min_gross_salary DROP DEFAULT;
            ALTER TABLE salary_structures ALTER COLUMN version_number DROP DEFAULT;

            ALTER TABLE payroll_payment_batches
                ALTER COLUMN wps_status TYPE varchar(40) USING wps_status::varchar(40);

            CREATE INDEX IF NOT EXISTS "IX_approval_requests_tenant_id_company_id"
                ON approval_requests (tenant_id, company_id);
            CREATE INDEX IF NOT EXISTS "IX_approval_requests_tenant_id_company_id_status"
                ON approval_requests (tenant_id, company_id, status);
            CREATE INDEX IF NOT EXISTS "IX_approval_requests_tenant_id_status_current_approver_employee_id"
                ON approval_requests (tenant_id, status, current_approver_employee_id);
            CREATE INDEX IF NOT EXISTS "IX_approval_requests_tenant_id_status_current_approver_user_id"
                ON approval_requests (tenant_id, status, current_approver_user_id);
            CREATE INDEX IF NOT EXISTS "IX_approval_requests_tenant_id_status_due_at_utc"
                ON approval_requests (tenant_id, status, due_at_utc);
            CREATE INDEX IF NOT EXISTS "IX_employee_change_requests_approval_request_id"
                ON employee_change_requests (approval_request_id);

            CREATE INDEX IF NOT EXISTS "IX_benefit_contributions_tenant_id_company_id"
                ON benefit_contributions (tenant_id, company_id);
            CREATE INDEX IF NOT EXISTS "IX_benefit_eligibility_rules_tenant_id_company_id"
                ON benefit_eligibility_rules (tenant_id, company_id);
            CREATE INDEX IF NOT EXISTS "IX_benefit_enrollments_tenant_id_company_id"
                ON benefit_enrollments (tenant_id, company_id);
            CREATE INDEX IF NOT EXISTS "IX_benefit_payroll_deduction_links_tenant_id_company_id"
                ON benefit_payroll_deduction_links (tenant_id, company_id);
            CREATE INDEX IF NOT EXISTS "IX_benefit_plans_tenant_id_company_id"
                ON benefit_plans (tenant_id, company_id);
            CREATE INDEX IF NOT EXISTS "IX_payroll_opening_balances_tenant_id_company_id"
                ON payroll_opening_balances (tenant_id, company_id);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP INDEX IF EXISTS "IX_payroll_opening_balances_tenant_id_company_id";
            DROP INDEX IF EXISTS "IX_benefit_plans_tenant_id_company_id";
            DROP INDEX IF EXISTS "IX_benefit_payroll_deduction_links_tenant_id_company_id";
            DROP INDEX IF EXISTS "IX_benefit_enrollments_tenant_id_company_id";
            DROP INDEX IF EXISTS "IX_benefit_eligibility_rules_tenant_id_company_id";
            DROP INDEX IF EXISTS "IX_benefit_contributions_tenant_id_company_id";
            DROP INDEX IF EXISTS "IX_employee_change_requests_approval_request_id";
            DROP INDEX IF EXISTS "IX_approval_requests_tenant_id_status_due_at_utc";
            DROP INDEX IF EXISTS "IX_approval_requests_tenant_id_status_current_approver_user_id";
            DROP INDEX IF EXISTS "IX_approval_requests_tenant_id_status_current_approver_employee_id";
            DROP INDEX IF EXISTS "IX_approval_requests_tenant_id_company_id_status";
            DROP INDEX IF EXISTS "IX_approval_requests_tenant_id_company_id";

            ALTER TABLE payroll_payment_batches ALTER COLUMN wps_status TYPE text;

            ALTER TABLE salary_structures DROP COLUMN IF EXISTS version_number;
            ALTER TABLE salary_structures DROP COLUMN IF EXISTS previous_version_id;
            ALTER TABLE salary_structures DROP COLUMN IF EXISTS min_gross_salary;
            ALTER TABLE salary_structures DROP COLUMN IF EXISTS min_basic_salary;
            ALTER TABLE salary_structures DROP COLUMN IF EXISTS max_gross_salary;
            ALTER TABLE salary_structures DROP COLUMN IF EXISTS max_basic_salary;
            ALTER TABLE salary_structures DROP COLUMN IF EXISTS eligible_grade_ids_json;
            ALTER TABLE salary_structures DROP COLUMN IF EXISTS eligible_designation_ids_json;

            ALTER TABLE employee_change_requests DROP COLUMN IF EXISTS approval_request_id;

            ALTER TABLE approval_requests DROP COLUMN IF EXISTS requested_for_employee_id;
            ALTER TABLE approval_requests DROP COLUMN IF EXISTS priority;
            ALTER TABLE approval_requests DROP COLUMN IF EXISTS escalated_to_role;
            ALTER TABLE approval_requests DROP COLUMN IF EXISTS escalated_at_utc;
            ALTER TABLE approval_requests DROP COLUMN IF EXISTS last_routed_at_utc;
            ALTER TABLE approval_requests DROP COLUMN IF EXISTS due_at_utc;
            ALTER TABLE approval_requests DROP COLUMN IF EXISTS sla_hours;
            ALTER TABLE approval_requests DROP COLUMN IF EXISTS current_queue;
            ALTER TABLE approval_requests DROP COLUMN IF EXISTS current_approver_type;
            ALTER TABLE approval_requests DROP COLUMN IF EXISTS current_approver_role;
            ALTER TABLE approval_requests DROP COLUMN IF EXISTS current_approver_name;
            ALTER TABLE approval_requests DROP COLUMN IF EXISTS current_approver_user_id;
            ALTER TABLE approval_requests DROP COLUMN IF EXISTS current_approver_employee_id;
            ALTER TABLE approval_requests DROP COLUMN IF EXISTS company_id;
            """);
    }
}
