-- org-per-company-precheck.sql — READ ONLY. Run on production BEFORE deploying the
-- PerCompanyOrganisationColumns + BackfillOrganisationPerCompany migrations (Addendum B1,
-- docs/design/company-setup-wizard.md), and the leave-per-company pair (PerCompanyLeaveColumns +
-- BackfillLeavePerCompany; last section, with the balance-invariant pre-snapshot).
--
--   psql "$DATABASE_URL" -f docs/runbooks/org-per-company-precheck.sql
--
-- It writes nothing: the whole script runs inside a READ ONLY transaction and ends in ROLLBACK.
-- It is written for the database as it is BEFORE the migration (departments, designations and grades
-- have no company_id yet), and mirrors the backfill's rules exactly:
--   used by one company        -> assigned to it
--   used by several companies  -> kept by the company with the most employees on it (ties: oldest
--                                 company), copied for each other company, references re-pointed
--   used by nobody             -> the tenant's only company; otherwise left unassigned (Setup asks)
-- "Used by" = that company's non-deleted employees reference the row, a department's branch or cost
-- centre belongs to it, or (transitively) a designation / child department used by it references it.
--
-- Output: one row per (tenant, metric, subject) with a count. Metrics:
--   rows_assigned_single_user / rows_kept_by_keeper / copies_to_create / rows_assigned_only_company /
--   rows_left_unassigned                         — per kind (department, designation, grade)
--   refs_repointed                               — per FK column the backfill moves to a copy
--   child_rows_copied                            — pay-scale lines, tenant-wide entitlement cells, staffing budgets
--   rules_copied                                 — per rule table: workspace-wide rules the CopyWorkspaceRulesPerCompany
--                                                  migration copies for a company that got a copy of a row they name
--   refs_left_on_keeper                          — recruitment records (no company column) / immutable evidence that keep
--                                                  pointing at the keeper's row (owner review)
--   links_cleared                                — a kept department whose branch / cost centre is another company's
--   arabic_name_possibly_phonetic                — heuristic, see the last section

BEGIN TRANSACTION READ ONLY;

WITH RECURSIVE
co AS (
    SELECT c.tenant_id, c.id AS company_id, c.created_at_utc
      FROM companies c WHERE NOT c.is_deleted),
org AS (
    SELECT 'department'::text AS kind, d.tenant_id, d.id FROM departments d
    UNION ALL SELECT 'designation', x.tenant_id, x.id FROM designations x
    UNION ALL SELECT 'grade', g.tenant_id, g.id FROM grades g),
emp_use AS (
    SELECT u.kind, u.id, u.company_id, count(*) AS employees
      FROM (SELECT 'department'::text AS kind, e.department_id AS id, e.company_id, e.tenant_id FROM employees e WHERE e.department_id IS NOT NULL AND NOT e.is_deleted
            UNION ALL SELECT 'designation', e.designation_id, e.company_id, e.tenant_id FROM employees e WHERE e.designation_id IS NOT NULL AND NOT e.is_deleted
            UNION ALL SELECT 'grade', e.grade_id, e.company_id, e.tenant_id FROM employees e WHERE e.grade_id IS NOT NULL AND NOT e.is_deleted) u
      JOIN co ON co.tenant_id = u.tenant_id AND co.company_id = u.company_id
      JOIN org o ON o.kind = u.kind AND o.id = u.id AND o.tenant_id = u.tenant_id
     GROUP BY u.kind, u.id, u.company_id),
seed AS (
    SELECT kind, id, company_id FROM emp_use
    UNION SELECT 'department', d.id, b.company_id FROM departments d
            JOIN branches b ON b.tenant_id = d.tenant_id AND b.id = d.branch_id
            JOIN co ON co.tenant_id = b.tenant_id AND co.company_id = b.company_id
    UNION SELECT 'department', d.id, cc.company_id FROM departments d
            JOIN cost_centers cc ON cc.tenant_id = d.tenant_id AND cc.id = d.cost_center_id
            JOIN co ON co.tenant_id = cc.tenant_id AND co.company_id = cc.company_id),
edge AS (
    SELECT 'designation'::text AS from_kind, x.id AS from_id, 'grade'::text AS to_kind, x.grade_id AS to_id
      FROM designations x JOIN grades g ON g.tenant_id = x.tenant_id AND g.id = x.grade_id
    UNION ALL SELECT 'designation', x.id, 'department', x.department_id
      FROM designations x JOIN departments d ON d.tenant_id = x.tenant_id AND d.id = x.department_id
    UNION ALL SELECT 'department', c.id, 'department', c.parent_department_id
      FROM departments c JOIN departments p ON p.tenant_id = c.tenant_id AND p.id = c.parent_department_id),
usage(kind, id, company_id) AS (
    SELECT kind, id, company_id FROM seed
    UNION
    SELECT e.to_kind, e.to_id, u.company_id FROM usage u JOIN edge e ON e.from_kind = u.kind AND e.from_id = u.id),
users AS (
    SELECT o.kind, o.tenant_id, o.id, u.company_id, COALESCE(eu.employees, 0) AS employees, co.created_at_utc,
           count(*) OVER (PARTITION BY o.kind, o.id) AS n_users
      FROM org o
      JOIN usage u ON u.kind = o.kind AND u.id = o.id
      JOIN co ON co.tenant_id = o.tenant_id AND co.company_id = u.company_id
      LEFT JOIN emp_use eu ON eu.kind = o.kind AND eu.id = o.id AND eu.company_id = u.company_id),
ranked AS (
    SELECT users.*, row_number() OVER (PARTITION BY kind, id ORDER BY employees DESC, created_at_utc, company_id) AS rn
      FROM users),
-- (row, company) pairs that get a COPY: every reference of that company to the row moves.
moves AS (SELECT kind, tenant_id, id, company_id FROM ranked WHERE rn > 1),
keepers AS (SELECT kind, tenant_id, id, company_id FROM ranked WHERE rn = 1),
only_co AS (SELECT tenant_id, min(company_id::text)::uuid AS company_id FROM co GROUP BY tenant_id HAVING count(*) = 1),
-- Workspace-wide rules and the references they name (CopyWorkspaceRulesPerCompany). A rule is copied for company C
-- when one of its references gets a C copy, every reference then belongs to C (its copy, or a row C keeps, or an
-- unassigned one), and a branch filter, if any, is C's branch. Eligibility rows count only when they have no company.
rule_refs AS (
    SELECT 'approval_policies'::text AS tbl, t.tenant_id, t.id, t.department_id AS r1, 'department'::text AS k1, t.grade_id AS r2, 'grade'::text AS k2, NULL::uuid AS branch_id FROM approval_policies t
    UNION ALL SELECT 'approval_workflows', t.tenant_id, t.id, t.department_id, 'department', t.grade_id, 'grade', NULL FROM approval_workflows t
    UNION ALL SELECT 'attendance_policies', t.tenant_id, t.id, t.department_id, 'department', t.grade_id, 'grade', t.branch_id FROM attendance_policies t
    UNION ALL SELECT 'overtime_policies', t.tenant_id, t.id, t.department_id, 'department', t.grade_id, 'grade', t.branch_id FROM overtime_policies t
    UNION ALL SELECT 'overtime_budgets', t.tenant_id, t.id, t.department_id, 'department', NULL, NULL, NULL FROM overtime_budgets t
    UNION ALL SELECT 'onboarding_checklists', t.tenant_id, t.id, t.department_id, 'department', NULL, NULL, NULL FROM onboarding_checklists t
    UNION ALL SELECT 'leave_policy_eligibilities', t.tenant_id, t.id, t.department_id, 'department', t.grade_id, 'grade', t.branch_id FROM leave_policy_eligibilities t WHERE t.company_id IS NULL
    UNION ALL SELECT 'benefit_eligibility_rules', t.tenant_id, t.id, t.grade_id, 'grade', NULL, NULL, NULL FROM benefit_eligibility_rules t WHERE t.company_id IS NULL),
