using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    public partial class BackfillEmployeeChangeApprovalRequests : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE EXTENSION IF NOT EXISTS pgcrypto;

                ALTER TABLE employee_change_requests
                ADD COLUMN IF NOT EXISTS approval_request_id uuid NULL;

                CREATE INDEX IF NOT EXISTS ix_employee_change_requests_approval_request_id
                ON employee_change_requests (approval_request_id);

                INSERT INTO approval_workflows (id, tenant_id, code, name, entity_name, is_active, created_at_utc)
                SELECT gen_random_uuid(), tenant_id, 'EMPLOYEE-CHANGE', 'Employee Master Change Approval', 'EmployeeChangeRequest', true, now()
                FROM employee_change_requests ecr
                WHERE ecr.status = 'PendingApproval'
                  AND ecr.approval_request_id IS NULL
                  AND NOT EXISTS (
                      SELECT 1 FROM approval_workflows aw
                      WHERE aw.tenant_id = ecr.tenant_id
                        AND aw.code = 'EMPLOYEE-CHANGE'
                  )
                GROUP BY tenant_id;

                INSERT INTO approval_workflow_steps (id, tenant_id, workflow_id, step_order, step_name, approver_role, approver_type, is_final_step)
                SELECT gen_random_uuid(), aw.tenant_id, aw.id, 1, 'HR Manager Approval', 'HR Manager', 'Role', true
                FROM approval_workflows aw
                WHERE aw.code = 'EMPLOYEE-CHANGE'
                  AND NOT EXISTS (
                      SELECT 1 FROM approval_workflow_steps s
                      WHERE s.tenant_id = aw.tenant_id
                        AND s.workflow_id = aw.id
                  );

                WITH pending_changes AS (
                    SELECT ecr.id, ecr.tenant_id, ecr.employee_id, ecr.requested_by_user_id, ecr.created_at_utc, e.employee_code, e.full_name, aw.id AS workflow_id
                    FROM employee_change_requests ecr
                    JOIN employees e ON e.tenant_id = ecr.tenant_id AND e.id = ecr.employee_id
                    JOIN approval_workflows aw ON aw.tenant_id = ecr.tenant_id AND aw.code = 'EMPLOYEE-CHANGE'
                    WHERE ecr.status = 'PendingApproval'
                      AND ecr.approval_request_id IS NULL
                ),
                created_requests AS (
                    INSERT INTO approval_requests (id, tenant_id, workflow_id, entity_name, entity_id, title, status, current_step_order, requested_by_user_id, created_at_utc)
                    SELECT gen_random_uuid(), tenant_id, workflow_id, 'EmployeeChangeRequest', id::text,
                           'Employee change approval - ' || employee_code || ' ' || full_name,
                           'Pending', 1, requested_by_user_id, created_at_utc
                    FROM pending_changes
                    RETURNING id, tenant_id, entity_id
                )
                UPDATE employee_change_requests ecr
                SET approval_request_id = cr.id
                FROM created_requests cr
                WHERE ecr.tenant_id = cr.tenant_id
                  AND ecr.id::text = cr.entity_id;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE employee_change_requests
                SET approval_request_id = NULL
                WHERE approval_request_id IN (
                    SELECT id FROM approval_requests WHERE entity_name = 'EmployeeChangeRequest'
                );

                DELETE FROM approval_requests
                WHERE entity_name = 'EmployeeChangeRequest';
                """);
        }
    }
}
