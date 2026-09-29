using Microsoft.EntityFrameworkCore.Migrations;
using Zayra.Api.Data;

#nullable disable

namespace Zayra.Api.Migrations
{
    // See 20260713061000 for why these attributes are load-bearing. Written by hand in 15148d0
    // (2026-07-13) without them; invisible to EF for 70 days.
    //
    // GENUINELY UNAPPLIED IN PRODUCTION (re-verified 2026-09-21 against the live database: absent
    // from __EFMigrationsHistory). Restoring visibility means the next `--migrate` runs it for real.
    // Every statement is guarded — CREATE EXTENSION/INDEX IF NOT EXISTS, ADD COLUMN IF NOT EXISTS,
    // NOT EXISTS on the workflow inserts, and `approval_request_id IS NULL` on the backfill itself —
    // so it is idempotent and only touches rows it has not already fixed. Today that is exactly one
    // row: 1 of 9 PendingApproval employee_change_requests has a NULL approval_request_id
    // (employee 4776, tenant 91dcd203…). Its tenant already has the EMPLOYEE-CHANGE workflow and a
    // step, so the two INSERTs above the backfill no-op and only the approval_requests INSERT works.
    //
    // As written in 15148d0 that INSERT would have ABORTED on production with 23502, taking the
    // pre-deploy migration step down with it. See the ordering-repair block in Up() for why, and
    // note that being idempotent is not the same as being able to run at all.
    [Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof(ZayraDbContext))]
    [Migration("20260713062000_BackfillEmployeeChangeApprovalRequests")]
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

                -- ── Ordering repair: make the approval_requests INSERT below legal ───────────────
                -- The INSERT lists ten columns. On a database where 20260816013100_RepairMigrationModelParity
                -- has ALREADY run but this migration has not (that is production, verified 2026-09-21),
                -- approval_requests carries seven further columns that are NOT NULL with NO DEFAULT --
                -- current_approver_name/role/type, current_queue, sla_hours, escalated_to_role, priority.
                -- Parity added them WITH defaults and then explicitly DROPped those defaults to match the
                -- EF model (that migration, lines 37-43). An INSERT that omits them therefore aborts with
                -- 23502 not_null_violation, and the whole pre-deploy migration step fails.
                --
                -- This never showed up because the migration was invisible to EF for 70 days: the only
                -- database that ever ran it was one where these columns did not exist yet.
                --
                -- Give the seven columns the defaults 20260713073000 declares for them, do the INSERT,
                -- then drop the defaults again, restoring exactly the prior schema. ADD COLUMN IF NOT
                -- EXISTS first so the subsequent ALTER COLUMN is legal on a fresh database too, where
                -- 20260713073000 has not created them yet; there its own ADD COLUMN IF NOT EXISTS then
                -- no-ops. Both orderings converge on identical schema and identical data.
                ALTER TABLE approval_requests ADD COLUMN IF NOT EXISTS current_approver_name character varying(180) NOT NULL DEFAULT '';
                ALTER TABLE approval_requests ADD COLUMN IF NOT EXISTS current_approver_role character varying(80) NOT NULL DEFAULT '';
                ALTER TABLE approval_requests ADD COLUMN IF NOT EXISTS current_approver_type character varying(60) NOT NULL DEFAULT '';
                ALTER TABLE approval_requests ADD COLUMN IF NOT EXISTS current_queue character varying(180) NOT NULL DEFAULT '';
                ALTER TABLE approval_requests ADD COLUMN IF NOT EXISTS sla_hours integer NOT NULL DEFAULT 24;
                ALTER TABLE approval_requests ADD COLUMN IF NOT EXISTS escalated_to_role character varying(80) NOT NULL DEFAULT '';
                ALTER TABLE approval_requests ADD COLUMN IF NOT EXISTS priority character varying(40) NOT NULL DEFAULT 'Normal';

                ALTER TABLE approval_requests ALTER COLUMN current_approver_name SET DEFAULT '';
                ALTER TABLE approval_requests ALTER COLUMN current_approver_role SET DEFAULT '';
                ALTER TABLE approval_requests ALTER COLUMN current_approver_type SET DEFAULT '';
                ALTER TABLE approval_requests ALTER COLUMN current_queue SET DEFAULT '';
                ALTER TABLE approval_requests ALTER COLUMN sla_hours SET DEFAULT 24;
                ALTER TABLE approval_requests ALTER COLUMN escalated_to_role SET DEFAULT '';
                ALTER TABLE approval_requests ALTER COLUMN priority SET DEFAULT 'Normal';

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

                -- Restore the no-default shape the EF model expects. Unconditional by design: no
                -- database reaches this point with a default on these columns that it needs to keep
                -- -- 20260713073000 re-adds them with defaults only when absent, and
                -- 20260816013100_RepairMigrationModelParity drops them again either way.
                ALTER TABLE approval_requests ALTER COLUMN current_approver_name DROP DEFAULT;
                ALTER TABLE approval_requests ALTER COLUMN current_approver_role DROP DEFAULT;
                ALTER TABLE approval_requests ALTER COLUMN current_approver_type DROP DEFAULT;
                ALTER TABLE approval_requests ALTER COLUMN current_queue DROP DEFAULT;
                ALTER TABLE approval_requests ALTER COLUMN sla_hours DROP DEFAULT;
                ALTER TABLE approval_requests ALTER COLUMN escalated_to_role DROP DEFAULT;
                ALTER TABLE approval_requests ALTER COLUMN priority DROP DEFAULT;
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