rule_copies AS (
    SELECT DISTINCT rr.tbl, rr.tenant_id, rr.id, m.company_id
      FROM rule_refs rr
      JOIN moves m ON m.tenant_id = rr.tenant_id AND ((m.kind = rr.k1 AND m.id = rr.r1) OR (m.kind = rr.k2 AND m.id = rr.r2))
     WHERE (rr.branch_id IS NULL OR EXISTS (SELECT 1 FROM branches b WHERE b.tenant_id = rr.tenant_id AND b.id = rr.branch_id AND b.company_id = m.company_id))
       AND (rr.r1 IS NULL OR EXISTS (SELECT 1 FROM moves x WHERE x.kind = rr.k1 AND x.id = rr.r1 AND x.company_id = m.company_id)
            OR NOT EXISTS (SELECT 1 FROM keepers k WHERE k.kind = rr.k1 AND k.id = rr.r1 AND k.company_id <> m.company_id))
       AND (rr.r2 IS NULL OR EXISTS (SELECT 1 FROM moves x WHERE x.kind = rr.k2 AND x.id = rr.r2 AND x.company_id = m.company_id)
            OR NOT EXISTS (SELECT 1 FROM keepers k WHERE k.kind = rr.k2 AND k.id = rr.r2 AND k.company_id <> m.company_id))),
