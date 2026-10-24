using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// Addendum B (B1), step 2: give every department, designation and grade an owning company.
    ///
    /// <para><b>Rules</b> (docs/design/company-setup-wizard.md, Addendum B1). Only rows whose <c>company_id</c> is still
    /// NULL are considered. A row is "used by" a company when that company's (non-deleted) employees reference it, when a
    /// department's branch or cost centre belongs to it, or — transitively — when a designation or child department that
    /// is itself used by the company references it.</para>
    /// <list type="number">
    /// <item>Used by exactly one company: assigned to it.</item>
    /// <item>Used by several: the row keeps its id for the company with the most employees on it (ties: the oldest
    /// company, then the lowest id). Every other user company gets a copy with the same code and values, and that
    /// company's references are re-pointed to its copy in every FK column listed in <see cref="RepointedColumns"/>.
    /// Child config is copied with it: grade pay scale components, the tenant-wide grade entitlement cells and the
    /// department staffing budgets. A copied grade takes its company's <c>default_currency</c>; nothing else changes
    /// currency.</item>
    /// <item>Used by nobody: the tenant's only company when it has exactly one; otherwise it stays NULL and Setup
    /// lists it as "Unassigned — choose the company".</item>
    /// </list>
    /// <para>When the target company already holds a row with the same code (only possible on a re-run after new rows
    /// were created), that row is used instead of a copy. A branch or cost centre of another company is cleared from a
    /// department that ends up in a different company (recorded in the audit metadata).</para>
    ///
    /// <para><b>Deterministic and idempotent.</b> Copy ids are <c>md5('opc:…')::uuid</c> of the source id and the
    /// company, the order of everything is fixed, and a re-run finds no NULL row in use, so it changes nothing.</para>
    ///
    /// <para><b>Left alone on purpose</b> (counted by docs/runbooks/org-per-company-precheck.sql): immutable evidence
    /// (<c>survey_responses</c>, append-only; <c>employee_entitlements.grade_entitlement_id</c>, close-only; the loan's
    /// <c>grade_entitlement_id</c> witness, which still points at a row with the figures it was decided under), and
    /// tenant-wide rules that carry no company (approval policies and workflows, attendance and overtime policies,
    /// overtime budgets, requisitions, job openings, workforce plans, onboarding checklists, and leave/benefit
    /// eligibility rows with a NULL company). Those keep pointing at the keeper's row.</para>
    ///
    /// <para><b>Audit.</b> One <c>audit_logs</c> row per assigned, copied or mapped row, chained into the tenant's
    /// SHA-256 hash chain exactly as <c>AuditService.ComputeHash</c> does, so <c>VerifyChain</c> still passes.</para>
    ///
    /// <para><b>Down</b> is a no-op: the assignment cannot be undone without losing which company a copy belongs to.
    /// Rolling the schema back past PerCompanyOrganisationColumns fails on its (tenant_id, code) unique index once a
    /// copy exists — restore the pre-deploy database branch instead.</para>
    /// </summary>
    public partial class BackfillOrganisationPerCompany : Migration
    {
        /// <summary>Every FK column the backfill re-points, as table.column (kind). Kept in one place for the tests and the report.</summary>
        public static readonly string[] RepointedColumns =
        [
            "employees.department_id (department)", "employees.designation_id (designation)", "employees.grade_id (grade)",
            "departments.parent_department_id (department)",
            "designations.department_id (department)", "designations.grade_id (grade)",
            "positions.department_id (department)", "positions.designation_id (designation)", "positions.grade_id (grade)",
            "grade_entitlements.grade_id (grade, company cells)",
            "employee_loans.grade_id_at_request (grade)",
            "pay_benchmark_rows.designation_id (designation)", "pay_benchmark_rows.grade_id (grade)",
            "comp_cycle_budgets.department_id (department)",
            "comp_worksheet_lines.department_id (department)", "comp_worksheet_lines.designation_id (designation)", "comp_worksheet_lines.grade_id (grade)",
            "learning_assignments.department_id (department)", "learning_assignments.designation_id (designation)", "learning_assignments.grade_id (grade)",
            "leave_policy_eligibilities.department_id (department, company rows)", "leave_policy_eligibilities.grade_id (grade, company rows)",
            "benefit_eligibility_rules.grade_id (grade, company rows)",
            "employee_transfer_requests.current_department_id (department)", "employee_transfer_requests.new_department_id (department)", "employee_transfer_requests.new_designation_id (designation)",
            "salary_structures.eligible_grade_ids_json (grade, json)", "salary_structures.eligible_designation_ids_json (designation, json)",
            "comp_cycles.eligibility_filters_json (grade + department, jsonb)",
            "grade_pay_scale_components.grade_id (copied with the grade)", "grade_entitlements tenant-wide cells (copied with the grade)",
            "department_staffing_budgets.department_id (copied with the department)",
        ];

        internal const string Actor = "system: migration BackfillOrganisationPerCompany";

        /// <summary>The whole backfill. Executed by <see cref="Up"/> and, verbatim, by the Postgres tests.</summary>
        public const string BackfillSql = """
DO $opc$
DECLARE
    total_new int;
    n int;
    r record;
    aud record;
    prev_hash text;
    ts timestamptz;
    meta text;
    canonical text;
    h text;
    cols text;
    current_tenant uuid := NULL;
    n_assigned int; n_only int; n_copied int; n_mapped int; n_unassigned int; n_refs bigint := 0;
BEGIN
    CREATE TEMP TABLE IF NOT EXISTS _opc_company ON COMMIT DROP AS
        SELECT c.tenant_id, c.id AS company_id, c.created_at_utc, NULLIF(btrim(c.default_currency), '') AS default_currency
          FROM companies c WHERE NOT c.is_deleted WITH NO DATA;
    TRUNCATE _opc_company;
    INSERT INTO _opc_company
        SELECT c.tenant_id, c.id AS company_id, c.created_at_utc, NULLIF(btrim(c.default_currency), '') AS default_currency
          FROM companies c WHERE NOT c.is_deleted;
    CREATE UNIQUE INDEX IF NOT EXISTS _opc_company_key ON _opc_company (tenant_id, company_id);

    CREATE TEMP TABLE IF NOT EXISTS _opc_all ON COMMIT DROP AS
        SELECT 'department'::text AS kind, d.tenant_id, d.id, d.company_id, d.code FROM departments d
        UNION ALL SELECT 'designation', x.tenant_id, x.id, x.company_id, x.code FROM designations x
        UNION ALL SELECT 'grade', g.tenant_id, g.id, g.company_id, g.code FROM grades g WITH NO DATA;
    TRUNCATE _opc_all;
    INSERT INTO _opc_all
        SELECT 'department'::text AS kind, d.tenant_id, d.id, d.company_id, d.code FROM departments d
        UNION ALL SELECT 'designation', x.tenant_id, x.id, x.company_id, x.code FROM designations x
        UNION ALL SELECT 'grade', g.tenant_id, g.id, g.company_id, g.code FROM grades g;
    CREATE UNIQUE INDEX IF NOT EXISTS _opc_all_key ON _opc_all (kind, id);

    -- Usage: (kind, row, company) with the number of that company's employees directly on the row.
    CREATE TEMP TABLE IF NOT EXISTS _opc_use (kind text NOT NULL, id uuid NOT NULL, company_id uuid NOT NULL, employees int NOT NULL DEFAULT 0,
                                PRIMARY KEY (kind, id, company_id)) ON COMMIT DROP;
    TRUNCATE _opc_use;
    INSERT INTO _opc_use (kind, id, company_id, employees)
    SELECT u.kind, u.id, u.company_id, count(*)
      FROM (SELECT 'department'::text AS kind, e.department_id AS id, e.company_id, e.tenant_id FROM employees e WHERE e.department_id IS NOT NULL AND NOT e.is_deleted
            UNION ALL SELECT 'designation', e.designation_id, e.company_id, e.tenant_id FROM employees e WHERE e.designation_id IS NOT NULL AND NOT e.is_deleted
            UNION ALL SELECT 'grade', e.grade_id, e.company_id, e.tenant_id FROM employees e WHERE e.grade_id IS NOT NULL AND NOT e.is_deleted) u
      JOIN _opc_company c ON c.tenant_id = u.tenant_id AND c.company_id = u.company_id
      JOIN _opc_all a ON a.kind = u.kind AND a.id = u.id AND a.tenant_id = u.tenant_id
     GROUP BY u.kind, u.id, u.company_id;
    -- A row that already has a company is used by it (so its links propagate).
    INSERT INTO _opc_use (kind, id, company_id)
    SELECT a.kind, a.id, a.company_id FROM _opc_all a JOIN _opc_company c ON c.tenant_id = a.tenant_id AND c.company_id = a.company_id
    ON CONFLICT DO NOTHING;
    -- A department is used by the company of its branch and of its cost centre.
    INSERT INTO _opc_use (kind, id, company_id)
    SELECT 'department', d.id, b.company_id FROM departments d
      JOIN branches b ON b.tenant_id = d.tenant_id AND b.id = d.branch_id
      JOIN _opc_company c ON c.tenant_id = b.tenant_id AND c.company_id = b.company_id
    ON CONFLICT DO NOTHING;
    INSERT INTO _opc_use (kind, id, company_id)
    SELECT 'department', d.id, cc.company_id FROM departments d
      JOIN cost_centers cc ON cc.tenant_id = d.tenant_id AND cc.id = d.cost_center_id
      JOIN _opc_company c ON c.tenant_id = cc.tenant_id AND c.company_id = cc.company_id
    ON CONFLICT DO NOTHING;
    -- Transitive: a designation used by C uses its grade and department; a department used by C uses its parent.
    LOOP
        total_new := 0;
        INSERT INTO _opc_use (kind, id, company_id)
        SELECT 'grade', x.grade_id, u.company_id FROM _opc_use u JOIN designations x ON u.kind = 'designation' AND x.id = u.id
          JOIN _opc_all a ON a.kind = 'grade' AND a.id = x.grade_id AND a.tenant_id = x.tenant_id
        ON CONFLICT DO NOTHING;
        GET DIAGNOSTICS n = ROW_COUNT; total_new := total_new + n;
        INSERT INTO _opc_use (kind, id, company_id)
        SELECT 'department', x.department_id, u.company_id FROM _opc_use u JOIN designations x ON u.kind = 'designation' AND x.id = u.id
          JOIN _opc_all a ON a.kind = 'department' AND a.id = x.department_id AND a.tenant_id = x.tenant_id
        ON CONFLICT DO NOTHING;
        GET DIAGNOSTICS n = ROW_COUNT; total_new := total_new + n;
        INSERT INTO _opc_use (kind, id, company_id)
        SELECT 'department', d.parent_department_id, u.company_id FROM _opc_use u JOIN departments d ON u.kind = 'department' AND d.id = u.id
          JOIN _opc_all a ON a.kind = 'department' AND a.id = d.parent_department_id AND a.tenant_id = d.tenant_id
        ON CONFLICT DO NOTHING;
        GET DIAGNOSTICS n = ROW_COUNT; total_new := total_new + n;
        EXIT WHEN total_new = 0;
    END LOOP;

    -- Decisions for every row still without a company: one line per (row, target company).
    CREATE TEMP TABLE IF NOT EXISTS _opc_target (kind text, tenant_id uuid, id uuid, code text, company_id uuid, target_id uuid, action text, employees int) ON COMMIT DROP;
    TRUNCATE _opc_target;
    INSERT INTO _opc_target (kind, tenant_id, id, code, company_id, target_id, action, employees)
    WITH users AS (
        SELECT a.kind, a.tenant_id, a.id, a.code, u.company_id, u.employees, c.created_at_utc,
               (SELECT o.id FROM _opc_all o WHERE o.kind = a.kind AND o.tenant_id = a.tenant_id AND o.company_id = u.company_id
                                              AND o.code = a.code AND o.id <> a.id ORDER BY o.id LIMIT 1) AS existing_id
          FROM _opc_all a
          JOIN _opc_use u ON u.kind = a.kind AND u.id = a.id
          JOIN _opc_company c ON c.tenant_id = a.tenant_id AND c.company_id = u.company_id
         WHERE a.company_id IS NULL),
    ranked AS (
        SELECT users.*, CASE WHEN existing_id IS NULL
                             THEN row_number() OVER (PARTITION BY kind, id, (existing_id IS NULL)
                                                     ORDER BY employees DESC, created_at_utc, company_id) END AS rn
          FROM users)
    SELECT kind, tenant_id, id, code, company_id,
           CASE WHEN existing_id IS NOT NULL THEN existing_id
                WHEN rn = 1 THEN id
                ELSE md5('opc:' || kind || ':' || id::text || ':' || company_id::text)::uuid END,
           CASE WHEN existing_id IS NOT NULL THEN 'mapped_existing'
                WHEN rn = 1 AND count(*) OVER (PARTITION BY kind, id) = 1 THEN 'assigned_single_user'
                WHEN rn = 1 THEN 'assigned_keeper'
                ELSE 'copied' END,
           employees
      FROM ranked;
    -- Unused rows: the tenant's only company, when it has exactly one and no row of that code.
    INSERT INTO _opc_target (kind, tenant_id, id, code, company_id, target_id, action, employees)
    SELECT a.kind, a.tenant_id, a.id, a.code, oc.company_id, a.id, 'assigned_only_company', 0
      FROM _opc_all a
      JOIN (SELECT tenant_id, min(company_id::text)::uuid AS company_id FROM _opc_company GROUP BY tenant_id HAVING count(*) = 1) oc
        ON oc.tenant_id = a.tenant_id
     WHERE a.company_id IS NULL
       AND NOT EXISTS (SELECT 1 FROM _opc_use u WHERE u.kind = a.kind AND u.id = a.id)
       AND NOT EXISTS (SELECT 1 FROM _opc_all o WHERE o.kind = a.kind AND o.tenant_id = a.tenant_id
                                               AND o.company_id = oc.company_id AND o.code = a.code AND o.id <> a.id);

    CREATE TEMP TABLE IF NOT EXISTS _opc_map ON COMMIT DROP AS
        SELECT kind, tenant_id, id AS original_id, company_id, target_id AS new_id, action
          FROM _opc_target WHERE target_id <> id WITH NO DATA;
    TRUNCATE _opc_map;
    INSERT INTO _opc_map
        SELECT kind, tenant_id, id AS original_id, company_id, target_id AS new_id, action
          FROM _opc_target WHERE target_id <> id;
    CREATE UNIQUE INDEX IF NOT EXISTS _opc_map_key ON _opc_map (kind, original_id, company_id);

    -- 1. Assign the kept rows. A branch or cost centre of another company does not follow the department.
    UPDATE departments d SET company_id = t.company_id,
           branch_id = CASE WHEN EXISTS (SELECT 1 FROM branches b WHERE b.tenant_id = d.tenant_id AND b.id = d.branch_id AND b.company_id = t.company_id) THEN d.branch_id END,
           cost_center_id = CASE WHEN EXISTS (SELECT 1 FROM cost_centers cc WHERE cc.tenant_id = d.tenant_id AND cc.id = d.cost_center_id AND cc.company_id = t.company_id) THEN d.cost_center_id END
      FROM _opc_target t
     WHERE t.kind = 'department' AND t.id = d.id AND t.target_id = d.id AND d.company_id IS NULL;
    UPDATE designations x SET company_id = t.company_id
      FROM _opc_target t WHERE t.kind = 'designation' AND t.id = x.id AND t.target_id = x.id AND x.company_id IS NULL;
    UPDATE grades g SET company_id = t.company_id
      FROM _opc_target t WHERE t.kind = 'grade' AND t.id = g.id AND t.target_id = g.id AND g.company_id IS NULL;

    -- 2. Copies (same values; new deterministic id; the company's own branch/cost centre only; links re-pointed in step 3).
    SELECT string_agg(quote_ident(column_name), ',' ORDER BY ordinal_position) INTO cols
      FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'departments' AND is_generated = 'NEVER';
    CREATE TEMP TABLE IF NOT EXISTS _opc_copy_dept ON COMMIT DROP AS
        SELECT d.*, m.company_id AS opc_company, m.new_id AS opc_new_id
          FROM departments d JOIN _opc_map m ON m.kind = 'department' AND m.original_id = d.id AND m.action = 'copied'
         WHERE NOT EXISTS (SELECT 1 FROM departments e WHERE e.id = m.new_id) WITH NO DATA;
    TRUNCATE _opc_copy_dept;
    INSERT INTO _opc_copy_dept
        SELECT d.*, m.company_id AS opc_company, m.new_id AS opc_new_id
          FROM departments d JOIN _opc_map m ON m.kind = 'department' AND m.original_id = d.id AND m.action = 'copied'
         WHERE NOT EXISTS (SELECT 1 FROM departments e WHERE e.id = m.new_id);
    UPDATE _opc_copy_dept SET id = opc_new_id, company_id = opc_company,
           branch_id = CASE WHEN EXISTS (SELECT 1 FROM branches b WHERE b.tenant_id = _opc_copy_dept.tenant_id AND b.id = _opc_copy_dept.branch_id AND b.company_id = opc_company) THEN branch_id END,
           cost_center_id = CASE WHEN EXISTS (SELECT 1 FROM cost_centers cc WHERE cc.tenant_id = _opc_copy_dept.tenant_id AND cc.id = _opc_copy_dept.cost_center_id AND cc.company_id = opc_company) THEN cost_center_id END;
    EXECUTE format('INSERT INTO departments (%s) SELECT %s FROM _opc_copy_dept', cols, cols);

    SELECT string_agg(quote_ident(column_name), ',' ORDER BY ordinal_position) INTO cols
      FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'designations' AND is_generated = 'NEVER';
    CREATE TEMP TABLE IF NOT EXISTS _opc_copy_desig ON COMMIT DROP AS
        SELECT x.*, m.company_id AS opc_company, m.new_id AS opc_new_id
          FROM designations x JOIN _opc_map m ON m.kind = 'designation' AND m.original_id = x.id AND m.action = 'copied'
         WHERE NOT EXISTS (SELECT 1 FROM designations e WHERE e.id = m.new_id) WITH NO DATA;
    TRUNCATE _opc_copy_desig;
    INSERT INTO _opc_copy_desig
        SELECT x.*, m.company_id AS opc_company, m.new_id AS opc_new_id
          FROM designations x JOIN _opc_map m ON m.kind = 'designation' AND m.original_id = x.id AND m.action = 'copied'
         WHERE NOT EXISTS (SELECT 1 FROM designations e WHERE e.id = m.new_id);
    UPDATE _opc_copy_desig SET id = opc_new_id, company_id = opc_company;
    EXECUTE format('INSERT INTO designations (%s) SELECT %s FROM _opc_copy_desig', cols, cols);

    SELECT string_agg(quote_ident(column_name), ',' ORDER BY ordinal_position) INTO cols
      FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'grades' AND is_generated = 'NEVER';
    CREATE TEMP TABLE IF NOT EXISTS _opc_copy_grade ON COMMIT DROP AS
        SELECT g.*, m.company_id AS opc_company, m.new_id AS opc_new_id
          FROM grades g JOIN _opc_map m ON m.kind = 'grade' AND m.original_id = g.id AND m.action = 'copied'
         WHERE NOT EXISTS (SELECT 1 FROM grades e WHERE e.id = m.new_id) WITH NO DATA;
    TRUNCATE _opc_copy_grade;
    INSERT INTO _opc_copy_grade
        SELECT g.*, m.company_id AS opc_company, m.new_id AS opc_new_id
          FROM grades g JOIN _opc_map m ON m.kind = 'grade' AND m.original_id = g.id AND m.action = 'copied'
         WHERE NOT EXISTS (SELECT 1 FROM grades e WHERE e.id = m.new_id);
    UPDATE _opc_copy_grade SET id = opc_new_id, company_id = opc_company,
           currency = COALESCE((SELECT c.default_currency FROM _opc_company c WHERE c.tenant_id = _opc_copy_grade.tenant_id AND c.company_id = opc_company), currency);
    EXECUTE format('INSERT INTO grades (%s) SELECT %s FROM _opc_copy_grade', cols, cols);

    -- Child config of a copied grade / department.
    SELECT string_agg(quote_ident(column_name), ',' ORDER BY ordinal_position) INTO cols
      FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'grade_pay_scale_components' AND is_generated = 'NEVER';
    CREATE TEMP TABLE IF NOT EXISTS _opc_copy_gpsc ON COMMIT DROP AS
        SELECT p.*, m.new_id AS opc_parent, md5('opc:gpsc:' || p.id::text || ':' || m.company_id::text)::uuid AS opc_new_id
          FROM grade_pay_scale_components p JOIN _opc_map m ON m.kind = 'grade' AND m.original_id = p.grade_id AND m.tenant_id = p.tenant_id AND m.action = 'copied' WITH NO DATA;
    TRUNCATE _opc_copy_gpsc;
    INSERT INTO _opc_copy_gpsc
        SELECT p.*, m.new_id AS opc_parent, md5('opc:gpsc:' || p.id::text || ':' || m.company_id::text)::uuid AS opc_new_id
          FROM grade_pay_scale_components p JOIN _opc_map m ON m.kind = 'grade' AND m.original_id = p.grade_id AND m.tenant_id = p.tenant_id AND m.action = 'copied';
    DELETE FROM _opc_copy_gpsc WHERE EXISTS (SELECT 1 FROM grade_pay_scale_components e WHERE e.id = _opc_copy_gpsc.opc_new_id);
    UPDATE _opc_copy_gpsc SET id = opc_new_id, grade_id = opc_parent;
    EXECUTE format('INSERT INTO grade_pay_scale_components (%s) SELECT %s FROM _opc_copy_gpsc', cols, cols);

    -- Tenant-wide entitlement cells (company_id NULL) follow the grade: the copy carries the same grid. Company cells
    -- are re-pointed in step 3 instead (they already belong to one company).
    SELECT string_agg(quote_ident(column_name), ',' ORDER BY ordinal_position) INTO cols
      FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'grade_entitlements' AND is_generated = 'NEVER';
    CREATE TEMP TABLE IF NOT EXISTS _opc_copy_ge ON COMMIT DROP AS
        SELECT ge.*, m.new_id AS opc_parent, md5('opc:ge:' || ge.id::text || ':' || m.company_id::text)::uuid AS opc_new_id
          FROM grade_entitlements ge JOIN _opc_map m ON m.kind = 'grade' AND m.original_id = ge.grade_id AND m.tenant_id = ge.tenant_id AND m.action = 'copied'
         WHERE ge.company_id IS NULL WITH NO DATA;
    TRUNCATE _opc_copy_ge;
    INSERT INTO _opc_copy_ge
        SELECT ge.*, m.new_id AS opc_parent, md5('opc:ge:' || ge.id::text || ':' || m.company_id::text)::uuid AS opc_new_id
          FROM grade_entitlements ge JOIN _opc_map m ON m.kind = 'grade' AND m.original_id = ge.grade_id AND m.tenant_id = ge.tenant_id AND m.action = 'copied'
         WHERE ge.company_id IS NULL;
    DELETE FROM _opc_copy_ge WHERE EXISTS (SELECT 1 FROM grade_entitlements e WHERE e.id = _opc_copy_ge.opc_new_id);
    UPDATE _opc_copy_ge SET id = opc_new_id, grade_id = opc_parent;
    EXECUTE format('INSERT INTO grade_entitlements (%s) SELECT %s FROM _opc_copy_ge', cols, cols);

    SELECT string_agg(quote_ident(column_name), ',' ORDER BY ordinal_position) INTO cols
      FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'department_staffing_budgets' AND is_generated = 'NEVER';
    CREATE TEMP TABLE IF NOT EXISTS _opc_copy_dsb ON COMMIT DROP AS
        SELECT b.*, m.new_id AS opc_parent, md5('opc:dsb:' || b.id::text || ':' || m.company_id::text)::uuid AS opc_new_id
          FROM department_staffing_budgets b JOIN _opc_map m ON m.kind = 'department' AND m.original_id = b.department_id AND m.tenant_id = b.tenant_id AND m.action = 'copied' WITH NO DATA;
    TRUNCATE _opc_copy_dsb;
    INSERT INTO _opc_copy_dsb
        SELECT b.*, m.new_id AS opc_parent, md5('opc:dsb:' || b.id::text || ':' || m.company_id::text)::uuid AS opc_new_id
          FROM department_staffing_budgets b JOIN _opc_map m ON m.kind = 'department' AND m.original_id = b.department_id AND m.tenant_id = b.tenant_id AND m.action = 'copied';
    DELETE FROM _opc_copy_dsb WHERE EXISTS (SELECT 1 FROM department_staffing_budgets e WHERE e.id = _opc_copy_dsb.opc_new_id);
    UPDATE _opc_copy_dsb SET id = opc_new_id, department_id = opc_parent;
    EXECUTE format('INSERT INTO department_staffing_budgets (%s) SELECT %s FROM _opc_copy_dsb', cols, cols);

    -- 3. Re-point every reference whose own company has a copy (or an existing same-code row) of the row.
    FOR r IN SELECT * FROM (VALUES
            ('employees', 'department_id', 'department'), ('employees', 'designation_id', 'designation'), ('employees', 'grade_id', 'grade'),
            ('departments', 'parent_department_id', 'department'),
            ('designations', 'department_id', 'department'), ('designations', 'grade_id', 'grade'),
            ('positions', 'department_id', 'department'), ('positions', 'designation_id', 'designation'), ('positions', 'grade_id', 'grade'),
            ('grade_entitlements', 'grade_id', 'grade'),
            ('employee_loans', 'grade_id_at_request', 'grade'),
            ('pay_benchmark_rows', 'designation_id', 'designation'), ('pay_benchmark_rows', 'grade_id', 'grade'),
            ('comp_cycle_budgets', 'department_id', 'department'),
            ('comp_worksheet_lines', 'department_id', 'department'), ('comp_worksheet_lines', 'designation_id', 'designation'), ('comp_worksheet_lines', 'grade_id', 'grade'),
            ('learning_assignments', 'department_id', 'department'), ('learning_assignments', 'designation_id', 'designation'), ('learning_assignments', 'grade_id', 'grade'),
            ('leave_policy_eligibilities', 'department_id', 'department'), ('leave_policy_eligibilities', 'grade_id', 'grade'),
            ('benefit_eligibility_rules', 'grade_id', 'grade')) v(tbl, col, kind)
    LOOP
        EXECUTE format('UPDATE %1$I t SET %2$I = m.new_id FROM _opc_map m
                         WHERE m.kind = %3$L AND m.tenant_id = t.tenant_id AND m.original_id = t.%2$I AND m.company_id = t.company_id',
                       r.tbl, r.col, r.kind);
        GET DIAGNOSTICS n = ROW_COUNT; n_refs := n_refs + n;
    END LOOP;

    -- Transfer requests carry no company: the employee's company for the current department; the new branch's company
    -- (else the employee's) for the new department and designation.
    UPDATE employee_transfer_requests t SET current_department_id = m.new_id
      FROM employees e, _opc_map m
     WHERE e.tenant_id = t.tenant_id AND e.id = t.employee_id
       AND m.kind = 'department' AND m.tenant_id = t.tenant_id AND m.original_id = t.current_department_id AND m.company_id = e.company_id;
    GET DIAGNOSTICS n = ROW_COUNT; n_refs := n_refs + n;
    UPDATE employee_transfer_requests t SET new_department_id = m.new_id
      FROM employees e, _opc_map m
     WHERE e.tenant_id = t.tenant_id AND e.id = t.employee_id
       AND m.kind = 'department' AND m.tenant_id = t.tenant_id AND m.original_id = t.new_department_id
       AND m.company_id = COALESCE((SELECT b.company_id FROM branches b WHERE b.tenant_id = t.tenant_id AND b.id = t.new_branch_id), e.company_id);
    GET DIAGNOSTICS n = ROW_COUNT; n_refs := n_refs + n;
    UPDATE employee_transfer_requests t SET new_designation_id = m.new_id
      FROM employees e, _opc_map m
     WHERE e.tenant_id = t.tenant_id AND e.id = t.employee_id
       AND m.kind = 'designation' AND m.tenant_id = t.tenant_id AND m.original_id = t.new_designation_id
       AND m.company_id = COALESCE((SELECT b.company_id FROM branches b WHERE b.tenant_id = t.tenant_id AND b.id = t.new_branch_id), e.company_id);
    GET DIAGNOSTICS n = ROW_COUNT; n_refs := n_refs + n;

    -- JSON id lists. A company row swaps each id for its company's version; a tenant-wide row (company NULL) keeps every
    -- id and gains each copy, so it still covers every company it covered before.
    CREATE OR REPLACE FUNCTION pg_temp.opc_remap(arr jsonb, p_tenant uuid, p_company uuid, p_kind text) RETURNS jsonb
    LANGUAGE sql STABLE AS $f$
        SELECT CASE WHEN arr IS NULL OR jsonb_typeof(arr) <> 'array' THEN arr
               WHEN p_company IS NOT NULL THEN COALESCE((
                    SELECT jsonb_agg(COALESCE(to_jsonb(m.new_id::text), e.v) ORDER BY e.o)
                      FROM jsonb_array_elements(arr) WITH ORDINALITY e(v, o)
                      LEFT JOIN _opc_map m ON m.kind = p_kind AND m.tenant_id = p_tenant AND m.company_id = p_company
                                          AND jsonb_typeof(e.v) = 'string' AND m.original_id::text = lower(e.v #>> '{}')), '[]'::jsonb)
               ELSE arr || COALESCE((
                    SELECT jsonb_agg(to_jsonb(m.new_id::text) ORDER BY m.company_id, m.new_id)
                      FROM _opc_map m
                     WHERE m.kind = p_kind AND m.tenant_id = p_tenant
                       AND EXISTS (SELECT 1 FROM jsonb_array_elements(arr) e(v)
                                    WHERE jsonb_typeof(e.v) = 'string' AND lower(e.v #>> '{}') = m.original_id::text)
                       AND NOT EXISTS (SELECT 1 FROM jsonb_array_elements(arr) e(v)
                                        WHERE jsonb_typeof(e.v) = 'string' AND lower(e.v #>> '{}') = m.new_id::text)), '[]'::jsonb)
               END
    $f$;

    UPDATE salary_structures s
       SET eligible_grade_ids_json = pg_temp.opc_remap(s.eligible_grade_ids_json::jsonb, s.tenant_id, s.company_id, 'grade')::json
     WHERE pg_temp.opc_remap(s.eligible_grade_ids_json::jsonb, s.tenant_id, s.company_id, 'grade') IS DISTINCT FROM s.eligible_grade_ids_json::jsonb;
    GET DIAGNOSTICS n = ROW_COUNT; n_refs := n_refs + n;
    UPDATE salary_structures s
       SET eligible_designation_ids_json = pg_temp.opc_remap(s.eligible_designation_ids_json::jsonb, s.tenant_id, s.company_id, 'designation')::json
     WHERE pg_temp.opc_remap(s.eligible_designation_ids_json::jsonb, s.tenant_id, s.company_id, 'designation') IS DISTINCT FROM s.eligible_designation_ids_json::jsonb;
    GET DIAGNOSTICS n = ROW_COUNT; n_refs := n_refs + n;

    UPDATE comp_cycles c
       SET eligibility_filters_json = c.eligibility_filters_json
           || jsonb_strip_nulls(jsonb_build_object(
                'includeGradeIds', pg_temp.opc_remap(c.eligibility_filters_json -> 'includeGradeIds', c.tenant_id, c.company_id, 'grade'),
                'excludeGradeIds', pg_temp.opc_remap(c.eligibility_filters_json -> 'excludeGradeIds', c.tenant_id, c.company_id, 'grade'),
                'includeDepartmentIds', pg_temp.opc_remap(c.eligibility_filters_json -> 'includeDepartmentIds', c.tenant_id, c.company_id, 'department'),
                'excludeDepartmentIds', pg_temp.opc_remap(c.eligibility_filters_json -> 'excludeDepartmentIds', c.tenant_id, c.company_id, 'department')))
     WHERE jsonb_typeof(c.eligibility_filters_json) = 'object'
       AND EXISTS (SELECT 1 FROM _opc_map m WHERE m.tenant_id = c.tenant_id AND m.company_id = c.company_id
                                            AND position(m.original_id::text IN lower(c.eligibility_filters_json::text)) > 0);
    GET DIAGNOSTICS n = ROW_COUNT; n_refs := n_refs + n;
    DROP FUNCTION pg_temp.opc_remap(jsonb, uuid, uuid, text);

    -- 4. Audit: one row per assigned, copied or mapped row, chained per tenant exactly like AuditService.ComputeHash.
    FOR aud IN
        SELECT tg.*, CASE tg.kind WHEN 'department' THEN 'Department' WHEN 'designation' THEN 'Designation' ELSE 'Grade' END AS entity_name
          FROM _opc_target tg
         ORDER BY tg.tenant_id, tg.kind, tg.id, tg.company_id
    LOOP
        IF current_tenant IS DISTINCT FROM aud.tenant_id THEN
            current_tenant := aud.tenant_id;
            SELECT a.entry_hash, a.created_at_utc INTO prev_hash, ts
              FROM audit_logs a WHERE a.tenant_id = aud.tenant_id
             ORDER BY a.created_at_utc DESC, a.id DESC LIMIT 1;
            prev_hash := COALESCE(prev_hash, '');
            ts := date_trunc('milliseconds', GREATEST(clock_timestamp(), COALESCE(ts, clock_timestamp())));
        END IF;
        ts := ts + interval '1 millisecond';
        meta := json_build_object(
            'rule', aud.action, 'companyId', aud.company_id, 'sourceId', aud.id, 'rowId', aud.target_id, 'code', aud.code,
            'employeesOnRow', aud.employees, 'migration', 'BackfillOrganisationPerCompany')::text;
        canonical := aud.tenant_id::text || chr(10) || '' || chr(10)
                  || 'organization.company_backfill_' || aud.action || chr(10) || aud.entity_name || chr(10)
                  || aud.target_id::text || chr(10) || '' || chr(10)
                  || 'system: migration BackfillOrganisationPerCompany' || chr(10) || meta || chr(10)
                  || prev_hash || chr(10)
                  || to_char(ts AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US') || '0Z';
        h := encode(sha256(convert_to(canonical, 'UTF8')), 'hex');
        INSERT INTO audit_logs (id, tenant_id, company_id, user_id, action, entity_name, entity_id, ip_address, user_agent,
                                metadata, previous_hash, entry_hash, hash_algorithm, created_at_utc)
        VALUES (md5('opc:audit:' || aud.kind || ':' || aud.id::text || ':' || aud.company_id::text)::uuid, aud.tenant_id, aud.company_id, NULL,
                'organization.company_backfill_' || aud.action, aud.entity_name, aud.target_id::text, NULL,
                'system: migration BackfillOrganisationPerCompany', meta::json, prev_hash, h, 'SHA-256', ts)
        ON CONFLICT (id) DO NOTHING;
        prev_hash := h;
    END LOOP;

    SELECT count(*) FILTER (WHERE action IN ('assigned_single_user', 'assigned_keeper')),
           count(*) FILTER (WHERE action = 'assigned_only_company'),
           count(*) FILTER (WHERE action = 'copied'),
           count(*) FILTER (WHERE action = 'mapped_existing')
      INTO n_assigned, n_only, n_copied, n_mapped FROM _opc_target;
    SELECT count(*) INTO n_unassigned FROM _opc_all a
     WHERE a.company_id IS NULL AND NOT EXISTS (SELECT 1 FROM _opc_target t WHERE t.kind = a.kind AND t.id = a.id);
    RAISE NOTICE 'BackfillOrganisationPerCompany: assigned % (used by one/keeper), % (only company), copied %, mapped to existing %, left unassigned %, references re-pointed %',
        n_assigned, n_only, n_copied, n_mapped, n_unassigned, n_refs;

END
$opc$;
""";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(BackfillSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately empty: see the class summary (restore the pre-deploy database branch to roll back).
        }
    }
}
