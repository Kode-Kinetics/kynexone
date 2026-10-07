-- Runbook: leave encashments HR-approved before the role-gate hotfix (#180).
--
-- Why: before #180, encashment hr-approve resolved to leave.approve, so a line Manager could take the HR
-- step. Since #180 every HR approval writes an audit row (action 'leave.encashment.hr_approved'), and the
-- payroll step refuses the person who took the HR step. Requests HR-approved BEFORE the deploy have no such
-- row: the separation check cannot see who approved them. Review each one before a payroll approver acts.
--
-- READ-ONLY. Changes nothing. Run against the production database with a read-only role.
-- Narrow to one tenant by uncommenting the tenant filter.

SELECT
    r.tenant_id,
    r.company_id,
    r.id               AS encashment_id,
    r.employee_id,
    r.employee_name,
    r.leave_type_name,
    r.year,
    r.days_to_encash,
    r.total_amount,
    r.currency,
    r.created_at_utc,
    r.h_r_notes        AS hr_notes
FROM leave_encashment_requests r
WHERE r.status = 'HRApproved'
  AND NOT EXISTS (
        SELECT 1
        FROM audit_logs a
        WHERE a.tenant_id   = r.tenant_id
          AND a.entity_name = 'LeaveEncashmentRequest'
          AND a.entity_id   = r.id::text
          AND a.action      = 'leave.encashment.hr_approved')
  -- AND r.tenant_id = '<tenant uuid>'
ORDER BY r.tenant_id, r.created_at_utc;

-- What to do with each row (owner decision; nothing here is automatic):
--   * Confirm with HR who approved it. If the approver was not an HR approver (Admin, HR Manager,
--     HR Director), have HR reject it (POST /api/leave/encashment/{id}/reject) and re-raise it.
--   * Otherwise leave it for the payroll approver, making sure it is a different person from the HR approver.
