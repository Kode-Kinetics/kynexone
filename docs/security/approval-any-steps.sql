-- Runbook: approval steps that route to "Any" (no named approver role).
--
-- Why: an approval step whose role is blank or "Any" used to be decidable by every approvals.decide holder in
-- the tenant (Payroll Manager, Finance, Finance Approver, ManagerPortal users), which reaches employee master
-- changes such as IBAN and salary. Since PR #181 such a step also needs manager.approve or approvals.override,
-- new workflow saves must name a role, and new requisitions are routed to their configured step-1 role.
-- Rows already saved or in flight keep working. Use these queries to find them and act.
--
-- READ-ONLY. Changes nothing. Run with a read-only role; add the tenant filter to narrow to one tenant.

-- 1. Pending approval requests sitting on an "Any" or blank step, with no named approver.
--    Act: confirm each is being handled by HR or Admin. Requisitions submitted before #181 appear here
--    (they were created without routing); they stay decidable by HR Manager, Manager, HR Director or Admin.
--    For an employee change (EmployeeChangeRequest: IBAN, salary, bank), check who is expected to decide it.
--    configured_role shows what the workflow step was set to: a finance role there (Finance, Finance Approver,
--    Payroll Manager) means that request now waits for HR or Admin instead of Finance.
SELECT
    r.tenant_id,
    r.id                    AS approval_request_id,
    r.entity_name,
    r.entity_id,
    r.title,
    r.current_step_order,
    r.current_approver_type,
    r.current_approver_role,
    ws.approver_type        AS configured_type,
    ws.approver_role        AS configured_role,
    r.created_at_utc
FROM approval_requests r
LEFT JOIN approval_workflow_steps ws
       ON ws.workflow_id = r.workflow_id AND ws.step_order = r.current_step_order
WHERE r.status = 'Pending'
  AND r.current_approver_user_id IS NULL
  AND r.current_approver_employee_id IS NULL
  AND (COALESCE(TRIM(r.current_approver_role), '') = '' OR LOWER(TRIM(r.current_approver_role)) = 'any')
  -- AND r.tenant_id = '<tenant uuid>'
ORDER BY r.tenant_id, r.entity_name, r.created_at_utc;

-- 2. Workflow configuration that produces "Any": active workflows with a Role step (a blank type is a Role
--    step) whose role is blank or "Any".
--    Act: edit each workflow in Setup > Approval workflows and name the role that should decide the step
--    (for example HR Manager). Saving it unchanged is now refused with a message saying so.
SELECT
    w.tenant_id,
    w.id                    AS workflow_id,
    w.code,
    w.name,
    w.entity_name,
    s.step_order,
    s.step_name,
    s.approver_type,
    s.approver_role
FROM approval_workflows w
JOIN approval_workflow_steps s ON s.workflow_id = w.id
WHERE w.is_active
  AND (COALESCE(TRIM(s.approver_type), '') = '' OR LOWER(TRIM(s.approver_type)) = 'role')
  AND (COALESCE(TRIM(s.approver_role), '') = '' OR LOWER(TRIM(s.approver_role)) = 'any')
  -- AND w.tenant_id = '<tenant uuid>'
ORDER BY w.tenant_id, w.entity_name, w.code, s.step_order;
