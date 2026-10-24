using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// Leave per company, step 2: give every leave type and every tenant-wide leave policy an owning company (owner
    /// decision 2026-10-23, "separate everything"; same rules as Addendum B1 for departments, designations and grades).
    ///
    /// <para><b>Leave types.</b> Only rows whose <c>company_id</c> is still NULL are considered. A type is "used by" a
    /// company when that company's employees have a balance, ledger row, request, encashment or absence regularisation on
    /// it (the employee's CURRENT company decides, so all of one employee's rows always move together), when a policy of
    /// that company points at it, or when an <i>Active</i> tenant-wide policy on it reaches that company's employees.</para>
    /// <list type="number">
    /// <item>Used by exactly one company: assigned to it.</item>
    /// <item>Used by several: the row keeps its id for the company with the most employees on it (ties: the oldest
    /// company, then the lowest id). Every other user company gets a copy with the same code and values, and that
    /// company's employees' rows are re-pointed to the copy in every table listed in <see cref="RepointedColumns"/>.</item>
    /// <item>Used by nobody: the tenant's only company when it has exactly one; otherwise it stays NULL and Setup lists
    /// it as "Unassigned — choose the company" (a fallback match for any company without a type of that code).</item>
    /// </list>
    /// <para>When the company already owns a type with the same code (only possible on a re-run after new rows were
    /// created) that row is used instead of a copy — unless one of the company's employees holds a balance or an accrual
    /// on both for the same year, in which case the legacy type is left untouched (counted in the NOTICE) rather than
    /// merge two balances.</para>
    ///
    /// <para><b>Tenant-wide policies</b> (<c>leave_policies.company_id</c> NULL, which used to mean "every company"). A
    /// policy reaches the company of its branch when it names one; else the companies its active eligibility rows name
    /// when every active row names one; else every company of the tenant — minus companies in another country than a
    /// policy that names a country (ISO-2/ISO-3 aware; an unknown code never excludes). It goes to each reached company
    /// that has a version of its leave type, plus the company of every employee with a request on it. The company that
    /// holds the type's original id keeps the policy's id (else the one with most employees, oldest, lowest id); every
    /// other company gets a copy pointing at its own version of the type, with the policy's accrual rules and its
    /// company-less eligibility rows copied, and its eligibility rows naming that company moved to the copy. A policy
    /// that reaches nobody goes to the only company of a single-company tenant, else stays NULL. Every assigned or
    /// copied policy is marked <c>legacy_group_default</c>, which keeps its precedence below the company-specific
    /// policies it used to lose to (<c>LeaveService.PolicySpecificity</c>).</para>
    ///
    /// <para><b>After CopyWorkspaceRulesPerCompany</b> (which must run first). That migration copied each company-less
    /// eligibility row for every company with its own department/grade copy, as a row naming that company but still on the
    /// ORIGINAL policy; like every row naming a company, it moves to that company's copy of the policy, and the
    /// company-less original is then not copied for that company again. A policy pinned to an approval workflow the rule
    /// copy duplicated for its company names that company's copy (the rule copy's chained audit rows are the map).</para>
    ///
    /// <para><b>Eligibility rows follow the policy's company.</b> A department or grade on an eligibility row of a policy
    /// owned by company C that belongs to another company is re-pointed to C's row of the same code (the organisation
    /// backfill copied it for C and moved C's employees onto it, so the old link no longer matched them).</para>
    ///
    /// <para><b>Invariant.</b> Nothing is created or deleted on the money side: balance, ledger, request and encashment
    /// rows are re-pointed one to one, so every employee's entitled / accrued / used / pending / carried / encashed /
    /// expired / adjusted figures per leave code are identical before and after, and every request's <c>policy_id</c>
    /// still names a policy of the employee's company on the request's own leave type.</para>
    ///
    /// <para><b>Deterministic and idempotent.</b> Copy ids are <c>md5('olp:…')::uuid</c> of the source id and the
    /// company, the order of everything is fixed, and a re-run finds no NULL row in use, so it changes nothing.</para>
    ///
    /// <para><b>Audit.</b> One <c>audit_logs</c> row per assigned, copied or mapped type and policy, chained into the
    /// tenant's SHA-256 hash chain exactly as <c>AuditService.ComputeHash</c> does, so <c>VerifyChain</c> still passes.</para>
    ///
    /// <para><b>Down</b> is a no-op: restore the pre-deploy database branch to roll back.</para>
    /// </summary>
    public partial class BackfillLeavePerCompany : Migration
    {
        /// <summary>Every column the backfill re-points or copies, as table.column (how). Kept in one place for the tests and the report.</summary>
        public static readonly string[] RepointedColumns =
        [
            "employee_leave_balances.leave_type_id (employee's company)",
            "leave_balance_transactions.leave_type_id (employee's company; accrual, debit, reversal, carry-forward, encashment, adjustment)",
            "leave_requests.leave_type_id (employee's company; every status incl. pending and approved)",
            "leave_requests.policy_id (employee's company)",
            "leave_encashment_requests.leave_type_id (employee's company)",
            "absence_regularization_requests.leave_type_id (employee's company)",
            "leave_policies.leave_type_id (the policy's company)",
            "leave_policy_eligibilities.leave_policy_id (rows naming a company move to that company's copy)",
            "leave_policy_eligibilities.department_id / grade_id (aligned to the policy's company by code)",
            "leave_policy_eligibilities (company-less rows copied with the policy)",
            "leave_accrual_rules (copied with the policy)",
            "leave_policies.approval_workflow_id (the policy's company's workflow copy made by CopyWorkspaceRulesPerCompany)",
            "leave_policy_eligibilities rows CopyWorkspaceRulesPerCompany copied for a company (moved to that company's policy copy)",
        ];

        internal const string Actor = "system: migration BackfillLeavePerCompany";

        /// <summary>The whole backfill. Executed by <see cref="Up"/> and, verbatim, by the Postgres tests.</summary>
        public const string BackfillSql = """
DO $olp$
DECLARE
    n int;
    ref_tbl record;
    aud record;
    prev_hash text;
    ts timestamptz;
    meta text;
    canonical text;
    h text;
    cols text;
    current_tenant uuid := NULL;
    n_assigned int; n_only int; n_copied int; n_mapped int; n_unassigned int; n_conflicts int;
    p_assigned int; p_copied int; p_unassigned int; n_aligned bigint := 0; n_refs bigint := 0;
BEGIN
    -- Scratch tables are ON COMMIT DROP (never dropped explicitly) and truncated first, so a second run in the same
    -- transaction starts clean.
    CREATE OR REPLACE FUNCTION pg_temp.olp_iso2(v text) RETURNS text LANGUAGE sql IMMUTABLE AS $f$
        SELECT CASE upper(btrim(coalesce(v, '')))
            WHEN 'SA' THEN 'SA' WHEN 'SAU' THEN 'SA' WHEN 'KSA' THEN 'SA'
            WHEN 'AE' THEN 'AE' WHEN 'ARE' THEN 'AE' WHEN 'UAE' THEN 'AE'
            WHEN 'KW' THEN 'KW' WHEN 'KWT' THEN 'KW'
            WHEN 'BH' THEN 'BH' WHEN 'BHR' THEN 'BH'
            WHEN 'OM' THEN 'OM' WHEN 'OMN' THEN 'OM'
            WHEN 'QA' THEN 'QA' WHEN 'QAT' THEN 'QA'
            WHEN 'EG' THEN 'EG' WHEN 'EGY' THEN 'EG'
            WHEN 'JO' THEN 'JO' WHEN 'JOR' THEN 'JO'
            WHEN 'LB' THEN 'LB' WHEN 'LBN' THEN 'LB'
            WHEN 'IN' THEN 'IN' WHEN 'IND' THEN 'IN'
            WHEN 'PK' THEN 'PK' WHEN 'PAK' THEN 'PK'
            WHEN 'PH' THEN 'PH' WHEN 'PHL' THEN 'PH'
            WHEN 'GB' THEN 'GB' WHEN 'GBR' THEN 'GB'
            WHEN 'US' THEN 'US' WHEN 'USA' THEN 'US'
            ELSE NULL END
    $f$;

    CREATE TEMP TABLE IF NOT EXISTS _olp_company ON COMMIT DROP AS
SELECT c.tenant_id, c.id AS company_id, c.created_at_utc, pg_temp.olp_iso2(c.country_code) AS iso2
          FROM companies c WHERE NOT c.is_deleted WITH NO DATA;
    TRUNCATE _olp_company;
    INSERT INTO _olp_company
SELECT c.tenant_id, c.id AS company_id, c.created_at_utc, pg_temp.olp_iso2(c.country_code) AS iso2
          FROM companies c WHERE NOT c.is_deleted;
    CREATE UNIQUE INDEX ON _olp_company (tenant_id, company_id);

    -- Employees with a live company. An employee without one keeps every row on the original type.
    CREATE TEMP TABLE IF NOT EXISTS _olp_emp ON COMMIT DROP AS
SELECT e.tenant_id, e.id AS employee_id, e.company_id FROM employees e
          JOIN _olp_company c ON c.tenant_id = e.tenant_id AND c.company_id = e.company_id WITH NO DATA;
    TRUNCATE _olp_emp;
    INSERT INTO _olp_emp
SELECT e.tenant_id, e.id AS employee_id, e.company_id FROM employees e
          JOIN _olp_company c ON c.tenant_id = e.tenant_id AND c.company_id = e.company_id;
    CREATE UNIQUE INDEX ON _olp_emp (tenant_id, employee_id);

    CREATE TEMP TABLE IF NOT EXISTS _olp_type ON COMMIT DROP AS
SELECT t.tenant_id, t.id, t.company_id, t.code FROM leave_types t WITH NO DATA;
    TRUNCATE _olp_type;
    INSERT INTO _olp_type
SELECT t.tenant_id, t.id, t.company_id, t.code FROM leave_types t;
    CREATE UNIQUE INDEX ON _olp_type (id);

    -- Tenant-wide policies and whom they reach.
    CREATE TEMP TABLE IF NOT EXISTS _olp_pol ON COMMIT DROP AS
SELECT p.id, p.tenant_id, p.leave_type_id, p.status, pg_temp.olp_iso2(p.country_code) AS iso2,
               (SELECT b.company_id FROM branches b WHERE b.tenant_id = p.tenant_id AND b.id = p.branch_id) AS branch_company,
               EXISTS (SELECT 1 FROM leave_policy_eligibilities el
                        WHERE el.tenant_id = p.tenant_id AND el.leave_policy_id = p.id AND el.is_active) AS has_rows,
               EXISTS (SELECT 1 FROM leave_policy_eligibilities el
                        WHERE el.tenant_id = p.tenant_id AND el.leave_policy_id = p.id AND el.is_active AND el.company_id IS NULL) AS has_open_row
          FROM leave_policies p WHERE p.company_id IS NULL WITH NO DATA;
    TRUNCATE _olp_pol;
    INSERT INTO _olp_pol
SELECT p.id, p.tenant_id, p.leave_type_id, p.status, pg_temp.olp_iso2(p.country_code) AS iso2,
               (SELECT b.company_id FROM branches b WHERE b.tenant_id = p.tenant_id AND b.id = p.branch_id) AS branch_company,
               EXISTS (SELECT 1 FROM leave_policy_eligibilities el
                        WHERE el.tenant_id = p.tenant_id AND el.leave_policy_id = p.id AND el.is_active) AS has_rows,
               EXISTS (SELECT 1 FROM leave_policy_eligibilities el
                        WHERE el.tenant_id = p.tenant_id AND el.leave_policy_id = p.id AND el.is_active AND el.company_id IS NULL) AS has_open_row
          FROM leave_policies p WHERE p.company_id IS NULL;
    CREATE UNIQUE INDEX ON _olp_pol (id);

    CREATE TEMP TABLE IF NOT EXISTS _olp_reach (policy_id uuid NOT NULL, company_id uuid NOT NULL, PRIMARY KEY (policy_id, company_id)) ON COMMIT DROP;
    TRUNCATE _olp_reach;
    INSERT INTO _olp_reach (policy_id, company_id)
    SELECT p.id, c.company_id
      FROM _olp_pol p JOIN _olp_company c ON c.tenant_id = p.tenant_id
     WHERE (p.iso2 IS NULL OR c.iso2 IS NULL OR p.iso2 = c.iso2)
       AND CASE WHEN p.branch_company IS NOT NULL THEN c.company_id = p.branch_company
                WHEN p.has_rows AND NOT p.has_open_row THEN EXISTS (
                     SELECT 1 FROM leave_policy_eligibilities el
                      WHERE el.tenant_id = p.tenant_id AND el.leave_policy_id = p.id AND el.is_active AND el.company_id = c.company_id)
                ELSE true END;

    -- Usage of a leave type: (type, company) with the number of that company's employees holding rows on it.
    CREATE TEMP TABLE IF NOT EXISTS _olp_use (id uuid NOT NULL, company_id uuid NOT NULL, employees int NOT NULL DEFAULT 0,
                                PRIMARY KEY (id, company_id)) ON COMMIT DROP;
    TRUNCATE _olp_use;
    INSERT INTO _olp_use (id, company_id, employees)
    SELECT u.leave_type_id, e.company_id, count(DISTINCT u.employee_id)
      FROM (SELECT tenant_id, leave_type_id, employee_id FROM employee_leave_balances
            UNION SELECT tenant_id, leave_type_id, employee_id FROM leave_balance_transactions
            UNION SELECT tenant_id, leave_type_id, employee_id FROM leave_requests
            UNION SELECT tenant_id, leave_type_id, employee_id FROM leave_encashment_requests
            UNION SELECT tenant_id, leave_type_id, employee_id FROM absence_regularization_requests WHERE leave_type_id IS NOT NULL) u
      JOIN _olp_emp e ON e.tenant_id = u.tenant_id AND e.employee_id = u.employee_id
      JOIN _olp_type t ON t.tenant_id = u.tenant_id AND t.id = u.leave_type_id
     GROUP BY u.leave_type_id, e.company_id;
    -- A type that already has a company is used by it.
    INSERT INTO _olp_use (id, company_id)
    SELECT t.id, t.company_id FROM _olp_type t JOIN _olp_company c ON c.tenant_id = t.tenant_id AND c.company_id = t.company_id
    ON CONFLICT DO NOTHING;
    -- A company's own policy uses the type.
    INSERT INTO _olp_use (id, company_id)
    SELECT p.leave_type_id, p.company_id FROM leave_policies p
      JOIN _olp_company c ON c.tenant_id = p.tenant_id AND c.company_id = p.company_id
      JOIN _olp_type t ON t.tenant_id = p.tenant_id AND t.id = p.leave_type_id
    ON CONFLICT DO NOTHING;
    -- An Active tenant-wide policy governs every company it reaches, employees or not yet.
    INSERT INTO _olp_use (id, company_id)
    SELECT p.leave_type_id, r.company_id FROM _olp_reach r JOIN _olp_pol p ON p.id = r.policy_id
      JOIN _olp_type t ON t.tenant_id = p.tenant_id AND t.id = p.leave_type_id
     WHERE p.status = 'Active'
    ON CONFLICT DO NOTHING;

    -- Decisions for every type still without a company: one line per (type, target company).
    CREATE TEMP TABLE IF NOT EXISTS _olp_target (tenant_id uuid, id uuid, code text, company_id uuid, target_id uuid, action text, employees int) ON COMMIT DROP;
    TRUNCATE _olp_target;
    INSERT INTO _olp_target (tenant_id, id, code, company_id, target_id, action, employees)
    WITH users AS (
        SELECT t.tenant_id, t.id, t.code, u.company_id, u.employees, c.created_at_utc,
               (SELECT o.id FROM _olp_type o WHERE o.tenant_id = t.tenant_id AND o.company_id = u.company_id
                                              AND o.code = t.code AND o.id <> t.id ORDER BY o.id LIMIT 1) AS existing_id
          FROM _olp_type t
          JOIN _olp_use u ON u.id = t.id
          JOIN _olp_company c ON c.tenant_id = t.tenant_id AND c.company_id = u.company_id
         WHERE t.company_id IS NULL),
    ranked AS (
        SELECT users.*, CASE WHEN existing_id IS NULL
                             THEN row_number() OVER (PARTITION BY id, (existing_id IS NULL)
                                                     ORDER BY employees DESC, created_at_utc, company_id) END AS rn
          FROM users)
    SELECT tenant_id, id, code, company_id,
           CASE WHEN existing_id IS NOT NULL THEN existing_id
                WHEN rn = 1 THEN id
                ELSE md5('olp:type:' || id::text || ':' || company_id::text)::uuid END,
           CASE WHEN existing_id IS NOT NULL THEN 'mapped_existing'
                WHEN rn = 1 AND count(*) OVER (PARTITION BY id) = 1 THEN 'assigned_single_user'
                WHEN rn = 1 THEN 'assigned_keeper'
                ELSE 'copied' END,
           employees
      FROM ranked;

    -- A same-code type the company already owns is used only when no employee of it would end up with two balances
    -- (or two accruals) for one year. Otherwise the legacy type is left exactly as it is.
    CREATE TEMP TABLE IF NOT EXISTS _olp_conflict ON COMMIT DROP AS
SELECT DISTINCT tg.id FROM _olp_target tg
          JOIN _olp_emp e ON e.tenant_id = tg.tenant_id AND e.company_id = tg.company_id
         WHERE tg.action = 'mapped_existing'
           AND (EXISTS (SELECT 1 FROM employee_leave_balances b1
                          JOIN employee_leave_balances b2 ON b2.tenant_id = b1.tenant_id AND b2.employee_id = b1.employee_id
                                                         AND b2.year = b1.year AND b2.leave_type_id = tg.target_id
                         WHERE b1.tenant_id = tg.tenant_id AND b1.employee_id = e.employee_id AND b1.leave_type_id = tg.id)
             OR EXISTS (SELECT 1 FROM leave_balance_transactions t1
                          JOIN leave_balance_transactions t2 ON t2.tenant_id = t1.tenant_id AND t2.employee_id = t1.employee_id
                                                            AND t2.year = t1.year AND t2.reference = t1.reference
                                                            AND t2.transaction_type = 'Accrual' AND t2.leave_type_id = tg.target_id
                         WHERE t1.tenant_id = tg.tenant_id AND t1.employee_id = e.employee_id AND t1.leave_type_id = tg.id
                           AND t1.transaction_type = 'Accrual')) WITH NO DATA;
    TRUNCATE _olp_conflict;
    INSERT INTO _olp_conflict
SELECT DISTINCT tg.id FROM _olp_target tg
          JOIN _olp_emp e ON e.tenant_id = tg.tenant_id AND e.company_id = tg.company_id
         WHERE tg.action = 'mapped_existing'
           AND (EXISTS (SELECT 1 FROM employee_leave_balances b1
                          JOIN employee_leave_balances b2 ON b2.tenant_id = b1.tenant_id AND b2.employee_id = b1.employee_id
                                                         AND b2.year = b1.year AND b2.leave_type_id = tg.target_id
                         WHERE b1.tenant_id = tg.tenant_id AND b1.employee_id = e.employee_id AND b1.leave_type_id = tg.id)
             OR EXISTS (SELECT 1 FROM leave_balance_transactions t1
                          JOIN leave_balance_transactions t2 ON t2.tenant_id = t1.tenant_id AND t2.employee_id = t1.employee_id
                                                            AND t2.year = t1.year AND t2.reference = t1.reference
                                                            AND t2.transaction_type = 'Accrual' AND t2.leave_type_id = tg.target_id
                         WHERE t1.tenant_id = tg.tenant_id AND t1.employee_id = e.employee_id AND t1.leave_type_id = tg.id
                           AND t1.transaction_type = 'Accrual'));
    DELETE FROM _olp_target WHERE id IN (SELECT id FROM _olp_conflict);
    SELECT count(*) INTO n_conflicts FROM _olp_conflict;

    -- Unused types: the tenant's only company, when it has exactly one and no type of that code.
    INSERT INTO _olp_target (tenant_id, id, code, company_id, target_id, action, employees)
    SELECT t.tenant_id, t.id, t.code, oc.company_id, t.id, 'assigned_only_company', 0
      FROM _olp_type t
      JOIN (SELECT tenant_id, min(company_id::text)::uuid AS company_id FROM _olp_company GROUP BY tenant_id HAVING count(*) = 1) oc
        ON oc.tenant_id = t.tenant_id
     WHERE t.company_id IS NULL
       AND NOT EXISTS (SELECT 1 FROM _olp_use u WHERE u.id = t.id)
       AND t.id NOT IN (SELECT id FROM _olp_conflict)
       AND NOT EXISTS (SELECT 1 FROM _olp_type o WHERE o.tenant_id = t.tenant_id AND o.company_id = oc.company_id
                                                 AND o.code = t.code AND o.id <> t.id);

    CREATE TEMP TABLE IF NOT EXISTS _olp_map ON COMMIT DROP AS
SELECT tenant_id, id AS original_id, company_id, target_id AS new_id, action
          FROM _olp_target WHERE target_id <> id WITH NO DATA;
    TRUNCATE _olp_map;
    INSERT INTO _olp_map
SELECT tenant_id, id AS original_id, company_id, target_id AS new_id, action
          FROM _olp_target WHERE target_id <> id;
    CREATE UNIQUE INDEX ON _olp_map (tenant_id, original_id, company_id);

    -- Which row is "type T" inside company C after this run.
    CREATE TEMP TABLE IF NOT EXISTS _olp_version ON COMMIT DROP AS
SELECT tenant_id, id AS original_id, company_id, target_id AS version_id FROM _olp_target
        UNION
        SELECT t.tenant_id, t.id, t.company_id, t.id FROM _olp_type t WHERE t.company_id IS NOT NULL WITH NO DATA;
    TRUNCATE _olp_version;
    INSERT INTO _olp_version
SELECT tenant_id, id AS original_id, company_id, target_id AS version_id FROM _olp_target
        UNION
        SELECT t.tenant_id, t.id, t.company_id, t.id FROM _olp_type t WHERE t.company_id IS NOT NULL;
    CREATE UNIQUE INDEX ON _olp_version (tenant_id, original_id, company_id);

    -- 1. Assign the kept types, then insert the copies (same values; new deterministic id).
    UPDATE leave_types t SET company_id = tg.company_id
      FROM _olp_target tg WHERE tg.id = t.id AND tg.target_id = t.id AND t.company_id IS NULL;

    SELECT string_agg(quote_ident(column_name), ',' ORDER BY ordinal_position) INTO cols
      FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'leave_types' AND is_generated = 'NEVER';
    CREATE TEMP TABLE IF NOT EXISTS _olp_copy_types ON COMMIT DROP AS
SELECT t.*, m.company_id AS olp_company, m.new_id AS olp_new_id
          FROM leave_types t JOIN _olp_map m ON m.original_id = t.id AND m.tenant_id = t.tenant_id AND m.action = 'copied'
         WHERE NOT EXISTS (SELECT 1 FROM leave_types e WHERE e.id = m.new_id) WITH NO DATA;
    TRUNCATE _olp_copy_types;
    INSERT INTO _olp_copy_types
SELECT t.*, m.company_id AS olp_company, m.new_id AS olp_new_id
          FROM leave_types t JOIN _olp_map m ON m.original_id = t.id AND m.tenant_id = t.tenant_id AND m.action = 'copied'
         WHERE NOT EXISTS (SELECT 1 FROM leave_types e WHERE e.id = m.new_id);
    UPDATE _olp_copy_types SET id = olp_new_id, company_id = olp_company;
    EXECUTE format('INSERT INTO leave_types (%s) SELECT %s FROM _olp_copy_types', cols, cols);

    -- 2. Tenant-wide policies: where each one goes.
    CREATE TEMP TABLE IF NOT EXISTS _olp_pcomp (policy_id uuid NOT NULL, company_id uuid NOT NULL, PRIMARY KEY (policy_id, company_id)) ON COMMIT DROP;
    TRUNCATE _olp_pcomp;
    INSERT INTO _olp_pcomp (policy_id, company_id)
    SELECT r.policy_id, r.company_id FROM _olp_reach r JOIN _olp_pol p ON p.id = r.policy_id
      JOIN _olp_version v ON v.tenant_id = p.tenant_id AND v.original_id = p.leave_type_id AND v.company_id = r.company_id;
    -- Every request on the policy must still resolve it in its employee's company.
    INSERT INTO _olp_pcomp (policy_id, company_id)
    SELECT DISTINCT p.id, e.company_id FROM _olp_pol p
      JOIN leave_requests lr ON lr.tenant_id = p.tenant_id AND lr.policy_id = p.id
      JOIN _olp_emp e ON e.tenant_id = lr.tenant_id AND e.employee_id = lr.employee_id
      JOIN _olp_version v ON v.tenant_id = p.tenant_id AND v.original_id = p.leave_type_id AND v.company_id = e.company_id
    ON CONFLICT DO NOTHING;
    -- Reaches nobody: the only company of a single-company tenant, when its type has a version there.
    INSERT INTO _olp_pcomp (policy_id, company_id)
    SELECT p.id, oc.company_id FROM _olp_pol p
      JOIN (SELECT tenant_id, min(company_id::text)::uuid AS company_id FROM _olp_company GROUP BY tenant_id HAVING count(*) = 1) oc
        ON oc.tenant_id = p.tenant_id
      JOIN _olp_version v ON v.tenant_id = p.tenant_id AND v.original_id = p.leave_type_id AND v.company_id = oc.company_id
     WHERE NOT EXISTS (SELECT 1 FROM _olp_pcomp x WHERE x.policy_id = p.id)
    ON CONFLICT DO NOTHING;

    CREATE TEMP TABLE IF NOT EXISTS _olp_ptarget (tenant_id uuid, id uuid, company_id uuid, target_id uuid, type_id uuid, action text, employees int) ON COMMIT DROP;
    TRUNCATE _olp_ptarget;
    INSERT INTO _olp_ptarget (tenant_id, id, company_id, target_id, type_id, action, employees)
    WITH ranked AS (
        SELECT p.tenant_id, p.id, pc.company_id, v.version_id AS type_id, COALESCE(u.employees, 0) AS employees,
               row_number() OVER (PARTITION BY p.id
                                  ORDER BY (v.version_id = p.leave_type_id) DESC, COALESCE(u.employees, 0) DESC,
                                           c.created_at_utc, pc.company_id) AS rn,
               count(*) OVER (PARTITION BY p.id) AS users
          FROM _olp_pol p
          JOIN _olp_pcomp pc ON pc.policy_id = p.id
          JOIN _olp_company c ON c.tenant_id = p.tenant_id AND c.company_id = pc.company_id
          JOIN _olp_version v ON v.tenant_id = p.tenant_id AND v.original_id = p.leave_type_id AND v.company_id = pc.company_id
          LEFT JOIN _olp_use u ON u.id = p.leave_type_id AND u.company_id = pc.company_id)
    SELECT tenant_id, id, company_id,
           CASE WHEN rn = 1 THEN id ELSE md5('olp:policy:' || id::text || ':' || company_id::text)::uuid END,
           type_id,
           CASE WHEN rn = 1 AND users = 1 THEN 'assigned_single_company'
                WHEN rn = 1 THEN 'assigned_keeper'
                ELSE 'copied' END,
           employees
      FROM ranked;

    CREATE TEMP TABLE IF NOT EXISTS _olp_pmap ON COMMIT DROP AS
SELECT tenant_id, id AS original_id, company_id, target_id AS new_id FROM _olp_ptarget WHERE target_id <> id WITH NO DATA;
    TRUNCATE _olp_pmap;
    INSERT INTO _olp_pmap
SELECT tenant_id, id AS original_id, company_id, target_id AS new_id FROM _olp_ptarget WHERE target_id <> id;
    CREATE UNIQUE INDEX ON _olp_pmap (tenant_id, original_id, company_id);

    UPDATE leave_policies p SET company_id = pt.company_id, leave_type_id = pt.type_id, legacy_group_default = true
      FROM _olp_ptarget pt WHERE pt.id = p.id AND pt.target_id = p.id AND p.company_id IS NULL;

    SELECT string_agg(quote_ident(column_name), ',' ORDER BY ordinal_position) INTO cols
      FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'leave_policies' AND is_generated = 'NEVER';
    CREATE TEMP TABLE IF NOT EXISTS _olp_copy_policies ON COMMIT DROP AS
SELECT p.*, pt.company_id AS olp_company, pt.target_id AS olp_new_id, pt.type_id AS olp_type
          FROM leave_policies p JOIN _olp_ptarget pt ON pt.id = p.id AND pt.action = 'copied'
         WHERE NOT EXISTS (SELECT 1 FROM leave_policies e WHERE e.id = pt.target_id) WITH NO DATA;
    TRUNCATE _olp_copy_policies;
    INSERT INTO _olp_copy_policies
SELECT p.*, pt.company_id AS olp_company, pt.target_id AS olp_new_id, pt.type_id AS olp_type
          FROM leave_policies p JOIN _olp_ptarget pt ON pt.id = p.id AND pt.action = 'copied'
         WHERE NOT EXISTS (SELECT 1 FROM leave_policies e WHERE e.id = pt.target_id);
    UPDATE _olp_copy_policies SET id = olp_new_id, company_id = olp_company, leave_type_id = olp_type, legacy_group_default = true;
    EXECUTE format('INSERT INTO leave_policies (%s) SELECT %s FROM _olp_copy_policies', cols, cols);

    -- Child config of a copied policy: accrual rules and the company-less eligibility rows are copied; a row naming a
    -- company that has its own copy moves to it.
    SELECT string_agg(quote_ident(column_name), ',' ORDER BY ordinal_position) INTO cols
      FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'leave_accrual_rules' AND is_generated = 'NEVER';
    CREATE TEMP TABLE IF NOT EXISTS _olp_copy_rules ON COMMIT DROP AS
SELECT a.*, m.new_id AS olp_parent, md5('olp:lar:' || a.id::text || ':' || m.company_id::text)::uuid AS olp_new_id
          FROM leave_accrual_rules a JOIN _olp_pmap m ON m.tenant_id = a.tenant_id AND m.original_id = a.leave_policy_id WITH NO DATA;
    TRUNCATE _olp_copy_rules;
    INSERT INTO _olp_copy_rules
SELECT a.*, m.new_id AS olp_parent, md5('olp:lar:' || a.id::text || ':' || m.company_id::text)::uuid AS olp_new_id
          FROM leave_accrual_rules a JOIN _olp_pmap m ON m.tenant_id = a.tenant_id AND m.original_id = a.leave_policy_id;
    DELETE FROM _olp_copy_rules WHERE EXISTS (SELECT 1 FROM leave_accrual_rules e WHERE e.id = _olp_copy_rules.olp_new_id);
    UPDATE _olp_copy_rules SET id = olp_new_id, leave_policy_id = olp_parent;
    EXECUTE format('INSERT INTO leave_accrual_rules (%s) SELECT %s FROM _olp_copy_rules', cols, cols);

    SELECT string_agg(quote_ident(column_name), ',' ORDER BY ordinal_position) INTO cols
      FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'leave_policy_eligibilities' AND is_generated = 'NEVER';
    CREATE TEMP TABLE IF NOT EXISTS _olp_copy_elig ON COMMIT DROP AS
SELECT el.*, m.new_id AS olp_parent, md5('olp:pel:' || el.id::text || ':' || m.company_id::text)::uuid AS olp_new_id
          FROM leave_policy_eligibilities el JOIN _olp_pmap m ON m.tenant_id = el.tenant_id AND m.original_id = el.leave_policy_id
         WHERE el.company_id IS NULL WITH NO DATA;
    TRUNCATE _olp_copy_elig;
    INSERT INTO _olp_copy_elig
SELECT el.*, m.new_id AS olp_parent, md5('olp:pel:' || el.id::text || ':' || m.company_id::text)::uuid AS olp_new_id
          FROM leave_policy_eligibilities el JOIN _olp_pmap m ON m.tenant_id = el.tenant_id AND m.original_id = el.leave_policy_id
         WHERE el.company_id IS NULL;
    DELETE FROM _olp_copy_elig WHERE EXISTS (SELECT 1 FROM leave_policy_eligibilities e WHERE e.id = _olp_copy_elig.olp_new_id);
    -- CopyWorkspaceRulesPerCompany already made this company's copy of the row (company C, C's department/grade, still on
    -- the original policy): that copy moves to C's policy just below, so the company-less row is not copied a second time.
    DELETE FROM _olp_copy_elig ce USING _olp_pmap pm
     WHERE pm.new_id = ce.olp_parent
       AND EXISTS (SELECT 1 FROM leave_policy_eligibilities x
                    WHERE x.id = md5('orc:leave_policy_eligibilities:' || ce.id::text || ':' || pm.company_id::text)::uuid);
    UPDATE _olp_copy_elig SET id = olp_new_id, leave_policy_id = olp_parent;
    EXECUTE format('INSERT INTO leave_policy_eligibilities (%s) SELECT %s FROM _olp_copy_elig', cols, cols);

    UPDATE leave_policy_eligibilities el SET leave_policy_id = m.new_id
      FROM _olp_pmap m WHERE m.tenant_id = el.tenant_id AND m.original_id = el.leave_policy_id AND m.company_id = el.company_id;
    GET DIAGNOSTICS n = ROW_COUNT; n_refs := n_refs + n;

    -- A policy follows its company's approval workflow: when CopyWorkspaceRulesPerCompany copied the pinned workflow for
    -- the policy's company (its chained audit rows are the original -> copy map), the policy names that copy.
    CREATE TEMP TABLE IF NOT EXISTS _olp_wf (tenant_id uuid NOT NULL, original_id uuid NOT NULL, company_id uuid NOT NULL,
        new_id uuid NOT NULL, PRIMARY KEY (original_id, company_id)) ON COMMIT DROP;
    TRUNCATE _olp_wf;
    INSERT INTO _olp_wf (tenant_id, original_id, company_id, new_id)
    SELECT a.tenant_id, (a.metadata::jsonb ->> 'sourceId')::uuid, (a.metadata::jsonb ->> 'companyId')::uuid, (a.metadata::jsonb ->> 'rowId')::uuid
      FROM audit_logs a
     WHERE a.action IN ('organization.company_backfill_rule_copied', 'organization.company_backfill_copied', 'organization.company_backfill_mapped_existing')
       AND a.tenant_id IS NOT NULL AND a.metadata::jsonb ->> 'table' = 'approval_workflows'
       AND EXISTS (SELECT 1 FROM approval_workflows w WHERE w.id = (a.metadata::jsonb ->> 'rowId')::uuid)
    ON CONFLICT DO NOTHING;
    UPDATE leave_policies p SET approval_workflow_id = w.new_id
      FROM _olp_wf w WHERE w.tenant_id = p.tenant_id AND w.original_id = p.approval_workflow_id AND w.company_id = p.company_id;
    GET DIAGNOSTICS n = ROW_COUNT; n_refs := n_refs + n;

    -- 3. Re-point every reference whose employee's company has a copy (or an existing same-code row) of the type.
    FOR ref_tbl IN SELECT * FROM (VALUES
            ('employee_leave_balances'), ('leave_balance_transactions'), ('leave_requests'),
            ('leave_encashment_requests'), ('absence_regularization_requests')) v(tbl)
    LOOP
        EXECUTE format('UPDATE %1$I t SET leave_type_id = m.new_id FROM _olp_emp e, _olp_map m
                         WHERE e.tenant_id = t.tenant_id AND e.employee_id = t.employee_id
                           AND m.tenant_id = t.tenant_id AND m.original_id = t.leave_type_id AND m.company_id = e.company_id',
                       ref_tbl.tbl);
        GET DIAGNOSTICS n = ROW_COUNT; n_refs := n_refs + n;
    END LOOP;

    UPDATE leave_requests t SET policy_id = pm.new_id FROM _olp_emp e, _olp_pmap pm
     WHERE e.tenant_id = t.tenant_id AND e.employee_id = t.employee_id
       AND pm.tenant_id = t.tenant_id AND pm.original_id = t.policy_id AND pm.company_id = e.company_id;
    GET DIAGNOSTICS n = ROW_COUNT; n_refs := n_refs + n;

    -- A company's own policies point at the company's version of their type.
    UPDATE leave_policies p SET leave_type_id = m.new_id
      FROM _olp_map m WHERE m.tenant_id = p.tenant_id AND m.original_id = p.leave_type_id AND m.company_id = p.company_id;
    GET DIAGNOSTICS n = ROW_COUNT; n_refs := n_refs + n;

    -- 4. Eligibility rows follow their policy's company: a department or grade of another company becomes the
    --    policy company's row of the same code, when it has one.
    UPDATE leave_policy_eligibilities el SET department_id = d2.id
      FROM leave_policies p, departments d, departments d2
     WHERE p.tenant_id = el.tenant_id AND p.id = el.leave_policy_id AND p.company_id IS NOT NULL
       AND (el.company_id IS NULL OR el.company_id = p.company_id)
       AND d.tenant_id = el.tenant_id AND d.id = el.department_id AND d.company_id IS NOT NULL AND d.company_id <> p.company_id
       AND d2.tenant_id = el.tenant_id AND d2.company_id = p.company_id AND d2.code = d.code;
    GET DIAGNOSTICS n = ROW_COUNT; n_aligned := n_aligned + n;
    UPDATE leave_policy_eligibilities el SET grade_id = g2.id
      FROM leave_policies p, grades g, grades g2
     WHERE p.tenant_id = el.tenant_id AND p.id = el.leave_policy_id AND p.company_id IS NOT NULL
       AND (el.company_id IS NULL OR el.company_id = p.company_id)
       AND g.tenant_id = el.tenant_id AND g.id = el.grade_id AND g.company_id IS NOT NULL AND g.company_id <> p.company_id
       AND g2.tenant_id = el.tenant_id AND g2.company_id = p.company_id AND g2.code = g.code;
    GET DIAGNOSTICS n = ROW_COUNT; n_aligned := n_aligned + n;

    -- 5. Audit: one row per assigned, copied or mapped type and policy, chained per tenant exactly like
    --    AuditService.ComputeHash.
    FOR aud IN
        SELECT * FROM (
            SELECT tg.tenant_id, 'type' AS kind, 'LeaveType' AS entity_name, tg.id, tg.company_id, tg.target_id, tg.action,
                   tg.code AS label, tg.employees
              FROM _olp_target tg
            UNION ALL
            SELECT pt.tenant_id, 'policy', 'LeavePolicy', pt.id, pt.company_id, pt.target_id, pt.action,
                   lp.name, pt.employees
              FROM _olp_ptarget pt JOIN leave_policies lp ON lp.id = pt.id) x
         ORDER BY x.tenant_id, x.kind DESC, x.id, x.company_id
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
            'rule', aud.action, 'companyId', aud.company_id, 'sourceId', aud.id, 'rowId', aud.target_id, 'label', aud.label,
            'employeesOnRow', aud.employees, 'migration', 'BackfillLeavePerCompany')::text;
        canonical := aud.tenant_id::text || chr(10) || '' || chr(10)
                  || 'leave.company_backfill_' || aud.action || chr(10) || aud.entity_name || chr(10)
                  || aud.target_id::text || chr(10) || '' || chr(10)
                  || 'system: migration BackfillLeavePerCompany' || chr(10) || meta || chr(10)
                  || prev_hash || chr(10)
                  || to_char(ts AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US') || '0Z';
        h := encode(sha256(convert_to(canonical, 'UTF8')), 'hex');
        INSERT INTO audit_logs (id, tenant_id, company_id, user_id, action, entity_name, entity_id, ip_address, user_agent,
                                metadata, previous_hash, entry_hash, hash_algorithm, created_at_utc)
        VALUES (md5('olp:audit:' || aud.kind || ':' || aud.id::text || ':' || aud.company_id::text)::uuid, aud.tenant_id, aud.company_id, NULL,
                'leave.company_backfill_' || aud.action, aud.entity_name, aud.target_id::text, NULL,
                'system: migration BackfillLeavePerCompany', meta::json, prev_hash, h, 'SHA-256', ts)
        ON CONFLICT (id) DO NOTHING;
        prev_hash := h;
    END LOOP;

    SELECT count(*) FILTER (WHERE action IN ('assigned_single_user', 'assigned_keeper')),
           count(*) FILTER (WHERE action = 'assigned_only_company'),
           count(*) FILTER (WHERE action = 'copied'),
           count(*) FILTER (WHERE action = 'mapped_existing')
      INTO n_assigned, n_only, n_copied, n_mapped FROM _olp_target;
    SELECT count(*) INTO n_unassigned FROM leave_types t WHERE t.company_id IS NULL;
    SELECT count(*) FILTER (WHERE action IN ('assigned_single_company', 'assigned_keeper')), count(*) FILTER (WHERE action = 'copied')
      INTO p_assigned, p_copied FROM _olp_ptarget;
    SELECT count(*) INTO p_unassigned FROM leave_policies p WHERE p.company_id IS NULL;
    RAISE NOTICE 'BackfillLeavePerCompany: types assigned % (+% only company), copied %, mapped to existing %, left as is for a balance conflict %, unassigned now %; policies assigned %, copied %, unassigned now %; references re-pointed %, eligibility org links aligned %',
        n_assigned, n_only, n_copied, n_mapped, n_conflicts, n_unassigned, p_assigned, p_copied, p_unassigned, n_refs, n_aligned;
END
$olp$;
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
