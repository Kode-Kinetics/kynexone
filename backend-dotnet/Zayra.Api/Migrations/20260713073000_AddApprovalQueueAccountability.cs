using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    public partial class AddApprovalQueueAccountability : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE approval_requests ADD COLUMN IF NOT EXISTS requested_for_employee_id integer NULL;
                ALTER TABLE approval_requests ADD COLUMN IF NOT EXISTS company_id uuid NULL;
                ALTER TABLE approval_requests ADD COLUMN IF NOT EXISTS current_approver_employee_id integer NULL;
                ALTER TABLE approval_requests ADD COLUMN IF NOT EXISTS current_approver_user_id uuid NULL;
                ALTER TABLE approval_requests ADD COLUMN IF NOT EXISTS current_approver_name character varying(180) NOT NULL DEFAULT '';
                ALTER TABLE approval_requests ADD COLUMN IF NOT EXISTS current_approver_role character varying(80) NOT NULL DEFAULT '';
                ALTER TABLE approval_requests ADD COLUMN IF NOT EXISTS current_approver_type character varying(60) NOT NULL DEFAULT '';
                ALTER TABLE approval_requests ADD COLUMN IF NOT EXISTS current_queue character varying(180) NOT NULL DEFAULT '';
                ALTER TABLE approval_requests ADD COLUMN IF NOT EXISTS sla_hours integer NOT NULL DEFAULT 24;
                ALTER TABLE approval_requests ADD COLUMN IF NOT EXISTS due_at_utc timestamp with time zone NULL;
                ALTER TABLE approval_requests ADD COLUMN IF NOT EXISTS last_routed_at_utc timestamp with time zone NULL;
                ALTER TABLE approval_requests ADD COLUMN IF NOT EXISTS escalated_at_utc timestamp with time zone NULL;
                ALTER TABLE approval_requests ADD COLUMN IF NOT EXISTS escalated_to_role character varying(80) NOT NULL DEFAULT '';
                ALTER TABLE approval_requests ADD COLUMN IF NOT EXISTS priority character varying(40) NOT NULL DEFAULT 'Normal';

                CREATE INDEX IF NOT EXISTS ix_approval_requests_tenant_status_current_approver_user_id
                    ON approval_requests (tenant_id, status, current_approver_user_id);
                CREATE INDEX IF NOT EXISTS ix_approval_requests_tenant_status_current_approver_employee_id
                    ON approval_requests (tenant_id, status, current_approver_employee_id);
                CREATE INDEX IF NOT EXISTS ix_approval_requests_tenant_status_due_at_utc
                    ON approval_requests (tenant_id, status, due_at_utc);
                CREATE INDEX IF NOT EXISTS ix_approval_requests_tenant_company_status
                    ON approval_requests (tenant_id, company_id, status);

                WITH employee_change_requests_pending AS (
                    SELECT ar.id AS approval_request_id,
                           e.id AS employee_id,
                           e.company_id,
                           e.manager_employee_id,
                           m.full_name AS manager_name,
                           m.designation AS manager_designation,
                           m.user_account_id AS manager_user_id
                    FROM approval_requests ar
                    JOIN employee_change_requests ecr ON ecr.tenant_id = ar.tenant_id AND ecr.id::text = ar.entity_id
                    JOIN employees e ON e.tenant_id = ar.tenant_id AND e.id = ecr.employee_id
                    LEFT JOIN employees m ON m.tenant_id = ar.tenant_id AND m.id = e.manager_employee_id
                    WHERE ar.entity_name = 'EmployeeChangeRequest'
                      AND ar.status = 'Pending'
                )
                UPDATE approval_requests ar
                SET requested_for_employee_id = src.employee_id,
                    company_id = src.company_id,
                    current_approver_employee_id = src.manager_employee_id,
                    current_approver_user_id = src.manager_user_id,
                    current_approver_name = COALESCE(src.manager_name, ''),
                    current_approver_role = COALESCE(NULLIF(src.manager_designation, ''), 'Manager'),
                    current_approver_type = CASE WHEN src.manager_employee_id IS NULL THEN 'Role' ELSE 'Manager' END,
                    current_queue = CASE WHEN src.manager_employee_id IS NULL THEN 'Role:HR Manager' ELSE 'Manager:' || src.manager_name END,
                    sla_hours = 24,
                    due_at_utc = COALESCE(ar.due_at_utc, ar.created_at_utc + interval '24 hours'),
                    last_routed_at_utc = COALESCE(ar.last_routed_at_utc, ar.created_at_utc),
                    priority = CASE WHEN ar.priority = '' THEN 'High' ELSE ar.priority END
                FROM employee_change_requests_pending src
                WHERE ar.id = src.approval_request_id;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS ix_approval_requests_tenant_company_status;
                DROP INDEX IF EXISTS ix_approval_requests_tenant_status_due_at_utc;
                DROP INDEX IF EXISTS ix_approval_requests_tenant_status_current_approver_employee_id;
                DROP INDEX IF EXISTS ix_approval_requests_tenant_status_current_approver_user_id;

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
                ALTER TABLE approval_requests DROP COLUMN IF EXISTS requested_for_employee_id;
                """);
        }
    }
}
