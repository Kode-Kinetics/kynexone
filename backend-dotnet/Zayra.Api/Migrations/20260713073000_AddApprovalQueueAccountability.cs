using Microsoft.EntityFrameworkCore.Migrations;
using Zayra.Api.Data;

#nullable disable

namespace Zayra.Api.Migrations
{
    // See 20260713061000 for why these attributes are load-bearing. Written by hand in 15148d0
    // (2026-07-13) without them; invisible to EF for 70 days.
    //
    // GENUINELY UNAPPLIED IN PRODUCTION (re-verified 2026-09-21 against the live database: absent
    // from __EFMigrationsHistory), but all fourteen columns and all four indexes are already there —
    // 20260816013100_RepairMigrationModelParity created them independently. So on the next
    // `--migrate` the DDL half is all IF NOT EXISTS and no-ops, and only the routing UPDATE works.
    //
    // THAT UPDATE IS NOT IDEMPOTENT AGAINST LIVE DATA, and an earlier note here claiming it was is
    // wrong. current_approver_*, current_queue and sla_hours are assigned unconditionally, not
    // COALESCEd. The eight Pending EmployeeChangeRequest approvals in production have since been
    // routed by the application to `Role:HR Manager` with sla_hours = 48. Re-deriving from July-era
    // employee data would have silently re-routed the six with a manager away from the HR Manager
    // queue to `Manager:<name>` and halved their SLA to 24h — changing who must act on eight of a
    // pilot client's live approvals, with no audit row. A backfill must not overwrite the state the
    // running system has since established.
    //
    // So the UPDATE is now restricted to rows that were never routed (`current_queue = ''`, the
    // column default). That is precisely the population the backfill was written for. In production
    // it matches exactly one row: the approval_request that 20260713062000 creates immediately
    // before this migration runs. The eight already-routed rows are left alone.
    //
    // This keeps the migration safe to run and needs no manual __EFMigrationsHistory row.
    [Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof(ZayraDbContext))]
    [Migration("20260713073000_AddApprovalQueueAccountability")]
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
                      -- Backfill only. An empty current_queue is the column default, i.e. a row
                      -- that has never been routed. Anything already routed belongs to the running
                      -- application, not to this migration; see the class comment.
                      AND ar.current_queue = ''
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
