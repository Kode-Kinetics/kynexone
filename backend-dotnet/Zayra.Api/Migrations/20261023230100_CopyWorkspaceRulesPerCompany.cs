using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// Addendum B, owner decision 2026-10-23 ("nothing may silently stop applying"). When
    /// <see cref="BackfillOrganisationPerCompany"/> copied a department, designation or grade for a company, every
    /// workspace-wide rule that names the original gets a copy for that company, pointed at that company's copy —
    /// otherwise the rule would match only the keeper company's employees from now on.
    ///
    /// <para><b>Which copies.</b> The backfill's own audit rows (<c>organization.company_backfill_copied</c> and
    /// <c>…_mapped_existing</c>) are the map: (original row, company) → that company's row. A rule gets a copy for
    /// company C when at least one of its references has a C version; every reference is then pointed at C's version
    /// (or kept, when the original already is C's). A rule whose references are all C's own is left alone: the
    /// original still serves C. A rule limited to a branch of another company never applied to C and is not copied.</para>
    ///
    /// <para><b>Rule families</b> (child rows copied with their rule): approval policies (+ steps), approval workflows
    /// (+ steps), attendance policies (+ rules), overtime policies (+ multipliers, rules), overtime budgets (a copy's ConsumedAmount is re-derived, see the SQL, never carried over), onboarding
    /// checklists (+ template tasks), and leave-policy / benefit eligibility rows with no company (the copy carries
    /// C as its company). Eligibility rows that already belong to a company were re-pointed by the backfill.
    /// Copies keep every value; a code gets the suffix <c>-XXXXXX</c> (from the company id, as codes are unique per
    /// tenant) and a name gets " — {company}" so Setup can tell them apart.</para>
    ///
    /// <para><b>Not copied, counted in the NOTICE and the precheck:</b> manpower requisitions, job openings and workforce
    /// plans. They have no company column, so all of them are workspace-wide operational records; duplicating a live job
    /// opening would publish it twice. They stay on the keeper's row. Survey responses (append-only) and
    /// employee-entitlement witnesses (close-only) are evidence and stay as they are.</para>
    ///
    /// <para><b>Deterministic and idempotent.</b> Ids are <c>md5('orc:…')::uuid</c> of the source id and the company;
    /// an existing copy is never written again, so a re-run changes nothing. One chained <c>audit_logs</c> row per
    /// copied rule. Scratch tables are <c>ON COMMIT DROP</c>. Down is a no-op (restore the pre-deploy branch).</para>
    /// </summary>
    public partial class CopyWorkspaceRulesPerCompany : Migration
    {
        /// <summary>The whole copy. Executed by <see cref="Up"/> and, verbatim, by the Postgres tests.</summary>
        public const string CopyRulesSql = """
DO $orc$
DECLARE
    r record;
    ch record;
    aud record;
    cols text;
    n int;
    report text := '';
    current_tenant uuid := NULL;
    prev_hash text;
    ts timestamptz;
    meta text;
    canonical text;
    h text;
    left_on_keeper bigint;
BEGIN
    -- (original row, company) -> that company's row, from the backfill's audit trail; only rows that still exist.
    CREATE TEMP TABLE IF NOT EXISTS _orc_map (kind text NOT NULL, tenant_id uuid NOT NULL, original_id uuid NOT NULL,
        company_id uuid NOT NULL, new_id uuid NOT NULL, PRIMARY KEY (kind, original_id, company_id)) ON COMMIT DROP;
    TRUNCATE _orc_map;
    INSERT INTO _orc_map (kind, tenant_id, original_id, company_id, new_id)
    SELECT lower(a.entity_name), a.tenant_id, (a.metadata::jsonb ->> 'sourceId')::uuid,
           (a.metadata::jsonb ->> 'companyId')::uuid, (a.metadata::jsonb ->> 'rowId')::uuid
      FROM audit_logs a
     WHERE a.action IN ('organization.company_backfill_copied', 'organization.company_backfill_mapped_existing')
       AND a.tenant_id IS NOT NULL
    ON CONFLICT DO NOTHING;
    DELETE FROM _orc_map m
     WHERE NOT EXISTS (SELECT 1 FROM departments d WHERE m.kind = 'department' AND d.id = m.new_id)
       AND NOT EXISTS (SELECT 1 FROM designations x WHERE m.kind = 'designation' AND x.id = m.new_id)
       AND NOT EXISTS (SELECT 1 FROM grades g WHERE m.kind = 'grade' AND g.id = m.new_id);

    -- The company that owns each organisation row: a copy must point only at rows of its own company.
    CREATE TEMP TABLE IF NOT EXISTS _orc_owner (kind text NOT NULL, id uuid NOT NULL, company_id uuid NOT NULL,
        PRIMARY KEY (kind, id)) ON COMMIT DROP;
    TRUNCATE _orc_owner;
    INSERT INTO _orc_owner (kind, id, company_id)
    SELECT 'department', id, company_id FROM departments WHERE company_id IS NOT NULL
    UNION ALL SELECT 'designation', id, company_id FROM designations WHERE company_id IS NOT NULL
    UNION ALL SELECT 'grade', id, company_id FROM grades WHERE company_id IS NOT NULL;

    CREATE TEMP TABLE IF NOT EXISTS _orc_plan (tbl text NOT NULL, tenant_id uuid NOT NULL, rule_id uuid NOT NULL,
        company_id uuid NOT NULL, new_id uuid NOT NULL, r1 uuid, r2 uuid, inserted boolean NOT NULL DEFAULT false,
        PRIMARY KEY (tbl, rule_id, company_id)) ON COMMIT DROP;
    TRUNCATE _orc_plan;

    -- tbl, ref1, kind1, ref2, kind2 (NULL = one reference), branch column, company-less eligibility row, has code, has name
    FOR r IN SELECT * FROM (VALUES
            ('approval_policies',          'department_id', 'department', 'grade_id', 'grade', NULL,        false, false, true),
            ('approval_workflows',         'department_id', 'department', 'grade_id', 'grade', NULL,        false, true,  true),
            ('attendance_policies',        'department_id', 'department', 'grade_id', 'grade', 'branch_id', false, true,  true),
            ('overtime_policies',          'department_id', 'department', 'grade_id', 'grade', 'branch_id', false, true,  true),
            ('overtime_budgets',           'department_id', 'department', NULL,       NULL,    NULL,        false, false, false),
            ('onboarding_checklists',      'department_id', 'department', NULL,       NULL,    NULL,        false, true,  true),
            ('leave_policy_eligibilities', 'department_id', 'department', 'grade_id', 'grade', 'branch_id', true,  false, false),
            ('benefit_eligibility_rules',  'grade_id',      'grade',      NULL,       NULL,    NULL,        true,  false, false))
            v(tbl, ref1, kind1, ref2, kind2, branch_col, eligibility, has_code, has_name)
    LOOP
        -- 1. Plan: one line per (rule, company that has a version of at least one of its references).
        EXECUTE format($q$
            INSERT INTO _orc_plan (tbl, tenant_id, rule_id, company_id, new_id, r1, r2)
            SELECT DISTINCT %1$L, t.tenant_id, t.id, c.company_id,
                   md5('orc:' || %1$L || ':' || t.id::text || ':' || c.company_id::text)::uuid,
                   COALESCE(m1.new_id, t.%2$I), %6$s
              FROM %1$I t
              JOIN (SELECT m.tenant_id, m.kind, m.original_id, m.company_id FROM _orc_map m) c
                ON c.tenant_id = t.tenant_id
               AND ((c.kind = %3$L AND c.original_id = t.%2$I) %7$s)
              LEFT JOIN _orc_map m1 ON m1.kind = %3$L AND m1.original_id = t.%2$I AND m1.company_id = c.company_id
              %8$s
             WHERE true %9$s %10$s
               -- every reference of the copy must be C's own row (or unassigned): this also keeps a re-run from
               -- treating an earlier copy as a rule to copy again
               AND NOT EXISTS (SELECT 1 FROM _orc_owner o WHERE o.kind = %3$L AND o.id = COALESCE(m1.new_id, t.%2$I) AND o.company_id <> c.company_id)
               %11$s
            ON CONFLICT DO NOTHING$q$,
            r.tbl, r.ref1, r.kind1, r.ref2, r.kind2,
            CASE WHEN r.ref2 IS NULL THEN 'NULL::uuid' ELSE format('COALESCE(m2.new_id, t.%I)', r.ref2) END,
            CASE WHEN r.ref2 IS NULL THEN '' ELSE format('OR (c.kind = %L AND c.original_id = t.%I)', r.kind2, r.ref2) END,
            CASE WHEN r.ref2 IS NULL THEN '' ELSE format('LEFT JOIN _orc_map m2 ON m2.kind = %L AND m2.original_id = t.%I AND m2.company_id = c.company_id', r.kind2, r.ref2) END,
            CASE WHEN r.eligibility THEN 'AND t.company_id IS NULL' ELSE '' END,
            CASE WHEN r.branch_col IS NULL THEN '' ELSE format(
                'AND (t.%1$I IS NULL OR EXISTS (SELECT 1 FROM branches b WHERE b.tenant_id = t.tenant_id AND b.id = t.%1$I AND b.company_id = c.company_id))', r.branch_col) END,
            CASE WHEN r.ref2 IS NULL THEN '' ELSE format(
                'AND NOT EXISTS (SELECT 1 FROM _orc_owner o WHERE o.kind = %L AND o.id = COALESCE(m2.new_id, t.%I) AND o.company_id <> c.company_id)', r.kind2, r.ref2) END);

        -- 2. Copy the rule rows that do not exist yet: same values, the company's references, code/name suffix.
        SELECT string_agg(quote_ident(column_name), ',' ORDER BY ordinal_position) INTO cols
          FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = r.tbl AND is_generated = 'NEVER';
        EXECUTE format('CREATE TEMP TABLE IF NOT EXISTS %I ON COMMIT DROP AS SELECT t.*, NULL::uuid AS orc_new_id FROM %I t WITH NO DATA',
                       '_orc_copy_' || r.tbl, r.tbl);
        EXECUTE format('TRUNCATE %I', '_orc_copy_' || r.tbl);
        EXECUTE format($q$
            INSERT INTO %1$I SELECT t.*, p.new_id FROM %2$I t
              JOIN _orc_plan p ON p.tbl = %2$L AND p.rule_id = t.id
             WHERE NOT EXISTS (SELECT 1 FROM %2$I e WHERE e.id = p.new_id)$q$, '_orc_copy_' || r.tbl, r.tbl);
        EXECUTE format($q$
            UPDATE %1$I c SET id = p.new_id, %2$I = p.r1 %3$s %4$s %5$s %6$s
              FROM _orc_plan p WHERE p.new_id = c.orc_new_id$q$,
            '_orc_copy_' || r.tbl, r.ref1,
            CASE WHEN r.ref2 IS NULL THEN '' ELSE format(', %I = p.r2', r.ref2) END,
            CASE WHEN r.eligibility THEN ', company_id = p.company_id' ELSE '' END,
            CASE WHEN r.has_code THEN ', code = c.code || ''-'' || upper(left(replace(p.company_id::text, ''-'', ''''), 6))' ELSE '' END,
            CASE WHEN r.has_name THEN format(', name = left(c.name || '' — '' || COALESCE((SELECT co.legal_name_en FROM companies co WHERE co.id = p.company_id), ''''), %s)',
                 CASE WHEN r.tbl = 'approval_policies' THEN 180 ELSE 1000 END) ELSE '' END);
        EXECUTE format('INSERT INTO %I (%s) SELECT %s FROM %I', r.tbl, cols, cols, '_orc_copy_' || r.tbl);
        EXECUTE format('UPDATE _orc_plan p SET inserted = true FROM %I c WHERE p.new_id = c.orc_new_id', '_orc_copy_' || r.tbl);
        GET DIAGNOSTICS n = ROW_COUNT;
        report := report || format('%s %s, ', r.tbl, n);
    END LOOP;

    -- 3. Child rows of the rules copied in THIS run (a re-run copies nothing, so it writes no children).
    FOR ch IN SELECT * FROM (VALUES
            ('approval_policy_steps',               'policy_id',            'approval_policies'),
            ('approval_workflow_steps',             'workflow_id',          'approval_workflows'),
            ('attendance_rules',                    'attendance_policy_id', 'attendance_policies'),
            ('overtime_multipliers',                'overtime_policy_id',   'overtime_policies'),
            ('overtime_rules',                      'overtime_policy_id',   'overtime_policies'),
            ('onboarding_checklist_template_tasks', 'checklist_id',         'onboarding_checklists'))
            v(tbl, fk, parent)
    LOOP
        SELECT string_agg(quote_ident(column_name), ',' ORDER BY ordinal_position) INTO cols
          FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = ch.tbl AND is_generated = 'NEVER';
        EXECUTE format('CREATE TEMP TABLE IF NOT EXISTS %I ON COMMIT DROP AS SELECT c.*, NULL::uuid AS orc_new_id, NULL::uuid AS orc_parent FROM %I c WITH NO DATA',
                       '_orc_child_' || ch.tbl, ch.tbl);
        EXECUTE format('TRUNCATE %I', '_orc_child_' || ch.tbl);
        EXECUTE format($q$
            INSERT INTO %1$I SELECT c.*, md5('orc:' || %2$L || ':' || c.id::text || ':' || p.company_id::text)::uuid, p.new_id
              FROM %2$I c JOIN _orc_plan p ON p.tbl = %3$L AND p.rule_id = c.%4$I AND p.inserted
             WHERE NOT EXISTS (SELECT 1 FROM %2$I e WHERE e.id = md5('orc:' || %2$L || ':' || c.id::text || ':' || p.company_id::text)::uuid)$q$,
            '_orc_child_' || ch.tbl, ch.tbl, ch.parent, ch.fk);
        EXECUTE format('UPDATE %I SET id = orc_new_id, %I = orc_parent', '_orc_child_' || ch.tbl, ch.fk);
        EXECUTE format('INSERT INTO %I (%s) SELECT %s FROM %I', ch.tbl, cols, cols, '_orc_child_' || ch.tbl);
        GET DIAGNOSTICS n = ROW_COUNT;
        report := report || format('%s %s, ', ch.tbl, n);
    END LOOP;

    -- 3b. Overtime budgets: a copy must not carry the keeper's consumption. Consumption is re-derived per budget from
    -- the approved overtime it covers (calculation amount, same department — which is per company now — month,
    -- project and currency). It is used only when the re-derived total over the keeper and its copies reproduces the
    -- stored ConsumedAmount (so the stored figure provably came from those records). Otherwise the copies start at 0
    -- and the keeper keeps its figure. BudgetAmount is never changed: the envelope is duplicated (owner's call later).
    CREATE TEMP TABLE IF NOT EXISTS _orc_budget (budget_id uuid PRIMARY KEY, keeper_id uuid NOT NULL, company_id uuid,
        consumed_before numeric NOT NULL, consumed_after numeric, method text) ON COMMIT DROP;
    TRUNCATE _orc_budget;
    INSERT INTO _orc_budget (budget_id, keeper_id, company_id, consumed_before)
    SELECT p.new_id, p.rule_id, p.company_id, k.consumed_amount
      FROM _orc_plan p JOIN overtime_budgets k ON k.id = p.rule_id
     WHERE p.tbl = 'overtime_budgets' AND p.inserted
    UNION ALL
    SELECT DISTINCT k.id, k.id, NULL::uuid, k.consumed_amount
      FROM _orc_plan p JOIN overtime_budgets k ON k.id = p.rule_id
     WHERE p.tbl = 'overtime_budgets' AND p.inserted;
    UPDATE _orc_budget ob SET consumed_after = COALESCE((
        SELECT sum(c.amount) FROM overtime_budgets b
          JOIN employees e ON e.tenant_id = b.tenant_id AND e.department_id = b.department_id
          JOIN overtime_calculations c ON c.tenant_id = b.tenant_id AND c.employee_id = e.id AND c.currency = b.currency
          JOIN overtime_requests oreq ON oreq.tenant_id = b.tenant_id AND oreq.id = c.overtime_request_id AND oreq.status = 'Approved'
         WHERE b.id = ob.budget_id
           AND EXTRACT(YEAR FROM oreq.work_date) = b.year AND EXTRACT(MONTH FROM oreq.work_date) = b.month
           AND (b.project_id IS NULL OR oreq.project_id = b.project_id)), 0);
    UPDATE _orc_budget ob SET method = CASE
            WHEN (SELECT round(sum(x.consumed_after), 2) FROM _orc_budget x WHERE x.keeper_id = ob.keeper_id) = round(ob.consumed_before, 2)
            THEN 'recomputed_from_overtime' ELSE 'copies_zeroed' END;
    UPDATE _orc_budget SET consumed_after = CASE WHEN budget_id = keeper_id THEN consumed_before ELSE 0 END
     WHERE method = 'copies_zeroed';
    UPDATE overtime_budgets b SET consumed_amount = ob.consumed_after
      FROM _orc_budget ob WHERE b.id = ob.budget_id AND b.consumed_amount IS DISTINCT FROM ob.consumed_after;

    -- 4. Audit: one row per copied rule, chained per tenant exactly like AuditService.ComputeHash.
    FOR aud IN SELECT * FROM _orc_plan WHERE inserted ORDER BY tenant_id, tbl, rule_id, company_id
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
        meta := (jsonb_build_object('rule', 'rule_copied', 'table', aud.tbl, 'companyId', aud.company_id, 'sourceId', aud.rule_id,
            'rowId', aud.new_id, 'migration', 'CopyWorkspaceRulesPerCompany')
            || COALESCE((SELECT jsonb_build_object('consumedMethod', ob.method, 'copyConsumed', ob.consumed_after,
                                    'keeperConsumedBefore', k.consumed_before, 'keeperConsumedAfter', k.consumed_after,
                                    'budget', 'duplicated for the company; splitting the envelope is the owner''s call')
                           FROM _orc_budget ob JOIN _orc_budget k ON k.budget_id = ob.keeper_id
                          WHERE ob.budget_id = aud.new_id), '{}'::jsonb))::text;
        canonical := aud.tenant_id::text || chr(10) || '' || chr(10)
                  || 'organization.company_backfill_rule_copied' || chr(10) || aud.tbl || chr(10)
                  || aud.new_id::text || chr(10) || '' || chr(10)
                  || 'system: migration CopyWorkspaceRulesPerCompany' || chr(10) || meta || chr(10)
                  || prev_hash || chr(10)
                  || to_char(ts AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US') || '0Z';
        h := encode(sha256(convert_to(canonical, 'UTF8')), 'hex');
        INSERT INTO audit_logs (id, tenant_id, company_id, user_id, action, entity_name, entity_id, ip_address, user_agent,
                                metadata, previous_hash, entry_hash, hash_algorithm, created_at_utc)
        VALUES (md5('orc:audit:' || aud.tbl || ':' || aud.rule_id::text || ':' || aud.company_id::text)::uuid, aud.tenant_id, aud.company_id, NULL,
                'organization.company_backfill_rule_copied', aud.tbl, aud.new_id::text, NULL,
                'system: migration CopyWorkspaceRulesPerCompany', meta::json, prev_hash, h, 'SHA-256', ts)
        ON CONFLICT (id) DO NOTHING;
        prev_hash := h;
    END LOOP;

    -- 5. Operational recruitment records stay on the keeper's row (no company column: all are workspace-wide).
    SELECT (SELECT count(*) FROM manpower_requisitions t WHERE EXISTS (SELECT 1 FROM _orc_map m WHERE m.tenant_id = t.tenant_id AND m.original_id IN (t.department_id, t.designation_id)))
         + (SELECT count(*) FROM job_openings t WHERE EXISTS (SELECT 1 FROM _orc_map m WHERE m.tenant_id = t.tenant_id AND m.original_id IN (t.department_id, t.designation_id)))
         + (SELECT count(*) FROM workforce_plans t WHERE EXISTS (SELECT 1 FROM _orc_map m WHERE m.tenant_id = t.tenant_id AND m.original_id = t.department_id))
      INTO left_on_keeper;
    RAISE NOTICE 'CopyWorkspaceRulesPerCompany: copied %left on the keeper (requisitions, job openings, workforce plans) %; overtime budgets duplicated %, consumption moved to copies % (recomputed %, copies zeroed %)',
        report, left_on_keeper,
        (SELECT count(*) FROM _orc_budget WHERE budget_id <> keeper_id),
        (SELECT COALESCE(sum(consumed_after), 0) FROM _orc_budget WHERE budget_id <> keeper_id),
        (SELECT count(*) FROM _orc_budget WHERE budget_id <> keeper_id AND method = 'recomputed_from_overtime'),
        (SELECT count(*) FROM _orc_budget WHERE budget_id <> keeper_id AND method = 'copies_zeroed');
END
$orc$;
""";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(CopyRulesSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately empty: see the class summary (restore the pre-deploy database branch to roll back).
        }
    }
}