metrics AS (
    SELECT tenant_id, 'rows_assigned_single_user' AS metric, kind AS subject, count(*) AS n FROM ranked WHERE rn = 1 AND n_users = 1 GROUP BY 1, 3
    UNION ALL SELECT tenant_id, 'rows_kept_by_keeper', kind, count(*) FROM ranked WHERE rn = 1 AND n_users > 1 GROUP BY 1, 3
    UNION ALL SELECT tenant_id, 'copies_to_create', kind, count(*) FROM moves GROUP BY 1, 3
    UNION ALL SELECT o.tenant_id, 'rows_assigned_only_company', o.kind, count(*) FROM org o JOIN only_co oc ON oc.tenant_id = o.tenant_id
               WHERE NOT EXISTS (SELECT 1 FROM usage u WHERE u.kind = o.kind AND u.id = o.id) GROUP BY 1, 3
    UNION ALL SELECT o.tenant_id, 'rows_left_unassigned', o.kind, count(*) FROM org o
               WHERE NOT EXISTS (SELECT 1 FROM only_co oc WHERE oc.tenant_id = o.tenant_id)
                 AND NOT EXISTS (SELECT 1 FROM usage u JOIN co ON co.company_id = u.company_id WHERE u.kind = o.kind AND u.id = o.id) GROUP BY 1, 3
    -- references re-pointed (the row's own company has a copy)
    UNION ALL SELECT m.tenant_id, 'refs_repointed', 'employees.department_id', count(*) FROM employees t JOIN moves m ON m.kind = 'department' AND m.id = t.department_id AND m.company_id = t.company_id GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'refs_repointed', 'employees.designation_id', count(*) FROM employees t JOIN moves m ON m.kind = 'designation' AND m.id = t.designation_id AND m.company_id = t.company_id GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'refs_repointed', 'employees.grade_id', count(*) FROM employees t JOIN moves m ON m.kind = 'grade' AND m.id = t.grade_id AND m.company_id = t.company_id GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'refs_repointed', 'positions.department_id', count(*) FROM positions t JOIN moves m ON m.kind = 'department' AND m.id = t.department_id AND m.company_id = t.company_id GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'refs_repointed', 'positions.designation_id', count(*) FROM positions t JOIN moves m ON m.kind = 'designation' AND m.id = t.designation_id AND m.company_id = t.company_id GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'refs_repointed', 'positions.grade_id', count(*) FROM positions t JOIN moves m ON m.kind = 'grade' AND m.id = t.grade_id AND m.company_id = t.company_id GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'refs_repointed', 'grade_entitlements.grade_id (company cells)', count(*) FROM grade_entitlements t JOIN moves m ON m.kind = 'grade' AND m.id = t.grade_id AND m.company_id = t.company_id GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'refs_repointed', 'employee_loans.grade_id_at_request', count(*) FROM employee_loans t JOIN moves m ON m.kind = 'grade' AND m.id = t.grade_id_at_request AND m.company_id = t.company_id GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'refs_repointed', 'pay_benchmark_rows.designation_id', count(*) FROM pay_benchmark_rows t JOIN moves m ON m.kind = 'designation' AND m.id = t.designation_id AND m.company_id = t.company_id GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'refs_repointed', 'pay_benchmark_rows.grade_id', count(*) FROM pay_benchmark_rows t JOIN moves m ON m.kind = 'grade' AND m.id = t.grade_id AND m.company_id = t.company_id GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'refs_repointed', 'comp_cycle_budgets.department_id', count(*) FROM comp_cycle_budgets t JOIN moves m ON m.kind = 'department' AND m.id = t.department_id AND m.company_id = t.company_id GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'refs_repointed', 'comp_worksheet_lines.department_id', count(*) FROM comp_worksheet_lines t JOIN moves m ON m.kind = 'department' AND m.id = t.department_id AND m.company_id = t.company_id GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'refs_repointed', 'comp_worksheet_lines.designation_id', count(*) FROM comp_worksheet_lines t JOIN moves m ON m.kind = 'designation' AND m.id = t.designation_id AND m.company_id = t.company_id GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'refs_repointed', 'comp_worksheet_lines.grade_id', count(*) FROM comp_worksheet_lines t JOIN moves m ON m.kind = 'grade' AND m.id = t.grade_id AND m.company_id = t.company_id GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'refs_repointed', 'learning_assignments.department_id', count(*) FROM learning_assignments t JOIN moves m ON m.kind = 'department' AND m.id = t.department_id AND m.company_id = t.company_id GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'refs_repointed', 'learning_assignments.designation_id', count(*) FROM learning_assignments t JOIN moves m ON m.kind = 'designation' AND m.id = t.designation_id AND m.company_id = t.company_id GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'refs_repointed', 'learning_assignments.grade_id', count(*) FROM learning_assignments t JOIN moves m ON m.kind = 'grade' AND m.id = t.grade_id AND m.company_id = t.company_id GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'refs_repointed', 'leave_policy_eligibilities.department_id', count(*) FROM leave_policy_eligibilities t JOIN moves m ON m.kind = 'department' AND m.id = t.department_id AND m.company_id = t.company_id GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'refs_repointed', 'leave_policy_eligibilities.grade_id', count(*) FROM leave_policy_eligibilities t JOIN moves m ON m.kind = 'grade' AND m.id = t.grade_id AND m.company_id = t.company_id GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'refs_repointed', 'benefit_eligibility_rules.grade_id', count(*) FROM benefit_eligibility_rules t JOIN moves m ON m.kind = 'grade' AND m.id = t.grade_id AND m.company_id = t.company_id GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'refs_repointed', 'employee_transfer_requests.current_department_id', count(*) FROM employee_transfer_requests t JOIN employees e ON e.tenant_id = t.tenant_id AND e.id = t.employee_id
               JOIN moves m ON m.kind = 'department' AND m.id = t.current_department_id AND m.company_id = e.company_id GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'refs_repointed', 'departments.parent_department_id', count(*) FROM departments t
               JOIN usage u ON u.kind = 'department' AND u.id = t.id
               JOIN moves m ON m.kind = 'department' AND m.id = t.parent_department_id AND m.company_id = u.company_id GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'refs_repointed', 'designations.department_id / grade_id', count(*) FROM designations t
               JOIN usage u ON u.kind = 'designation' AND u.id = t.id
               JOIN moves m ON m.company_id = u.company_id AND ((m.kind = 'department' AND m.id = t.department_id) OR (m.kind = 'grade' AND m.id = t.grade_id)) GROUP BY 1
    -- child config copied with the row
    UNION ALL SELECT m.tenant_id, 'child_rows_copied', 'grade_pay_scale_components', count(*) FROM grade_pay_scale_components t JOIN moves m ON m.kind = 'grade' AND m.id = t.grade_id GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'child_rows_copied', 'grade_entitlements (tenant-wide cells)', count(*) FROM grade_entitlements t JOIN moves m ON m.kind = 'grade' AND m.id = t.grade_id WHERE t.company_id IS NULL GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'child_rows_copied', 'department_staffing_budgets', count(*) FROM department_staffing_budgets t JOIN moves m ON m.kind = 'department' AND m.id = t.department_id GROUP BY 1
    UNION ALL SELECT tenant_id, 'rules_copied', tbl, count(*) FROM rule_copies GROUP BY 1, 3
    -- left on the keeper's row (tenant-wide rules carry no company; evidence is immutable)
    UNION ALL SELECT k.tenant_id, 'refs_left_on_keeper', 'manpower_requisitions', count(*) FROM manpower_requisitions t JOIN keepers k ON k.id IN (t.department_id, t.designation_id) AND EXISTS (SELECT 1 FROM moves m WHERE m.id = k.id) GROUP BY 1
    UNION ALL SELECT k.tenant_id, 'refs_left_on_keeper', 'job_openings', count(*) FROM job_openings t JOIN keepers k ON k.id IN (t.department_id, t.designation_id) AND EXISTS (SELECT 1 FROM moves m WHERE m.id = k.id) GROUP BY 1
    UNION ALL SELECT k.tenant_id, 'refs_left_on_keeper', 'workforce_plans', count(*) FROM workforce_plans t JOIN keepers k ON k.id = t.department_id AND EXISTS (SELECT 1 FROM moves m WHERE m.id = k.id) GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'refs_left_on_keeper', 'survey_responses (append-only evidence)', count(*) FROM survey_responses t
               JOIN moves m ON m.company_id = t.company_id AND m.id IN (t.department_id, t.designation_id) GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'refs_left_on_keeper', 'employee_entitlements.grade_entitlement_id (close-only witness)', count(*) FROM employee_entitlements t
               JOIN grade_entitlements ge ON ge.tenant_id = t.tenant_id AND ge.id = t.grade_entitlement_id AND ge.company_id IS NULL
               JOIN moves m ON m.kind = 'grade' AND m.id = ge.grade_id AND m.company_id = t.company_id GROUP BY 1
    -- a kept department loses a branch / cost centre that belongs to another company
    UNION ALL SELECT k.tenant_id, 'links_cleared', 'departments.branch_id', count(*) FROM departments d JOIN keepers k ON k.kind = 'department' AND k.id = d.id
               JOIN branches b ON b.tenant_id = d.tenant_id AND b.id = d.branch_id WHERE b.company_id <> k.company_id GROUP BY 1
    UNION ALL SELECT k.tenant_id, 'links_cleared', 'departments.cost_center_id', count(*) FROM departments d JOIN keepers k ON k.kind = 'department' AND k.id = d.id
               JOIN cost_centers cc ON cc.tenant_id = d.tenant_id AND cc.id = d.cost_center_id WHERE cc.company_id IS DISTINCT FROM k.company_id GROUP BY 1
)
SELECT tenant_id, metric, subject, n FROM metrics WHERE n > 0 ORDER BY tenant_id, metric, subject;

-- Overtime budgets duplicated for a company (CopyWorkspaceRulesPerCompany). BudgetAmount is copied unchanged — splitting
-- the envelope is the owner's call. Consumption is re-derived from the approved overtime each budget covers (department,
-- month, project, currency, split by the employee's company) when that re-derivation reproduces the stored figure
-- (method recomputed_from_overtime); otherwise the copy starts at 0 and the keeper keeps its figure (copies_zeroed).
WITH RECURSIVE
co AS (
    SELECT c.tenant_id, c.id AS company_id, c.created_at_utc
      FROM companies c WHERE NOT c.is_deleted),
org AS (
    SELECT 'department'::text AS kind, d.tenant_id, d.id FROM departments d
    UNION ALL SELECT 'designation', x.tenant_id, x.id FROM designations x
    UNION ALL SELECT 'grade', g.tenant_id, g.id FROM grades g),
emp_use AS (
    SELECT u.kind, u.id, u.company_id, count(*) AS employees
      FROM (SELECT 'department'::text AS kind, e.department_id AS id, e.company_id, e.tenant_id FROM employees e WHERE e.department_id IS NOT NULL AND NOT e.is_deleted
            UNION ALL SELECT 'designation', e.designation_id, e.company_id, e.tenant_id FROM employees e WHERE e.designation_id IS NOT NULL AND NOT e.is_deleted
            UNION ALL SELECT 'grade', e.grade_id, e.company_id, e.tenant_id FROM employees e WHERE e.grade_id IS NOT NULL AND NOT e.is_deleted) u
      JOIN co ON co.tenant_id = u.tenant_id AND co.company_id = u.company_id
      JOIN org o ON o.kind = u.kind AND o.id = u.id AND o.tenant_id = u.tenant_id
     GROUP BY u.kind, u.id, u.company_id),
seed AS (
    SELECT kind, id, company_id FROM emp_use
    UNION SELECT 'department', d.id, b.company_id FROM departments d
            JOIN branches b ON b.tenant_id = d.tenant_id AND b.id = d.branch_id
            JOIN co ON co.tenant_id = b.tenant_id AND co.company_id = b.company_id
    UNION SELECT 'department', d.id, cc.company_id FROM departments d
            JOIN cost_centers cc ON cc.tenant_id = d.tenant_id AND cc.id = d.cost_center_id
            JOIN co ON co.tenant_id = cc.tenant_id AND co.company_id = cc.company_id),
edge AS (
    SELECT 'designation'::text AS from_kind, x.id AS from_id, 'grade'::text AS to_kind, x.grade_id AS to_id
      FROM designations x JOIN grades g ON g.tenant_id = x.tenant_id AND g.id = x.grade_id
    UNION ALL SELECT 'designation', x.id, 'department', x.department_id
      FROM designations x JOIN departments d ON d.tenant_id = x.tenant_id AND d.id = x.department_id
    UNION ALL SELECT 'department', c.id, 'department', c.parent_department_id
      FROM departments c JOIN departments p ON p.tenant_id = c.tenant_id AND p.id = c.parent_department_id),
usage(kind, id, company_id) AS (
    SELECT kind, id, company_id FROM seed
    UNION
    SELECT e.to_kind, e.to_id, u.company_id FROM usage u JOIN edge e ON e.from_kind = u.kind AND e.from_id = u.id),
users AS (
    SELECT o.kind, o.tenant_id, o.id, u.company_id, COALESCE(eu.employees, 0) AS employees, co.created_at_utc,
           count(*) OVER (PARTITION BY o.kind, o.id) AS n_users
      FROM org o
      JOIN usage u ON u.kind = o.kind AND u.id = o.id
      JOIN co ON co.tenant_id = o.tenant_id AND co.company_id = u.company_id
      LEFT JOIN emp_use eu ON eu.kind = o.kind AND eu.id = o.id AND eu.company_id = u.company_id),
ranked AS (
    SELECT users.*, row_number() OVER (PARTITION BY kind, id ORDER BY employees DESC, created_at_utc, company_id) AS rn
      FROM users),
-- (row, company) pairs that get a COPY: every reference of that company to the row moves.
moves AS (SELECT kind, tenant_id, id, company_id FROM ranked WHERE rn > 1),
keepers AS (SELECT kind, tenant_id, id, company_id FROM ranked WHERE rn = 1),
only_co AS (SELECT tenant_id, min(company_id::text)::uuid AS company_id FROM co GROUP BY tenant_id HAVING count(*) = 1),
-- Workspace-wide rules and the references they name (CopyWorkspaceRulesPerCompany). A rule is copied for company C
-- when one of its references gets a C copy, every reference then belongs to C (its copy, or a row C keeps, or an
-- unassigned one), and a branch filter, if any, is C's branch. Eligibility rows count only when they have no company.
rule_refs AS (
    SELECT 'approval_policies'::text AS tbl, t.tenant_id, t.id, t.department_id AS r1, 'department'::text AS k1, t.grade_id AS r2, 'grade'::text AS k2, NULL::uuid AS branch_id FROM approval_policies t
    UNION ALL SELECT 'approval_workflows', t.tenant_id, t.id, t.department_id, 'department', t.grade_id, 'grade', NULL FROM approval_workflows t
    UNION ALL SELECT 'attendance_policies', t.tenant_id, t.id, t.department_id, 'department', t.grade_id, 'grade', t.branch_id FROM attendance_policies t
    UNION ALL SELECT 'overtime_policies', t.tenant_id, t.id, t.department_id, 'department', t.grade_id, 'grade', t.branch_id FROM overtime_policies t
    UNION ALL SELECT 'overtime_budgets', t.tenant_id, t.id, t.department_id, 'department', NULL, NULL, NULL FROM overtime_budgets t
    UNION ALL SELECT 'onboarding_checklists', t.tenant_id, t.id, t.department_id, 'department', NULL, NULL, NULL FROM onboarding_checklists t
    UNION ALL SELECT 'leave_policy_eligibilities', t.tenant_id, t.id, t.department_id, 'department', t.grade_id, 'grade', t.branch_id FROM leave_policy_eligibilities t WHERE t.company_id IS NULL
    UNION ALL SELECT 'benefit_eligibility_rules', t.tenant_id, t.id, t.grade_id, 'grade', NULL, NULL, NULL FROM benefit_eligibility_rules t WHERE t.company_id IS NULL),
rule_copies AS (
    SELECT DISTINCT rr.tbl, rr.tenant_id, rr.id, m.company_id
      FROM rule_refs rr
      JOIN moves m ON m.tenant_id = rr.tenant_id AND ((m.kind = rr.k1 AND m.id = rr.r1) OR (m.kind = rr.k2 AND m.id = rr.r2))
     WHERE (rr.branch_id IS NULL OR EXISTS (SELECT 1 FROM branches b WHERE b.tenant_id = rr.tenant_id AND b.id = rr.branch_id AND b.company_id = m.company_id))
       AND (rr.r1 IS NULL OR EXISTS (SELECT 1 FROM moves x WHERE x.kind = rr.k1 AND x.id = rr.r1 AND x.company_id = m.company_id)
            OR NOT EXISTS (SELECT 1 FROM keepers k WHERE k.kind = rr.k1 AND k.id = rr.r1 AND k.company_id <> m.company_id))
       AND (rr.r2 IS NULL OR EXISTS (SELECT 1 FROM moves x WHERE x.kind = rr.k2 AND x.id = rr.r2 AND x.company_id = m.company_id)
            OR NOT EXISTS (SELECT 1 FROM keepers k WHERE k.kind = rr.k2 AND k.id = rr.r2 AND k.company_id <> m.company_id))),
metrics AS (
    SELECT tenant_id, 'rows_assigned_single_user' AS metric, kind AS subject, count(*) AS n FROM ranked WHERE rn = 1 AND n_users = 1 GROUP BY 1, 3
    UNION ALL SELECT tenant_id, 'rows_kept_by_keeper', kind, count(*) FROM ranked WHERE rn = 1 AND n_users > 1 GROUP BY 1, 3
    UNION ALL SELECT tenant_id, 'copies_to_create', kind, count(*) FROM moves GROUP BY 1, 3
    UNION ALL SELECT o.tenant_id, 'rows_assigned_only_company', o.kind, count(*) FROM org o JOIN only_co oc ON oc.tenant_id = o.tenant_id
               WHERE NOT EXISTS (SELECT 1 FROM usage u WHERE u.kind = o.kind AND u.id = o.id) GROUP BY 1, 3
    UNION ALL SELECT o.tenant_id, 'rows_left_unassigned', o.kind, count(*) FROM org o
               WHERE NOT EXISTS (SELECT 1 FROM only_co oc WHERE oc.tenant_id = o.tenant_id)
                 AND NOT EXISTS (SELECT 1 FROM usage u JOIN co ON co.company_id = u.company_id WHERE u.kind = o.kind AND u.id = o.id) GROUP BY 1, 3
    -- references re-pointed (the row's own company has a copy)
    UNION ALL SELECT m.tenant_id, 'refs_repointed', 'employees.department_id', count(*) FROM employees t JOIN moves m ON m.kind = 'department' AND m.id = t.department_id AND m.company_id = t.company_id GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'refs_repointed', 'employees.designation_id', count(*) FROM employees t JOIN moves m ON m.kind = 'designation' AND m.id = t.designation_id AND m.company_id = t.company_id GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'refs_repointed', 'employees.grade_id', count(*) FROM employees t JOIN moves m ON m.kind = 'grade' AND m.id = t.grade_id AND m.company_id = t.company_id GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'refs_repointed', 'positions.department_id', count(*) FROM positions t JOIN moves m ON m.kind = 'department' AND m.id = t.department_id AND m.company_id = t.company_id GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'refs_repointed', 'positions.designation_id', count(*) FROM positions t JOIN moves m ON m.kind = 'designation' AND m.id = t.designation_id AND m.company_id = t.company_id GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'refs_repointed', 'positions.grade_id', count(*) FROM positions t JOIN moves m ON m.kind = 'grade' AND m.id = t.grade_id AND m.company_id = t.company_id GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'refs_repointed', 'grade_entitlements.grade_id (company cells)', count(*) FROM grade_entitlements t JOIN moves m ON m.kind = 'grade' AND m.id = t.grade_id AND m.company_id = t.company_id GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'refs_repointed', 'employee_loans.grade_id_at_request', count(*) FROM employee_loans t JOIN moves m ON m.kind = 'grade' AND m.id = t.grade_id_at_request AND m.company_id = t.company_id GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'refs_repointed', 'pay_benchmark_rows.designation_id', count(*) FROM pay_benchmark_rows t JOIN moves m ON m.kind = 'designation' AND m.id = t.designation_id AND m.company_id = t.company_id GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'refs_repointed', 'pay_benchmark_rows.grade_id', count(*) FROM pay_benchmark_rows t JOIN moves m ON m.kind = 'grade' AND m.id = t.grade_id AND m.company_id = t.company_id GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'refs_repointed', 'comp_cycle_budgets.department_id', count(*) FROM comp_cycle_budgets t JOIN moves m ON m.kind = 'department' AND m.id = t.department_id AND m.company_id = t.company_id GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'refs_repointed', 'comp_worksheet_lines.department_id', count(*) FROM comp_worksheet_lines t JOIN moves m ON m.kind = 'department' AND m.id = t.department_id AND m.company_id = t.company_id GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'refs_repointed', 'comp_worksheet_lines.designation_id', count(*) FROM comp_worksheet_lines t JOIN moves m ON m.kind = 'designation' AND m.id = t.designation_id AND m.company_id = t.company_id GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'refs_repointed', 'comp_worksheet_lines.grade_id', count(*) FROM comp_worksheet_lines t JOIN moves m ON m.kind = 'grade' AND m.id = t.grade_id AND m.company_id = t.company_id GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'refs_repointed', 'learning_assignments.department_id', count(*) FROM learning_assignments t JOIN moves m ON m.kind = 'department' AND m.id = t.department_id AND m.company_id = t.company_id GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'refs_repointed', 'learning_assignments.designation_id', count(*) FROM learning_assignments t JOIN moves m ON m.kind = 'designation' AND m.id = t.designation_id AND m.company_id = t.company_id GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'refs_repointed', 'learning_assignments.grade_id', count(*) FROM learning_assignments t JOIN moves m ON m.kind = 'grade' AND m.id = t.grade_id AND m.company_id = t.company_id GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'refs_repointed', 'leave_policy_eligibilities.department_id', count(*) FROM leave_policy_eligibilities t JOIN moves m ON m.kind = 'department' AND m.id = t.department_id AND m.company_id = t.company_id GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'refs_repointed', 'leave_policy_eligibilities.grade_id', count(*) FROM leave_policy_eligibilities t JOIN moves m ON m.kind = 'grade' AND m.id = t.grade_id AND m.company_id = t.company_id GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'refs_repointed', 'benefit_eligibility_rules.grade_id', count(*) FROM benefit_eligibility_rules t JOIN moves m ON m.kind = 'grade' AND m.id = t.grade_id AND m.company_id = t.company_id GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'refs_repointed', 'employee_transfer_requests.current_department_id', count(*) FROM employee_transfer_requests t JOIN employees e ON e.tenant_id = t.tenant_id AND e.id = t.employee_id
               JOIN moves m ON m.kind = 'department' AND m.id = t.current_department_id AND m.company_id = e.company_id GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'refs_repointed', 'departments.parent_department_id', count(*) FROM departments t
               JOIN usage u ON u.kind = 'department' AND u.id = t.id
               JOIN moves m ON m.kind = 'department' AND m.id = t.parent_department_id AND m.company_id = u.company_id GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'refs_repointed', 'designations.department_id / grade_id', count(*) FROM designations t
               JOIN usage u ON u.kind = 'designation' AND u.id = t.id
               JOIN moves m ON m.company_id = u.company_id AND ((m.kind = 'department' AND m.id = t.department_id) OR (m.kind = 'grade' AND m.id = t.grade_id)) GROUP BY 1
    -- child config copied with the row
    UNION ALL SELECT m.tenant_id, 'child_rows_copied', 'grade_pay_scale_components', count(*) FROM grade_pay_scale_components t JOIN moves m ON m.kind = 'grade' AND m.id = t.grade_id GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'child_rows_copied', 'grade_entitlements (tenant-wide cells)', count(*) FROM grade_entitlements t JOIN moves m ON m.kind = 'grade' AND m.id = t.grade_id WHERE t.company_id IS NULL GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'child_rows_copied', 'department_staffing_budgets', count(*) FROM department_staffing_budgets t JOIN moves m ON m.kind = 'department' AND m.id = t.department_id GROUP BY 1
    UNION ALL SELECT tenant_id, 'rules_copied', tbl, count(*) FROM rule_copies GROUP BY 1, 3
    -- left on the keeper's row (tenant-wide rules carry no company; evidence is immutable)
    UNION ALL SELECT k.tenant_id, 'refs_left_on_keeper', 'manpower_requisitions', count(*) FROM manpower_requisitions t JOIN keepers k ON k.id IN (t.department_id, t.designation_id) AND EXISTS (SELECT 1 FROM moves m WHERE m.id = k.id) GROUP BY 1
    UNION ALL SELECT k.tenant_id, 'refs_left_on_keeper', 'job_openings', count(*) FROM job_openings t JOIN keepers k ON k.id IN (t.department_id, t.designation_id) AND EXISTS (SELECT 1 FROM moves m WHERE m.id = k.id) GROUP BY 1
    UNION ALL SELECT k.tenant_id, 'refs_left_on_keeper', 'workforce_plans', count(*) FROM workforce_plans t JOIN keepers k ON k.id = t.department_id AND EXISTS (SELECT 1 FROM moves m WHERE m.id = k.id) GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'refs_left_on_keeper', 'survey_responses (append-only evidence)', count(*) FROM survey_responses t
               JOIN moves m ON m.company_id = t.company_id AND m.id IN (t.department_id, t.designation_id) GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'refs_left_on_keeper', 'employee_entitlements.grade_entitlement_id (close-only witness)', count(*) FROM employee_entitlements t
               JOIN grade_entitlements ge ON ge.tenant_id = t.tenant_id AND ge.id = t.grade_entitlement_id AND ge.company_id IS NULL
               JOIN moves m ON m.kind = 'grade' AND m.id = ge.grade_id AND m.company_id = t.company_id GROUP BY 1
    -- a kept department loses a branch / cost centre that belongs to another company
    UNION ALL SELECT k.tenant_id, 'links_cleared', 'departments.branch_id', count(*) FROM departments d JOIN keepers k ON k.kind = 'department' AND k.id = d.id
               JOIN branches b ON b.tenant_id = d.tenant_id AND b.id = d.branch_id WHERE b.company_id <> k.company_id GROUP BY 1
    UNION ALL SELECT k.tenant_id, 'links_cleared', 'departments.cost_center_id', count(*) FROM departments d JOIN keepers k ON k.kind = 'department' AND k.id = d.id
               JOIN cost_centers cc ON cc.tenant_id = d.tenant_id AND cc.id = d.cost_center_id WHERE cc.company_id IS DISTINCT FROM k.company_id GROUP BY 1
),
budget_split AS (
    SELECT b.tenant_id, b.id AS budget_id, d.code AS department_code, b.year, b.month, b.budget_amount, b.currency,
           b.consumed_amount AS consumed_stored, m.company_id AS copy_company, k.company_id AS keeper_company,
           (SELECT COALESCE(sum(c.amount), 0) FROM employees e
              JOIN overtime_calculations c ON c.tenant_id = b.tenant_id AND c.employee_id = e.id AND c.currency = b.currency
              JOIN overtime_requests r ON r.tenant_id = b.tenant_id AND r.id = c.overtime_request_id AND r.status = 'Approved'
             WHERE e.tenant_id = b.tenant_id AND e.department_id = b.department_id
               AND EXTRACT(YEAR FROM r.work_date) = b.year AND EXTRACT(MONTH FROM r.work_date) = b.month
               AND (b.project_id IS NULL OR r.project_id = b.project_id)) AS derived_total,
           (SELECT COALESCE(sum(c.amount), 0) FROM employees e
              JOIN overtime_calculations c ON c.tenant_id = b.tenant_id AND c.employee_id = e.id AND c.currency = b.currency
              JOIN overtime_requests r ON r.tenant_id = b.tenant_id AND r.id = c.overtime_request_id AND r.status = 'Approved'
             WHERE e.tenant_id = b.tenant_id AND e.department_id = b.department_id AND e.company_id = m.company_id
               AND EXTRACT(YEAR FROM r.work_date) = b.year AND EXTRACT(MONTH FROM r.work_date) = b.month
               AND (b.project_id IS NULL OR r.project_id = b.project_id)) AS derived_copy,
           (SELECT COALESCE(sum(c.amount), 0) FROM employees e
              JOIN overtime_calculations c ON c.tenant_id = b.tenant_id AND c.employee_id = e.id AND c.currency = b.currency
              JOIN overtime_requests r ON r.tenant_id = b.tenant_id AND r.id = c.overtime_request_id AND r.status = 'Approved'
             WHERE e.tenant_id = b.tenant_id AND e.department_id = b.department_id AND e.company_id = k.company_id
               AND EXTRACT(YEAR FROM r.work_date) = b.year AND EXTRACT(MONTH FROM r.work_date) = b.month
               AND (b.project_id IS NULL OR r.project_id = b.project_id)) AS derived_keeper
      FROM overtime_budgets b
      JOIN moves m ON m.kind = 'department' AND m.id = b.department_id AND m.tenant_id = b.tenant_id
      JOIN keepers k ON k.kind = 'department' AND k.id = b.department_id
      JOIN departments d ON d.id = b.department_id)
SELECT s.tenant_id, s.budget_id, s.department_code, s.year, s.month,
       'budget duplicated for company ' || COALESCE(cc.legal_name_en, s.copy_company::text) AS budget,
       s.budget_amount || ' ' || s.currency AS budget_amount_copied,
       CASE WHEN round(s.derived_total, 2) = round(s.consumed_stored, 2) THEN 'recomputed_from_overtime' ELSE 'copies_zeroed' END AS method,
       s.consumed_stored AS keeper_consumed_before,
       CASE WHEN round(s.derived_total, 2) = round(s.consumed_stored, 2) THEN s.derived_keeper ELSE s.consumed_stored END AS keeper_consumed_after,
       CASE WHEN round(s.derived_total, 2) = round(s.consumed_stored, 2) THEN s.derived_copy ELSE 0 END AS consumed_moved_to_copy
  FROM budget_split s LEFT JOIN companies cc ON cc.id = s.copy_company
 ORDER BY s.tenant_id, s.department_code, s.year, s.month, budget;


-- Arabic names that may be phonetic transliterations (the old "Suggest (AR)" sounded English out in
-- Arabic letters, e.g. "Engineering" -> "ينغينيرنغ"). HEURISTIC, for review only — nothing is changed:
-- an Arabic name containing "ينغ" (eng/ing), ending in "نغ" (-ng) or in "شن" (-tion) is rare in genuine
-- Arabic wording. It undercounts (not every transliteration has these letters); review the rows it finds.
SELECT o.tenant_id, o.tbl, count(*) AS arabic_name_possibly_phonetic
  FROM (SELECT tenant_id, 'departments' AS tbl, name_ar AS ar FROM departments
        UNION ALL SELECT tenant_id, 'designations', title_ar FROM designations
        UNION ALL SELECT tenant_id, 'grades', name_ar FROM grades
        UNION ALL SELECT tenant_id, 'branches', name_ar FROM branches) o
 WHERE o.ar ~ '[؀-ۿ]' AND (o.ar ~ 'ينغ' OR o.ar ~ 'نغ(\s|$)' OR o.ar ~ 'شن(\s|$)')
 GROUP BY 1, 2 ORDER BY 1, 2;


-- ══ Leave per company (PerCompanyLeaveColumns + BackfillLeavePerCompany, after CopyWorkspaceRulesPerCompany) ══
-- Written for the database BEFORE those migrations (leave_types has no company_id yet) and mirroring the backfill:
--   a leave type used by one company -> assigned; by several -> kept by the company with most employees on it (ties:
--   oldest), copied for each other company, that company's employees' rows re-pointed; by nobody -> the only company,
--   else left unassigned. "Used by" = the company's employees hold a balance / ledger row / request / encashment /
--   absence regularisation on it, a policy of that company names it, or an ACTIVE tenant-wide policy on it reaches the
--   company (its branch's company; else the companies its active eligibility rows name when all name one; else every
--   company — minus companies in another country than the policy names).
-- Metrics: leave_types_* / leave_type_copies_to_create, leave_policies_* / leave_policy_copies_to_create,
--   leave_refs_repointed (per table), leave_requests_touched (per status, incl. pending / approved),
--   leave_children (accrual rules copied, eligibility rows copied / moved — rows the rule copy makes for a company move
--   with them), leave_policy_workflow_repin_candidates, leave_policy_orphans_archived.
WITH
iso(code, iso2) AS (VALUES ('SA','SA'),('SAU','SA'),('KSA','SA'),('AE','AE'),('ARE','AE'),('UAE','AE'),('KW','KW'),('KWT','KW'),
    ('BH','BH'),('BHR','BH'),('OM','OM'),('OMN','OM'),('QA','QA'),('QAT','QA'),('EG','EG'),('EGY','EG'),('JO','JO'),('JOR','JO'),
    ('LB','LB'),('LBN','LB'),('IN','IN'),('IND','IN'),('PK','PK'),('PAK','PK'),('PH','PH'),('PHL','PH'),('GB','GB'),('GBR','GB'),('US','US'),('USA','US')),
co AS (
    SELECT c.tenant_id, c.id AS company_id, c.created_at_utc,
           (SELECT i.iso2 FROM iso i WHERE i.code = upper(btrim(coalesce(c.country_code, '')))) AS iso2
      FROM companies c WHERE NOT c.is_deleted),
only_co AS (SELECT tenant_id, min(company_id::text)::uuid AS company_id FROM co GROUP BY tenant_id HAVING count(*) = 1),
emp AS (SELECT e.tenant_id, e.id AS employee_id, e.company_id FROM employees e JOIN co ON co.tenant_id = e.tenant_id AND co.company_id = e.company_id),
lrows AS (
    SELECT 'employee_leave_balances' AS tbl, tenant_id, leave_type_id, employee_id, NULL::text AS status FROM employee_leave_balances
    UNION ALL SELECT 'leave_balance_transactions', tenant_id, leave_type_id, employee_id, NULL FROM leave_balance_transactions
    UNION ALL SELECT 'leave_requests', tenant_id, leave_type_id, employee_id, status FROM leave_requests
    UNION ALL SELECT 'leave_encashment_requests', tenant_id, leave_type_id, employee_id, NULL FROM leave_encashment_requests
    UNION ALL SELECT 'absence_regularization_requests', tenant_id, leave_type_id, employee_id, NULL FROM absence_regularization_requests WHERE leave_type_id IS NOT NULL),
pol AS (
    SELECT p.id, p.tenant_id, p.leave_type_id, p.status, p.approval_workflow_id,
           (SELECT i.iso2 FROM iso i WHERE i.code = upper(btrim(coalesce(p.country_code, '')))) AS iso2,
           (SELECT b.company_id FROM branches b WHERE b.tenant_id = p.tenant_id AND b.id = p.branch_id) AS branch_company,
           EXISTS (SELECT 1 FROM leave_policy_eligibilities el WHERE el.tenant_id = p.tenant_id AND el.leave_policy_id = p.id AND el.is_active) AS has_rows,
           EXISTS (SELECT 1 FROM leave_policy_eligibilities el WHERE el.tenant_id = p.tenant_id AND el.leave_policy_id = p.id AND el.is_active AND el.company_id IS NULL) AS has_open_row
      FROM leave_policies p
     WHERE p.company_id IS NULL
        OR NOT EXISTS (SELECT 1 FROM companies c WHERE c.tenant_id = p.tenant_id AND c.id = p.company_id)),
reach AS (
    SELECT p.id AS policy_id, c.company_id FROM pol p JOIN co c ON c.tenant_id = p.tenant_id
     WHERE (p.iso2 IS NULL OR c.iso2 IS NULL OR p.iso2 = c.iso2)
       AND CASE WHEN p.branch_company IS NOT NULL THEN c.company_id = p.branch_company
                WHEN p.has_rows AND NOT p.has_open_row THEN EXISTS (SELECT 1 FROM leave_policy_eligibilities el
                     WHERE el.tenant_id = p.tenant_id AND el.leave_policy_id = p.id AND el.is_active AND el.company_id = c.company_id)
                ELSE true END),
use_raw AS (
    SELECT r.leave_type_id AS id, e.company_id, count(DISTINCT r.employee_id) AS employees
      FROM lrows r JOIN emp e ON e.tenant_id = r.tenant_id AND e.employee_id = r.employee_id GROUP BY 1, 2
    UNION ALL SELECT p.leave_type_id, p.company_id, 0 FROM leave_policies p JOIN co ON co.tenant_id = p.tenant_id AND co.company_id = p.company_id
    UNION ALL SELECT p.leave_type_id, r.company_id, 0 FROM reach r JOIN pol p ON p.id = r.policy_id WHERE p.status = 'Active'),
usage AS (SELECT id, company_id, max(employees) AS employees FROM use_raw GROUP BY 1, 2),
ttarget AS (
    SELECT t.tenant_id, t.id, t.code, u.company_id, u.employees,
           row_number() OVER (PARTITION BY t.id ORDER BY u.employees DESC, c.created_at_utc, u.company_id) AS rn,
           count(*) OVER (PARTITION BY t.id) AS users
      FROM leave_types t JOIN usage u ON u.id = t.id JOIN co c ON c.tenant_id = t.tenant_id AND c.company_id = u.company_id),
tmoves AS (SELECT tenant_id, id, company_id FROM ttarget WHERE rn > 1),
versions AS (
    SELECT tenant_id, id, company_id, rn = 1 AS is_original FROM ttarget
    UNION ALL SELECT t.tenant_id, t.id, oc.company_id, true FROM leave_types t JOIN only_co oc ON oc.tenant_id = t.tenant_id
     WHERE NOT EXISTS (SELECT 1 FROM usage u WHERE u.id = t.id)),
pcomp AS (
    SELECT r.policy_id, r.company_id FROM reach r JOIN pol p ON p.id = r.policy_id
      JOIN versions v ON v.tenant_id = p.tenant_id AND v.id = p.leave_type_id AND v.company_id = r.company_id
    UNION SELECT p.id, e.company_id FROM pol p JOIN leave_requests lr ON lr.tenant_id = p.tenant_id AND lr.policy_id = p.id
      JOIN emp e ON e.tenant_id = lr.tenant_id AND e.employee_id = lr.employee_id
      JOIN versions v ON v.tenant_id = p.tenant_id AND v.id = p.leave_type_id AND v.company_id = e.company_id),
pcomp_all AS (
    SELECT * FROM pcomp
    UNION SELECT p.id, oc.company_id FROM pol p JOIN only_co oc ON oc.tenant_id = p.tenant_id
      JOIN versions v ON v.tenant_id = p.tenant_id AND v.id = p.leave_type_id AND v.company_id = oc.company_id
     WHERE NOT EXISTS (SELECT 1 FROM pcomp x WHERE x.policy_id = p.id)),
ptarget AS (
    SELECT p.tenant_id, p.id, pc.company_id, p.approval_workflow_id,
           row_number() OVER (PARTITION BY p.id ORDER BY v.is_original DESC, COALESCE(u.employees, 0) DESC, c.created_at_utc, pc.company_id) AS rn,
           count(*) OVER (PARTITION BY p.id) AS users
      FROM pol p JOIN pcomp_all pc ON pc.policy_id = p.id
      JOIN co c ON c.tenant_id = p.tenant_id AND c.company_id = pc.company_id
      JOIN versions v ON v.tenant_id = p.tenant_id AND v.id = p.leave_type_id AND v.company_id = pc.company_id
      LEFT JOIN usage u ON u.id = p.leave_type_id AND u.company_id = pc.company_id),
pmoves AS (SELECT tenant_id, id, company_id, approval_workflow_id FROM ptarget WHERE rn > 1),
metrics(tenant_id, metric, subject, n) AS (
    SELECT tenant_id, 'leave_types_assigned_single_user', 'leave_types', count(*) FROM ttarget WHERE rn = 1 AND users = 1 GROUP BY 1
    UNION ALL SELECT tenant_id, 'leave_types_kept_by_keeper', 'leave_types', count(*) FROM ttarget WHERE rn = 1 AND users > 1 GROUP BY 1
    UNION ALL SELECT tenant_id, 'leave_type_copies_to_create', 'leave_types', count(*) FROM tmoves GROUP BY 1
    UNION ALL SELECT t.tenant_id, 'leave_types_assigned_only_company', 'leave_types', count(*) FROM leave_types t JOIN only_co oc ON oc.tenant_id = t.tenant_id
               WHERE NOT EXISTS (SELECT 1 FROM usage u WHERE u.id = t.id) GROUP BY 1
    UNION ALL SELECT t.tenant_id, 'leave_types_left_unassigned', 'leave_types', count(*) FROM leave_types t
               WHERE NOT EXISTS (SELECT 1 FROM usage u WHERE u.id = t.id) AND NOT EXISTS (SELECT 1 FROM only_co oc WHERE oc.tenant_id = t.tenant_id) GROUP BY 1
    UNION ALL SELECT r.tenant_id, 'leave_refs_repointed', r.tbl || '.leave_type_id', count(*) FROM lrows r
               JOIN emp e ON e.tenant_id = r.tenant_id AND e.employee_id = r.employee_id
               JOIN tmoves m ON m.id = r.leave_type_id AND m.company_id = e.company_id GROUP BY 1, 3
    UNION ALL SELECT r.tenant_id, 'leave_requests_touched', coalesce(r.status, '(none)'), count(*) FROM lrows r
               JOIN emp e ON e.tenant_id = r.tenant_id AND e.employee_id = r.employee_id
               JOIN tmoves m ON m.id = r.leave_type_id AND m.company_id = e.company_id
              WHERE r.tbl = 'leave_requests' GROUP BY 1, 3
    UNION ALL SELECT lr.tenant_id, 'leave_refs_repointed', 'leave_requests.policy_id', count(*) FROM leave_requests lr
               JOIN emp e ON e.tenant_id = lr.tenant_id AND e.employee_id = lr.employee_id
               JOIN pmoves m ON m.id = lr.policy_id AND m.company_id = e.company_id GROUP BY 1
    UNION ALL SELECT p.tenant_id, 'leave_refs_repointed', 'leave_policies.leave_type_id (company policies)', count(*) FROM leave_policies p
               JOIN tmoves m ON m.id = p.leave_type_id AND m.company_id = p.company_id GROUP BY 1
    UNION ALL SELECT tenant_id, 'leave_policies_assigned', 'leave_policies', count(*) FROM ptarget WHERE rn = 1 GROUP BY 1
    UNION ALL SELECT tenant_id, 'leave_policy_copies_to_create', 'leave_policies', count(*) FROM pmoves GROUP BY 1
    UNION ALL SELECT p.tenant_id, 'leave_policies_left_unassigned', 'leave_policies', count(*) FROM pol p
               WHERE NOT EXISTS (SELECT 1 FROM ptarget x WHERE x.id = p.id) GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'leave_children', 'leave_accrual_rules copied', count(*) FROM leave_accrual_rules a JOIN pmoves m ON m.id = a.leave_policy_id GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'leave_children', 'leave_policy_eligibilities copied (company-less; fewer where the rule copy made the company''s row)', count(*)
               FROM leave_policy_eligibilities el JOIN pmoves m ON m.id = el.leave_policy_id WHERE el.company_id IS NULL GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'leave_children', 'leave_policy_eligibilities moved (naming the copy''s company; plus the rule copy''s rows)', count(*)
               FROM leave_policy_eligibilities el JOIN pmoves m ON m.id = el.leave_policy_id AND m.company_id = el.company_id GROUP BY 1
    UNION ALL SELECT m.tenant_id, 'leave_policy_workflow_repin_candidates', 'policy copies pinned to a department/grade-scoped workflow', count(*)
               FROM pmoves m JOIN approval_workflows w ON w.tenant_id = m.tenant_id AND w.id = m.approval_workflow_id
              WHERE w.department_id IS NOT NULL OR w.grade_id IS NOT NULL GROUP BY 1
    UNION ALL SELECT p.tenant_id, 'leave_policy_orphans_archived', 'leave_policies.company_id names no company of the tenant', count(*) FROM leave_policies p
               WHERE p.company_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM companies c WHERE c.tenant_id = p.tenant_id AND c.id = p.company_id) GROUP BY 1
)
SELECT tenant_id, metric, subject, n FROM metrics WHERE n > 0 ORDER BY tenant_id, metric, subject;

-- Leave balance invariant, PRE-snapshot. Keyed by employee and leave CODE (the ids move, the figures must not). Save
-- this output; run the SAME query after deploy — every tenant's checksum and row count must be identical.
SELECT x.tenant_id, count(*) AS figure_rows, md5(string_agg(x.line, '|' ORDER BY x.line)) AS leave_figures_checksum
  FROM (SELECT b.tenant_id, concat_ws(';', b.employee_id, t.code, b.year, sum(b.entitled), sum(b.accrued), sum(b.used), sum(b.pending),
                                      sum(b.carried_forward), sum(b.encashed), sum(b.expired), sum(b.manual_adjustment), count(*)) AS line
          FROM employee_leave_balances b JOIN leave_types t ON t.id = b.leave_type_id GROUP BY b.tenant_id, b.employee_id, t.code, b.year
        UNION ALL
        SELECT l.tenant_id, concat_ws(';', 'ledger', l.employee_id, t.code, l.year, l.transaction_type, sum(l.amount), count(*))
          FROM leave_balance_transactions l JOIN leave_types t ON t.id = l.leave_type_id GROUP BY l.tenant_id, l.employee_id, t.code, l.year, l.transaction_type
        UNION ALL
        SELECT r.tenant_id, concat_ws(';', 'request', r.employee_id, t.code, r.status, sum(r.total_days), count(*))
          FROM leave_requests r JOIN leave_types t ON t.id = r.leave_type_id GROUP BY r.tenant_id, r.employee_id, t.code, r.status) x
 GROUP BY x.tenant_id ORDER BY x.tenant_id;

ROLLBACK;
