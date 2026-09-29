using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// F1 — approval engine convergence. Makes <c>approval_workflows</c> the single approval
    /// configuration model and carries every <c>approval_policies</c> row into it.
    ///
    /// <para><b>Schema (additive):</b> <c>approval_workflows</c> gains nullable <c>department_id</c>,
    /// nullable <c>grade_id</c>, <c>is_default</c> (NOT NULL DEFAULT false) and the router's lookup
    /// index. Nothing is dropped: <c>approval_policies</c>/<c>approval_policy_steps</c> are left in place
    /// and frozen (no code reads or writes them) so that an APPLICATION rollback to the previous build
    /// still finds the configuration it reads. A follow-up migration drops them after one release.</para>
    ///
    /// <para><b>Data:</b> see <see cref="CopyPoliciesSql"/>. Each non-deleted policy becomes a workflow
    /// with the SAME primary key, so every <c>approval_requests.workflow_id</c> that the old leave code
    /// wrote as a policy id now resolves to a real workflow with no rewrite of request rows.</para>
    ///
    /// <para><b>Rollback:</b> <see cref="Down"/> deletes exactly the migrated rows (identified by
    /// sharing an id with an <c>approval_policies</c> row), deactivates any workflow created since with a
    /// department/grade scope (so dropping the scope columns cannot silently widen it to the whole
    /// tenant), then drops the index and columns. The policy tables were never modified, so the old
    /// build's routing is exactly as it was.</para>
    /// </summary>
    public partial class ConvergeApprovalPolicyIntoWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "department_id",
                table: "approval_workflows",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "grade_id",
                table: "approval_workflows",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_default",
                table: "approval_workflows",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_approval_workflows_routing",
                table: "approval_workflows",
                columns: new[] { "tenant_id", "entity_name", "is_active", "department_id", "grade_id" });

            migrationBuilder.Sql(CopyPoliciesSql);
            migrationBuilder.Sql(CopyPolicyStepsSql);
            migrationBuilder.Sql(EnsureFinalStepSql);
        }

        /// <summary>
        /// Copies each non-deleted <c>approval_policies</c> row into <c>approval_workflows</c> (same id).
        /// <list type="bullet">
        /// <item>WorkflowType → EntityName: Leave→LeaveRequest, Overtime→OvertimeRequest, Payroll→PayrollRun.
        /// Only "Leave" had a runtime consumer; the other two map to the entity names the provisioning
        /// defaults now use, which have no consumer that routes by entity today, so nothing is newly
        /// switched on. Any other type (e.g. Recruitment, Expense, Travel) keeps its name verbatim — it
        /// deliberately does NOT become "ManpowerRequisition", which would silently start routing
        /// requisitions that have never required approval.</item>
        /// <item>Code: <c>POLICY-</c> + the full 32-hex id — unique by construction, because the
        /// tenant's (tenant_id, code) index is unique and policy names are not codes. The insert is
        /// idempotent on id.</item>
        /// <item>IsDefault: only an unscoped default policy stays default.</item>
        /// <item>IsActive is preserved EXCEPT, to keep routing behaviour identical or strictly more
        /// correct, the row is inserted inactive when: (a) it is unscoped and not default — the old
        /// resolver could never select such a policy; (b) it is unscoped and the tenant ALREADY has an
        /// active unscoped workflow for the same entity — that workflow is the configuration the tenant
        /// sees in the Approvals UI and is what F1 makes effective (this is the defect being fixed);
        /// (c) it duplicates the scope of an earlier migrated policy — the earliest (default first,
        /// then created_at, then id: the router's own tie-break) stays active. Inactive rows keep the
        /// configuration for reference; an admin can re-activate them.</item>
        /// </list>
        /// </summary>
        internal const string CopyPoliciesSql = """
            WITH src AS (
                SELECT p.id, p.tenant_id, p.name, p.department_id, p.grade_id, p.is_default, p.is_active, p.created_at_utc,
                       CASE lower(btrim(p.workflow_type))
                            WHEN 'leave'    THEN 'LeaveRequest'
                            WHEN 'overtime' THEN 'OvertimeRequest'
                            WHEN 'payroll'  THEN 'PayrollRun'
                            ELSE btrim(p.workflow_type)
                       END AS entity_name
                FROM approval_policies p
                WHERE NOT p.is_deleted
                  AND NOT EXISTS (SELECT 1 FROM approval_workflows w WHERE w.id = p.id)
            ),
            ranked AS (
                SELECT s.*,
                       row_number() OVER (
                           PARTITION BY s.tenant_id, s.entity_name, s.department_id, s.grade_id
                           ORDER BY (s.is_active AND (s.department_id IS NOT NULL OR s.grade_id IS NOT NULL OR s.is_default)) DESC,
                                    s.is_default DESC, s.created_at_utc, s.id) AS scope_rank
                FROM src s
            )
            INSERT INTO approval_workflows
                (id, tenant_id, code, name, entity_name, department_id, grade_id, is_default, is_active, created_at_utc)
            SELECT r.id, r.tenant_id,
                   'POLICY-' || upper(replace(r.id::text, '-', '')),
                   r.name, r.entity_name, r.department_id, r.grade_id,
                   (r.is_default AND r.department_id IS NULL AND r.grade_id IS NULL),
                   r.is_active
                     AND r.scope_rank = 1
                     AND NOT (r.department_id IS NULL AND r.grade_id IS NULL AND NOT r.is_default)
                     AND NOT (r.department_id IS NULL AND r.grade_id IS NULL AND EXISTS (
                         SELECT 1 FROM approval_workflows w
                         WHERE w.tenant_id = r.tenant_id AND w.entity_name = r.entity_name AND w.is_active
                           AND w.department_id IS NULL AND w.grade_id IS NULL)),
                   r.created_at_utc
            FROM ranked r;
            """;

        /// <summary>
        /// Copies the steps of every migrated policy (same step ids). approval_workflow_steps.approver_role
        /// is NOT NULL, so a null policy role becomes '' — or 'HR Manager' for an HR step, the queue the
        /// router routes HR steps to. Old leave routing IGNORED IsFinalStep (the last step always
        /// completed the request), so to preserve exactly that behaviour the migrated chain is normalised
        /// to "last step final, earlier steps not" — never shortening a configured chain.
        /// </summary>
        internal const string CopyPolicyStepsSql = """
            INSERT INTO approval_workflow_steps
                (id, tenant_id, workflow_id, step_order, step_name, approver_role, approver_type,
                 specific_employee_id, escalation_after_hours, is_final_step)
            SELECT s.id, s.tenant_id, s.policy_id, s.step_order, s.step_name,
                   COALESCE(NULLIF(btrim(s.approver_role), ''), CASE WHEN upper(btrim(s.approver_type)) = 'HR' THEN 'HR Manager' ELSE '' END),
                   COALESCE(NULLIF(btrim(s.approver_type), ''), 'Manager'),
                   s.specific_employee_id, s.escalation_after_hours,
                   s.step_order = max(s.step_order) OVER (PARTITION BY s.policy_id)
            FROM approval_policy_steps s
            JOIN approval_policies p ON p.id = s.policy_id AND NOT p.is_deleted
            JOIN approval_workflows w ON w.id = s.policy_id
            WHERE NOT EXISTS (SELECT 1 FROM approval_workflow_steps x WHERE x.workflow_id = s.policy_id);
            """;

        /// <summary>
        /// A workflow with steps but no final step could never complete under F1's "only the final step
        /// approves" rule. The previous code approved when it ran out of steps, so marking the LAST step
        /// final is behaviour-preserving. Workflows that already have a final step are untouched.
        /// </summary>
        /// <summary>Down's data step: removes the copied rows and neutralises org-scoped workflows before their scope columns are dropped.</summary>
        internal const string RevertDataSql = """
            DELETE FROM approval_workflow_steps s USING approval_policies p WHERE s.workflow_id = p.id;
            DELETE FROM approval_workflows w USING approval_policies p WHERE w.id = p.id;
            UPDATE approval_workflows SET is_active = false WHERE department_id IS NOT NULL OR grade_id IS NOT NULL;
            """;

        internal const string EnsureFinalStepSql = """
            UPDATE approval_workflow_steps s
            SET is_final_step = true
            WHERE s.step_order = (SELECT max(x.step_order) FROM approval_workflow_steps x WHERE x.workflow_id = s.workflow_id)
              AND NOT EXISTS (SELECT 1 FROM approval_workflow_steps f WHERE f.workflow_id = s.workflow_id AND f.is_final_step);
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Remove exactly the rows Up copied (they share their id with a policy row). Steps first,
            // although the FK cascades. EnsureFinalStepSql is deliberately not reversed: it only marked
            // a final step where none existed, which the previous build treats identically.
            migrationBuilder.Sql(RevertDataSql);

            migrationBuilder.DropIndex(
                name: "IX_approval_workflows_routing",
                table: "approval_workflows");

            migrationBuilder.DropColumn(
                name: "department_id",
                table: "approval_workflows");

            migrationBuilder.DropColumn(
                name: "grade_id",
                table: "approval_workflows");

            migrationBuilder.DropColumn(
                name: "is_default",
                table: "approval_workflows");
        }
    }
}
