-- PostgreSQL database dump




-- Name: app; Type: SCHEMA; Schema: -; Owner: -

CREATE SCHEMA app;


-- Name: SCHEMA app; Type: COMMENT; Schema: -; Owner: -

COMMENT ON SCHEMA app IS 'Security surface: the GUC accessors every RLS policy calls, the four named bypass surfaces of §19.2, and the partition-maintenance template of §19.3. Contains no business table.';


-- Name: btree_gist; Type: EXTENSION; Schema: -; Owner: -

CREATE EXTENSION IF NOT EXISTS btree_gist WITH SCHEMA public;


-- Name: EXTENSION btree_gist; Type: COMMENT; Schema: -; Owner: -

COMMENT ON EXTENSION btree_gist IS 'support for indexing common datatypes in GiST';


-- Name: pg_stat_statements; Type: EXTENSION; Schema: -; Owner: -

CREATE EXTENSION IF NOT EXISTS pg_stat_statements WITH SCHEMA public;


-- Name: EXTENSION pg_stat_statements; Type: COMMENT; Schema: -; Owner: -

COMMENT ON EXTENSION pg_stat_statements IS 'track planning and execution statistics of all SQL statements executed';


-- Name: pg_trgm; Type: EXTENSION; Schema: -; Owner: -

CREATE EXTENSION IF NOT EXISTS pg_trgm WITH SCHEMA public;


-- Name: EXTENSION pg_trgm; Type: COMMENT; Schema: -; Owner: -

COMMENT ON EXTENSION pg_trgm IS 'text similarity measurement and index searching based on trigrams';


-- Name: pgcrypto; Type: EXTENSION; Schema: -; Owner: -

CREATE EXTENSION IF NOT EXISTS pgcrypto WITH SCHEMA public;


-- Name: EXTENSION pgcrypto; Type: COMMENT; Schema: -; Owner: -

COMMENT ON EXTENSION pgcrypto IS 'cryptographic functions';


-- Name: current_tenant(); Type: FUNCTION; Schema: app; Owner: -

CREATE FUNCTION app.current_tenant() RETURNS uuid
    LANGUAGE sql STABLE PARALLEL SAFE
    AS $$
    SELECT nullif(current_setting('app.tenant_id', true), '')::uuid
$$;


-- Name: FUNCTION current_tenant(); Type: COMMENT; Schema: app; Owner: -

COMMENT ON FUNCTION app.current_tenant() IS 'The tenant this session acts for, or NULL when unset. Self-assertable BY DESIGN (§19.2): a session may choose which tenant it acts for, and it is the JWT-bound middleware and the DbConnectionInterceptor that bind the choice to the authenticated principal. A malformed GUC raises on the cast, which is a refusal, not a leak.';


-- Name: ensure_partition_headroom(integer); Type: FUNCTION; Schema: app; Owner: -

CREATE FUNCTION app.ensure_partition_headroom(p_months_ahead integer DEFAULT 3) RETURNS integer
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'app'
    AS $$
DECLARE
    i      integer;
    v_made integer := 0;
BEGIN
    IF p_months_ahead IS NULL OR p_months_ahead < 0 OR p_months_ahead > 24 THEN
        RAISE EXCEPTION 'app.ensure_partition_headroom: months ahead must be 0..24';
    END IF;

    FOR i IN 0 .. p_months_ahead LOOP
        v_made := v_made
            + app.ensure_partition_month((date_trunc('month', now()) + make_interval(months => i))::date);
    END LOOP;

    RETURN v_made;
END
$$;


-- Name: FUNCTION ensure_partition_headroom(p_months_ahead integer); Type: COMMENT; Schema: app; Owner: -

COMMENT ON FUNCTION app.ensure_partition_headroom(p_months_ahead integer) IS 'What background_jobs kind=''PartitionMaintenance'' calls: ensures the current month and the next p_months_ahead exist on all five parents (§19.3). Idempotent.';


-- Name: ensure_partition_month(date); Type: FUNCTION; Schema: app; Owner: -

CREATE FUNCTION app.ensure_partition_month(p_month date) RETURNS integer
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'app'
    AS $$
DECLARE
    r        record;
    v_lo     date := date_trunc('month', p_month)::date;
    v_hi     date := (date_trunc('month', p_month) + interval '1 month')::date;
    v_child  text;
    v_type   text;
    v_from   text;
    v_to     text;
    v_made   integer := 0;
BEGIN
    IF p_month IS NULL THEN
        RAISE EXCEPTION 'app.ensure_partition_month: month is required';
    END IF;

    FOR r IN
        SELECT * FROM (VALUES
            ('attendance_punches',   'occurred_at'),
            ('attendance_days',      'work_date'),
            ('timesheet_entries',    'work_date'),
            ('audit_logs',           'created_at'),
            ('background_job_items', 'created_at')
        ) AS v(tbl, key)
    LOOP
        v_child := format('%s_y%sm%s', r.tbl, to_char(v_lo, 'YYYY'), to_char(v_lo, 'MM'));

        -- already attached? then there is nothing to do and nothing to report.
        IF EXISTS (
            SELECT 1
              FROM pg_inherits i
              JOIN pg_class ch ON ch.oid = i.inhrelid
             WHERE i.inhparent = format('public.%I', r.tbl)::regclass
               AND ch.relname  = v_child)
        THEN
            CONTINUE;
        END IF;

        SELECT a.atttypid::regtype::text INTO v_type
          FROM pg_attribute a
         WHERE a.attrelid = format('public.%I', r.tbl)::regclass
           AND a.attname  = r.key;

        IF v_type = 'timestamp with time zone' THEN
            v_from := to_char(v_lo, 'YYYY-MM-DD') || ' 00:00:00+00';
            v_to   := to_char(v_hi, 'YYYY-MM-DD') || ' 00:00:00+00';
        ELSE
            v_from := to_char(v_lo, 'YYYY-MM-DD');
            v_to   := to_char(v_hi, 'YYYY-MM-DD');
        END IF;

        -- ---- THE TEMPLATE. Three statements. No GRANT. ----
        EXECUTE format(
            'CREATE TABLE public.%I PARTITION OF public.%I FOR VALUES FROM (%L) TO (%L)',
            v_child, r.tbl, v_from, v_to);
        EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY', v_child);
        EXECUTE format('ALTER TABLE public.%I FORCE ROW LEVEL SECURITY', v_child);
        -- ---- end of template ----

        v_made := v_made + 1;
    END LOOP;

    RETURN v_made;
END
$$;


-- Name: FUNCTION ensure_partition_month(p_month date); Type: COMMENT; Schema: app; Owner: -

COMMENT ON FUNCTION app.ensure_partition_month(p_month date) IS 'The fixed partition template of §19.3: creates the month''s child on each of the five partitioned parents, enables and FORCES row level security on it, and issues no GRANT of any kind. The parent list is hard-coded, so the month is the only input. Returns how many children it created.';


-- Name: is_platform(); Type: FUNCTION; Schema: app; Owner: -

CREATE FUNCTION app.is_platform() RETURNS boolean
    LANGUAGE sql STABLE PARALLEL SAFE
    AS $$
    SELECT coalesce(current_setting('app.platform', true), 'off') = 'on'
       AND pg_has_role(current_user, 'kynex_platform', 'MEMBER')
$$;


-- Name: FUNCTION is_platform(); Type: COMMENT; Schema: app; Owner: -

COMMENT ON FUNCTION app.is_platform() IS 'True only when the session has BOTH set app.platform=''on'' AND holds membership in kynex_platform. The GUC can only narrow an authority the role already has; it can never confer one, which is what stops a kynex_app session from promoting itself (§19.2).';


-- Name: partition_headroom(); Type: FUNCTION; Schema: app; Owner: -

CREATE FUNCTION app.partition_headroom() RETURNS TABLE(parent text, months_headroom integer, default_rows bigint)
    LANGUAGE plpgsql STABLE
    AS $_$
DECLARE
    r      record;
    v_max  date;
    v_rows bigint;
BEGIN
    FOR r IN
        SELECT unnest(ARRAY['attendance_punches','attendance_days','timesheet_entries',
                            'audit_logs','background_job_items']) AS tbl
    LOOP
        SELECT max(to_date(substring(ch.relname from 'y(\d{4})m(\d{2})$') ||
                           substring(ch.relname from 'm(\d{2})$'), 'YYYYMM'))
          INTO v_max
          FROM pg_inherits i
          JOIN pg_class ch ON ch.oid = i.inhrelid
         WHERE i.inhparent = format('public.%I', r.tbl)::regclass
           AND ch.relname ~ 'y\d{4}m\d{2}$';

        EXECUTE format('SELECT count(*) FROM public.%I', r.tbl || '_default') INTO v_rows;

        parent          := r.tbl;
        months_headroom := CASE WHEN v_max IS NULL THEN 0 ELSE
            (extract(year from age(v_max, date_trunc('month', now())::date)) * 12
             + extract(month from age(v_max, date_trunc('month', now())::date)))::integer END;
        default_rows    := v_rows;
        RETURN NEXT;
    END LOOP;
END
$_$;


-- Name: FUNCTION partition_headroom(); Type: COMMENT; Schema: app; Owner: -

COMMENT ON FUNCTION app.partition_headroom() IS 'The two §19.3 alert signals: months of pre-created headroom per parent (alert below 2) and rows sitting in the DEFAULT partition (alert on the first one). Not SECURITY DEFINER, so the DEFAULT row count it returns is the caller''s own tenant''s.';


-- Name: resolve_login(text, text); Type: FUNCTION; Schema: app; Owner: -

CREATE FUNCTION app.resolve_login(p_tenant_slug text, p_email text) RETURNS TABLE(user_id uuid, tenant_id uuid, password_hash text, status character varying, lockout_end timestamp with time zone)
    LANGUAGE plpgsql STABLE SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'app'
    AS $$
DECLARE
    v_tenant_id uuid;
BEGIN
    IF p_tenant_slug IS NULL OR p_email IS NULL THEN
        RETURN;
    END IF;

    SELECT t.id INTO v_tenant_id
      FROM public.tenants t
     WHERE t.slug = lower(btrim(p_tenant_slug))
       AND t.status = 'Active'
       AND t.soft_deleted_at IS NULL
       AND t.purged_at IS NULL;

    IF v_tenant_id IS NULL THEN
        RETURN;
    END IF;

    -- Tenant confinement. A session already bound to tenant A may resolve a
    -- login only for tenant A. An unbound session (the login path) may resolve
    -- any tenant, which is unavoidable: the tenant is not known until this call
    -- returns. 42501 rather than an empty result, so the attempt is an error the
    -- application logs rather than a silent miss indistinguishable from a typo.
    IF app.current_tenant() IS NOT NULL AND app.current_tenant() <> v_tenant_id THEN
        RAISE EXCEPTION
            'app.resolve_login: session is bound to another tenant'
            USING ERRCODE = 'insufficient_privilege';
    END IF;

    RETURN QUERY
    SELECT u.id, u.tenant_id, u.password_hash, u.status, u.lockout_end
      FROM public.users u
     WHERE u.tenant_id = v_tenant_id
       AND u.normalized_email = lower(btrim(p_email))
       AND u.deleted_at IS NULL;
END
$$;


-- Name: FUNCTION resolve_login(p_tenant_slug text, p_email text); Type: COMMENT; Schema: app; Owner: -

COMMENT ON FUNCTION app.resolve_login(p_tenant_slug text, p_email text) IS 'Named bypass surface 1 (§19.2): the ONLY way to read a tenant credential before a tenant is known. Keyed on (tenant_slug, email) because email is unique per tenant, not globally. EXECUTE revoked from PUBLIC and granted to kynex_app alone, so kynex_ro cannot obtain password_hash through it. Refuses with 42501 when the session is already bound to a different tenant.';


-- Name: resolve_platform_login(text); Type: FUNCTION; Schema: app; Owner: -

CREATE FUNCTION app.resolve_platform_login(p_email text) RETURNS TABLE(platform_user_id uuid, password_hash text, status character varying, platform_role character varying, lockout_end timestamp with time zone)
    LANGUAGE plpgsql STABLE SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'app'
    AS $$
BEGIN
    IF p_email IS NULL THEN
        RETURN;
    END IF;

    RETURN QUERY
    SELECT p.id, p.password_hash, p.status, p.platform_role, p.lockout_end
      FROM public.platform_users p
     WHERE p.email = lower(btrim(p_email))
       AND p.deleted_at IS NULL;
END
$$;


-- Name: FUNCTION resolve_platform_login(p_email text); Type: COMMENT; Schema: app; Owner: -

COMMENT ON FUNCTION app.resolve_platform_login(p_email text) IS 'Named bypass surface 1, platform twin (§19.2). Keyed on email alone because platform_users.email is globally unique. EXECUTE revoked from PUBLIC; granted to kynex_app only.';


-- Name: fn_append_only(); Type: FUNCTION; Schema: public; Owner: -

CREATE FUNCTION public.fn_append_only() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    RAISE EXCEPTION '% on % is refused: the table is append-only; write a reversing row instead',
        TG_OP, TG_TABLE_NAME USING ERRCODE = 'check_violation';
END $$;


-- Name: fn_audit_logs_append_only(); Type: FUNCTION; Schema: public; Owner: -

CREATE FUNCTION public.fn_audit_logs_append_only() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    erasable CONSTANT text[] := ARRAY['personal_data', 'before', 'after', 'personal_data_erased_at'];
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'DELETE on audit_logs is refused: §12.4 retains audit rows indefinitely, '
            'redacted in place, never deleted' USING ERRCODE = 'check_violation';
    END IF;

    IF OLD.personal_data_erased_at IS NOT NULL THEN
        RAISE EXCEPTION 'audit_logs.% has already been erased at %; it can never be updated again',
            OLD.id, OLD.personal_data_erased_at USING ERRCODE = 'check_violation';
    END IF;

    IF NEW.personal_data_erased_at IS NULL
       OR NEW.personal_data IS NOT NULL
       OR NEW.before IS NOT NULL
       OR NEW.after IS NOT NULL THEN
        RAISE EXCEPTION 'audit_logs is append-only: the only permitted UPDATE nulls personal_data, '
            'before and after and stamps personal_data_erased_at (§12.3)'
            USING ERRCODE = 'check_violation';
    END IF;

    IF (to_jsonb(OLD) - erasable) <> (to_jsonb(NEW) - erasable) THEN
        RAISE EXCEPTION 'audit_logs.% erasure changed a column it may not touch: %',
            OLD.id,
            (SELECT string_agg(key, ', ' ORDER BY key)
               FROM jsonb_each(to_jsonb(OLD) - erasable) o
              WHERE o.value IS DISTINCT FROM (to_jsonb(NEW) - erasable) -> o.key)
            USING ERRCODE = 'check_violation';
    END IF;

    RETURN NEW;
END $$;


-- Name: FUNCTION fn_audit_logs_append_only(); Type: COMMENT; Schema: public; Owner: -

COMMENT ON FUNCTION public.fn_audit_logs_append_only() IS '§12.3 PDPL erasure: the single permitted UPDATE shape on audit_logs. Column-exact by design — a hash comparison would prove nothing, because whoever can UPDATE the row could rewrite the hash with it. envelope_hash and the three digests survive so a verifier can recompute the envelope hash against a Merkle root published before the erasure.';


-- Name: fn_cascade_parent_guard(); Type: FUNCTION; Schema: public; Owner: -

CREATE FUNCTION public.fn_cascade_parent_guard() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    editable text[] := TG_ARGV;          -- the states in which the parent is still a draft
    st       text   := to_jsonb(OLD) ->> 'status';
BEGIN
    IF NOT (st = ANY (editable)) THEN
        RAISE EXCEPTION
            'cannot delete %.%: its state is % and its children are no longer a draft (legal: %)',
            TG_TABLE_NAME, OLD.id, st, array_to_string(editable, ', ')
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN OLD;
END $$;


-- Name: FUNCTION fn_cascade_parent_guard(); Type: COMMENT; Schema: public; Owner: -

COMMENT ON FUNCTION public.fn_cascade_parent_guard() IS '§8.1: a CASCADE may only ever fire on a draft. Generic over any parent with a status column; the editable states are the trigger arguments.';


-- Name: fn_eos_calculation_frozen(); Type: FUNCTION; Schema: public; Owner: -

CREATE FUNCTION public.fn_eos_calculation_frozen() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE before_j jsonb; after_j jsonb;
BEGIN
    IF OLD.status = 'Estimate' THEN
        RETURN CASE TG_OP WHEN 'DELETE' THEN OLD ELSE NEW END;
    END IF;

    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'DELETE on eos_calculations.% is refused: the calculation is %',
            OLD.id, OLD.status USING ERRCODE = 'check_violation';
    END IF;

    -- §10.10: "Final -> Superseded only by a recalculation that writes a new Final row."
    before_j := to_jsonb(OLD) - ARRAY['status', 'updated_at', 'updated_by'];
    after_j  := to_jsonb(NEW) - ARRAY['status', 'updated_at', 'updated_by'];
    IF before_j <> after_j THEN
        RAISE EXCEPTION 'eos_calculations.% is frozen (status %): write a new Final row instead',
            OLD.id, OLD.status USING ERRCODE = 'check_violation';
    END IF;
    IF NOT (OLD.status = 'Final' AND NEW.status = 'Superseded') THEN
        RAISE EXCEPTION 'illegal eos_calculations transition % -> % on %',
            OLD.status, NEW.status, OLD.id USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END $$;


-- Name: fn_gl_balance(); Type: FUNCTION; Schema: public; Owner: -

CREATE FUNCTION public.fn_gl_balance() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    rec jsonb := to_jsonb(CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END);
    v_tenant uuid := (rec ->> 'tenant_id')::uuid;
    v_j uuid; t record;
BEGIN
    v_j := (rec ->> CASE TG_TABLE_NAME WHEN 'gl_journals' THEN 'id' ELSE 'journal_id' END)::uuid;

    IF NOT EXISTS (SELECT 1 FROM gl_journals WHERE tenant_id = v_tenant AND id = v_j) THEN
        RETURN NULL;
    END IF;

    SELECT coalesce(sum(debit), 0) AS d, coalesce(sum(credit), 0) AS c, count(*) AS n INTO t
      FROM gl_journal_lines WHERE tenant_id = v_tenant AND journal_id = v_j;

    -- A journal with no lines yet is legal only while it is Draft.
    IF t.n = 0 THEN
        IF (SELECT status FROM gl_journals WHERE tenant_id = v_tenant AND id = v_j) = 'Draft' THEN
            RETURN NULL;
        END IF;
        RAISE EXCEPTION 'gl_journals.% has no lines but is not Draft', v_j
            USING ERRCODE = 'check_violation';
    END IF;

    IF t.d <> t.c THEN
        RAISE EXCEPTION 'gl_journals.% does not balance: debit % <> credit %', v_j, t.d, t.c
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN NULL;
END $$;


-- Name: fn_gosi_filing_frozen(); Type: FUNCTION; Schema: public; Owner: -

CREATE FUNCTION public.fn_gosi_filing_frozen() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE before_j jsonb; after_j jsonb;
BEGIN
    IF OLD.status = 'Draft' THEN
        RETURN CASE TG_OP WHEN 'DELETE' THEN OLD ELSE NEW END;
    END IF;

    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'DELETE on gosi_filings.% is refused: the filing is %', OLD.id, OLD.status
            USING ERRCODE = 'check_violation';
    END IF;

    -- §10.6 moves a filed return only through status, the variance it explains and
    -- the reconciliation stamp. A corrected return is a NEW revision (§8 uq_gosi_filings__period),
    -- never an edit of the one that was sent.
    before_j := to_jsonb(OLD) - ARRAY['status', 'gosi_invoice_amount', 'variance_amount',
                                      'variance_reason', 'reconciled_at', 'updated_at', 'updated_by'];
    after_j  := to_jsonb(NEW) - ARRAY['status', 'gosi_invoice_amount', 'variance_amount',
                                      'variance_reason', 'reconciled_at', 'updated_at', 'updated_by'];
    IF before_j <> after_j THEN
        RAISE EXCEPTION 'gosi_filings.% is frozen (status %): file a new revision instead',
            OLD.id, OLD.status USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END $$;


-- Name: fn_gosi_filing_totals(); Type: FUNCTION; Schema: public; Owner: -

CREATE FUNCTION public.fn_gosi_filing_totals() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE t record;
BEGIN
    IF NEW.status = 'Draft' THEN RETURN NULL; END IF;
    IF NOT EXISTS (SELECT 1 FROM gosi_filings WHERE tenant_id = NEW.tenant_id AND id = NEW.id) THEN
        RETURN NULL;
    END IF;

    SELECT
      coalesce(sum(l.amount) FILTER (WHERE l.gosi_branch = 'Annuities'          AND l.gosi_payer = 'Employee'), 0) AS ann_e,
      coalesce(sum(l.amount) FILTER (WHERE l.gosi_branch = 'Annuities'          AND l.gosi_payer = 'Employer'), 0) AS ann_r,
      coalesce(sum(l.amount) FILTER (WHERE l.gosi_branch = 'SANED'              AND l.gosi_payer = 'Employee'), 0) AS san_e,
      coalesce(sum(l.amount) FILTER (WHERE l.gosi_branch = 'SANED'              AND l.gosi_payer = 'Employer'), 0) AS san_r,
      coalesce(sum(l.amount) FILTER (WHERE l.gosi_branch = 'OccupationalHazards'), 0)                              AS oh_r
      INTO t
      FROM payroll_slip_lines l
      JOIN payroll_slips s ON s.tenant_id = l.tenant_id AND s.id = l.slip_id
      JOIN payroll_runs  r ON r.tenant_id = s.tenant_id AND r.id = s.run_id
     WHERE l.tenant_id = NEW.tenant_id
       AND r.company_id = NEW.company_id
       AND r.year = NEW.year AND r.month = NEW.month
       AND r.status <> 'Voided'
       AND s.inclusion_status = 'Included'
       AND s.employer_gosi_registration_no = NEW.gosi_registration_no;

    IF (NEW.annuities_employee, NEW.annuities_employer, NEW.saned_employee,
        NEW.saned_employer, NEW.occupational_hazards_employer)
       IS DISTINCT FROM (t.ann_e, t.ann_r, t.san_e, t.san_r, t.oh_r)
       AND NEW.variance_reason IS NULL THEN
        RAISE WARNING
            'gosi_filings.% does not reconcile to the period''s slip lines and records no '
            'variance_reason (filed %/%/%/%/%; slips say %/%/%/%/%)',
            NEW.id, NEW.annuities_employee, NEW.annuities_employer, NEW.saned_employee,
            NEW.saned_employer, NEW.occupational_hazards_employer,
            t.ann_e, t.ann_r, t.san_e, t.san_r, t.oh_r;
    END IF;
    RETURN NULL;
END $$;


-- Name: fn_loan_outstanding(); Type: FUNCTION; Schema: public; Owner: -

CREATE FUNCTION public.fn_loan_outstanding() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    rec jsonb := to_jsonb(CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END);
    v_tenant uuid := (rec ->> 'tenant_id')::uuid;
    v_loan uuid; l record; recovered numeric;
BEGIN
    v_loan := (rec ->> CASE TG_TABLE_NAME WHEN 'loans' THEN 'id' ELSE 'loan_id' END)::uuid;

    SELECT * INTO l FROM loans WHERE tenant_id = v_tenant AND id = v_loan;
    IF NOT FOUND THEN RETURN NULL; END IF;

    SELECT coalesce(sum(amount), 0) INTO recovered
      FROM loan_installments
     WHERE tenant_id = v_tenant AND loan_id = v_loan AND status = 'Recovered';

    IF l.outstanding <> l.principal + l.opening_outstanding - recovered THEN
        RAISE EXCEPTION
            'loans.% outstanding % <> principal % + opening % - recovered %',
            v_loan, l.outstanding, l.principal, l.opening_outstanding, recovered
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN NULL;
END $$;


-- Name: fn_notifications_cascade_guard(); Type: FUNCTION; Schema: public; Owner: -

CREATE FUNCTION public.fn_notifications_cascade_guard() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF EXISTS (SELECT 1 FROM notification_deliveries
                WHERE tenant_id = OLD.tenant_id AND notification_id = OLD.id
                  AND status NOT IN ('Queued', 'Suppressed')) THEN
        RAISE EXCEPTION 'cannot delete notifications.%: a delivery has already left the queue', OLD.id
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN OLD;
END $$;


-- Name: fn_payroll_run_release_claims(); Type: FUNCTION; Schema: public; Owner: -

CREATE FUNCTION public.fn_payroll_run_release_claims() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    UPDATE payroll_inputs
       SET status = 'Pending', claimed_by_run_id = NULL, claimed_at = NULL
     WHERE tenant_id = NEW.tenant_id
       AND claimed_by_run_id = NEW.id
       AND status = 'Claimed';
    RETURN NULL;
END $$;


-- Name: FUNCTION fn_payroll_run_release_claims(); Type: COMMENT; Schema: public; Owner: -

COMMENT ON FUNCTION public.fn_payroll_run_release_claims() IS '§10.1 / §19.5 (ii): the Processing -> Draft transition releases every payroll_inputs row this run holds as Claimed back to Pending, which is the only path that unsticks a crashed run.';


-- Name: fn_payroll_run_transition(); Type: FUNCTION; Schema: public; Owner: -

CREATE FUNCTION public.fn_payroll_run_transition() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    legal      text[];
    n_included bigint;
    n_excluded bigint;
    n_open     bigint;
BEGIN
    IF NEW.run_type IS DISTINCT FROM OLD.run_type THEN
        RAISE EXCEPTION 'payroll_runs.run_type is immutable (% -> %) on run %',
            OLD.run_type, NEW.run_type, OLD.id
            USING ERRCODE = 'check_violation';
    END IF;

    IF NEW.status IS NOT DISTINCT FROM OLD.status THEN
        RETURN NEW;
    END IF;

    -- --- the Opening-run restriction -------------------------------------
    -- §10.1: "an Opening run may only be Draft -> Locked and never Paid."
    -- Read literally, which is how it is enforced: an Opening run that was a
    -- mistake is abandoned by deleting it while it is still Draft (PART 5 allows
    -- exactly that), not by voiding it. ck_payroll_runs__opening_never_paid
    -- already blocks the Paid state itself; this blocks every route to it.
    IF OLD.run_type = 'Opening' THEN
        IF NOT (OLD.status = 'Draft' AND NEW.status = 'Locked') THEN
            RAISE EXCEPTION
                'an Opening run may only move Draft -> Locked; % -> % was attempted on run %',
                OLD.status, NEW.status, OLD.id
                USING ERRCODE = 'check_violation';
        END IF;
    ELSE
        legal := CASE OLD.status
            WHEN 'Draft'                THEN ARRAY['Processing', 'Voided']
            WHEN 'Processing'           THEN ARRAY['Processed', 'Draft']
            WHEN 'Processed'            THEN ARRAY['PendingFinanceReview', 'Approved', 'Draft']
            WHEN 'PendingFinanceReview' THEN ARRAY['Approved', 'Draft']
            WHEN 'Approved'             THEN ARRAY['Locked', 'Draft']
            WHEN 'Locked'               THEN ARRAY['Paid', 'Voided']
            WHEN 'Paid'                 THEN ARRAY['Completed']
            ELSE ARRAY[]::text[]
        END;

        IF NOT (NEW.status = ANY (legal)) THEN
            RAISE EXCEPTION 'illegal payroll run transition % -> % on run % (legal: %)',
                OLD.status, NEW.status, OLD.id,
                coalesce(array_to_string(legal, ', '), '(terminal)')
                USING ERRCODE = 'check_violation';
        END IF;
    END IF;

    -- --- Processing -> Processed: the exit condition (§19.5 (iii)) --------
    -- "slips + explicitly excluded = selected_employee_count", not
    -- "slips = selection": an employee who becomes ineligible mid-run is written
    -- as a slip with inclusion_status='ExcludedBy*' plus a payroll_issues row, so
    -- one ineligible employee cannot wedge the run in Processing forever.
    IF OLD.status = 'Processing' AND NEW.status = 'Processed' THEN
        SELECT count(*) FILTER (WHERE inclusion_status = 'Included'),
               count(*) FILTER (WHERE inclusion_status <> 'Included')
          INTO n_included, n_excluded
          FROM payroll_slips
         WHERE tenant_id = OLD.tenant_id AND run_id = OLD.id;

        IF n_included + n_excluded <> NEW.selected_employee_count THEN
            RAISE EXCEPTION
                'run % cannot leave Processing: % slips + % excluded <> % selected employees',
                OLD.id, n_included, n_excluded, NEW.selected_employee_count
                USING ERRCODE = 'check_violation';
        END IF;
    END IF;

    -- --- Approved -> Draft: only while no WPS batch exists ----------------
    IF OLD.status = 'Approved' AND NEW.status = 'Draft' THEN
        PERFORM 1 FROM wps_batches
          WHERE tenant_id = OLD.tenant_id AND run_id = OLD.id LIMIT 1;
        IF FOUND THEN
            RAISE EXCEPTION 'run % cannot return to Draft: a WPS batch already exists for it', OLD.id
                USING ERRCODE = 'check_violation';
        END IF;
    END IF;

    -- --- Locked -> Voided: no accepted WPS batch, no posted GL journal ----
    IF OLD.status = 'Locked' AND NEW.status = 'Voided' THEN
        PERFORM 1 FROM wps_batches
          WHERE tenant_id = OLD.tenant_id AND run_id = OLD.id
            AND status IN ('Accepted', 'PartiallyRejected') LIMIT 1;
        IF FOUND THEN
            RAISE EXCEPTION 'run % cannot be voided: the bank has accepted a WPS batch for it', OLD.id
                USING ERRCODE = 'check_violation';
        END IF;

        PERFORM 1 FROM gl_journals
          WHERE tenant_id = OLD.tenant_id AND source_type = 'PayrollRun'
            AND source_id = OLD.id AND status = 'Posted' LIMIT 1;
        IF FOUND THEN
            RAISE EXCEPTION 'run % cannot be voided: a GL journal for it is already Posted', OLD.id
                USING ERRCODE = 'check_violation';
        END IF;
    END IF;

    -- --- Paid -> Completed: every bank status terminal --------------------
    IF OLD.status = 'Paid' AND NEW.status = 'Completed' THEN
        SELECT count(*) INTO n_open
          FROM wps_lines l
          JOIN wps_batches b ON b.tenant_id = l.tenant_id AND b.id = l.batch_id
         WHERE l.tenant_id = OLD.tenant_id AND b.run_id = OLD.id
           AND l.bank_status IN ('Pending', 'OnHold');
        IF n_open > 0 THEN
            RAISE EXCEPTION 'run % cannot complete: % WPS lines are still Pending or OnHold',
                OLD.id, n_open USING ERRCODE = 'check_violation';
        END IF;
    END IF;

    -- --- the attendance lock (§11.6: payroll_runs is authoritative) -------
    IF NEW.status = 'Approved' AND NEW.attendance_locked_range IS NULL THEN
        NEW.attendance_locked_range := daterange(
            make_date(NEW.year, NEW.month, 1),
            (make_date(NEW.year, NEW.month, 1) + interval '1 month')::date, '[)');
    ELSIF NEW.status = 'Voided' THEN
        NEW.attendance_locked_range := NULL;
    END IF;

    IF NEW.status = 'Approved' THEN
        NEW.approved_at := coalesce(NEW.approved_at, now());
    ELSIF NEW.status = 'Locked' THEN
        NEW.locked_at := coalesce(NEW.locked_at, now());
    END IF;

    RETURN NEW;
END $$;


-- Name: FUNCTION fn_payroll_run_transition(); Type: COMMENT; Schema: public; Owner: -

COMMENT ON FUNCTION public.fn_payroll_run_transition() IS '§10.1 payroll_runs state machine, enforced in the database because the money path gets a guarantee rather than a service promise.';


-- Name: fn_payroll_slip_frozen(); Type: FUNCTION; Schema: public; Owner: -

CREATE FUNCTION public.fn_payroll_slip_frozen() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    -- One function serves both the parent and the child table, so the row is read
    -- through to_jsonb rather than by field: PL/pgSQL would refuse to compile
    -- NEW.slip_id against a payroll_slips record, whichever branch it sits in.
    rec      jsonb := to_jsonb(CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END);
    v_tenant uuid  := (rec ->> 'tenant_id')::uuid;
    v_slip   uuid;
    v_run    text;
BEGIN
    v_slip := (rec ->> CASE TG_TABLE_NAME WHEN 'payroll_slips' THEN 'id' ELSE 'slip_id' END)::uuid;

    SELECT r.status INTO v_run
      FROM payroll_slips s
      JOIN payroll_runs  r ON r.tenant_id = s.tenant_id AND r.id = s.run_id
     WHERE s.tenant_id = v_tenant AND s.id = v_slip;

    -- The run is being deleted out from under its own draft slips (PART 5 has
    -- already decided whether that delete is legal), so there is nothing to freeze.
    IF v_run IS NULL THEN
        RETURN CASE TG_OP WHEN 'DELETE' THEN OLD ELSE NEW END;
    END IF;

    IF v_run IN ('Approved', 'Locked', 'Paid', 'Completed') THEN
        RAISE EXCEPTION '% on %.% is frozen: its payroll run is %',
            TG_OP, TG_TABLE_NAME, v_slip, v_run
            USING ERRCODE = 'check_violation';
    END IF;

    RETURN CASE TG_OP WHEN 'DELETE' THEN OLD ELSE NEW END;
END $$;


-- Name: FUNCTION fn_payroll_slip_frozen(); Type: COMMENT; Schema: public; Owner: -

COMMENT ON FUNCTION public.fn_payroll_slip_frozen() IS '§10.1: "slips and lines are immutable from Approved onward". Voided is deliberately not in the frozen set — a voided run''s slips are already unreachable, and §12 purges them.';


-- Name: fn_payroll_slips_cascade_guard(); Type: FUNCTION; Schema: public; Owner: -

CREATE FUNCTION public.fn_payroll_slips_cascade_guard() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE v_run text;
BEGIN
    SELECT status INTO v_run FROM payroll_runs WHERE tenant_id = OLD.tenant_id AND id = OLD.run_id;
    IF v_run IS NOT NULL AND v_run NOT IN ('Draft', 'Processing', 'Voided') THEN
        RAISE EXCEPTION 'cannot delete payroll_slips.%: its run is %', OLD.id, v_run
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN OLD;
END $$;


-- Name: fn_row_stamp(); Type: FUNCTION; Schema: public; Owner: -

CREATE FUNCTION public.fn_row_stamp() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    actor uuid := nullif(current_setting('app.user_id', true), '')::uuid;
BEGIN
    IF TG_OP = 'INSERT' THEN
        NEW.created_at := now();
        NEW.created_by := coalesce(actor, NEW.created_by);
        NEW.updated_at := NULL;
        NEW.updated_by := NULL;
    ELSE
        -- created_* is a fact about the insert and can never be rewritten.
        NEW.created_at := OLD.created_at;
        NEW.created_by := OLD.created_by;
        NEW.updated_at := now();
        NEW.updated_by := coalesce(actor, NEW.updated_by);
    END IF;
    RETURN NEW;
END $$;


-- Name: FUNCTION fn_row_stamp(); Type: COMMENT; Schema: public; Owner: -

COMMENT ON FUNCTION public.fn_row_stamp() IS '§1 row stamping. Reads the actor from the app.user_id GUC; an unset GUC leaves whatever the statement supplied, so the baseline and the seeders can run without a session context.';


-- Name: fn_run_totals(); Type: FUNCTION; Schema: public; Owner: -

CREATE FUNCTION public.fn_run_totals() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE t record;
BEGIN
    IF NOT EXISTS (SELECT 1 FROM payroll_runs WHERE tenant_id = NEW.tenant_id AND id = NEW.id) THEN
        RETURN NULL;
    END IF;

    SELECT count(*)                          AS n,
           coalesce(sum(gross), 0)           AS gross,
           coalesce(sum(deductions), 0)      AS deductions,
           coalesce(sum(net), 0)             AS net,
           coalesce(sum(employer_statutory_total), 0) AS employer_stat
      INTO t
      FROM payroll_slips
     WHERE tenant_id = NEW.tenant_id AND run_id = NEW.id
       AND inclusion_status = 'Included';

    IF NEW.employee_count <> t.n
       OR NEW.total_gross <> t.gross
       OR NEW.total_deductions <> t.deductions
       OR NEW.total_net <> t.net
       OR NEW.total_employer_statutory <> t.employer_stat THEN
        RAISE EXCEPTION 'payroll_runs.% totals do not reconcile to its included slips '
            '(stored count/gross/ded/net/empr = %/%/%/%/%; slips say %/%/%/%/%)',
            NEW.id, NEW.employee_count, NEW.total_gross, NEW.total_deductions,
            NEW.total_net, NEW.total_employer_statutory,
            t.n, t.gross, t.deductions, t.net, t.employer_stat
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN NULL;
END $$;


-- Name: fn_settlement_line_frozen(); Type: FUNCTION; Schema: public; Owner: -

CREATE FUNCTION public.fn_settlement_line_frozen() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    rec  jsonb := to_jsonb(CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END);
    v_st text;
BEGIN
    SELECT status INTO v_st FROM final_settlements
     WHERE tenant_id = (rec ->> 'tenant_id')::uuid
       AND id = (rec ->> 'settlement_id')::uuid;

    IF v_st IN ('Approved', 'Paid') THEN
        RAISE EXCEPTION '% on final_settlement_lines is frozen: its settlement is %', TG_OP, v_st
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN CASE TG_OP WHEN 'DELETE' THEN OLD ELSE NEW END;
END $$;


-- Name: FUNCTION fn_settlement_line_frozen(); Type: COMMENT; Schema: public; Owner: -

COMMENT ON FUNCTION public.fn_settlement_line_frozen() IS '§10.10: "Lines freeze at Approved".';


-- Name: fn_settlement_totals(); Type: FUNCTION; Schema: public; Owner: -

CREATE FUNCTION public.fn_settlement_totals() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    rec jsonb := to_jsonb(CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END);
    v_tenant uuid := (rec ->> 'tenant_id')::uuid;
    v_st uuid; s record; t record;
BEGIN
    v_st := (rec ->> CASE TG_TABLE_NAME WHEN 'final_settlements' THEN 'id' ELSE 'settlement_id' END)::uuid;

    SELECT * INTO s FROM final_settlements WHERE tenant_id = v_tenant AND id = v_st;
    IF NOT FOUND THEN RETURN NULL; END IF;

    SELECT coalesce(sum(amount) FILTER (WHERE kind NOT IN ('LoanRecovery', 'OtherDeduction')), 0) AS gross,
           coalesce(sum(amount) FILTER (WHERE kind IN ('LoanRecovery', 'OtherDeduction')), 0)     AS deductions
      INTO t
      FROM final_settlement_lines WHERE tenant_id = v_tenant AND settlement_id = v_st;

    IF s.gross <> t.gross OR s.deductions <> t.deductions OR s.net <> t.gross - t.deductions THEN
        RAISE EXCEPTION 'final_settlements.% totals do not reconcile (stored %/%/%; lines say %/%/%)',
            v_st, s.gross, s.deductions, s.net, t.gross, t.deductions, t.gross - t.deductions
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN NULL;
END $$;


-- Name: fn_slip_totals(); Type: FUNCTION; Schema: public; Owner: -

CREATE FUNCTION public.fn_slip_totals() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    rec jsonb := to_jsonb(CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END);
    v_tenant uuid := (rec ->> 'tenant_id')::uuid;
    v_slip uuid; s record; t record;
BEGIN
    v_slip := (rec ->> CASE TG_TABLE_NAME WHEN 'payroll_slips' THEN 'id' ELSE 'slip_id' END)::uuid;

    SELECT * INTO s FROM payroll_slips WHERE tenant_id = v_tenant AND id = v_slip;
    IF NOT FOUND THEN RETURN NULL; END IF;

    SELECT
        coalesce(sum(l.amount) FILTER (WHERE l.kind = 'Earning'), 0)              AS gross,
        coalesce(sum(l.amount) FILTER (WHERE l.kind = 'Deduction'), 0)            AS deductions,
        coalesce(sum(l.amount) FILTER (WHERE l.kind = 'EmployerContribution'), 0) AS employer_stat,
        coalesce(sum(l.amount) FILTER (WHERE l.kind = 'Deduction'
                     AND l.gosi_branch IS NOT NULL AND l.gosi_payer = 'Employee'), 0) AS employee_stat,
        coalesce(sum(l.amount) FILTER (WHERE l.kind = 'Deduction'
                     AND l.loan_installment_id IS NOT NULL), 0)                   AS loan_deductions,
        coalesce(sum(CASE WHEN l.kind = 'Earning' THEN l.amount
                          WHEN l.kind = 'Deduction' THEN -l.amount ELSE 0 END)
                 FILTER (WHERE pi.kind = 'Arrears'), 0)                           AS arrears
      INTO t
      FROM payroll_slip_lines l
      LEFT JOIN payroll_inputs pi
             ON pi.tenant_id = l.tenant_id AND pi.id = l.payroll_input_id
     WHERE l.tenant_id = v_tenant AND l.slip_id = v_slip;

    IF s.gross <> t.gross OR s.deductions <> t.deductions
       OR s.net <> t.gross - t.deductions
       OR s.employer_statutory_total <> t.employer_stat
       OR s.employee_statutory_total <> t.employee_stat
       OR s.loan_deductions <> t.loan_deductions
       OR s.arrears_amount <> t.arrears THEN
        RAISE EXCEPTION 'payroll_slips.% totals do not reconcile to its lines '
            '(stored gross/ded/net/empr/empe/loan/arrears = %/%/%/%/%/%/%; lines say %/%/%/%/%/%/%)',
            v_slip, s.gross, s.deductions, s.net, s.employer_statutory_total,
            s.employee_statutory_total, s.loan_deductions, s.arrears_amount,
            t.gross, t.deductions, t.gross - t.deductions, t.employer_stat,
            t.employee_stat, t.loan_deductions, t.arrears
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN NULL;
END $$;


-- Name: FUNCTION fn_slip_totals(); Type: COMMENT; Schema: public; Owner: -

COMMENT ON FUNCTION public.fn_slip_totals() IS '§11.2 trg_slip_totals. Four of the seven totals map onto payroll_slip_lines.kind directly. THREE DO NOT, because §11.2 says "the matching kinds" and no line column names them: employee_statutory_total is read as the Deduction lines carrying a gosi_branch with gosi_payer=''Employee''; loan_deductions as the Deduction lines carrying a loan_installment_id; arrears_amount as the signed sum of the lines whose payroll_input is kind=''Arrears''. Those three readings are inferences, and they are the only ones the schema supports.';


-- Name: fn_statutory_rules_cascade_guard(); Type: FUNCTION; Schema: public; Owner: -

CREATE FUNCTION public.fn_statutory_rules_cascade_guard() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF EXISTS (SELECT 1 FROM payroll_slip_lines WHERE statutory_rule_id = OLD.id)
       OR EXISTS (SELECT 1 FROM payroll_slip_lines l JOIN statutory_rule_bands b
                        ON b.id = l.statutory_rule_band_id
                   WHERE b.statutory_rule_id = OLD.id) THEN
        RAISE EXCEPTION 'cannot delete statutory_rules.%: a payroll slip line was calculated from it',
            OLD.id USING ERRCODE = 'check_violation';
    END IF;
    RETURN OLD;
END $$;


-- Name: fn_timesheet_minutes(); Type: FUNCTION; Schema: public; Owner: -

CREATE FUNCTION public.fn_timesheet_minutes() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    rec jsonb := to_jsonb(CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END);
    v_tenant uuid := (rec ->> 'tenant_id')::uuid;
    v_ts uuid; ts record; total bigint;
BEGIN
    v_ts := (rec ->> CASE TG_TABLE_NAME WHEN 'timesheets' THEN 'id' ELSE 'timesheet_id' END)::uuid;

    SELECT * INTO ts FROM timesheets WHERE tenant_id = v_tenant AND id = v_ts;
    IF NOT FOUND THEN RETURN NULL; END IF;

    SELECT coalesce(sum(minutes), 0) INTO total
      FROM timesheet_entries WHERE tenant_id = v_tenant AND timesheet_id = v_ts;

    IF ts.total_minutes <> total THEN
        RAISE EXCEPTION 'timesheets.% total_minutes % <> % summed from its entries',
            v_ts, ts.total_minutes, total USING ERRCODE = 'check_violation';
    END IF;
    RETURN NULL;
END $$;


-- Name: fn_wps_line_frozen(); Type: FUNCTION; Schema: public; Owner: -

CREATE FUNCTION public.fn_wps_line_frozen() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    rec      jsonb := to_jsonb(CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END);
    v_batch  text;
    before_j jsonb;
    after_j  jsonb;
BEGIN
    SELECT status INTO v_batch FROM wps_batches
     WHERE tenant_id = (rec ->> 'tenant_id')::uuid
       AND id = (rec ->> 'batch_id')::uuid;

    IF v_batch IS NULL OR v_batch = 'Generated' THEN
        RETURN CASE TG_OP WHEN 'DELETE' THEN OLD ELSE NEW END;
    END IF;

    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'DELETE on wps_lines.% is frozen: its batch is %', OLD.id, v_batch
            USING ERRCODE = 'check_violation';
    END IF;

    -- §10.5: "Lines freeze at Submitted; only bank_status and the confirmation
    -- columns may change afterwards, and only by the confirmation importer."
    before_j := to_jsonb(OLD) - ARRAY['bank_status', 'bank_reference', 'confirmed_amount',
                                      'reason_code', 'value_date', 'confirmation_job_id'];
    after_j  := to_jsonb(NEW) - ARRAY['bank_status', 'bank_reference', 'confirmed_amount',
                                      'reason_code', 'value_date', 'confirmation_job_id'];
    IF before_j <> after_j THEN
        RAISE EXCEPTION
            'wps_lines.% is frozen (batch is %): only bank_status and the confirmation columns may change',
            OLD.id, v_batch USING ERRCODE = 'check_violation';
    END IF;

    RETURN NEW;
END $$;


-- Name: fn_wps_totals(); Type: FUNCTION; Schema: public; Owner: -

CREATE FUNCTION public.fn_wps_totals() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    rec jsonb := to_jsonb(CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END);
    v_tenant uuid := (rec ->> 'tenant_id')::uuid;
    v_batch uuid; b record; t record;
BEGIN
    v_batch := (rec ->> CASE TG_TABLE_NAME WHEN 'wps_batches' THEN 'id' ELSE 'batch_id' END)::uuid;

    SELECT * INTO b FROM wps_batches WHERE tenant_id = v_tenant AND id = v_batch;
    IF NOT FOUND THEN RETURN NULL; END IF;

    SELECT count(*) AS n, coalesce(sum(net), 0) AS amount INTO t
      FROM wps_lines WHERE tenant_id = v_tenant AND batch_id = v_batch;

    IF b.employee_count <> t.n OR b.total_amount <> t.amount THEN
        RAISE EXCEPTION 'wps_batches.% totals do not reconcile (stored %/%; lines say %/%)',
            v_batch, b.employee_count, b.total_amount, t.n, t.amount
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN NULL;
END $$;




-- Name: rls_manifest; Type: TABLE; Schema: app; Owner: -

CREATE TABLE app.rls_manifest (
    relname text NOT NULL,
    shape text NOT NULL,
    note text,
    CONSTRAINT rls_manifest_shape_check CHECK ((shape = ANY (ARRAY['a'::text, 'b'::text, 'c'::text, 'p_auth'::text, 'platform'::text, 'self_tenant'::text, 'keyring'::text, 'view'::text])))
);

ALTER TABLE ONLY app.rls_manifest FORCE ROW LEVEL SECURITY;


-- Name: TABLE rls_manifest; Type: COMMENT; Schema: app; Owner: -

COMMENT ON TABLE app.rls_manifest IS 'table → declared RLS shape, for the §19.2 ratchet. A table with no entry, or an entry with no table, fails CI by absence.';


-- Name: approval_actions; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.approval_actions (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    request_id uuid NOT NULL,
    actor_user_id uuid NOT NULL,
    on_behalf_of_user_id uuid,
    action character varying(40) NOT NULL,
    step integer NOT NULL,
    comment text,
    acted_at timestamp with time zone DEFAULT now() NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT chk_approval_actions_action CHECK (((action)::text = ANY ((ARRAY['Approve'::character varying, 'Reject'::character varying, 'Return'::character varying, 'Comment'::character varying, 'Escalate'::character varying])::text[])))
);

ALTER TABLE ONLY public.approval_actions FORCE ROW LEVEL SECURITY;


-- Name: TABLE approval_actions; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.approval_actions IS 'Records each approve, reject, return, comment or escalate decision taken on an approval request, including the person it was taken on behalf of. @tier:T @owner:HR @retention:84m-keep';


-- Name: approval_delegations; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.approval_delegations (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    delegator_user_id uuid NOT NULL,
    delegate_user_id uuid NOT NULL,
    effective_from date NOT NULL,
    effective_to date,
    request_types text[] DEFAULT '{}'::text[] NOT NULL,
    reason text,
    is_active boolean DEFAULT true NOT NULL,
    revoked_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT ck_approval_delegations__distinct_parties CHECK ((delegator_user_id <> delegate_user_id)),
    CONSTRAINT ck_approval_delegations__effective_range CHECK (((effective_to IS NULL) OR (effective_to >= effective_from)))
);

ALTER TABLE ONLY public.approval_delegations FORCE ROW LEVEL SECURITY;


-- Name: TABLE approval_delegations; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.approval_delegations IS 'Time-boxes the handover of one user approval authority to another for a named set of request types. @tier:T @owner:HR @retention:84m-keep';


-- Name: approval_requests; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.approval_requests (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    workflow_id uuid NOT NULL,
    requester_user_id uuid NOT NULL,
    employee_id uuid,
    request_type character varying(40) NOT NULL,
    subject_type character varying(40) NOT NULL,
    subject_id uuid NOT NULL,
    status character varying(40) DEFAULT 'Draft'::character varying NOT NULL,
    current_step integer DEFAULT 0 NOT NULL,
    current_approver_user_id uuid,
    current_approver_employee_id uuid,
    payload jsonb,
    workflow_snapshot jsonb NOT NULL,
    due_at timestamp with time zone,
    submitted_at timestamp with time zone,
    decided_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT chk_approval_requests_status CHECK (((status)::text = ANY ((ARRAY['Draft'::character varying, 'Pending'::character varying, 'Approved'::character varying, 'Rejected'::character varying, 'Returned'::character varying, 'Cancelled'::character varying, 'Expired'::character varying])::text[]))),
    CONSTRAINT ck_approval_requests__request_type CHECK (((request_type)::text = ANY ((ARRAY['Leave'::character varying, 'LeaveCancel'::character varying, 'Overtime'::character varying, 'Loan'::character varying, 'Advance'::character varying, 'PayrollRun'::character varying, 'FinalSettlement'::character varying, 'ProfileChange'::character varying, 'Transfer'::character varying, 'SalaryChange'::character varying, 'LetterRequest'::character varying, 'AttendanceCorrection'::character varying, 'Timesheet'::character varying])::text[]))),
    CONSTRAINT ck_approval_requests__subject_type CHECK (((subject_type)::text = ANY ((ARRAY['Leave'::character varying, 'LeaveCancel'::character varying, 'Overtime'::character varying, 'Loan'::character varying, 'Advance'::character varying, 'PayrollRun'::character varying, 'FinalSettlement'::character varying, 'ProfileChange'::character varying, 'Transfer'::character varying, 'SalaryChange'::character varying, 'LetterRequest'::character varying, 'AttendanceCorrection'::character varying, 'Timesheet'::character varying])::text[])))
);

ALTER TABLE ONLY public.approval_requests FORCE ROW LEVEL SECURITY;


-- Name: TABLE approval_requests; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.approval_requests IS 'Is the single authoritative record of whether something was approved, carrying the frozen workflow it runs against, the current step and the current approver the inbox filters on. @tier:T @owner:HR @retention:84m-keep';


-- Name: approval_workflows; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.approval_workflows (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    company_id uuid,
    request_type character varying(40) NOT NULL,
    name text NOT NULL,
    steps jsonb DEFAULT '[]'::jsonb NOT NULL,
    is_active boolean DEFAULT true NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT ck_approval_workflows__request_type CHECK (((request_type)::text = ANY ((ARRAY['Leave'::character varying, 'LeaveCancel'::character varying, 'Overtime'::character varying, 'Loan'::character varying, 'Advance'::character varying, 'PayrollRun'::character varying, 'FinalSettlement'::character varying, 'ProfileChange'::character varying, 'Transfer'::character varying, 'SalaryChange'::character varying, 'LetterRequest'::character varying, 'AttendanceCorrection'::character varying, 'Timesheet'::character varying])::text[])))
);

ALTER TABLE ONLY public.approval_workflows FORCE ROW LEVEL SECURITY;


-- Name: TABLE approval_workflows; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.approval_workflows IS 'Defines the ordered approval steps, approver rules, amount thresholds and service levels for one request type, optionally narrowed to a company. @tier:T @owner:HR @retention:tenant-lifecycle';


-- Name: attendance_days; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.attendance_days (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    shift_id uuid,
    locked_run_id uuid,
    status character varying(40) NOT NULL,
    work_date date NOT NULL,
    first_in timestamp with time zone,
    last_out timestamp with time zone,
    scheduled_minutes integer DEFAULT 0 NOT NULL,
    worked_minutes integer DEFAULT 0 NOT NULL,
    break_minutes integer DEFAULT 0 NOT NULL,
    late_minutes integer DEFAULT 0 NOT NULL,
    early_out_minutes integer DEFAULT 0 NOT NULL,
    overtime_minutes integer DEFAULT 0 NOT NULL,
    absent_minutes integer DEFAULT 0 NOT NULL,
    exceptions jsonb DEFAULT '[]'::jsonb NOT NULL,
    computed_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT chk_attendance_days_status CHECK (((status)::text = ANY ((ARRAY['Present'::character varying, 'Absent'::character varying, 'Leave'::character varying, 'Holiday'::character varying, 'WeeklyOff'::character varying, 'Incomplete'::character varying])::text[]))),
    CONSTRAINT ck_attendance_days__minutes_non_negative CHECK (((scheduled_minutes >= 0) AND (worked_minutes >= 0) AND (break_minutes >= 0) AND (late_minutes >= 0) AND (early_out_minutes >= 0) AND (overtime_minutes >= 0) AND (absent_minutes >= 0)))
)
PARTITION BY RANGE (work_date);

ALTER TABLE ONLY public.attendance_days FORCE ROW LEVEL SECURITY;


-- Name: TABLE attendance_days; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.attendance_days IS 'Holds the computed attendance result for one employee on one local working day in whole minutes, with its exceptions and the payroll run that locked it. @tier:T @owner:HR @retention:24m-purge';


-- Name: attendance_days_default; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.attendance_days_default (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    shift_id uuid,
    locked_run_id uuid,
    status character varying(40) NOT NULL,
    work_date date NOT NULL,
    first_in timestamp with time zone,
    last_out timestamp with time zone,
    scheduled_minutes integer DEFAULT 0 NOT NULL,
    worked_minutes integer DEFAULT 0 NOT NULL,
    break_minutes integer DEFAULT 0 NOT NULL,
    late_minutes integer DEFAULT 0 NOT NULL,
    early_out_minutes integer DEFAULT 0 NOT NULL,
    overtime_minutes integer DEFAULT 0 NOT NULL,
    absent_minutes integer DEFAULT 0 NOT NULL,
    exceptions jsonb DEFAULT '[]'::jsonb NOT NULL,
    computed_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT chk_attendance_days_status CHECK (((status)::text = ANY ((ARRAY['Present'::character varying, 'Absent'::character varying, 'Leave'::character varying, 'Holiday'::character varying, 'WeeklyOff'::character varying, 'Incomplete'::character varying])::text[]))),
    CONSTRAINT ck_attendance_days__minutes_non_negative CHECK (((scheduled_minutes >= 0) AND (worked_minutes >= 0) AND (break_minutes >= 0) AND (late_minutes >= 0) AND (early_out_minutes >= 0) AND (overtime_minutes >= 0) AND (absent_minutes >= 0)))
);

ALTER TABLE ONLY public.attendance_days_default FORCE ROW LEVEL SECURITY;


-- Name: TABLE attendance_days_default; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.attendance_days_default IS 'DEFAULT catch-all (§19.3 rule 3). A row landing here is an incident: the month partition was missing. Alert on the FIRST row — recovery is DETACH CONCURRENTLY, create the month, batched INSERT…SELECT, then an ATTACH that takes ACCESS EXCLUSIVE and a full validation scan.';


-- Name: attendance_days_y2025m10; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.attendance_days_y2025m10 (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    shift_id uuid,
    locked_run_id uuid,
    status character varying(40) NOT NULL,
    work_date date NOT NULL,
    first_in timestamp with time zone,
    last_out timestamp with time zone,
    scheduled_minutes integer DEFAULT 0 NOT NULL,
    worked_minutes integer DEFAULT 0 NOT NULL,
    break_minutes integer DEFAULT 0 NOT NULL,
    late_minutes integer DEFAULT 0 NOT NULL,
    early_out_minutes integer DEFAULT 0 NOT NULL,
    overtime_minutes integer DEFAULT 0 NOT NULL,
    absent_minutes integer DEFAULT 0 NOT NULL,
    exceptions jsonb DEFAULT '[]'::jsonb NOT NULL,
    computed_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT chk_attendance_days_status CHECK (((status)::text = ANY ((ARRAY['Present'::character varying, 'Absent'::character varying, 'Leave'::character varying, 'Holiday'::character varying, 'WeeklyOff'::character varying, 'Incomplete'::character varying])::text[]))),
    CONSTRAINT ck_attendance_days__minutes_non_negative CHECK (((scheduled_minutes >= 0) AND (worked_minutes >= 0) AND (break_minutes >= 0) AND (late_minutes >= 0) AND (early_out_minutes >= 0) AND (overtime_minutes >= 0) AND (absent_minutes >= 0)))
);

ALTER TABLE ONLY public.attendance_days_y2025m10 FORCE ROW LEVEL SECURITY;


-- Name: attendance_days_y2025m11; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.attendance_days_y2025m11 (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    shift_id uuid,
    locked_run_id uuid,
    status character varying(40) NOT NULL,
    work_date date NOT NULL,
    first_in timestamp with time zone,
    last_out timestamp with time zone,
    scheduled_minutes integer DEFAULT 0 NOT NULL,
    worked_minutes integer DEFAULT 0 NOT NULL,
    break_minutes integer DEFAULT 0 NOT NULL,
    late_minutes integer DEFAULT 0 NOT NULL,
    early_out_minutes integer DEFAULT 0 NOT NULL,
    overtime_minutes integer DEFAULT 0 NOT NULL,
    absent_minutes integer DEFAULT 0 NOT NULL,
    exceptions jsonb DEFAULT '[]'::jsonb NOT NULL,
    computed_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT chk_attendance_days_status CHECK (((status)::text = ANY ((ARRAY['Present'::character varying, 'Absent'::character varying, 'Leave'::character varying, 'Holiday'::character varying, 'WeeklyOff'::character varying, 'Incomplete'::character varying])::text[]))),
    CONSTRAINT ck_attendance_days__minutes_non_negative CHECK (((scheduled_minutes >= 0) AND (worked_minutes >= 0) AND (break_minutes >= 0) AND (late_minutes >= 0) AND (early_out_minutes >= 0) AND (overtime_minutes >= 0) AND (absent_minutes >= 0)))
);

ALTER TABLE ONLY public.attendance_days_y2025m11 FORCE ROW LEVEL SECURITY;


-- Name: attendance_days_y2025m12; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.attendance_days_y2025m12 (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    shift_id uuid,
    locked_run_id uuid,
    status character varying(40) NOT NULL,
    work_date date NOT NULL,
    first_in timestamp with time zone,
    last_out timestamp with time zone,
    scheduled_minutes integer DEFAULT 0 NOT NULL,
    worked_minutes integer DEFAULT 0 NOT NULL,
    break_minutes integer DEFAULT 0 NOT NULL,
    late_minutes integer DEFAULT 0 NOT NULL,
    early_out_minutes integer DEFAULT 0 NOT NULL,
    overtime_minutes integer DEFAULT 0 NOT NULL,
    absent_minutes integer DEFAULT 0 NOT NULL,
    exceptions jsonb DEFAULT '[]'::jsonb NOT NULL,
    computed_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT chk_attendance_days_status CHECK (((status)::text = ANY ((ARRAY['Present'::character varying, 'Absent'::character varying, 'Leave'::character varying, 'Holiday'::character varying, 'WeeklyOff'::character varying, 'Incomplete'::character varying])::text[]))),
    CONSTRAINT ck_attendance_days__minutes_non_negative CHECK (((scheduled_minutes >= 0) AND (worked_minutes >= 0) AND (break_minutes >= 0) AND (late_minutes >= 0) AND (early_out_minutes >= 0) AND (overtime_minutes >= 0) AND (absent_minutes >= 0)))
);

ALTER TABLE ONLY public.attendance_days_y2025m12 FORCE ROW LEVEL SECURITY;


-- Name: attendance_days_y2026m01; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.attendance_days_y2026m01 (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    shift_id uuid,
    locked_run_id uuid,
    status character varying(40) NOT NULL,
    work_date date NOT NULL,
    first_in timestamp with time zone,
    last_out timestamp with time zone,
    scheduled_minutes integer DEFAULT 0 NOT NULL,
    worked_minutes integer DEFAULT 0 NOT NULL,
    break_minutes integer DEFAULT 0 NOT NULL,
    late_minutes integer DEFAULT 0 NOT NULL,
    early_out_minutes integer DEFAULT 0 NOT NULL,
    overtime_minutes integer DEFAULT 0 NOT NULL,
    absent_minutes integer DEFAULT 0 NOT NULL,
    exceptions jsonb DEFAULT '[]'::jsonb NOT NULL,
    computed_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT chk_attendance_days_status CHECK (((status)::text = ANY ((ARRAY['Present'::character varying, 'Absent'::character varying, 'Leave'::character varying, 'Holiday'::character varying, 'WeeklyOff'::character varying, 'Incomplete'::character varying])::text[]))),
    CONSTRAINT ck_attendance_days__minutes_non_negative CHECK (((scheduled_minutes >= 0) AND (worked_minutes >= 0) AND (break_minutes >= 0) AND (late_minutes >= 0) AND (early_out_minutes >= 0) AND (overtime_minutes >= 0) AND (absent_minutes >= 0)))
);

ALTER TABLE ONLY public.attendance_days_y2026m01 FORCE ROW LEVEL SECURITY;


-- Name: attendance_days_y2026m02; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.attendance_days_y2026m02 (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    shift_id uuid,
    locked_run_id uuid,
    status character varying(40) NOT NULL,
    work_date date NOT NULL,
    first_in timestamp with time zone,
    last_out timestamp with time zone,
    scheduled_minutes integer DEFAULT 0 NOT NULL,
    worked_minutes integer DEFAULT 0 NOT NULL,
    break_minutes integer DEFAULT 0 NOT NULL,
    late_minutes integer DEFAULT 0 NOT NULL,
    early_out_minutes integer DEFAULT 0 NOT NULL,
    overtime_minutes integer DEFAULT 0 NOT NULL,
    absent_minutes integer DEFAULT 0 NOT NULL,
    exceptions jsonb DEFAULT '[]'::jsonb NOT NULL,
    computed_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT chk_attendance_days_status CHECK (((status)::text = ANY ((ARRAY['Present'::character varying, 'Absent'::character varying, 'Leave'::character varying, 'Holiday'::character varying, 'WeeklyOff'::character varying, 'Incomplete'::character varying])::text[]))),
    CONSTRAINT ck_attendance_days__minutes_non_negative CHECK (((scheduled_minutes >= 0) AND (worked_minutes >= 0) AND (break_minutes >= 0) AND (late_minutes >= 0) AND (early_out_minutes >= 0) AND (overtime_minutes >= 0) AND (absent_minutes >= 0)))
);

ALTER TABLE ONLY public.attendance_days_y2026m02 FORCE ROW LEVEL SECURITY;


-- Name: attendance_days_y2026m03; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.attendance_days_y2026m03 (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    shift_id uuid,
    locked_run_id uuid,
    status character varying(40) NOT NULL,
    work_date date NOT NULL,
    first_in timestamp with time zone,
    last_out timestamp with time zone,
    scheduled_minutes integer DEFAULT 0 NOT NULL,
    worked_minutes integer DEFAULT 0 NOT NULL,
    break_minutes integer DEFAULT 0 NOT NULL,
    late_minutes integer DEFAULT 0 NOT NULL,
    early_out_minutes integer DEFAULT 0 NOT NULL,
    overtime_minutes integer DEFAULT 0 NOT NULL,
    absent_minutes integer DEFAULT 0 NOT NULL,
    exceptions jsonb DEFAULT '[]'::jsonb NOT NULL,
    computed_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT chk_attendance_days_status CHECK (((status)::text = ANY ((ARRAY['Present'::character varying, 'Absent'::character varying, 'Leave'::character varying, 'Holiday'::character varying, 'WeeklyOff'::character varying, 'Incomplete'::character varying])::text[]))),
    CONSTRAINT ck_attendance_days__minutes_non_negative CHECK (((scheduled_minutes >= 0) AND (worked_minutes >= 0) AND (break_minutes >= 0) AND (late_minutes >= 0) AND (early_out_minutes >= 0) AND (overtime_minutes >= 0) AND (absent_minutes >= 0)))
);

ALTER TABLE ONLY public.attendance_days_y2026m03 FORCE ROW LEVEL SECURITY;


-- Name: attendance_days_y2026m04; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.attendance_days_y2026m04 (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    shift_id uuid,
    locked_run_id uuid,
    status character varying(40) NOT NULL,
    work_date date NOT NULL,
    first_in timestamp with time zone,
    last_out timestamp with time zone,
    scheduled_minutes integer DEFAULT 0 NOT NULL,
    worked_minutes integer DEFAULT 0 NOT NULL,
    break_minutes integer DEFAULT 0 NOT NULL,
    late_minutes integer DEFAULT 0 NOT NULL,
    early_out_minutes integer DEFAULT 0 NOT NULL,
    overtime_minutes integer DEFAULT 0 NOT NULL,
    absent_minutes integer DEFAULT 0 NOT NULL,
    exceptions jsonb DEFAULT '[]'::jsonb NOT NULL,
    computed_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT chk_attendance_days_status CHECK (((status)::text = ANY ((ARRAY['Present'::character varying, 'Absent'::character varying, 'Leave'::character varying, 'Holiday'::character varying, 'WeeklyOff'::character varying, 'Incomplete'::character varying])::text[]))),
    CONSTRAINT ck_attendance_days__minutes_non_negative CHECK (((scheduled_minutes >= 0) AND (worked_minutes >= 0) AND (break_minutes >= 0) AND (late_minutes >= 0) AND (early_out_minutes >= 0) AND (overtime_minutes >= 0) AND (absent_minutes >= 0)))
);

ALTER TABLE ONLY public.attendance_days_y2026m04 FORCE ROW LEVEL SECURITY;


-- Name: attendance_days_y2026m05; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.attendance_days_y2026m05 (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    shift_id uuid,
    locked_run_id uuid,
    status character varying(40) NOT NULL,
    work_date date NOT NULL,
    first_in timestamp with time zone,
    last_out timestamp with time zone,
    scheduled_minutes integer DEFAULT 0 NOT NULL,
    worked_minutes integer DEFAULT 0 NOT NULL,
    break_minutes integer DEFAULT 0 NOT NULL,
    late_minutes integer DEFAULT 0 NOT NULL,
    early_out_minutes integer DEFAULT 0 NOT NULL,
    overtime_minutes integer DEFAULT 0 NOT NULL,
    absent_minutes integer DEFAULT 0 NOT NULL,
    exceptions jsonb DEFAULT '[]'::jsonb NOT NULL,
    computed_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT chk_attendance_days_status CHECK (((status)::text = ANY ((ARRAY['Present'::character varying, 'Absent'::character varying, 'Leave'::character varying, 'Holiday'::character varying, 'WeeklyOff'::character varying, 'Incomplete'::character varying])::text[]))),
    CONSTRAINT ck_attendance_days__minutes_non_negative CHECK (((scheduled_minutes >= 0) AND (worked_minutes >= 0) AND (break_minutes >= 0) AND (late_minutes >= 0) AND (early_out_minutes >= 0) AND (overtime_minutes >= 0) AND (absent_minutes >= 0)))
);

ALTER TABLE ONLY public.attendance_days_y2026m05 FORCE ROW LEVEL SECURITY;


-- Name: attendance_days_y2026m06; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.attendance_days_y2026m06 (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    shift_id uuid,
    locked_run_id uuid,
    status character varying(40) NOT NULL,
    work_date date NOT NULL,
    first_in timestamp with time zone,
    last_out timestamp with time zone,
    scheduled_minutes integer DEFAULT 0 NOT NULL,
    worked_minutes integer DEFAULT 0 NOT NULL,
    break_minutes integer DEFAULT 0 NOT NULL,
    late_minutes integer DEFAULT 0 NOT NULL,
    early_out_minutes integer DEFAULT 0 NOT NULL,
    overtime_minutes integer DEFAULT 0 NOT NULL,
    absent_minutes integer DEFAULT 0 NOT NULL,
    exceptions jsonb DEFAULT '[]'::jsonb NOT NULL,
    computed_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT chk_attendance_days_status CHECK (((status)::text = ANY ((ARRAY['Present'::character varying, 'Absent'::character varying, 'Leave'::character varying, 'Holiday'::character varying, 'WeeklyOff'::character varying, 'Incomplete'::character varying])::text[]))),
    CONSTRAINT ck_attendance_days__minutes_non_negative CHECK (((scheduled_minutes >= 0) AND (worked_minutes >= 0) AND (break_minutes >= 0) AND (late_minutes >= 0) AND (early_out_minutes >= 0) AND (overtime_minutes >= 0) AND (absent_minutes >= 0)))
);

ALTER TABLE ONLY public.attendance_days_y2026m06 FORCE ROW LEVEL SECURITY;


-- Name: attendance_days_y2026m07; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.attendance_days_y2026m07 (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    shift_id uuid,
    locked_run_id uuid,
    status character varying(40) NOT NULL,
    work_date date NOT NULL,
    first_in timestamp with time zone,
    last_out timestamp with time zone,
    scheduled_minutes integer DEFAULT 0 NOT NULL,
    worked_minutes integer DEFAULT 0 NOT NULL,
    break_minutes integer DEFAULT 0 NOT NULL,
    late_minutes integer DEFAULT 0 NOT NULL,
    early_out_minutes integer DEFAULT 0 NOT NULL,
    overtime_minutes integer DEFAULT 0 NOT NULL,
    absent_minutes integer DEFAULT 0 NOT NULL,
    exceptions jsonb DEFAULT '[]'::jsonb NOT NULL,
    computed_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT chk_attendance_days_status CHECK (((status)::text = ANY ((ARRAY['Present'::character varying, 'Absent'::character varying, 'Leave'::character varying, 'Holiday'::character varying, 'WeeklyOff'::character varying, 'Incomplete'::character varying])::text[]))),
    CONSTRAINT ck_attendance_days__minutes_non_negative CHECK (((scheduled_minutes >= 0) AND (worked_minutes >= 0) AND (break_minutes >= 0) AND (late_minutes >= 0) AND (early_out_minutes >= 0) AND (overtime_minutes >= 0) AND (absent_minutes >= 0)))
);

ALTER TABLE ONLY public.attendance_days_y2026m07 FORCE ROW LEVEL SECURITY;


-- Name: attendance_days_y2026m08; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.attendance_days_y2026m08 (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    shift_id uuid,
    locked_run_id uuid,
    status character varying(40) NOT NULL,
    work_date date NOT NULL,
    first_in timestamp with time zone,
    last_out timestamp with time zone,
    scheduled_minutes integer DEFAULT 0 NOT NULL,
    worked_minutes integer DEFAULT 0 NOT NULL,
    break_minutes integer DEFAULT 0 NOT NULL,
    late_minutes integer DEFAULT 0 NOT NULL,
    early_out_minutes integer DEFAULT 0 NOT NULL,
    overtime_minutes integer DEFAULT 0 NOT NULL,
    absent_minutes integer DEFAULT 0 NOT NULL,
    exceptions jsonb DEFAULT '[]'::jsonb NOT NULL,
    computed_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT chk_attendance_days_status CHECK (((status)::text = ANY ((ARRAY['Present'::character varying, 'Absent'::character varying, 'Leave'::character varying, 'Holiday'::character varying, 'WeeklyOff'::character varying, 'Incomplete'::character varying])::text[]))),
    CONSTRAINT ck_attendance_days__minutes_non_negative CHECK (((scheduled_minutes >= 0) AND (worked_minutes >= 0) AND (break_minutes >= 0) AND (late_minutes >= 0) AND (early_out_minutes >= 0) AND (overtime_minutes >= 0) AND (absent_minutes >= 0)))
);

ALTER TABLE ONLY public.attendance_days_y2026m08 FORCE ROW LEVEL SECURITY;


-- Name: attendance_days_y2026m09; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.attendance_days_y2026m09 (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    shift_id uuid,
    locked_run_id uuid,
    status character varying(40) NOT NULL,
    work_date date NOT NULL,
    first_in timestamp with time zone,
    last_out timestamp with time zone,
    scheduled_minutes integer DEFAULT 0 NOT NULL,
    worked_minutes integer DEFAULT 0 NOT NULL,
    break_minutes integer DEFAULT 0 NOT NULL,
    late_minutes integer DEFAULT 0 NOT NULL,
    early_out_minutes integer DEFAULT 0 NOT NULL,
    overtime_minutes integer DEFAULT 0 NOT NULL,
    absent_minutes integer DEFAULT 0 NOT NULL,
    exceptions jsonb DEFAULT '[]'::jsonb NOT NULL,
    computed_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT chk_attendance_days_status CHECK (((status)::text = ANY ((ARRAY['Present'::character varying, 'Absent'::character varying, 'Leave'::character varying, 'Holiday'::character varying, 'WeeklyOff'::character varying, 'Incomplete'::character varying])::text[]))),
    CONSTRAINT ck_attendance_days__minutes_non_negative CHECK (((scheduled_minutes >= 0) AND (worked_minutes >= 0) AND (break_minutes >= 0) AND (late_minutes >= 0) AND (early_out_minutes >= 0) AND (overtime_minutes >= 0) AND (absent_minutes >= 0)))
);

ALTER TABLE ONLY public.attendance_days_y2026m09 FORCE ROW LEVEL SECURITY;


-- Name: attendance_days_y2026m10; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.attendance_days_y2026m10 (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    shift_id uuid,
    locked_run_id uuid,
    status character varying(40) NOT NULL,
    work_date date NOT NULL,
    first_in timestamp with time zone,
    last_out timestamp with time zone,
    scheduled_minutes integer DEFAULT 0 NOT NULL,
    worked_minutes integer DEFAULT 0 NOT NULL,
    break_minutes integer DEFAULT 0 NOT NULL,
    late_minutes integer DEFAULT 0 NOT NULL,
    early_out_minutes integer DEFAULT 0 NOT NULL,
    overtime_minutes integer DEFAULT 0 NOT NULL,
    absent_minutes integer DEFAULT 0 NOT NULL,
    exceptions jsonb DEFAULT '[]'::jsonb NOT NULL,
    computed_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT chk_attendance_days_status CHECK (((status)::text = ANY ((ARRAY['Present'::character varying, 'Absent'::character varying, 'Leave'::character varying, 'Holiday'::character varying, 'WeeklyOff'::character varying, 'Incomplete'::character varying])::text[]))),
    CONSTRAINT ck_attendance_days__minutes_non_negative CHECK (((scheduled_minutes >= 0) AND (worked_minutes >= 0) AND (break_minutes >= 0) AND (late_minutes >= 0) AND (early_out_minutes >= 0) AND (overtime_minutes >= 0) AND (absent_minutes >= 0)))
);

ALTER TABLE ONLY public.attendance_days_y2026m10 FORCE ROW LEVEL SECURITY;


-- Name: attendance_days_y2026m11; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.attendance_days_y2026m11 (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    shift_id uuid,
    locked_run_id uuid,
    status character varying(40) NOT NULL,
    work_date date NOT NULL,
    first_in timestamp with time zone,
    last_out timestamp with time zone,
    scheduled_minutes integer DEFAULT 0 NOT NULL,
    worked_minutes integer DEFAULT 0 NOT NULL,
    break_minutes integer DEFAULT 0 NOT NULL,
    late_minutes integer DEFAULT 0 NOT NULL,
    early_out_minutes integer DEFAULT 0 NOT NULL,
    overtime_minutes integer DEFAULT 0 NOT NULL,
    absent_minutes integer DEFAULT 0 NOT NULL,
    exceptions jsonb DEFAULT '[]'::jsonb NOT NULL,
    computed_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT chk_attendance_days_status CHECK (((status)::text = ANY ((ARRAY['Present'::character varying, 'Absent'::character varying, 'Leave'::character varying, 'Holiday'::character varying, 'WeeklyOff'::character varying, 'Incomplete'::character varying])::text[]))),
    CONSTRAINT ck_attendance_days__minutes_non_negative CHECK (((scheduled_minutes >= 0) AND (worked_minutes >= 0) AND (break_minutes >= 0) AND (late_minutes >= 0) AND (early_out_minutes >= 0) AND (overtime_minutes >= 0) AND (absent_minutes >= 0)))
);

ALTER TABLE ONLY public.attendance_days_y2026m11 FORCE ROW LEVEL SECURITY;


-- Name: attendance_days_y2026m12; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.attendance_days_y2026m12 (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    shift_id uuid,
    locked_run_id uuid,
    status character varying(40) NOT NULL,
    work_date date NOT NULL,
    first_in timestamp with time zone,
    last_out timestamp with time zone,
    scheduled_minutes integer DEFAULT 0 NOT NULL,
    worked_minutes integer DEFAULT 0 NOT NULL,
    break_minutes integer DEFAULT 0 NOT NULL,
    late_minutes integer DEFAULT 0 NOT NULL,
    early_out_minutes integer DEFAULT 0 NOT NULL,
    overtime_minutes integer DEFAULT 0 NOT NULL,
    absent_minutes integer DEFAULT 0 NOT NULL,
    exceptions jsonb DEFAULT '[]'::jsonb NOT NULL,
    computed_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT chk_attendance_days_status CHECK (((status)::text = ANY ((ARRAY['Present'::character varying, 'Absent'::character varying, 'Leave'::character varying, 'Holiday'::character varying, 'WeeklyOff'::character varying, 'Incomplete'::character varying])::text[]))),
    CONSTRAINT ck_attendance_days__minutes_non_negative CHECK (((scheduled_minutes >= 0) AND (worked_minutes >= 0) AND (break_minutes >= 0) AND (late_minutes >= 0) AND (early_out_minutes >= 0) AND (overtime_minutes >= 0) AND (absent_minutes >= 0)))
);

ALTER TABLE ONLY public.attendance_days_y2026m12 FORCE ROW LEVEL SECURITY;


-- Name: attendance_devices; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.attendance_devices (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    branch_id uuid NOT NULL,
    serial character varying(64) NOT NULL,
    name text,
    model character varying(64),
    api_key_hash text NOT NULL,
    is_active boolean DEFAULT true NOT NULL,
    last_seen_at timestamp with time zone,
    sync_watermark timestamp with time zone,
    recent_nonces jsonb DEFAULT '[]'::jsonb NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid
);

ALTER TABLE ONLY public.attendance_devices FORCE ROW LEVEL SECURITY;


-- Name: TABLE attendance_devices; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.attendance_devices IS 'Registers a biometric or terminal device at a branch with its API credential hash, sync watermark and replay-nonce window. @tier:C @owner:HR @retention:tenant-lifecycle';


-- Name: attendance_punches; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.attendance_punches (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    device_id uuid,
    approval_request_id uuid,
    direction character varying(40) NOT NULL,
    source character varying(40) NOT NULL,
    occurred_at timestamp with time zone NOT NULL,
    latitude numeric(9,6),
    longitude numeric(9,6),
    external_id character varying(64),
    idempotency_key text NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    CONSTRAINT ck_attendance_punches__direction CHECK (((direction)::text = ANY ((ARRAY['In'::character varying, 'Out'::character varying])::text[]))),
    CONSTRAINT ck_attendance_punches__source CHECK (((source)::text = ANY ((ARRAY['Device'::character varying, 'Mobile'::character varying, 'Import'::character varying, 'Correction'::character varying])::text[])))
)
PARTITION BY RANGE (occurred_at);

ALTER TABLE ONLY public.attendance_punches FORCE ROW LEVEL SECURITY;


-- Name: TABLE attendance_punches; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.attendance_punches IS 'Stores every raw clock event exactly as received from a device, mobile app, import or correction, written once and never edited. @tier:T @owner:HR @retention:24m-purge';


-- Name: attendance_punches_default; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.attendance_punches_default (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    device_id uuid,
    approval_request_id uuid,
    direction character varying(40) NOT NULL,
    source character varying(40) NOT NULL,
    occurred_at timestamp with time zone NOT NULL,
    latitude numeric(9,6),
    longitude numeric(9,6),
    external_id character varying(64),
    idempotency_key text NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    CONSTRAINT ck_attendance_punches__direction CHECK (((direction)::text = ANY ((ARRAY['In'::character varying, 'Out'::character varying])::text[]))),
    CONSTRAINT ck_attendance_punches__source CHECK (((source)::text = ANY ((ARRAY['Device'::character varying, 'Mobile'::character varying, 'Import'::character varying, 'Correction'::character varying])::text[])))
);

ALTER TABLE ONLY public.attendance_punches_default FORCE ROW LEVEL SECURITY;


-- Name: TABLE attendance_punches_default; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.attendance_punches_default IS 'DEFAULT catch-all (§19.3 rule 3). A row landing here is an incident: the month partition was missing. Alert on the FIRST row — recovery is DETACH CONCURRENTLY, create the month, batched INSERT…SELECT, then an ATTACH that takes ACCESS EXCLUSIVE and a full validation scan.';


-- Name: attendance_punches_y2025m10; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.attendance_punches_y2025m10 (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    device_id uuid,
    approval_request_id uuid,
    direction character varying(40) NOT NULL,
    source character varying(40) NOT NULL,
    occurred_at timestamp with time zone NOT NULL,
    latitude numeric(9,6),
    longitude numeric(9,6),
    external_id character varying(64),
    idempotency_key text NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    CONSTRAINT ck_attendance_punches__direction CHECK (((direction)::text = ANY ((ARRAY['In'::character varying, 'Out'::character varying])::text[]))),
    CONSTRAINT ck_attendance_punches__source CHECK (((source)::text = ANY ((ARRAY['Device'::character varying, 'Mobile'::character varying, 'Import'::character varying, 'Correction'::character varying])::text[])))
);

ALTER TABLE ONLY public.attendance_punches_y2025m10 FORCE ROW LEVEL SECURITY;


-- Name: attendance_punches_y2025m11; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.attendance_punches_y2025m11 (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    device_id uuid,
    approval_request_id uuid,
    direction character varying(40) NOT NULL,
    source character varying(40) NOT NULL,
    occurred_at timestamp with time zone NOT NULL,
    latitude numeric(9,6),
    longitude numeric(9,6),
    external_id character varying(64),
    idempotency_key text NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    CONSTRAINT ck_attendance_punches__direction CHECK (((direction)::text = ANY ((ARRAY['In'::character varying, 'Out'::character varying])::text[]))),
    CONSTRAINT ck_attendance_punches__source CHECK (((source)::text = ANY ((ARRAY['Device'::character varying, 'Mobile'::character varying, 'Import'::character varying, 'Correction'::character varying])::text[])))
);

ALTER TABLE ONLY public.attendance_punches_y2025m11 FORCE ROW LEVEL SECURITY;


-- Name: attendance_punches_y2025m12; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.attendance_punches_y2025m12 (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    device_id uuid,
    approval_request_id uuid,
    direction character varying(40) NOT NULL,
    source character varying(40) NOT NULL,
    occurred_at timestamp with time zone NOT NULL,
    latitude numeric(9,6),
    longitude numeric(9,6),
    external_id character varying(64),
    idempotency_key text NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    CONSTRAINT ck_attendance_punches__direction CHECK (((direction)::text = ANY ((ARRAY['In'::character varying, 'Out'::character varying])::text[]))),
    CONSTRAINT ck_attendance_punches__source CHECK (((source)::text = ANY ((ARRAY['Device'::character varying, 'Mobile'::character varying, 'Import'::character varying, 'Correction'::character varying])::text[])))
);

ALTER TABLE ONLY public.attendance_punches_y2025m12 FORCE ROW LEVEL SECURITY;


-- Name: attendance_punches_y2026m01; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.attendance_punches_y2026m01 (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    device_id uuid,
    approval_request_id uuid,
    direction character varying(40) NOT NULL,
    source character varying(40) NOT NULL,
    occurred_at timestamp with time zone NOT NULL,
    latitude numeric(9,6),
    longitude numeric(9,6),
    external_id character varying(64),
    idempotency_key text NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    CONSTRAINT ck_attendance_punches__direction CHECK (((direction)::text = ANY ((ARRAY['In'::character varying, 'Out'::character varying])::text[]))),
    CONSTRAINT ck_attendance_punches__source CHECK (((source)::text = ANY ((ARRAY['Device'::character varying, 'Mobile'::character varying, 'Import'::character varying, 'Correction'::character varying])::text[])))
);

ALTER TABLE ONLY public.attendance_punches_y2026m01 FORCE ROW LEVEL SECURITY;


-- Name: attendance_punches_y2026m02; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.attendance_punches_y2026m02 (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    device_id uuid,
    approval_request_id uuid,
    direction character varying(40) NOT NULL,
    source character varying(40) NOT NULL,
    occurred_at timestamp with time zone NOT NULL,
    latitude numeric(9,6),
    longitude numeric(9,6),
    external_id character varying(64),
    idempotency_key text NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    CONSTRAINT ck_attendance_punches__direction CHECK (((direction)::text = ANY ((ARRAY['In'::character varying, 'Out'::character varying])::text[]))),
    CONSTRAINT ck_attendance_punches__source CHECK (((source)::text = ANY ((ARRAY['Device'::character varying, 'Mobile'::character varying, 'Import'::character varying, 'Correction'::character varying])::text[])))
);

ALTER TABLE ONLY public.attendance_punches_y2026m02 FORCE ROW LEVEL SECURITY;


-- Name: attendance_punches_y2026m03; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.attendance_punches_y2026m03 (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    device_id uuid,
    approval_request_id uuid,
    direction character varying(40) NOT NULL,
    source character varying(40) NOT NULL,
    occurred_at timestamp with time zone NOT NULL,
    latitude numeric(9,6),
    longitude numeric(9,6),
    external_id character varying(64),
    idempotency_key text NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    CONSTRAINT ck_attendance_punches__direction CHECK (((direction)::text = ANY ((ARRAY['In'::character varying, 'Out'::character varying])::text[]))),
    CONSTRAINT ck_attendance_punches__source CHECK (((source)::text = ANY ((ARRAY['Device'::character varying, 'Mobile'::character varying, 'Import'::character varying, 'Correction'::character varying])::text[])))
);

ALTER TABLE ONLY public.attendance_punches_y2026m03 FORCE ROW LEVEL SECURITY;


-- Name: attendance_punches_y2026m04; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.attendance_punches_y2026m04 (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    device_id uuid,
    approval_request_id uuid,
    direction character varying(40) NOT NULL,
    source character varying(40) NOT NULL,
    occurred_at timestamp with time zone NOT NULL,
    latitude numeric(9,6),
    longitude numeric(9,6),
    external_id character varying(64),
    idempotency_key text NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    CONSTRAINT ck_attendance_punches__direction CHECK (((direction)::text = ANY ((ARRAY['In'::character varying, 'Out'::character varying])::text[]))),
    CONSTRAINT ck_attendance_punches__source CHECK (((source)::text = ANY ((ARRAY['Device'::character varying, 'Mobile'::character varying, 'Import'::character varying, 'Correction'::character varying])::text[])))
);

ALTER TABLE ONLY public.attendance_punches_y2026m04 FORCE ROW LEVEL SECURITY;


-- Name: attendance_punches_y2026m05; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.attendance_punches_y2026m05 (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    device_id uuid,
    approval_request_id uuid,
    direction character varying(40) NOT NULL,
    source character varying(40) NOT NULL,
    occurred_at timestamp with time zone NOT NULL,
    latitude numeric(9,6),
    longitude numeric(9,6),
    external_id character varying(64),
    idempotency_key text NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    CONSTRAINT ck_attendance_punches__direction CHECK (((direction)::text = ANY ((ARRAY['In'::character varying, 'Out'::character varying])::text[]))),
    CONSTRAINT ck_attendance_punches__source CHECK (((source)::text = ANY ((ARRAY['Device'::character varying, 'Mobile'::character varying, 'Import'::character varying, 'Correction'::character varying])::text[])))
);

ALTER TABLE ONLY public.attendance_punches_y2026m05 FORCE ROW LEVEL SECURITY;


-- Name: attendance_punches_y2026m06; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.attendance_punches_y2026m06 (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    device_id uuid,
    approval_request_id uuid,
    direction character varying(40) NOT NULL,
    source character varying(40) NOT NULL,
    occurred_at timestamp with time zone NOT NULL,
    latitude numeric(9,6),
    longitude numeric(9,6),
    external_id character varying(64),
    idempotency_key text NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    CONSTRAINT ck_attendance_punches__direction CHECK (((direction)::text = ANY ((ARRAY['In'::character varying, 'Out'::character varying])::text[]))),
    CONSTRAINT ck_attendance_punches__source CHECK (((source)::text = ANY ((ARRAY['Device'::character varying, 'Mobile'::character varying, 'Import'::character varying, 'Correction'::character varying])::text[])))
);

ALTER TABLE ONLY public.attendance_punches_y2026m06 FORCE ROW LEVEL SECURITY;


-- Name: attendance_punches_y2026m07; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.attendance_punches_y2026m07 (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    device_id uuid,
    approval_request_id uuid,
    direction character varying(40) NOT NULL,
    source character varying(40) NOT NULL,
    occurred_at timestamp with time zone NOT NULL,
    latitude numeric(9,6),
    longitude numeric(9,6),
    external_id character varying(64),
    idempotency_key text NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    CONSTRAINT ck_attendance_punches__direction CHECK (((direction)::text = ANY ((ARRAY['In'::character varying, 'Out'::character varying])::text[]))),
    CONSTRAINT ck_attendance_punches__source CHECK (((source)::text = ANY ((ARRAY['Device'::character varying, 'Mobile'::character varying, 'Import'::character varying, 'Correction'::character varying])::text[])))
);

ALTER TABLE ONLY public.attendance_punches_y2026m07 FORCE ROW LEVEL SECURITY;


-- Name: attendance_punches_y2026m08; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.attendance_punches_y2026m08 (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    device_id uuid,
    approval_request_id uuid,
    direction character varying(40) NOT NULL,
    source character varying(40) NOT NULL,
    occurred_at timestamp with time zone NOT NULL,
    latitude numeric(9,6),
    longitude numeric(9,6),
    external_id character varying(64),
    idempotency_key text NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    CONSTRAINT ck_attendance_punches__direction CHECK (((direction)::text = ANY ((ARRAY['In'::character varying, 'Out'::character varying])::text[]))),
    CONSTRAINT ck_attendance_punches__source CHECK (((source)::text = ANY ((ARRAY['Device'::character varying, 'Mobile'::character varying, 'Import'::character varying, 'Correction'::character varying])::text[])))
);

ALTER TABLE ONLY public.attendance_punches_y2026m08 FORCE ROW LEVEL SECURITY;


-- Name: attendance_punches_y2026m09; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.attendance_punches_y2026m09 (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    device_id uuid,
    approval_request_id uuid,
    direction character varying(40) NOT NULL,
    source character varying(40) NOT NULL,
    occurred_at timestamp with time zone NOT NULL,
    latitude numeric(9,6),
    longitude numeric(9,6),
    external_id character varying(64),
    idempotency_key text NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    CONSTRAINT ck_attendance_punches__direction CHECK (((direction)::text = ANY ((ARRAY['In'::character varying, 'Out'::character varying])::text[]))),
    CONSTRAINT ck_attendance_punches__source CHECK (((source)::text = ANY ((ARRAY['Device'::character varying, 'Mobile'::character varying, 'Import'::character varying, 'Correction'::character varying])::text[])))
);

ALTER TABLE ONLY public.attendance_punches_y2026m09 FORCE ROW LEVEL SECURITY;


-- Name: attendance_punches_y2026m10; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.attendance_punches_y2026m10 (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    device_id uuid,
    approval_request_id uuid,
    direction character varying(40) NOT NULL,
    source character varying(40) NOT NULL,
    occurred_at timestamp with time zone NOT NULL,
    latitude numeric(9,6),
    longitude numeric(9,6),
    external_id character varying(64),
    idempotency_key text NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    CONSTRAINT ck_attendance_punches__direction CHECK (((direction)::text = ANY ((ARRAY['In'::character varying, 'Out'::character varying])::text[]))),
    CONSTRAINT ck_attendance_punches__source CHECK (((source)::text = ANY ((ARRAY['Device'::character varying, 'Mobile'::character varying, 'Import'::character varying, 'Correction'::character varying])::text[])))
);

ALTER TABLE ONLY public.attendance_punches_y2026m10 FORCE ROW LEVEL SECURITY;


-- Name: attendance_punches_y2026m11; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.attendance_punches_y2026m11 (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    device_id uuid,
    approval_request_id uuid,
    direction character varying(40) NOT NULL,
    source character varying(40) NOT NULL,
    occurred_at timestamp with time zone NOT NULL,
    latitude numeric(9,6),
    longitude numeric(9,6),
    external_id character varying(64),
    idempotency_key text NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    CONSTRAINT ck_attendance_punches__direction CHECK (((direction)::text = ANY ((ARRAY['In'::character varying, 'Out'::character varying])::text[]))),
    CONSTRAINT ck_attendance_punches__source CHECK (((source)::text = ANY ((ARRAY['Device'::character varying, 'Mobile'::character varying, 'Import'::character varying, 'Correction'::character varying])::text[])))
);

ALTER TABLE ONLY public.attendance_punches_y2026m11 FORCE ROW LEVEL SECURITY;


-- Name: attendance_punches_y2026m12; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.attendance_punches_y2026m12 (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    device_id uuid,
    approval_request_id uuid,
    direction character varying(40) NOT NULL,
    source character varying(40) NOT NULL,
    occurred_at timestamp with time zone NOT NULL,
    latitude numeric(9,6),
    longitude numeric(9,6),
    external_id character varying(64),
    idempotency_key text NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    CONSTRAINT ck_attendance_punches__direction CHECK (((direction)::text = ANY ((ARRAY['In'::character varying, 'Out'::character varying])::text[]))),
    CONSTRAINT ck_attendance_punches__source CHECK (((source)::text = ANY ((ARRAY['Device'::character varying, 'Mobile'::character varying, 'Import'::character varying, 'Correction'::character varying])::text[])))
);

ALTER TABLE ONLY public.attendance_punches_y2026m12 FORCE ROW LEVEL SECURITY;


-- Name: audit_logs; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.audit_logs (
    id uuid NOT NULL,
    tenant_id uuid,
    company_id uuid,
    record_kind character varying(40) DEFAULT 'Event'::character varying NOT NULL,
    category character varying(40),
    chain_key character varying(64) NOT NULL,
    seq bigint NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    action character varying(64),
    entity character varying(64),
    entity_id uuid,
    actor_user_id uuid,
    on_behalf_of_user_id uuid,
    correlation_id uuid,
    hash_algorithm character varying(40) DEFAULT 'sha256'::character varying NOT NULL,
    before jsonb,
    after jsonb,
    personal_data jsonb,
    personal_data_hash text,
    before_hash text,
    after_hash text,
    envelope_hash text,
    personal_data_erased_at timestamp with time zone,
    covers_seq_from bigint,
    covers_seq_to bigint,
    covers_created_from timestamp with time zone,
    covers_created_to timestamp with time zone,
    root_hash text,
    prev_checkpoint_hash text,
    observed_gaps bigint[],
    CONSTRAINT ck_audit_logs__category CHECK (((category IS NULL) OR ((category)::text = ANY ((ARRAY['Auth'::character varying, 'Admin'::character varying, 'Employee'::character varying, 'Leave'::character varying, 'Attendance'::character varying, 'Loan'::character varying, 'Document'::character varying, 'ESS'::character varying])::text[])))),
    CONSTRAINT ck_audit_logs__checkpoint_shape CHECK ((((record_kind)::text <> 'Checkpoint'::text) OR ((root_hash IS NOT NULL) AND (covers_seq_from IS NOT NULL) AND (covers_seq_to IS NOT NULL) AND (covers_created_from IS NOT NULL) AND (covers_created_to IS NOT NULL) AND (covers_seq_to >= covers_seq_from) AND (covers_created_to >= covers_created_from)))),
    CONSTRAINT ck_audit_logs__erasure_is_complete CHECK (((personal_data_erased_at IS NULL) OR ((personal_data IS NULL) AND (before IS NULL) AND (after IS NULL)))),
    CONSTRAINT ck_audit_logs__event_shape CHECK ((((record_kind)::text <> 'Event'::text) OR ((envelope_hash IS NOT NULL) AND (root_hash IS NULL) AND (covers_seq_from IS NULL) AND (covers_seq_to IS NULL) AND (covers_created_from IS NULL) AND (covers_created_to IS NULL)))),
    CONSTRAINT ck_audit_logs__record_kind CHECK (((record_kind)::text = ANY ((ARRAY['Event'::character varying, 'Checkpoint'::character varying])::text[]))),
    CONSTRAINT ck_audit_logs__seq_positive CHECK ((seq > 0))
)
PARTITION BY RANGE (created_at);

ALTER TABLE ONLY public.audit_logs FORCE ROW LEVEL SECURITY;


-- Name: TABLE audit_logs; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.audit_logs IS 'Is the single append-only log of every audited event outside payroll money movement, made tamper-evident by periodic Merkle checkpoint rows and verifiable after a PDPL erasure because each row keeps the digests of the payload it no longer holds. @tier:T/P @owner:Compliance @retention:indefinite-keep';


-- Name: audit_logs_default; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.audit_logs_default (
    id uuid NOT NULL,
    tenant_id uuid,
    company_id uuid,
    record_kind character varying(40) DEFAULT 'Event'::character varying NOT NULL,
    category character varying(40),
    chain_key character varying(64) NOT NULL,
    seq bigint NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    action character varying(64),
    entity character varying(64),
    entity_id uuid,
    actor_user_id uuid,
    on_behalf_of_user_id uuid,
    correlation_id uuid,
    hash_algorithm character varying(40) DEFAULT 'sha256'::character varying NOT NULL,
    before jsonb,
    after jsonb,
    personal_data jsonb,
    personal_data_hash text,
    before_hash text,
    after_hash text,
    envelope_hash text,
    personal_data_erased_at timestamp with time zone,
    covers_seq_from bigint,
    covers_seq_to bigint,
    covers_created_from timestamp with time zone,
    covers_created_to timestamp with time zone,
    root_hash text,
    prev_checkpoint_hash text,
    observed_gaps bigint[],
    CONSTRAINT ck_audit_logs__category CHECK (((category IS NULL) OR ((category)::text = ANY ((ARRAY['Auth'::character varying, 'Admin'::character varying, 'Employee'::character varying, 'Leave'::character varying, 'Attendance'::character varying, 'Loan'::character varying, 'Document'::character varying, 'ESS'::character varying])::text[])))),
    CONSTRAINT ck_audit_logs__checkpoint_shape CHECK ((((record_kind)::text <> 'Checkpoint'::text) OR ((root_hash IS NOT NULL) AND (covers_seq_from IS NOT NULL) AND (covers_seq_to IS NOT NULL) AND (covers_created_from IS NOT NULL) AND (covers_created_to IS NOT NULL) AND (covers_seq_to >= covers_seq_from) AND (covers_created_to >= covers_created_from)))),
    CONSTRAINT ck_audit_logs__erasure_is_complete CHECK (((personal_data_erased_at IS NULL) OR ((personal_data IS NULL) AND (before IS NULL) AND (after IS NULL)))),
    CONSTRAINT ck_audit_logs__event_shape CHECK ((((record_kind)::text <> 'Event'::text) OR ((envelope_hash IS NOT NULL) AND (root_hash IS NULL) AND (covers_seq_from IS NULL) AND (covers_seq_to IS NULL) AND (covers_created_from IS NULL) AND (covers_created_to IS NULL)))),
    CONSTRAINT ck_audit_logs__record_kind CHECK (((record_kind)::text = ANY ((ARRAY['Event'::character varying, 'Checkpoint'::character varying])::text[]))),
    CONSTRAINT ck_audit_logs__seq_positive CHECK ((seq > 0))
);

ALTER TABLE ONLY public.audit_logs_default FORCE ROW LEVEL SECURITY;


-- Name: TABLE audit_logs_default; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.audit_logs_default IS 'DEFAULT catch-all (§19.3 rule 3). A row landing here is an incident: the month partition was missing. Alert on the FIRST row — recovery is DETACH CONCURRENTLY, create the month, batched INSERT…SELECT, then an ATTACH that takes ACCESS EXCLUSIVE and a full validation scan.';


-- Name: audit_logs_y2025m10; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.audit_logs_y2025m10 (
    id uuid NOT NULL,
    tenant_id uuid,
    company_id uuid,
    record_kind character varying(40) DEFAULT 'Event'::character varying NOT NULL,
    category character varying(40),
    chain_key character varying(64) NOT NULL,
    seq bigint NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    action character varying(64),
    entity character varying(64),
    entity_id uuid,
    actor_user_id uuid,
    on_behalf_of_user_id uuid,
    correlation_id uuid,
    hash_algorithm character varying(40) DEFAULT 'sha256'::character varying NOT NULL,
    before jsonb,
    after jsonb,
    personal_data jsonb,
    personal_data_hash text,
    before_hash text,
    after_hash text,
    envelope_hash text,
    personal_data_erased_at timestamp with time zone,
    covers_seq_from bigint,
    covers_seq_to bigint,
    covers_created_from timestamp with time zone,
    covers_created_to timestamp with time zone,
    root_hash text,
    prev_checkpoint_hash text,
    observed_gaps bigint[],
    CONSTRAINT ck_audit_logs__category CHECK (((category IS NULL) OR ((category)::text = ANY ((ARRAY['Auth'::character varying, 'Admin'::character varying, 'Employee'::character varying, 'Leave'::character varying, 'Attendance'::character varying, 'Loan'::character varying, 'Document'::character varying, 'ESS'::character varying])::text[])))),
    CONSTRAINT ck_audit_logs__checkpoint_shape CHECK ((((record_kind)::text <> 'Checkpoint'::text) OR ((root_hash IS NOT NULL) AND (covers_seq_from IS NOT NULL) AND (covers_seq_to IS NOT NULL) AND (covers_created_from IS NOT NULL) AND (covers_created_to IS NOT NULL) AND (covers_seq_to >= covers_seq_from) AND (covers_created_to >= covers_created_from)))),
    CONSTRAINT ck_audit_logs__erasure_is_complete CHECK (((personal_data_erased_at IS NULL) OR ((personal_data IS NULL) AND (before IS NULL) AND (after IS NULL)))),
    CONSTRAINT ck_audit_logs__event_shape CHECK ((((record_kind)::text <> 'Event'::text) OR ((envelope_hash IS NOT NULL) AND (root_hash IS NULL) AND (covers_seq_from IS NULL) AND (covers_seq_to IS NULL) AND (covers_created_from IS NULL) AND (covers_created_to IS NULL)))),
    CONSTRAINT ck_audit_logs__record_kind CHECK (((record_kind)::text = ANY ((ARRAY['Event'::character varying, 'Checkpoint'::character varying])::text[]))),
    CONSTRAINT ck_audit_logs__seq_positive CHECK ((seq > 0))
);

ALTER TABLE ONLY public.audit_logs_y2025m10 FORCE ROW LEVEL SECURITY;


-- Name: audit_logs_y2025m11; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.audit_logs_y2025m11 (
    id uuid NOT NULL,
    tenant_id uuid,
    company_id uuid,
    record_kind character varying(40) DEFAULT 'Event'::character varying NOT NULL,
    category character varying(40),
    chain_key character varying(64) NOT NULL,
    seq bigint NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    action character varying(64),
    entity character varying(64),
    entity_id uuid,
    actor_user_id uuid,
    on_behalf_of_user_id uuid,
    correlation_id uuid,
    hash_algorithm character varying(40) DEFAULT 'sha256'::character varying NOT NULL,
    before jsonb,
    after jsonb,
    personal_data jsonb,
    personal_data_hash text,
    before_hash text,
    after_hash text,
    envelope_hash text,
    personal_data_erased_at timestamp with time zone,
    covers_seq_from bigint,
    covers_seq_to bigint,
    covers_created_from timestamp with time zone,
    covers_created_to timestamp with time zone,
    root_hash text,
    prev_checkpoint_hash text,
    observed_gaps bigint[],
    CONSTRAINT ck_audit_logs__category CHECK (((category IS NULL) OR ((category)::text = ANY ((ARRAY['Auth'::character varying, 'Admin'::character varying, 'Employee'::character varying, 'Leave'::character varying, 'Attendance'::character varying, 'Loan'::character varying, 'Document'::character varying, 'ESS'::character varying])::text[])))),
    CONSTRAINT ck_audit_logs__checkpoint_shape CHECK ((((record_kind)::text <> 'Checkpoint'::text) OR ((root_hash IS NOT NULL) AND (covers_seq_from IS NOT NULL) AND (covers_seq_to IS NOT NULL) AND (covers_created_from IS NOT NULL) AND (covers_created_to IS NOT NULL) AND (covers_seq_to >= covers_seq_from) AND (covers_created_to >= covers_created_from)))),
    CONSTRAINT ck_audit_logs__erasure_is_complete CHECK (((personal_data_erased_at IS NULL) OR ((personal_data IS NULL) AND (before IS NULL) AND (after IS NULL)))),
    CONSTRAINT ck_audit_logs__event_shape CHECK ((((record_kind)::text <> 'Event'::text) OR ((envelope_hash IS NOT NULL) AND (root_hash IS NULL) AND (covers_seq_from IS NULL) AND (covers_seq_to IS NULL) AND (covers_created_from IS NULL) AND (covers_created_to IS NULL)))),
    CONSTRAINT ck_audit_logs__record_kind CHECK (((record_kind)::text = ANY ((ARRAY['Event'::character varying, 'Checkpoint'::character varying])::text[]))),
    CONSTRAINT ck_audit_logs__seq_positive CHECK ((seq > 0))
);

ALTER TABLE ONLY public.audit_logs_y2025m11 FORCE ROW LEVEL SECURITY;


-- Name: audit_logs_y2025m12; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.audit_logs_y2025m12 (
    id uuid NOT NULL,
    tenant_id uuid,
    company_id uuid,
    record_kind character varying(40) DEFAULT 'Event'::character varying NOT NULL,
    category character varying(40),
    chain_key character varying(64) NOT NULL,
    seq bigint NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    action character varying(64),
    entity character varying(64),
    entity_id uuid,
    actor_user_id uuid,
    on_behalf_of_user_id uuid,
    correlation_id uuid,
    hash_algorithm character varying(40) DEFAULT 'sha256'::character varying NOT NULL,
    before jsonb,
    after jsonb,
    personal_data jsonb,
    personal_data_hash text,
    before_hash text,
    after_hash text,
    envelope_hash text,
    personal_data_erased_at timestamp with time zone,
    covers_seq_from bigint,
    covers_seq_to bigint,
    covers_created_from timestamp with time zone,
    covers_created_to timestamp with time zone,
    root_hash text,
    prev_checkpoint_hash text,
    observed_gaps bigint[],
    CONSTRAINT ck_audit_logs__category CHECK (((category IS NULL) OR ((category)::text = ANY ((ARRAY['Auth'::character varying, 'Admin'::character varying, 'Employee'::character varying, 'Leave'::character varying, 'Attendance'::character varying, 'Loan'::character varying, 'Document'::character varying, 'ESS'::character varying])::text[])))),
    CONSTRAINT ck_audit_logs__checkpoint_shape CHECK ((((record_kind)::text <> 'Checkpoint'::text) OR ((root_hash IS NOT NULL) AND (covers_seq_from IS NOT NULL) AND (covers_seq_to IS NOT NULL) AND (covers_created_from IS NOT NULL) AND (covers_created_to IS NOT NULL) AND (covers_seq_to >= covers_seq_from) AND (covers_created_to >= covers_created_from)))),
    CONSTRAINT ck_audit_logs__erasure_is_complete CHECK (((personal_data_erased_at IS NULL) OR ((personal_data IS NULL) AND (before IS NULL) AND (after IS NULL)))),
    CONSTRAINT ck_audit_logs__event_shape CHECK ((((record_kind)::text <> 'Event'::text) OR ((envelope_hash IS NOT NULL) AND (root_hash IS NULL) AND (covers_seq_from IS NULL) AND (covers_seq_to IS NULL) AND (covers_created_from IS NULL) AND (covers_created_to IS NULL)))),
    CONSTRAINT ck_audit_logs__record_kind CHECK (((record_kind)::text = ANY ((ARRAY['Event'::character varying, 'Checkpoint'::character varying])::text[]))),
    CONSTRAINT ck_audit_logs__seq_positive CHECK ((seq > 0))
);

ALTER TABLE ONLY public.audit_logs_y2025m12 FORCE ROW LEVEL SECURITY;


-- Name: audit_logs_y2026m01; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.audit_logs_y2026m01 (
    id uuid NOT NULL,
    tenant_id uuid,
    company_id uuid,
    record_kind character varying(40) DEFAULT 'Event'::character varying NOT NULL,
    category character varying(40),
    chain_key character varying(64) NOT NULL,
    seq bigint NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    action character varying(64),
    entity character varying(64),
    entity_id uuid,
    actor_user_id uuid,
    on_behalf_of_user_id uuid,
    correlation_id uuid,
    hash_algorithm character varying(40) DEFAULT 'sha256'::character varying NOT NULL,
    before jsonb,
    after jsonb,
    personal_data jsonb,
    personal_data_hash text,
    before_hash text,
    after_hash text,
    envelope_hash text,
    personal_data_erased_at timestamp with time zone,
    covers_seq_from bigint,
    covers_seq_to bigint,
    covers_created_from timestamp with time zone,
    covers_created_to timestamp with time zone,
    root_hash text,
    prev_checkpoint_hash text,
    observed_gaps bigint[],
    CONSTRAINT ck_audit_logs__category CHECK (((category IS NULL) OR ((category)::text = ANY ((ARRAY['Auth'::character varying, 'Admin'::character varying, 'Employee'::character varying, 'Leave'::character varying, 'Attendance'::character varying, 'Loan'::character varying, 'Document'::character varying, 'ESS'::character varying])::text[])))),
    CONSTRAINT ck_audit_logs__checkpoint_shape CHECK ((((record_kind)::text <> 'Checkpoint'::text) OR ((root_hash IS NOT NULL) AND (covers_seq_from IS NOT NULL) AND (covers_seq_to IS NOT NULL) AND (covers_created_from IS NOT NULL) AND (covers_created_to IS NOT NULL) AND (covers_seq_to >= covers_seq_from) AND (covers_created_to >= covers_created_from)))),
    CONSTRAINT ck_audit_logs__erasure_is_complete CHECK (((personal_data_erased_at IS NULL) OR ((personal_data IS NULL) AND (before IS NULL) AND (after IS NULL)))),
    CONSTRAINT ck_audit_logs__event_shape CHECK ((((record_kind)::text <> 'Event'::text) OR ((envelope_hash IS NOT NULL) AND (root_hash IS NULL) AND (covers_seq_from IS NULL) AND (covers_seq_to IS NULL) AND (covers_created_from IS NULL) AND (covers_created_to IS NULL)))),
    CONSTRAINT ck_audit_logs__record_kind CHECK (((record_kind)::text = ANY ((ARRAY['Event'::character varying, 'Checkpoint'::character varying])::text[]))),
    CONSTRAINT ck_audit_logs__seq_positive CHECK ((seq > 0))
);

ALTER TABLE ONLY public.audit_logs_y2026m01 FORCE ROW LEVEL SECURITY;


-- Name: audit_logs_y2026m02; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.audit_logs_y2026m02 (
    id uuid NOT NULL,
    tenant_id uuid,
    company_id uuid,
    record_kind character varying(40) DEFAULT 'Event'::character varying NOT NULL,
    category character varying(40),
    chain_key character varying(64) NOT NULL,
    seq bigint NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    action character varying(64),
    entity character varying(64),
    entity_id uuid,
    actor_user_id uuid,
    on_behalf_of_user_id uuid,
    correlation_id uuid,
    hash_algorithm character varying(40) DEFAULT 'sha256'::character varying NOT NULL,
    before jsonb,
    after jsonb,
    personal_data jsonb,
    personal_data_hash text,
    before_hash text,
    after_hash text,
    envelope_hash text,
    personal_data_erased_at timestamp with time zone,
    covers_seq_from bigint,
    covers_seq_to bigint,
    covers_created_from timestamp with time zone,
    covers_created_to timestamp with time zone,
    root_hash text,
    prev_checkpoint_hash text,
    observed_gaps bigint[],
    CONSTRAINT ck_audit_logs__category CHECK (((category IS NULL) OR ((category)::text = ANY ((ARRAY['Auth'::character varying, 'Admin'::character varying, 'Employee'::character varying, 'Leave'::character varying, 'Attendance'::character varying, 'Loan'::character varying, 'Document'::character varying, 'ESS'::character varying])::text[])))),
    CONSTRAINT ck_audit_logs__checkpoint_shape CHECK ((((record_kind)::text <> 'Checkpoint'::text) OR ((root_hash IS NOT NULL) AND (covers_seq_from IS NOT NULL) AND (covers_seq_to IS NOT NULL) AND (covers_created_from IS NOT NULL) AND (covers_created_to IS NOT NULL) AND (covers_seq_to >= covers_seq_from) AND (covers_created_to >= covers_created_from)))),
    CONSTRAINT ck_audit_logs__erasure_is_complete CHECK (((personal_data_erased_at IS NULL) OR ((personal_data IS NULL) AND (before IS NULL) AND (after IS NULL)))),
    CONSTRAINT ck_audit_logs__event_shape CHECK ((((record_kind)::text <> 'Event'::text) OR ((envelope_hash IS NOT NULL) AND (root_hash IS NULL) AND (covers_seq_from IS NULL) AND (covers_seq_to IS NULL) AND (covers_created_from IS NULL) AND (covers_created_to IS NULL)))),
    CONSTRAINT ck_audit_logs__record_kind CHECK (((record_kind)::text = ANY ((ARRAY['Event'::character varying, 'Checkpoint'::character varying])::text[]))),
    CONSTRAINT ck_audit_logs__seq_positive CHECK ((seq > 0))
);

ALTER TABLE ONLY public.audit_logs_y2026m02 FORCE ROW LEVEL SECURITY;


-- Name: audit_logs_y2026m03; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.audit_logs_y2026m03 (
    id uuid NOT NULL,
    tenant_id uuid,
    company_id uuid,
    record_kind character varying(40) DEFAULT 'Event'::character varying NOT NULL,
    category character varying(40),
    chain_key character varying(64) NOT NULL,
    seq bigint NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    action character varying(64),
    entity character varying(64),
    entity_id uuid,
    actor_user_id uuid,
    on_behalf_of_user_id uuid,
    correlation_id uuid,
    hash_algorithm character varying(40) DEFAULT 'sha256'::character varying NOT NULL,
    before jsonb,
    after jsonb,
    personal_data jsonb,
    personal_data_hash text,
    before_hash text,
    after_hash text,
    envelope_hash text,
    personal_data_erased_at timestamp with time zone,
    covers_seq_from bigint,
    covers_seq_to bigint,
    covers_created_from timestamp with time zone,
    covers_created_to timestamp with time zone,
    root_hash text,
    prev_checkpoint_hash text,
    observed_gaps bigint[],
    CONSTRAINT ck_audit_logs__category CHECK (((category IS NULL) OR ((category)::text = ANY ((ARRAY['Auth'::character varying, 'Admin'::character varying, 'Employee'::character varying, 'Leave'::character varying, 'Attendance'::character varying, 'Loan'::character varying, 'Document'::character varying, 'ESS'::character varying])::text[])))),
    CONSTRAINT ck_audit_logs__checkpoint_shape CHECK ((((record_kind)::text <> 'Checkpoint'::text) OR ((root_hash IS NOT NULL) AND (covers_seq_from IS NOT NULL) AND (covers_seq_to IS NOT NULL) AND (covers_created_from IS NOT NULL) AND (covers_created_to IS NOT NULL) AND (covers_seq_to >= covers_seq_from) AND (covers_created_to >= covers_created_from)))),
    CONSTRAINT ck_audit_logs__erasure_is_complete CHECK (((personal_data_erased_at IS NULL) OR ((personal_data IS NULL) AND (before IS NULL) AND (after IS NULL)))),
    CONSTRAINT ck_audit_logs__event_shape CHECK ((((record_kind)::text <> 'Event'::text) OR ((envelope_hash IS NOT NULL) AND (root_hash IS NULL) AND (covers_seq_from IS NULL) AND (covers_seq_to IS NULL) AND (covers_created_from IS NULL) AND (covers_created_to IS NULL)))),
    CONSTRAINT ck_audit_logs__record_kind CHECK (((record_kind)::text = ANY ((ARRAY['Event'::character varying, 'Checkpoint'::character varying])::text[]))),
    CONSTRAINT ck_audit_logs__seq_positive CHECK ((seq > 0))
);

ALTER TABLE ONLY public.audit_logs_y2026m03 FORCE ROW LEVEL SECURITY;


-- Name: audit_logs_y2026m04; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.audit_logs_y2026m04 (
    id uuid NOT NULL,
    tenant_id uuid,
    company_id uuid,
    record_kind character varying(40) DEFAULT 'Event'::character varying NOT NULL,
    category character varying(40),
    chain_key character varying(64) NOT NULL,
    seq bigint NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    action character varying(64),
    entity character varying(64),
    entity_id uuid,
    actor_user_id uuid,
    on_behalf_of_user_id uuid,
    correlation_id uuid,
    hash_algorithm character varying(40) DEFAULT 'sha256'::character varying NOT NULL,
    before jsonb,
    after jsonb,
    personal_data jsonb,
    personal_data_hash text,
    before_hash text,
    after_hash text,
    envelope_hash text,
    personal_data_erased_at timestamp with time zone,
    covers_seq_from bigint,
    covers_seq_to bigint,
    covers_created_from timestamp with time zone,
    covers_created_to timestamp with time zone,
    root_hash text,
    prev_checkpoint_hash text,
    observed_gaps bigint[],
    CONSTRAINT ck_audit_logs__category CHECK (((category IS NULL) OR ((category)::text = ANY ((ARRAY['Auth'::character varying, 'Admin'::character varying, 'Employee'::character varying, 'Leave'::character varying, 'Attendance'::character varying, 'Loan'::character varying, 'Document'::character varying, 'ESS'::character varying])::text[])))),
    CONSTRAINT ck_audit_logs__checkpoint_shape CHECK ((((record_kind)::text <> 'Checkpoint'::text) OR ((root_hash IS NOT NULL) AND (covers_seq_from IS NOT NULL) AND (covers_seq_to IS NOT NULL) AND (covers_created_from IS NOT NULL) AND (covers_created_to IS NOT NULL) AND (covers_seq_to >= covers_seq_from) AND (covers_created_to >= covers_created_from)))),
    CONSTRAINT ck_audit_logs__erasure_is_complete CHECK (((personal_data_erased_at IS NULL) OR ((personal_data IS NULL) AND (before IS NULL) AND (after IS NULL)))),
    CONSTRAINT ck_audit_logs__event_shape CHECK ((((record_kind)::text <> 'Event'::text) OR ((envelope_hash IS NOT NULL) AND (root_hash IS NULL) AND (covers_seq_from IS NULL) AND (covers_seq_to IS NULL) AND (covers_created_from IS NULL) AND (covers_created_to IS NULL)))),
    CONSTRAINT ck_audit_logs__record_kind CHECK (((record_kind)::text = ANY ((ARRAY['Event'::character varying, 'Checkpoint'::character varying])::text[]))),
    CONSTRAINT ck_audit_logs__seq_positive CHECK ((seq > 0))
);

ALTER TABLE ONLY public.audit_logs_y2026m04 FORCE ROW LEVEL SECURITY;


-- Name: audit_logs_y2026m05; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.audit_logs_y2026m05 (
    id uuid NOT NULL,
    tenant_id uuid,
    company_id uuid,
    record_kind character varying(40) DEFAULT 'Event'::character varying NOT NULL,
    category character varying(40),
    chain_key character varying(64) NOT NULL,
    seq bigint NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    action character varying(64),
    entity character varying(64),
    entity_id uuid,
    actor_user_id uuid,
    on_behalf_of_user_id uuid,
    correlation_id uuid,
    hash_algorithm character varying(40) DEFAULT 'sha256'::character varying NOT NULL,
    before jsonb,
    after jsonb,
    personal_data jsonb,
    personal_data_hash text,
    before_hash text,
    after_hash text,
    envelope_hash text,
    personal_data_erased_at timestamp with time zone,
    covers_seq_from bigint,
    covers_seq_to bigint,
    covers_created_from timestamp with time zone,
    covers_created_to timestamp with time zone,
    root_hash text,
    prev_checkpoint_hash text,
    observed_gaps bigint[],
    CONSTRAINT ck_audit_logs__category CHECK (((category IS NULL) OR ((category)::text = ANY ((ARRAY['Auth'::character varying, 'Admin'::character varying, 'Employee'::character varying, 'Leave'::character varying, 'Attendance'::character varying, 'Loan'::character varying, 'Document'::character varying, 'ESS'::character varying])::text[])))),
    CONSTRAINT ck_audit_logs__checkpoint_shape CHECK ((((record_kind)::text <> 'Checkpoint'::text) OR ((root_hash IS NOT NULL) AND (covers_seq_from IS NOT NULL) AND (covers_seq_to IS NOT NULL) AND (covers_created_from IS NOT NULL) AND (covers_created_to IS NOT NULL) AND (covers_seq_to >= covers_seq_from) AND (covers_created_to >= covers_created_from)))),
    CONSTRAINT ck_audit_logs__erasure_is_complete CHECK (((personal_data_erased_at IS NULL) OR ((personal_data IS NULL) AND (before IS NULL) AND (after IS NULL)))),
    CONSTRAINT ck_audit_logs__event_shape CHECK ((((record_kind)::text <> 'Event'::text) OR ((envelope_hash IS NOT NULL) AND (root_hash IS NULL) AND (covers_seq_from IS NULL) AND (covers_seq_to IS NULL) AND (covers_created_from IS NULL) AND (covers_created_to IS NULL)))),
    CONSTRAINT ck_audit_logs__record_kind CHECK (((record_kind)::text = ANY ((ARRAY['Event'::character varying, 'Checkpoint'::character varying])::text[]))),
    CONSTRAINT ck_audit_logs__seq_positive CHECK ((seq > 0))
);

ALTER TABLE ONLY public.audit_logs_y2026m05 FORCE ROW LEVEL SECURITY;


-- Name: audit_logs_y2026m06; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.audit_logs_y2026m06 (
    id uuid NOT NULL,
    tenant_id uuid,
    company_id uuid,
    record_kind character varying(40) DEFAULT 'Event'::character varying NOT NULL,
    category character varying(40),
    chain_key character varying(64) NOT NULL,
    seq bigint NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    action character varying(64),
    entity character varying(64),
    entity_id uuid,
    actor_user_id uuid,
    on_behalf_of_user_id uuid,
    correlation_id uuid,
    hash_algorithm character varying(40) DEFAULT 'sha256'::character varying NOT NULL,
    before jsonb,
    after jsonb,
    personal_data jsonb,
    personal_data_hash text,
    before_hash text,
    after_hash text,
    envelope_hash text,
    personal_data_erased_at timestamp with time zone,
    covers_seq_from bigint,
    covers_seq_to bigint,
    covers_created_from timestamp with time zone,
    covers_created_to timestamp with time zone,
    root_hash text,
    prev_checkpoint_hash text,
    observed_gaps bigint[],
    CONSTRAINT ck_audit_logs__category CHECK (((category IS NULL) OR ((category)::text = ANY ((ARRAY['Auth'::character varying, 'Admin'::character varying, 'Employee'::character varying, 'Leave'::character varying, 'Attendance'::character varying, 'Loan'::character varying, 'Document'::character varying, 'ESS'::character varying])::text[])))),
    CONSTRAINT ck_audit_logs__checkpoint_shape CHECK ((((record_kind)::text <> 'Checkpoint'::text) OR ((root_hash IS NOT NULL) AND (covers_seq_from IS NOT NULL) AND (covers_seq_to IS NOT NULL) AND (covers_created_from IS NOT NULL) AND (covers_created_to IS NOT NULL) AND (covers_seq_to >= covers_seq_from) AND (covers_created_to >= covers_created_from)))),
    CONSTRAINT ck_audit_logs__erasure_is_complete CHECK (((personal_data_erased_at IS NULL) OR ((personal_data IS NULL) AND (before IS NULL) AND (after IS NULL)))),
    CONSTRAINT ck_audit_logs__event_shape CHECK ((((record_kind)::text <> 'Event'::text) OR ((envelope_hash IS NOT NULL) AND (root_hash IS NULL) AND (covers_seq_from IS NULL) AND (covers_seq_to IS NULL) AND (covers_created_from IS NULL) AND (covers_created_to IS NULL)))),
    CONSTRAINT ck_audit_logs__record_kind CHECK (((record_kind)::text = ANY ((ARRAY['Event'::character varying, 'Checkpoint'::character varying])::text[]))),
    CONSTRAINT ck_audit_logs__seq_positive CHECK ((seq > 0))
);

ALTER TABLE ONLY public.audit_logs_y2026m06 FORCE ROW LEVEL SECURITY;


-- Name: audit_logs_y2026m07; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.audit_logs_y2026m07 (
    id uuid NOT NULL,
    tenant_id uuid,
    company_id uuid,
    record_kind character varying(40) DEFAULT 'Event'::character varying NOT NULL,
    category character varying(40),
    chain_key character varying(64) NOT NULL,
    seq bigint NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    action character varying(64),
    entity character varying(64),
    entity_id uuid,
    actor_user_id uuid,
    on_behalf_of_user_id uuid,
    correlation_id uuid,
    hash_algorithm character varying(40) DEFAULT 'sha256'::character varying NOT NULL,
    before jsonb,
    after jsonb,
    personal_data jsonb,
    personal_data_hash text,
    before_hash text,
    after_hash text,
    envelope_hash text,
    personal_data_erased_at timestamp with time zone,
    covers_seq_from bigint,
    covers_seq_to bigint,
    covers_created_from timestamp with time zone,
    covers_created_to timestamp with time zone,
    root_hash text,
    prev_checkpoint_hash text,
    observed_gaps bigint[],
    CONSTRAINT ck_audit_logs__category CHECK (((category IS NULL) OR ((category)::text = ANY ((ARRAY['Auth'::character varying, 'Admin'::character varying, 'Employee'::character varying, 'Leave'::character varying, 'Attendance'::character varying, 'Loan'::character varying, 'Document'::character varying, 'ESS'::character varying])::text[])))),
    CONSTRAINT ck_audit_logs__checkpoint_shape CHECK ((((record_kind)::text <> 'Checkpoint'::text) OR ((root_hash IS NOT NULL) AND (covers_seq_from IS NOT NULL) AND (covers_seq_to IS NOT NULL) AND (covers_created_from IS NOT NULL) AND (covers_created_to IS NOT NULL) AND (covers_seq_to >= covers_seq_from) AND (covers_created_to >= covers_created_from)))),
    CONSTRAINT ck_audit_logs__erasure_is_complete CHECK (((personal_data_erased_at IS NULL) OR ((personal_data IS NULL) AND (before IS NULL) AND (after IS NULL)))),
    CONSTRAINT ck_audit_logs__event_shape CHECK ((((record_kind)::text <> 'Event'::text) OR ((envelope_hash IS NOT NULL) AND (root_hash IS NULL) AND (covers_seq_from IS NULL) AND (covers_seq_to IS NULL) AND (covers_created_from IS NULL) AND (covers_created_to IS NULL)))),
    CONSTRAINT ck_audit_logs__record_kind CHECK (((record_kind)::text = ANY ((ARRAY['Event'::character varying, 'Checkpoint'::character varying])::text[]))),
    CONSTRAINT ck_audit_logs__seq_positive CHECK ((seq > 0))
);

ALTER TABLE ONLY public.audit_logs_y2026m07 FORCE ROW LEVEL SECURITY;


-- Name: audit_logs_y2026m08; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.audit_logs_y2026m08 (
    id uuid NOT NULL,
    tenant_id uuid,
    company_id uuid,
    record_kind character varying(40) DEFAULT 'Event'::character varying NOT NULL,
    category character varying(40),
    chain_key character varying(64) NOT NULL,
    seq bigint NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    action character varying(64),
    entity character varying(64),
    entity_id uuid,
    actor_user_id uuid,
    on_behalf_of_user_id uuid,
    correlation_id uuid,
    hash_algorithm character varying(40) DEFAULT 'sha256'::character varying NOT NULL,
    before jsonb,
    after jsonb,
    personal_data jsonb,
    personal_data_hash text,
    before_hash text,
    after_hash text,
    envelope_hash text,
    personal_data_erased_at timestamp with time zone,
    covers_seq_from bigint,
    covers_seq_to bigint,
    covers_created_from timestamp with time zone,
    covers_created_to timestamp with time zone,
    root_hash text,
    prev_checkpoint_hash text,
    observed_gaps bigint[],
    CONSTRAINT ck_audit_logs__category CHECK (((category IS NULL) OR ((category)::text = ANY ((ARRAY['Auth'::character varying, 'Admin'::character varying, 'Employee'::character varying, 'Leave'::character varying, 'Attendance'::character varying, 'Loan'::character varying, 'Document'::character varying, 'ESS'::character varying])::text[])))),
    CONSTRAINT ck_audit_logs__checkpoint_shape CHECK ((((record_kind)::text <> 'Checkpoint'::text) OR ((root_hash IS NOT NULL) AND (covers_seq_from IS NOT NULL) AND (covers_seq_to IS NOT NULL) AND (covers_created_from IS NOT NULL) AND (covers_created_to IS NOT NULL) AND (covers_seq_to >= covers_seq_from) AND (covers_created_to >= covers_created_from)))),
    CONSTRAINT ck_audit_logs__erasure_is_complete CHECK (((personal_data_erased_at IS NULL) OR ((personal_data IS NULL) AND (before IS NULL) AND (after IS NULL)))),
    CONSTRAINT ck_audit_logs__event_shape CHECK ((((record_kind)::text <> 'Event'::text) OR ((envelope_hash IS NOT NULL) AND (root_hash IS NULL) AND (covers_seq_from IS NULL) AND (covers_seq_to IS NULL) AND (covers_created_from IS NULL) AND (covers_created_to IS NULL)))),
    CONSTRAINT ck_audit_logs__record_kind CHECK (((record_kind)::text = ANY ((ARRAY['Event'::character varying, 'Checkpoint'::character varying])::text[]))),
    CONSTRAINT ck_audit_logs__seq_positive CHECK ((seq > 0))
);

ALTER TABLE ONLY public.audit_logs_y2026m08 FORCE ROW LEVEL SECURITY;


-- Name: audit_logs_y2026m09; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.audit_logs_y2026m09 (
    id uuid NOT NULL,
    tenant_id uuid,
    company_id uuid,
    record_kind character varying(40) DEFAULT 'Event'::character varying NOT NULL,
    category character varying(40),
    chain_key character varying(64) NOT NULL,
    seq bigint NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    action character varying(64),
    entity character varying(64),
    entity_id uuid,
    actor_user_id uuid,
    on_behalf_of_user_id uuid,
    correlation_id uuid,
    hash_algorithm character varying(40) DEFAULT 'sha256'::character varying NOT NULL,
    before jsonb,
    after jsonb,
    personal_data jsonb,
    personal_data_hash text,
    before_hash text,
    after_hash text,
    envelope_hash text,
    personal_data_erased_at timestamp with time zone,
    covers_seq_from bigint,
    covers_seq_to bigint,
    covers_created_from timestamp with time zone,
    covers_created_to timestamp with time zone,
    root_hash text,
    prev_checkpoint_hash text,
    observed_gaps bigint[],
    CONSTRAINT ck_audit_logs__category CHECK (((category IS NULL) OR ((category)::text = ANY ((ARRAY['Auth'::character varying, 'Admin'::character varying, 'Employee'::character varying, 'Leave'::character varying, 'Attendance'::character varying, 'Loan'::character varying, 'Document'::character varying, 'ESS'::character varying])::text[])))),
    CONSTRAINT ck_audit_logs__checkpoint_shape CHECK ((((record_kind)::text <> 'Checkpoint'::text) OR ((root_hash IS NOT NULL) AND (covers_seq_from IS NOT NULL) AND (covers_seq_to IS NOT NULL) AND (covers_created_from IS NOT NULL) AND (covers_created_to IS NOT NULL) AND (covers_seq_to >= covers_seq_from) AND (covers_created_to >= covers_created_from)))),
    CONSTRAINT ck_audit_logs__erasure_is_complete CHECK (((personal_data_erased_at IS NULL) OR ((personal_data IS NULL) AND (before IS NULL) AND (after IS NULL)))),
    CONSTRAINT ck_audit_logs__event_shape CHECK ((((record_kind)::text <> 'Event'::text) OR ((envelope_hash IS NOT NULL) AND (root_hash IS NULL) AND (covers_seq_from IS NULL) AND (covers_seq_to IS NULL) AND (covers_created_from IS NULL) AND (covers_created_to IS NULL)))),
    CONSTRAINT ck_audit_logs__record_kind CHECK (((record_kind)::text = ANY ((ARRAY['Event'::character varying, 'Checkpoint'::character varying])::text[]))),
    CONSTRAINT ck_audit_logs__seq_positive CHECK ((seq > 0))
);

ALTER TABLE ONLY public.audit_logs_y2026m09 FORCE ROW LEVEL SECURITY;


-- Name: audit_logs_y2026m10; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.audit_logs_y2026m10 (
    id uuid NOT NULL,
    tenant_id uuid,
    company_id uuid,
    record_kind character varying(40) DEFAULT 'Event'::character varying NOT NULL,
    category character varying(40),
    chain_key character varying(64) NOT NULL,
    seq bigint NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    action character varying(64),
    entity character varying(64),
    entity_id uuid,
    actor_user_id uuid,
    on_behalf_of_user_id uuid,
    correlation_id uuid,
    hash_algorithm character varying(40) DEFAULT 'sha256'::character varying NOT NULL,
    before jsonb,
    after jsonb,
    personal_data jsonb,
    personal_data_hash text,
    before_hash text,
    after_hash text,
    envelope_hash text,
    personal_data_erased_at timestamp with time zone,
    covers_seq_from bigint,
    covers_seq_to bigint,
    covers_created_from timestamp with time zone,
    covers_created_to timestamp with time zone,
    root_hash text,
    prev_checkpoint_hash text,
    observed_gaps bigint[],
    CONSTRAINT ck_audit_logs__category CHECK (((category IS NULL) OR ((category)::text = ANY ((ARRAY['Auth'::character varying, 'Admin'::character varying, 'Employee'::character varying, 'Leave'::character varying, 'Attendance'::character varying, 'Loan'::character varying, 'Document'::character varying, 'ESS'::character varying])::text[])))),
    CONSTRAINT ck_audit_logs__checkpoint_shape CHECK ((((record_kind)::text <> 'Checkpoint'::text) OR ((root_hash IS NOT NULL) AND (covers_seq_from IS NOT NULL) AND (covers_seq_to IS NOT NULL) AND (covers_created_from IS NOT NULL) AND (covers_created_to IS NOT NULL) AND (covers_seq_to >= covers_seq_from) AND (covers_created_to >= covers_created_from)))),
    CONSTRAINT ck_audit_logs__erasure_is_complete CHECK (((personal_data_erased_at IS NULL) OR ((personal_data IS NULL) AND (before IS NULL) AND (after IS NULL)))),
    CONSTRAINT ck_audit_logs__event_shape CHECK ((((record_kind)::text <> 'Event'::text) OR ((envelope_hash IS NOT NULL) AND (root_hash IS NULL) AND (covers_seq_from IS NULL) AND (covers_seq_to IS NULL) AND (covers_created_from IS NULL) AND (covers_created_to IS NULL)))),
    CONSTRAINT ck_audit_logs__record_kind CHECK (((record_kind)::text = ANY ((ARRAY['Event'::character varying, 'Checkpoint'::character varying])::text[]))),
    CONSTRAINT ck_audit_logs__seq_positive CHECK ((seq > 0))
);

ALTER TABLE ONLY public.audit_logs_y2026m10 FORCE ROW LEVEL SECURITY;


-- Name: audit_logs_y2026m11; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.audit_logs_y2026m11 (
    id uuid NOT NULL,
    tenant_id uuid,
    company_id uuid,
    record_kind character varying(40) DEFAULT 'Event'::character varying NOT NULL,
    category character varying(40),
    chain_key character varying(64) NOT NULL,
    seq bigint NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    action character varying(64),
    entity character varying(64),
    entity_id uuid,
    actor_user_id uuid,
    on_behalf_of_user_id uuid,
    correlation_id uuid,
    hash_algorithm character varying(40) DEFAULT 'sha256'::character varying NOT NULL,
    before jsonb,
    after jsonb,
    personal_data jsonb,
    personal_data_hash text,
    before_hash text,
    after_hash text,
    envelope_hash text,
    personal_data_erased_at timestamp with time zone,
    covers_seq_from bigint,
    covers_seq_to bigint,
    covers_created_from timestamp with time zone,
    covers_created_to timestamp with time zone,
    root_hash text,
    prev_checkpoint_hash text,
    observed_gaps bigint[],
    CONSTRAINT ck_audit_logs__category CHECK (((category IS NULL) OR ((category)::text = ANY ((ARRAY['Auth'::character varying, 'Admin'::character varying, 'Employee'::character varying, 'Leave'::character varying, 'Attendance'::character varying, 'Loan'::character varying, 'Document'::character varying, 'ESS'::character varying])::text[])))),
    CONSTRAINT ck_audit_logs__checkpoint_shape CHECK ((((record_kind)::text <> 'Checkpoint'::text) OR ((root_hash IS NOT NULL) AND (covers_seq_from IS NOT NULL) AND (covers_seq_to IS NOT NULL) AND (covers_created_from IS NOT NULL) AND (covers_created_to IS NOT NULL) AND (covers_seq_to >= covers_seq_from) AND (covers_created_to >= covers_created_from)))),
    CONSTRAINT ck_audit_logs__erasure_is_complete CHECK (((personal_data_erased_at IS NULL) OR ((personal_data IS NULL) AND (before IS NULL) AND (after IS NULL)))),
    CONSTRAINT ck_audit_logs__event_shape CHECK ((((record_kind)::text <> 'Event'::text) OR ((envelope_hash IS NOT NULL) AND (root_hash IS NULL) AND (covers_seq_from IS NULL) AND (covers_seq_to IS NULL) AND (covers_created_from IS NULL) AND (covers_created_to IS NULL)))),
    CONSTRAINT ck_audit_logs__record_kind CHECK (((record_kind)::text = ANY ((ARRAY['Event'::character varying, 'Checkpoint'::character varying])::text[]))),
    CONSTRAINT ck_audit_logs__seq_positive CHECK ((seq > 0))
);

ALTER TABLE ONLY public.audit_logs_y2026m11 FORCE ROW LEVEL SECURITY;


-- Name: audit_logs_y2026m12; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.audit_logs_y2026m12 (
    id uuid NOT NULL,
    tenant_id uuid,
    company_id uuid,
    record_kind character varying(40) DEFAULT 'Event'::character varying NOT NULL,
    category character varying(40),
    chain_key character varying(64) NOT NULL,
    seq bigint NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    action character varying(64),
    entity character varying(64),
    entity_id uuid,
    actor_user_id uuid,
    on_behalf_of_user_id uuid,
    correlation_id uuid,
    hash_algorithm character varying(40) DEFAULT 'sha256'::character varying NOT NULL,
    before jsonb,
    after jsonb,
    personal_data jsonb,
    personal_data_hash text,
    before_hash text,
    after_hash text,
    envelope_hash text,
    personal_data_erased_at timestamp with time zone,
    covers_seq_from bigint,
    covers_seq_to bigint,
    covers_created_from timestamp with time zone,
    covers_created_to timestamp with time zone,
    root_hash text,
    prev_checkpoint_hash text,
    observed_gaps bigint[],
    CONSTRAINT ck_audit_logs__category CHECK (((category IS NULL) OR ((category)::text = ANY ((ARRAY['Auth'::character varying, 'Admin'::character varying, 'Employee'::character varying, 'Leave'::character varying, 'Attendance'::character varying, 'Loan'::character varying, 'Document'::character varying, 'ESS'::character varying])::text[])))),
    CONSTRAINT ck_audit_logs__checkpoint_shape CHECK ((((record_kind)::text <> 'Checkpoint'::text) OR ((root_hash IS NOT NULL) AND (covers_seq_from IS NOT NULL) AND (covers_seq_to IS NOT NULL) AND (covers_created_from IS NOT NULL) AND (covers_created_to IS NOT NULL) AND (covers_seq_to >= covers_seq_from) AND (covers_created_to >= covers_created_from)))),
    CONSTRAINT ck_audit_logs__erasure_is_complete CHECK (((personal_data_erased_at IS NULL) OR ((personal_data IS NULL) AND (before IS NULL) AND (after IS NULL)))),
    CONSTRAINT ck_audit_logs__event_shape CHECK ((((record_kind)::text <> 'Event'::text) OR ((envelope_hash IS NOT NULL) AND (root_hash IS NULL) AND (covers_seq_from IS NULL) AND (covers_seq_to IS NULL) AND (covers_created_from IS NULL) AND (covers_created_to IS NULL)))),
    CONSTRAINT ck_audit_logs__record_kind CHECK (((record_kind)::text = ANY ((ARRAY['Event'::character varying, 'Checkpoint'::character varying])::text[]))),
    CONSTRAINT ck_audit_logs__seq_positive CHECK ((seq > 0))
);

ALTER TABLE ONLY public.audit_logs_y2026m12 FORCE ROW LEVEL SECURITY;


-- Name: auth_sessions; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.auth_sessions (
    id uuid NOT NULL,
    tenant_id uuid,
    user_id uuid,
    platform_user_id uuid,
    device_id character varying(128) NOT NULL,
    subject_kind character varying(40) NOT NULL,
    refresh_token_hash text NOT NULL,
    previous_token_hash text,
    push_token text,
    push_platform character varying(40),
    ip text,
    user_agent text,
    last_seen_at timestamp with time zone,
    expires_at timestamp with time zone NOT NULL,
    revoked_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT chk_auth_subject_kind_auth_sessions CHECK (((subject_kind)::text = ANY ((ARRAY['Tenant'::character varying, 'Platform'::character varying])::text[]))),
    CONSTRAINT ck_auth_sessions__subject_xor CHECK (((((subject_kind)::text = 'Tenant'::text) AND (user_id IS NOT NULL) AND (platform_user_id IS NULL) AND (tenant_id IS NOT NULL)) OR (((subject_kind)::text = 'Platform'::text) AND (user_id IS NULL) AND (platform_user_id IS NOT NULL) AND (tenant_id IS NULL))))
);

ALTER TABLE ONLY public.auth_sessions FORCE ROW LEVEL SECURITY;


-- Name: TABLE auth_sessions; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.auth_sessions IS 'One row per signed-in device for either subject kind, carrying the rotated refresh token, the previous hash for reuse detection and the push registration, so re-login never loses a device. @tier:T/P @owner:Platform @retention:1-month-after-Expiry-then-Purge';


-- Name: COLUMN auth_sessions.tenant_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.auth_sessions.tenant_id IS 'NULL only for subject_kind=''Platform''. One of exactly two nullable-tenant client-adjacent tables; policed by the hand-written p_auth policy (§19.2).';


-- Name: COLUMN auth_sessions.previous_token_hash; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.auth_sessions.previous_token_hash IS 'Presented-again detection: a refresh with this hash means the token was replayed, and the whole session is revoked.';


-- Name: auth_tokens; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.auth_tokens (
    id uuid NOT NULL,
    tenant_id uuid,
    user_id uuid,
    platform_user_id uuid,
    token_hash text NOT NULL,
    subject_kind character varying(40) NOT NULL,
    purpose character varying(40) NOT NULL,
    attempts integer DEFAULT 0 NOT NULL,
    expires_at timestamp with time zone NOT NULL,
    consumed_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT chk_auth_subject_kind_auth_tokens CHECK (((subject_kind)::text = ANY ((ARRAY['Tenant'::character varying, 'Platform'::character varying])::text[]))),
    CONSTRAINT chk_auth_tokens_purpose CHECK (((purpose)::text = ANY ((ARRAY['PasswordReset'::character varying, 'MfaChallenge'::character varying, 'Invitation'::character varying, 'EmailConfirm'::character varying])::text[]))),
    CONSTRAINT ck_auth_tokens__attempts_nonnegative CHECK ((attempts >= 0)),
    CONSTRAINT ck_auth_tokens__subject_xor CHECK (((((subject_kind)::text = 'Tenant'::text) AND (user_id IS NOT NULL) AND (platform_user_id IS NULL) AND (tenant_id IS NOT NULL)) OR (((subject_kind)::text = 'Platform'::text) AND (user_id IS NULL) AND (platform_user_id IS NOT NULL) AND (tenant_id IS NULL))))
);

ALTER TABLE ONLY public.auth_tokens FORCE ROW LEVEL SECURITY;


-- Name: TABLE auth_tokens; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.auth_tokens IS 'Single-use, expiring tokens for password reset, MFA challenge, invitation and email confirmation, for either subject kind, with an attempt counter that supports lockout. @tier:T/P @owner:Platform @retention:1-month-after-Expiry-then-Purge';


-- Name: COLUMN auth_tokens.token_hash; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.auth_tokens.token_hash IS 'Secret column: REVOKE from kynex_ro by column privilege (§19.2). Lowercase hex; the plaintext token is never stored.';


-- Name: background_job_items; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.background_job_items (
    id uuid NOT NULL,
    tenant_id uuid,
    job_id uuid NOT NULL,
    status character varying(40) DEFAULT 'Pending'::character varying NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    row_ref text,
    entity_id uuid,
    correlation_id uuid,
    error_code character varying(64),
    error_message text,
    processed_at timestamp with time zone,
    CONSTRAINT chk_background_job_items_status CHECK (((status)::text = ANY ((ARRAY['Pending'::character varying, 'Succeeded'::character varying, 'Failed'::character varying, 'Skipped'::character varying])::text[])))
)
PARTITION BY RANGE (created_at);

ALTER TABLE ONLY public.background_job_items FORCE ROW LEVEL SECURITY;


-- Name: TABLE background_job_items; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.background_job_items IS 'Records the per-row outcome of a background job so a partially failed import names the rows that failed and why, rather than failing as a whole. @tier:T/P @owner:Platform @retention:6m-purge';


-- Name: background_job_items_default; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.background_job_items_default (
    id uuid NOT NULL,
    tenant_id uuid,
    job_id uuid NOT NULL,
    status character varying(40) DEFAULT 'Pending'::character varying NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    row_ref text,
    entity_id uuid,
    correlation_id uuid,
    error_code character varying(64),
    error_message text,
    processed_at timestamp with time zone,
    CONSTRAINT chk_background_job_items_status CHECK (((status)::text = ANY ((ARRAY['Pending'::character varying, 'Succeeded'::character varying, 'Failed'::character varying, 'Skipped'::character varying])::text[])))
);

ALTER TABLE ONLY public.background_job_items_default FORCE ROW LEVEL SECURITY;


-- Name: TABLE background_job_items_default; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.background_job_items_default IS 'DEFAULT catch-all (§19.3 rule 3). A row landing here is an incident: the month partition was missing. Alert on the FIRST row — recovery is DETACH CONCURRENTLY, create the month, batched INSERT…SELECT, then an ATTACH that takes ACCESS EXCLUSIVE and a full validation scan.';


-- Name: background_job_items_y2025m10; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.background_job_items_y2025m10 (
    id uuid NOT NULL,
    tenant_id uuid,
    job_id uuid NOT NULL,
    status character varying(40) DEFAULT 'Pending'::character varying NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    row_ref text,
    entity_id uuid,
    correlation_id uuid,
    error_code character varying(64),
    error_message text,
    processed_at timestamp with time zone,
    CONSTRAINT chk_background_job_items_status CHECK (((status)::text = ANY ((ARRAY['Pending'::character varying, 'Succeeded'::character varying, 'Failed'::character varying, 'Skipped'::character varying])::text[])))
);

ALTER TABLE ONLY public.background_job_items_y2025m10 FORCE ROW LEVEL SECURITY;


-- Name: background_job_items_y2025m11; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.background_job_items_y2025m11 (
    id uuid NOT NULL,
    tenant_id uuid,
    job_id uuid NOT NULL,
    status character varying(40) DEFAULT 'Pending'::character varying NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    row_ref text,
    entity_id uuid,
    correlation_id uuid,
    error_code character varying(64),
    error_message text,
    processed_at timestamp with time zone,
    CONSTRAINT chk_background_job_items_status CHECK (((status)::text = ANY ((ARRAY['Pending'::character varying, 'Succeeded'::character varying, 'Failed'::character varying, 'Skipped'::character varying])::text[])))
);

ALTER TABLE ONLY public.background_job_items_y2025m11 FORCE ROW LEVEL SECURITY;


-- Name: background_job_items_y2025m12; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.background_job_items_y2025m12 (
    id uuid NOT NULL,
    tenant_id uuid,
    job_id uuid NOT NULL,
    status character varying(40) DEFAULT 'Pending'::character varying NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    row_ref text,
    entity_id uuid,
    correlation_id uuid,
    error_code character varying(64),
    error_message text,
    processed_at timestamp with time zone,
    CONSTRAINT chk_background_job_items_status CHECK (((status)::text = ANY ((ARRAY['Pending'::character varying, 'Succeeded'::character varying, 'Failed'::character varying, 'Skipped'::character varying])::text[])))
);

ALTER TABLE ONLY public.background_job_items_y2025m12 FORCE ROW LEVEL SECURITY;


-- Name: background_job_items_y2026m01; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.background_job_items_y2026m01 (
    id uuid NOT NULL,
    tenant_id uuid,
    job_id uuid NOT NULL,
    status character varying(40) DEFAULT 'Pending'::character varying NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    row_ref text,
    entity_id uuid,
    correlation_id uuid,
    error_code character varying(64),
    error_message text,
    processed_at timestamp with time zone,
    CONSTRAINT chk_background_job_items_status CHECK (((status)::text = ANY ((ARRAY['Pending'::character varying, 'Succeeded'::character varying, 'Failed'::character varying, 'Skipped'::character varying])::text[])))
);

ALTER TABLE ONLY public.background_job_items_y2026m01 FORCE ROW LEVEL SECURITY;


-- Name: background_job_items_y2026m02; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.background_job_items_y2026m02 (
    id uuid NOT NULL,
    tenant_id uuid,
    job_id uuid NOT NULL,
    status character varying(40) DEFAULT 'Pending'::character varying NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    row_ref text,
    entity_id uuid,
    correlation_id uuid,
    error_code character varying(64),
    error_message text,
    processed_at timestamp with time zone,
    CONSTRAINT chk_background_job_items_status CHECK (((status)::text = ANY ((ARRAY['Pending'::character varying, 'Succeeded'::character varying, 'Failed'::character varying, 'Skipped'::character varying])::text[])))
);

ALTER TABLE ONLY public.background_job_items_y2026m02 FORCE ROW LEVEL SECURITY;


-- Name: background_job_items_y2026m03; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.background_job_items_y2026m03 (
    id uuid NOT NULL,
    tenant_id uuid,
    job_id uuid NOT NULL,
    status character varying(40) DEFAULT 'Pending'::character varying NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    row_ref text,
    entity_id uuid,
    correlation_id uuid,
    error_code character varying(64),
    error_message text,
    processed_at timestamp with time zone,
    CONSTRAINT chk_background_job_items_status CHECK (((status)::text = ANY ((ARRAY['Pending'::character varying, 'Succeeded'::character varying, 'Failed'::character varying, 'Skipped'::character varying])::text[])))
);

ALTER TABLE ONLY public.background_job_items_y2026m03 FORCE ROW LEVEL SECURITY;


-- Name: background_job_items_y2026m04; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.background_job_items_y2026m04 (
    id uuid NOT NULL,
    tenant_id uuid,
    job_id uuid NOT NULL,
    status character varying(40) DEFAULT 'Pending'::character varying NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    row_ref text,
    entity_id uuid,
    correlation_id uuid,
    error_code character varying(64),
    error_message text,
    processed_at timestamp with time zone,
    CONSTRAINT chk_background_job_items_status CHECK (((status)::text = ANY ((ARRAY['Pending'::character varying, 'Succeeded'::character varying, 'Failed'::character varying, 'Skipped'::character varying])::text[])))
);

ALTER TABLE ONLY public.background_job_items_y2026m04 FORCE ROW LEVEL SECURITY;


-- Name: background_job_items_y2026m05; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.background_job_items_y2026m05 (
    id uuid NOT NULL,
    tenant_id uuid,
    job_id uuid NOT NULL,
    status character varying(40) DEFAULT 'Pending'::character varying NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    row_ref text,
    entity_id uuid,
    correlation_id uuid,
    error_code character varying(64),
    error_message text,
    processed_at timestamp with time zone,
    CONSTRAINT chk_background_job_items_status CHECK (((status)::text = ANY ((ARRAY['Pending'::character varying, 'Succeeded'::character varying, 'Failed'::character varying, 'Skipped'::character varying])::text[])))
);

ALTER TABLE ONLY public.background_job_items_y2026m05 FORCE ROW LEVEL SECURITY;


-- Name: background_job_items_y2026m06; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.background_job_items_y2026m06 (
    id uuid NOT NULL,
    tenant_id uuid,
    job_id uuid NOT NULL,
    status character varying(40) DEFAULT 'Pending'::character varying NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    row_ref text,
    entity_id uuid,
    correlation_id uuid,
    error_code character varying(64),
    error_message text,
    processed_at timestamp with time zone,
    CONSTRAINT chk_background_job_items_status CHECK (((status)::text = ANY ((ARRAY['Pending'::character varying, 'Succeeded'::character varying, 'Failed'::character varying, 'Skipped'::character varying])::text[])))
);

ALTER TABLE ONLY public.background_job_items_y2026m06 FORCE ROW LEVEL SECURITY;


-- Name: background_job_items_y2026m07; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.background_job_items_y2026m07 (
    id uuid NOT NULL,
    tenant_id uuid,
    job_id uuid NOT NULL,
    status character varying(40) DEFAULT 'Pending'::character varying NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    row_ref text,
    entity_id uuid,
    correlation_id uuid,
    error_code character varying(64),
    error_message text,
    processed_at timestamp with time zone,
    CONSTRAINT chk_background_job_items_status CHECK (((status)::text = ANY ((ARRAY['Pending'::character varying, 'Succeeded'::character varying, 'Failed'::character varying, 'Skipped'::character varying])::text[])))
);

ALTER TABLE ONLY public.background_job_items_y2026m07 FORCE ROW LEVEL SECURITY;


-- Name: background_job_items_y2026m08; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.background_job_items_y2026m08 (
    id uuid NOT NULL,
    tenant_id uuid,
    job_id uuid NOT NULL,
    status character varying(40) DEFAULT 'Pending'::character varying NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    row_ref text,
    entity_id uuid,
    correlation_id uuid,
    error_code character varying(64),
    error_message text,
    processed_at timestamp with time zone,
    CONSTRAINT chk_background_job_items_status CHECK (((status)::text = ANY ((ARRAY['Pending'::character varying, 'Succeeded'::character varying, 'Failed'::character varying, 'Skipped'::character varying])::text[])))
);

ALTER TABLE ONLY public.background_job_items_y2026m08 FORCE ROW LEVEL SECURITY;


-- Name: background_job_items_y2026m09; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.background_job_items_y2026m09 (
    id uuid NOT NULL,
    tenant_id uuid,
    job_id uuid NOT NULL,
    status character varying(40) DEFAULT 'Pending'::character varying NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    row_ref text,
    entity_id uuid,
    correlation_id uuid,
    error_code character varying(64),
    error_message text,
    processed_at timestamp with time zone,
    CONSTRAINT chk_background_job_items_status CHECK (((status)::text = ANY ((ARRAY['Pending'::character varying, 'Succeeded'::character varying, 'Failed'::character varying, 'Skipped'::character varying])::text[])))
);

ALTER TABLE ONLY public.background_job_items_y2026m09 FORCE ROW LEVEL SECURITY;


-- Name: background_job_items_y2026m10; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.background_job_items_y2026m10 (
    id uuid NOT NULL,
    tenant_id uuid,
    job_id uuid NOT NULL,
    status character varying(40) DEFAULT 'Pending'::character varying NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    row_ref text,
    entity_id uuid,
    correlation_id uuid,
    error_code character varying(64),
    error_message text,
    processed_at timestamp with time zone,
    CONSTRAINT chk_background_job_items_status CHECK (((status)::text = ANY ((ARRAY['Pending'::character varying, 'Succeeded'::character varying, 'Failed'::character varying, 'Skipped'::character varying])::text[])))
);

ALTER TABLE ONLY public.background_job_items_y2026m10 FORCE ROW LEVEL SECURITY;


-- Name: background_job_items_y2026m11; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.background_job_items_y2026m11 (
    id uuid NOT NULL,
    tenant_id uuid,
    job_id uuid NOT NULL,
    status character varying(40) DEFAULT 'Pending'::character varying NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    row_ref text,
    entity_id uuid,
    correlation_id uuid,
    error_code character varying(64),
    error_message text,
    processed_at timestamp with time zone,
    CONSTRAINT chk_background_job_items_status CHECK (((status)::text = ANY ((ARRAY['Pending'::character varying, 'Succeeded'::character varying, 'Failed'::character varying, 'Skipped'::character varying])::text[])))
);

ALTER TABLE ONLY public.background_job_items_y2026m11 FORCE ROW LEVEL SECURITY;


-- Name: background_job_items_y2026m12; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.background_job_items_y2026m12 (
    id uuid NOT NULL,
    tenant_id uuid,
    job_id uuid NOT NULL,
    status character varying(40) DEFAULT 'Pending'::character varying NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    row_ref text,
    entity_id uuid,
    correlation_id uuid,
    error_code character varying(64),
    error_message text,
    processed_at timestamp with time zone,
    CONSTRAINT chk_background_job_items_status CHECK (((status)::text = ANY ((ARRAY['Pending'::character varying, 'Succeeded'::character varying, 'Failed'::character varying, 'Skipped'::character varying])::text[])))
);

ALTER TABLE ONLY public.background_job_items_y2026m12 FORCE ROW LEVEL SECURITY;


-- Name: background_jobs; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.background_jobs (
    id uuid NOT NULL,
    tenant_id uuid,
    source_file_id uuid,
    kind character varying(64) NOT NULL,
    status character varying(40) DEFAULT 'Queued'::character varying NOT NULL,
    correlation_id uuid,
    idempotency_key text,
    payload jsonb,
    result jsonb,
    progress_current integer DEFAULT 0 NOT NULL,
    progress_total integer,
    attempts integer DEFAULT 0 NOT NULL,
    last_error text,
    lease_owner character varying(128),
    heartbeat_at timestamp with time zone,
    lease_expires_at timestamp with time zone,
    source_file_sha256 text,
    scheduled_at timestamp with time zone,
    started_at timestamp with time zone,
    completed_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT chk_background_jobs_status CHECK (((status)::text = ANY ((ARRAY['Queued'::character varying, 'Leased'::character varying, 'Running'::character varying, 'Succeeded'::character varying, 'Failed'::character varying, 'Cancelled'::character varying])::text[]))),
    CONSTRAINT ck_background_jobs__attempts_non_negative CHECK ((attempts >= 0)),
    CONSTRAINT ck_background_jobs__progress CHECK (((progress_current >= 0) AND ((progress_total IS NULL) OR ((progress_total >= 0) AND (progress_current <= progress_total)))))
);

ALTER TABLE ONLY public.background_jobs FORCE ROW LEVEL SECURITY;


-- Name: TABLE background_jobs; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.background_jobs IS 'Is one asynchronous, resumable and idempotent unit of work with its payload, lease, heartbeat, attempt count and result, covering every import, export, sync and retention run in the product. @tier:T/P @owner:Platform @retention:6m-purge';


-- Name: branches; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.branches (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    company_id uuid NOT NULL,
    name text NOT NULL,
    city text,
    address text,
    lat numeric(9,6),
    lng numeric(9,6),
    geofence_radius_m integer,
    holiday_calendar_code character varying(40),
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid
);

ALTER TABLE ONLY public.branches FORCE ROW LEVEL SECURITY;


-- Name: TABLE branches; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.branches IS 'A physical site of a legal entity, carrying the geofence that validates a mobile punch and the holiday calendar that shapes its working days. @tier:C @owner:HR';


-- Name: companies; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.companies (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    cr_number character varying(15),
    mol_establishment_no character varying(20),
    gosi_registration_no character varying(20),
    name_en text NOT NULL,
    name_ar text,
    nitaqat_activity_code character varying(16),
    wps_bank_code character varying(16),
    wps_mol_id character varying(20),
    currency_code character(3) DEFAULT 'SAR'::bpchar NOT NULL,
    timezone_id character varying(64),
    go_live_year smallint,
    go_live_month smallint,
    settings jsonb DEFAULT '{}'::jsonb NOT NULL,
    soft_deleted_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT ck_companies__currency_code_format CHECK ((currency_code ~ '^[A-Z]{3}$'::text)),
    CONSTRAINT ck_companies__go_live_month_range CHECK (((go_live_month IS NULL) OR ((go_live_month >= 1) AND (go_live_month <= 12)))),
    CONSTRAINT ck_companies__go_live_period_complete CHECK (((go_live_year IS NULL) = (go_live_month IS NULL)))
);

ALTER TABLE ONLY public.companies FORCE ROW LEVEL SECURITY;


-- Name: TABLE companies; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.companies IS 'The legal entity and its MOL establishment: the registration identifiers every statutory filing is made under, the currency of record, an optional timezone override and the mid-year go-live period. @tier:T @owner:Finance @retention:Keep';


-- Name: COLUMN companies.gosi_registration_no; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.companies.gosi_registration_no IS 'The single writable copy of the GOSI establishment number (§11.5). A typo can exist in exactly one place and cannot propagate into a filing.';


-- Name: COLUMN companies.currency_code; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.companies.currency_code IS 'SAR is the currency of record. Every money column in the design is denominated in THIS company''s currency; there is no per-row currency column anywhere (§13.2).';


-- Name: COLUMN companies.go_live_year; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.companies.go_live_year IS 'The go-live period as two typed columns, not a named concept (§2.C, revision 6 correction).';


-- Name: COLUMN companies.settings; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.companies.settings IS 'Non-money company overrides only. Contractual pay parameters moved OUT to company_pay_policies in revision 3 precisely so they get the dated EXCLUDE discipline (§13.5).';


-- Name: company_pay_policies; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.company_pay_policies (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    company_id uuid NOT NULL,
    policy_key character varying(64) NOT NULL,
    pay_component_code character varying(32),
    effective_from date NOT NULL,
    effective_to date,
    rate numeric(9,6),
    amount numeric(18,2),
    value_json jsonb,
    approved_by uuid,
    source_reference text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT ck_company_pay_policies__effective_range CHECK (((effective_to IS NULL) OR (effective_to >= effective_from))),
    CONSTRAINT ck_company_pay_policies__value_json_bounded CHECK (((value_json IS NULL) OR (pg_column_size(value_json) <= 8192)))
);

ALTER TABLE ONLY public.company_pay_policies FORCE ROW LEVEL SECURITY;


-- Name: TABLE company_pay_policies; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.company_pay_policies IS 'Contractual, above-statutory-floor pay parameters per legal entity, effective-dated so the rate in force on any day is a single row rather than a JSON lookup. @tier:C @owner:Finance @retention:Keep';


-- Name: COLUMN company_pay_policies.value_json; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.company_pay_policies.value_json IS 'Bounded to 8 KiB by CHECK and schema-validated on write. May only EXCEED a statutory floor, checked against the rule in force (§11.6).';


-- Name: COLUMN company_pay_policies.approved_by; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.company_pay_policies.approved_by IS 'Plain uuid, not an FK: §8.2 registers no FK for this column and a purged approver must not block a contractual row (same rule as created_by).';


-- Name: cost_centers; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.cost_centers (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    company_id uuid NOT NULL,
    parent_id uuid,
    code character varying(40) NOT NULL,
    name text NOT NULL,
    gl_segment character varying(64),
    is_active boolean DEFAULT true NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT ck_cost_centers__parent_not_self CHECK (((parent_id IS NULL) OR (parent_id <> id)))
);

ALTER TABLE ONLY public.cost_centers FORCE ROW LEVEL SECURITY;


-- Name: TABLE cost_centers; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.cost_centers IS 'The costing dimension shared by GL export, timesheets, payroll inputs and slip lines, carrying the segment the ERP''s chart of accounts expects. @tier:C @owner:Finance';


-- Name: data_protection_keys; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.data_protection_keys (
    id uuid NOT NULL,
    friendly_name text NOT NULL,
    xml text NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid
);

ALTER TABLE ONLY public.data_protection_keys FORCE ROW LEVEL SECURITY;


-- Name: TABLE data_protection_keys; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.data_protection_keys IS 'The ASP.NET Data Protection key ring whose keys encrypt MFA secrets and single-use tokens at rest. @tier:P @owner:Platform @retention:Keep';


-- Name: COLUMN data_protection_keys.xml; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.data_protection_keys.xml IS 'Secret column: REVOKE from kynex_ro by column privilege (§19.2).';


-- Name: departments; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.departments (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    company_id uuid NOT NULL,
    parent_id uuid,
    cost_center_id uuid,
    name text NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT ck_departments__parent_not_self CHECK (((parent_id IS NULL) OR (parent_id <> id)))
);

ALTER TABLE ONLY public.departments FORCE ROW LEVEL SECURITY;


-- Name: TABLE departments; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.departments IS 'The org-unit hierarchy within a legal entity, optionally pointing at the cost centre its salary cost posts to. @tier:C @owner:HR';


-- Name: designations; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.designations (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    occupation_code character varying(16),
    title_en text NOT NULL,
    title_ar text,
    is_active boolean DEFAULT true NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid
);

ALTER TABLE ONLY public.designations FORCE ROW LEVEL SECURITY;


-- Name: TABLE designations; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.designations IS 'Job titles in English and Arabic with the MHRSD/GOSI occupation code that Saudization-restricted jobs and GOSI registration require. @tier:T @owner:HR';


-- Name: document_templates; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.document_templates (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    company_id uuid,
    code character varying(64) NOT NULL,
    kind character varying(40) NOT NULL,
    version integer DEFAULT 1 NOT NULL,
    body_en text,
    body_ar text,
    merge_fields jsonb,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT ck_document_templates__kind CHECK (((kind)::text = ANY ((ARRAY['Letter'::character varying, 'Payslip'::character varying, 'Contract'::character varying])::text[]))),
    CONSTRAINT ck_document_templates__version_positive CHECK ((version >= 1))
);

ALTER TABLE ONLY public.document_templates FORCE ROW LEVEL SECURITY;


-- Name: TABLE document_templates; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.document_templates IS 'Versioned letter, payslip and contract bodies in English and Arabic with their merge-field contract, scoped to one company or to the whole tenant. @tier:T @owner:HR';


-- Name: COLUMN document_templates.version; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.document_templates.version IS 'Immutable once a document or slip references it; a change is a NEW row with the next version (§6).';


-- Name: employee_assignments; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.employee_assignments (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    company_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    branch_id uuid NOT NULL,
    department_id uuid NOT NULL,
    designation_id uuid NOT NULL,
    grade_id uuid,
    manager_employee_id uuid,
    cost_center_id uuid,
    approval_request_id uuid,
    employment_status character varying(40),
    pay_group character varying(40),
    effective_from date NOT NULL,
    effective_to date,
    change_reason text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT ck_employee_assignments__effective_range CHECK (((effective_to IS NULL) OR (effective_to >= effective_from))),
    CONSTRAINT ck_employee_assignments__manager_not_self CHECK (((manager_employee_id IS NULL) OR (manager_employee_id <> employee_id)))
);

ALTER TABLE ONLY public.employee_assignments FORCE ROW LEVEL SECURITY;


-- Name: TABLE employee_assignments; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.employee_assignments IS 'The effective-dated record of where a person sat — company, branch, department, designation, grade, manager and cost centre — and the single authority for whether they were employed on any given date. @tier:C @owner:HR @retention:Keep';


-- Name: COLUMN employee_assignments.cost_center_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.employee_assignments.cost_center_id IS 'Optional in the schema on purpose: a company that posts GL by cost centre gets a payroll_issues BLOCK at calculation instead of a NOT NULL that stops HR saving an employee (§8 row 40).';


-- Name: COLUMN employee_assignments.approval_request_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.employee_assignments.approval_request_id IS 'The decision behind the change. The row has no status of its own: the approval is its only state (§11.1).';


-- Name: employee_bank_accounts; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.employee_bank_accounts (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    effective_from date NOT NULL,
    effective_to date,
    iban character varying(34) NOT NULL,
    bank_code character varying(16),
    account_holder_name text,
    payment_method character varying(40),
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT ck_employee_bank_accounts__effective_range CHECK (((effective_to IS NULL) OR (effective_to >= effective_from))),
    CONSTRAINT ck_employee_bank_accounts__iban_format CHECK (((iban)::text ~ '^SA[0-9]{22}$'::text))
);

ALTER TABLE ONLY public.employee_bank_accounts FORCE ROW LEVEL SECURITY;


-- Name: TABLE employee_bank_accounts; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.employee_bank_accounts IS 'The effective-dated payment instruction an employee is paid to, so a WPS file filed last March can still be explained by the IBAN that was current then. @tier:T @owner:Finance @retention:Keep';


-- Name: COLUMN employee_bank_accounts.iban; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.employee_bank_accounts.iban IS 'Bounded to 34 and pattern-checked for SA IBANs by CHECK; the mod-97 checksum is enforced in the service (§13.3).';


-- Name: employee_contracts; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.employee_contracts (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    document_id uuid,
    contract_type character varying(40),
    effective_from date NOT NULL,
    effective_to date,
    start_date date,
    end_date date,
    probation_end date,
    weekly_hours numeric(6,2),
    notice_days integer,
    qiwa_contract_no text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT ck_employee_contracts__effective_range CHECK (((effective_to IS NULL) OR (effective_to >= effective_from)))
);

ALTER TABLE ONLY public.employee_contracts FORCE ROW LEVEL SECURITY;


-- Name: TABLE employee_contracts; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.employee_contracts IS 'The effective-dated employment contract — type, term, probation, contracted hours and notice period — with an optional pointer to the signed scan. @tier:T @owner:HR @retention:Keep';


-- Name: COLUMN employee_contracts.end_date; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.employee_contracts.end_date IS 'Named end_date, not `end`: `end` is a reserved word (CONVENTIONS.md §1).';


-- Name: COLUMN employee_contracts.qiwa_contract_no; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.employee_contracts.qiwa_contract_no IS 'Kept per §2.D. §18 says the external_system/external_id/external_synced_at trio should replace it — unreconciled in revision 6.';


-- Name: employee_documents; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.employee_documents (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    file_id uuid,
    template_id uuid,
    supersedes_id uuid,
    leave_request_id uuid,
    document_number character varying(64),
    letter_number character varying(40),
    verification_code character varying(64),
    doc_type character varying(40) NOT NULL,
    status character varying(40) DEFAULT 'Active'::character varying NOT NULL,
    issue_date date,
    expiry_date date,
    issuing_country character(2),
    version integer DEFAULT 1 NOT NULL,
    template_version integer,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT chk_employee_documents_status CHECK (((status)::text = ANY ((ARRAY['Active'::character varying, 'Expired'::character varying, 'Superseded'::character varying, 'Revoked'::character varying])::text[]))),
    CONSTRAINT chk_employee_documents_type CHECK (((doc_type)::text = ANY ((ARRAY['Iqama'::character varying, 'Passport'::character varying, 'Visa'::character varying, 'WorkPermit'::character varying, 'Contract'::character varying, 'Letter'::character varying, 'Medical'::character varying, 'Other'::character varying])::text[]))),
    CONSTRAINT ck_employee_documents__issuing_country_format CHECK (((issuing_country IS NULL) OR (issuing_country ~ '^[A-Z]{2}$'::text))),
    CONSTRAINT ck_employee_documents__supersedes_not_self CHECK (((supersedes_id IS NULL) OR (supersedes_id <> id))),
    CONSTRAINT ck_employee_documents__version_positive CHECK ((version >= 1))
);

ALTER TABLE ONLY public.employee_documents FORCE ROW LEVEL SECURITY;


-- Name: TABLE employee_documents; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.employee_documents IS 'Every document attached to a person — iqama, passport, visa, work permit, contract scan, issued letter, sick note — with its expiry, its version chain and the file that holds the blob. @tier:T @owner:HR @retention:84-months-from-Expiry-then-Purge';


-- Name: COLUMN employee_documents.supersedes_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.employee_documents.supersedes_id IS 'Renewal chain. SET NULL on delete so a chain survives a removed predecessor (§8 row 50); indexed parent-side in revision 6 (§19.4).';


-- Name: COLUMN employee_documents.leave_request_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.employee_documents.leave_request_id IS 'Present only for sick notes. FK deferred to the cross-domain constraints pass: leave_requests is domain J.';


-- Name: COLUMN employee_documents.expiry_date; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.employee_documents.expiry_date IS 'Read by the expiry-reminder job straight into notifications. There is deliberately no reminder table (§2.D).';


-- Name: employee_gosi_registrations; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.employee_gosi_registrations (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    company_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    gosi_registration_no character varying(20) NOT NULL,
    gosi_employee_no character varying(20),
    status character varying(40) DEFAULT 'Registered'::character varying NOT NULL,
    effective_from date NOT NULL,
    effective_to date,
    occupation_code character varying(16),
    registered_on date,
    deregistered_on date,
    registered_contributory_wage numeric(18,2),
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT chk_egr_status CHECK (((status)::text = ANY ((ARRAY['Registered'::character varying, 'Suspended'::character varying, 'Deregistered'::character varying])::text[]))),
    CONSTRAINT ck_employee_gosi_registrations__effective_range CHECK (((effective_to IS NULL) OR (effective_to >= effective_from)))
);

ALTER TABLE ONLY public.employee_gosi_registrations FORCE ROW LEVEL SECURITY;


-- Name: TABLE employee_gosi_registrations; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.employee_gosi_registrations IS 'Tracks the effective-dated GOSI registration of an employee against an establishment, including the contributory wage GOSI itself holds on file, which is what a filing variance is explained against. @tier:C @owner:Finance @retention:84m-keep';


-- Name: employee_salaries; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.employee_salaries (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    approval_request_id uuid,
    effective_from date NOT NULL,
    effective_to date,
    basic numeric(18,2) NOT NULL,
    housing numeric(18,2) DEFAULT 0 NOT NULL,
    transport numeric(18,2) DEFAULT 0 NOT NULL,
    housing_in_kind boolean DEFAULT false NOT NULL,
    components jsonb,
    change_reason text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT ck_employee_salaries__effective_range CHECK (((effective_to IS NULL) OR (effective_to >= effective_from)))
);

ALTER TABLE ONLY public.employee_salaries FORCE ROW LEVEL SECURITY;


-- Name: TABLE employee_salaries; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.employee_salaries IS 'The effective-dated salary structure — basic, housing, transport and any further components — that a payroll run resolves as of the period, in the employing company''s currency. @tier:T @owner:Finance @retention:Keep';


-- Name: COLUMN employee_salaries.housing_in_kind; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.employee_salaries.housing_in_kind IS 'When true, housing is deemed at the statutory percentage of basic for the contributory wage rather than paid in cash (§2.E).';


-- Name: COLUMN employee_salaries.components; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.employee_salaries.components IS 'Amounts only. There is no per-row currency column anywhere; the currency is companies.currency_code (§13.2).';


-- Name: employees; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.employees (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    employee_number character varying(32) NOT NULL,
    status character varying(40) DEFAULT 'Draft'::character varying NOT NULL,
    privacy_status character varying(40) DEFAULT 'Normal'::character varying NOT NULL,
    name_en text NOT NULL,
    name_ar text,
    work_email text,
    gender character varying(40),
    dob date,
    nationality_code character(2),
    national_id character varying(10),
    iqama_no character varying(10),
    border_no character varying(12),
    joining_date date,
    gosi_first_registered_on date,
    wps_eligible boolean DEFAULT true NOT NULL,
    nitaqat_weight_override numeric(9,6),
    nitaqat_weight_override_reason text,
    eos_service_start_date date,
    eos_prior_paid_amount numeric(18,2),
    separation_date date,
    deleted_at timestamp with time zone,
    retention_until date,
    redacted_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT chk_employees_privacy CHECK (((privacy_status)::text = ANY ((ARRAY['Normal'::character varying, 'PendingErasure'::character varying, 'Anonymised'::character varying, 'MergedDuplicate'::character varying])::text[]))),
    CONSTRAINT chk_employees_status CHECK (((status)::text = ANY ((ARRAY['Draft'::character varying, 'Invited'::character varying, 'Active'::character varying, 'Suspended'::character varying, 'Offboarded'::character varying, 'Archived'::character varying])::text[]))),
    CONSTRAINT ck_employees__iqama_no_format CHECK (((iqama_no IS NULL) OR ((iqama_no)::text ~ '^[12][0-9]{9}$'::text))),
    CONSTRAINT ck_employees__national_id_format CHECK (((national_id IS NULL) OR ((national_id)::text ~ '^[12][0-9]{9}$'::text))),
    CONSTRAINT ck_employees__nationality_code_format CHECK (((nationality_code IS NULL) OR (nationality_code ~ '^[A-Z]{2}$'::text)))
);

ALTER TABLE ONLY public.employees FORCE ROW LEVEL SECURITY;


-- Name: TABLE employees; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.employees IS 'The person: current identity, statutory identifiers, employment anchors and the PDPL lifecycle that lets an erasure anonymise the record in place while every dependent payroll row keeps its foreign key. @tier:T @owner:HR @retention:84-months-from-Separation-then-Anonymise';


-- Name: COLUMN employees.employee_number; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.employees.employee_number IS 'Allocated from number_sequences. Retained through anonymisation so statutory payroll rows stay traceable (§12.3).';


-- Name: COLUMN employees.status; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.employees.status IS 'Current-state PROJECTION maintained by EmployeeLifecycleService (§10.7). employee_assignments is authoritative for "was this person employed on date D" (§11.3).';


-- Name: COLUMN employees.work_email; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.employees.work_email IS 'Added to satisfy §19.4 H1, which indexes it; §2.D does not list it. See the note above this comment.';


-- Name: COLUMN employees.gosi_first_registered_on; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.employees.gosi_first_registered_on IS 'Drives the GOSI cohort (Legacy vs Entrant2024). NULL raises a BLOCKING payroll_issues row; the code never defaults to a cohort (§2.E).';


-- Name: COLUMN employees.separation_date; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.employees.separation_date IS 'Projection of final_settlements.last_working_day, written when the settlement is approved; the settlement is authoritative (§11.3).';


-- Name: eos_calculations; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.eos_calculations (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    settlement_id uuid,
    status character varying(40) DEFAULT 'Estimate'::character varying NOT NULL,
    separation_reason character varying(40) NOT NULL,
    calculation_date date NOT NULL,
    service_start_date date NOT NULL,
    service_end_date date NOT NULL,
    service_days integer NOT NULL,
    excluded_unpaid_days integer DEFAULT 0 NOT NULL,
    last_wage_basis character varying(40),
    eligible_wage numeric(18,2) DEFAULT 0 NOT NULL,
    amount numeric(18,2) DEFAULT 0 NOT NULL,
    prior_paid_deducted numeric(18,2) DEFAULT 0 NOT NULL,
    rules_version character varying(40),
    rules_snapshot jsonb DEFAULT '{}'::jsonb NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT chk_eos_calculations_status CHECK (((status)::text = ANY ((ARRAY['Estimate'::character varying, 'Final'::character varying, 'Superseded'::character varying])::text[]))),
    CONSTRAINT ck_eos_calculations__separation_reason CHECK (((separation_reason)::text = ANY ((ARRAY['Art84'::character varying, 'Art85'::character varying, 'Art87'::character varying, 'Art77'::character varying])::text[]))),
    CONSTRAINT ck_eos_calculations__service CHECK (((service_days >= 0) AND (excluded_unpaid_days >= 0) AND (service_end_date >= service_start_date)))
);

ALTER TABLE ONLY public.eos_calculations FORCE ROW LEVEL SECURITY;


-- Name: TABLE eos_calculations; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.eos_calculations IS 'Computes an end-of-service award under the Labour Law articles that apply to the separation, freezing the resolved statutory bands so the figure can be reconstructed years later. @tier:T @owner:Finance @retention:84m-keep';


-- Name: files; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.files (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    uploaded_by uuid,
    storage_key text NOT NULL,
    purpose character varying(40) NOT NULL,
    purge_state character varying(40) DEFAULT 'Active'::character varying NOT NULL,
    bucket character varying(63) NOT NULL,
    mime character varying(255) NOT NULL,
    size_bytes bigint NOT NULL,
    sha256 text NOT NULL,
    retention_until date,
    purged_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT chk_files_purge_state CHECK (((purge_state)::text = ANY ((ARRAY['Active'::character varying, 'PendingPurge'::character varying, 'Purged'::character varying])::text[]))),
    CONSTRAINT ck_files__purpose CHECK (((purpose)::text = ANY ((ARRAY['EmployeeDocument'::character varying, 'Payslip'::character varying, 'WpsSif'::character varying, 'GlExport'::character varying, 'BankConfirmation'::character varying, 'Import'::character varying, 'LetterPdf'::character varying])::text[]))),
    CONSTRAINT ck_files__size_bytes_nonnegative CHECK ((size_bytes >= 0))
);

ALTER TABLE ONLY public.files FORCE ROW LEVEL SECURITY;


-- Name: TABLE files; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.files IS 'Every stored blob with its hash and purge state, so PDPL erasure can delete the object while the referencing row keeps the sha256 as evidence the document existed. @tier:T @owner:HR @retention:84-months-from-Expiry-then-Purge';


-- Name: COLUMN files.sha256; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.files.sha256 IS 'Lowercase hex digest. Survives a purge as evidence (§12.3).';


-- Name: final_settlement_lines; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.final_settlement_lines (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    settlement_id uuid NOT NULL,
    loan_installment_id uuid,
    kind character varying(40) NOT NULL,
    description text,
    amount numeric(18,2) NOT NULL,
    source_type character varying(40),
    source_id uuid,
    rules_version character varying(40),
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    CONSTRAINT ck_final_settlement_lines__kind CHECK (((kind)::text = ANY ((ARRAY['EOS'::character varying, 'LeaveEncashment'::character varying, 'UnpaidSalary'::character varying, 'NoticePay'::character varying, 'Art77Compensation'::character varying, 'LoanRecovery'::character varying, 'OtherDeduction'::character varying])::text[])))
);

ALTER TABLE ONLY public.final_settlement_lines FORCE ROW LEVEL SECURITY;


-- Name: TABLE final_settlement_lines; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.final_settlement_lines IS 'Itemises a final settlement into its end-of-service, encashment, notice-pay and recovery components, frozen once the settlement is approved. @tier:C @owner:Finance @retention:84m-keep';


-- Name: final_settlements; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.final_settlements (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    company_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    paid_via_run_id uuid,
    approval_request_id uuid,
    settlement_number character varying(40),
    separation_type character varying(40) NOT NULL,
    status character varying(40) DEFAULT 'Draft'::character varying NOT NULL,
    last_working_day date NOT NULL,
    notice_given_on date,
    notice_served_days integer,
    gross numeric(18,2) DEFAULT 0 NOT NULL,
    deductions numeric(18,2) DEFAULT 0 NOT NULL,
    net numeric(18,2) DEFAULT 0 NOT NULL,
    clearance jsonb DEFAULT '[]'::jsonb NOT NULL,
    approved_at timestamp with time zone,
    paid_at timestamp with time zone,
    cancelled_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT chk_final_settlements_status CHECK (((status)::text = ANY ((ARRAY['Draft'::character varying, 'PendingApproval'::character varying, 'Approved'::character varying, 'Paid'::character varying, 'Cancelled'::character varying])::text[]))),
    CONSTRAINT ck_final_settlements__separation_type CHECK (((separation_type)::text = ANY ((ARRAY['Resignation'::character varying, 'Termination'::character varying, 'EndOfContract'::character varying, 'Retirement'::character varying, 'Death'::character varying, 'Abscond'::character varying])::text[])))
);

ALTER TABLE ONLY public.final_settlements FORCE ROW LEVEL SECURITY;


-- Name: TABLE final_settlements; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.final_settlements IS 'Is the separation case and settlement header for one employee, carrying the clearance checklist, the approved totals and the payroll run the settlement was paid through. @tier:C @owner:Finance @retention:84m-keep';


-- Name: gl_journal_lines; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.gl_journal_lines (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    journal_id uuid NOT NULL,
    cost_center_id uuid,
    line_order integer DEFAULT 0 NOT NULL,
    account character varying(64) NOT NULL,
    project_code character varying(64),
    description text,
    debit numeric(18,2) DEFAULT 0 NOT NULL,
    credit numeric(18,2) DEFAULT 0 NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    CONSTRAINT ck_gl_journal_lines__amounts_non_negative CHECK (((debit >= (0)::numeric) AND (credit >= (0)::numeric))),
    CONSTRAINT ck_gl_journal_lines__debit_xor_credit CHECK (((debit * credit) = (0)::numeric))
);

ALTER TABLE ONLY public.gl_journal_lines FORCE ROW LEVEL SECURITY;


-- Name: TABLE gl_journal_lines; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.gl_journal_lines IS 'Carries the balanced debit and credit lines of a GL journal together with their cost-centre and project segments. @tier:C @owner:Finance @retention:84m-keep';


-- Name: gl_journals; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.gl_journals (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    company_id uuid NOT NULL,
    source_type character varying(40) NOT NULL,
    source_id uuid,
    status character varying(40) DEFAULT 'Draft'::character varying NOT NULL,
    year smallint NOT NULL,
    month smallint NOT NULL,
    reversal_of_id uuid,
    erp_reference text,
    file_id uuid,
    export_file_sha256 text,
    idempotency_key text,
    posted_at timestamp with time zone,
    exported_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT chk_gl_journals_status CHECK (((status)::text = ANY ((ARRAY['Draft'::character varying, 'Exported'::character varying, 'Posted'::character varying, 'Rejected'::character varying, 'Reversed'::character varying])::text[]))),
    CONSTRAINT ck_gl_journals__period CHECK ((((month >= 1) AND (month <= 12)) AND ((year >= 2000) AND (year <= 2200)))),
    CONSTRAINT ck_gl_journals__source_type CHECK (((source_type)::text = ANY ((ARRAY['PayrollRun'::character varying, 'FinalSettlement'::character varying, 'LoanDisbursement'::character varying, 'Reversal'::character varying])::text[])))
);

ALTER TABLE ONLY public.gl_journals FORCE ROW LEVEL SECURITY;


-- Name: TABLE gl_journals; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.gl_journals IS 'Represents one general-ledger journal per source event, carrying the exported file, its hash, the ERP acknowledgement and the reversal chain. @tier:C @owner:Finance @retention:84m-keep';


-- Name: gl_mappings; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.gl_mappings (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    company_id uuid,
    cost_center_id uuid,
    gl_driver character varying(40) NOT NULL,
    debit_account character varying(64) NOT NULL,
    credit_account character varying(64) NOT NULL,
    is_active boolean DEFAULT true NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid
);

ALTER TABLE ONLY public.gl_mappings FORCE ROW LEVEL SECURITY;


-- Name: TABLE gl_mappings; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.gl_mappings IS 'Maps each payroll GL driver, optionally narrowed by company and cost centre, onto the debit and credit account codes owned by the customer ERP chart of accounts. @tier:T @owner:Finance @retention:tenant-lifecycle';


-- Name: gl_period_closes; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.gl_period_closes (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    company_id uuid NOT NULL,
    status character varying(40) DEFAULT 'Open'::character varying NOT NULL,
    year smallint NOT NULL,
    month smallint NOT NULL,
    reopen_reason text,
    closed_at timestamp with time zone,
    closed_by uuid,
    reopened_at timestamp with time zone,
    reopened_by uuid,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT chk_gl_period_closes_status CHECK (((status)::text = ANY ((ARRAY['Open'::character varying, 'Closed'::character varying, 'Reopened'::character varying])::text[]))),
    CONSTRAINT ck_gl_period_closes__period CHECK ((((month >= 1) AND (month <= 12)) AND ((year >= 2000) AND (year <= 2200))))
);

ALTER TABLE ONLY public.gl_period_closes FORCE ROW LEVEL SECURITY;


-- Name: TABLE gl_period_closes; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.gl_period_closes IS 'Records the finance lock on one accounting period per company, including who closed it and the reason any reopening was granted. @tier:C @owner:Finance @retention:84m-keep';


-- Name: gosi_filings; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.gosi_filings (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    company_id uuid NOT NULL,
    gosi_registration_no character varying(20) NOT NULL,
    status character varying(40) DEFAULT 'Draft'::character varying NOT NULL,
    year smallint NOT NULL,
    month smallint NOT NULL,
    revision integer DEFAULT 1 NOT NULL,
    employee_count integer DEFAULT 0 NOT NULL,
    annuities_employee numeric(18,2) DEFAULT 0 NOT NULL,
    annuities_employer numeric(18,2) DEFAULT 0 NOT NULL,
    saned_employee numeric(18,2) DEFAULT 0 NOT NULL,
    saned_employer numeric(18,2) DEFAULT 0 NOT NULL,
    occupational_hazards_employer numeric(18,2) DEFAULT 0 NOT NULL,
    total_contributory_wage numeric(18,2) DEFAULT 0 NOT NULL,
    total_amount numeric(18,2) DEFAULT 0 NOT NULL,
    gosi_invoice_amount numeric(18,2),
    variance_amount numeric(18,2),
    variance_reason text,
    rules_version character varying(40),
    file_id uuid,
    file_sha256 text,
    filed_at timestamp with time zone,
    filed_by uuid,
    reconciled_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT chk_gosi_filings_status CHECK (((status)::text = ANY ((ARRAY['Draft'::character varying, 'Filed'::character varying, 'Reconciled'::character varying, 'Disputed'::character varying, 'Superseded'::character varying])::text[]))),
    CONSTRAINT ck_gosi_filings__period CHECK ((((month >= 1) AND (month <= 12)) AND ((year >= 2000) AND (year <= 2200)))),
    CONSTRAINT ck_gosi_filings__revision CHECK ((revision >= 1))
);

ALTER TABLE ONLY public.gosi_filings FORCE ROW LEVEL SECURITY;


-- Name: TABLE gosi_filings; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.gosi_filings IS 'Holds the monthly GOSI return per establishment exactly as filed, with its seven branch-by-payer totals, the invoice it is reconciled against and the revision that supersedes a correction. @tier:C @owner:Finance @retention:84m-keep';


-- Name: grades; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.grades (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    code character varying(40) NOT NULL,
    name text NOT NULL,
    min_basic numeric(18,2),
    max_basic numeric(18,2),
    pay_scale jsonb,
    is_active boolean DEFAULT true NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT ck_grades__basic_band_ordered CHECK (((min_basic IS NULL) OR (max_basic IS NULL) OR (max_basic >= min_basic)))
);

ALTER TABLE ONLY public.grades FORCE ROW LEVEL SECURITY;


-- Name: TABLE grades; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.grades IS 'Salary grades with their basic-pay band and an optional component pay scale, used to validate and default an employee''s salary structure. @tier:T @owner:HR';


-- Name: leave_ledger; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.leave_ledger (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    leave_type_id uuid NOT NULL,
    entry_type character varying(40) NOT NULL,
    entry_date date NOT NULL,
    days numeric(9,2) NOT NULL,
    reason text,
    source_type character varying(40),
    source_id uuid,
    idempotency_key text NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    CONSTRAINT ck_leave_ledger__entry_type CHECK (((entry_type)::text = ANY ((ARRAY['Opening'::character varying, 'Accrual'::character varying, 'Debit'::character varying, 'Reversal'::character varying, 'CarryForward'::character varying, 'Expiry'::character varying, 'Encashment'::character varying, 'Adjustment'::character varying])::text[])))
);

ALTER TABLE ONLY public.leave_ledger FORCE ROW LEVEL SECURITY;


-- Name: TABLE leave_ledger; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.leave_ledger IS 'Is the append-only record of every movement in an employee leave balance, where a correction is a reversing row and the balance itself is only ever read through v_leave_balances. @tier:T @owner:HR @retention:84m-keep';


-- Name: leave_requests; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.leave_requests (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    leave_type_id uuid NOT NULL,
    approval_request_id uuid,
    request_kind character varying(40) DEFAULT 'Leave'::character varying NOT NULL,
    status character varying(40) DEFAULT 'Draft'::character varying NOT NULL,
    start_date date NOT NULL,
    end_date date NOT NULL,
    return_date date,
    days numeric(9,2) NOT NULL,
    reason text,
    contact_during_leave text,
    day_breakdown jsonb DEFAULT '[]'::jsonb NOT NULL,
    submitted_at timestamp with time zone,
    decided_at timestamp with time zone,
    cancelled_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT chk_leave_requests_status CHECK (((status)::text = ANY ((ARRAY['Draft'::character varying, 'PendingApproval'::character varying, 'Approved'::character varying, 'Rejected'::character varying, 'Cancelled'::character varying, 'Taken'::character varying])::text[]))),
    CONSTRAINT ck_leave_requests__date_range CHECK ((end_date >= start_date)),
    CONSTRAINT ck_leave_requests__days_positive CHECK ((days > (0)::numeric)),
    CONSTRAINT ck_leave_requests__request_kind CHECK (((request_kind)::text = ANY ((ARRAY['Leave'::character varying, 'Encashment'::character varying])::text[])))
);

ALTER TABLE ONLY public.leave_requests FORCE ROW LEVEL SECURITY;


-- Name: TABLE leave_requests; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.leave_requests IS 'Captures an employee request to take leave or to encash it, with the per-day breakdown and the approval decision that turns it into a ledger movement. @tier:T @owner:HR @retention:84m-keep';


-- Name: leave_types; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.leave_types (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    code character varying(32) NOT NULL,
    name_en text NOT NULL,
    name_ar text,
    is_statutory boolean DEFAULT false NOT NULL,
    is_paid boolean DEFAULT true NOT NULL,
    pay_rule_key character varying(64),
    policy jsonb DEFAULT '{}'::jsonb NOT NULL,
    is_active boolean DEFAULT true NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid
);

ALTER TABLE ONLY public.leave_types FORCE ROW LEVEL SECURITY;


-- Name: TABLE leave_types; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.leave_types IS 'Defines each leave type a tenant offers together with its contractual entitlement, accrual, carry-forward and eligibility policy above the statutory floor. @tier:T @owner:HR @retention:tenant-lifecycle';


-- Name: loan_installments; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.loan_installments (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    loan_id uuid NOT NULL,
    installment_number integer NOT NULL,
    kind character varying(40) DEFAULT 'Scheduled'::character varying NOT NULL,
    status character varying(40) DEFAULT 'Due'::character varying NOT NULL,
    due_year smallint NOT NULL,
    due_month smallint NOT NULL,
    amount numeric(18,2) NOT NULL,
    recovered_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT chk_loan_installments_status CHECK (((status)::text = ANY ((ARRAY['Due'::character varying, 'Recovered'::character varying, 'Waived'::character varying, 'Cancelled'::character varying])::text[]))),
    CONSTRAINT ck_loan_installments__due_period CHECK ((((due_month >= 1) AND (due_month <= 12)) AND ((due_year >= 2000) AND (due_year <= 2200)))),
    CONSTRAINT ck_loan_installments__installment_number CHECK ((installment_number >= 1)),
    CONSTRAINT ck_loan_installments__kind CHECK (((kind)::text = ANY ((ARRAY['Scheduled'::character varying, 'EarlySettlement'::character varying, 'FinalSettlement'::character varying, 'Waiver'::character varying])::text[])))
);

ALTER TABLE ONLY public.loan_installments FORCE ROW LEVEL SECURITY;


-- Name: TABLE loan_installments; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.loan_installments IS 'Schedules each recovery of a loan or advance by due period and records whether it was recovered, waived or cancelled; the recovering payroll or settlement line points back at this row. @tier:T @owner:Finance @retention:84m-keep';


-- Name: loans; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.loans (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    approval_request_id uuid,
    kind character varying(40) NOT NULL,
    type_code character varying(40),
    status character varying(40) DEFAULT 'PendingApproval'::character varying NOT NULL,
    start_year smallint NOT NULL,
    start_month smallint NOT NULL,
    installment_count integer NOT NULL,
    reason text,
    principal numeric(18,2) NOT NULL,
    opening_outstanding numeric(18,2) DEFAULT 0 NOT NULL,
    outstanding numeric(18,2) DEFAULT 0 NOT NULL,
    disbursed_at timestamp with time zone,
    settled_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT chk_loans_status CHECK (((status)::text = ANY ((ARRAY['PendingApproval'::character varying, 'Active'::character varying, 'Settled'::character varying, 'Cancelled'::character varying, 'Rejected'::character varying])::text[]))),
    CONSTRAINT ck_loans__amounts CHECK (((principal >= (0)::numeric) AND (opening_outstanding >= (0)::numeric) AND (outstanding >= (0)::numeric))),
    CONSTRAINT ck_loans__installment_count CHECK ((installment_count >= 1)),
    CONSTRAINT ck_loans__kind CHECK (((kind)::text = ANY ((ARRAY['Loan'::character varying, 'Advance'::character varying])::text[]))),
    CONSTRAINT ck_loans__start_period CHECK ((((start_month >= 1) AND (start_month <= 12)) AND ((start_year >= 2000) AND (start_year <= 2200))))
);

ALTER TABLE ONLY public.loans FORCE ROW LEVEL SECURITY;


-- Name: TABLE loans; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.loans IS 'Holds an employee loan or salary advance with its principal, opening balance carried in at go-live, approval and the outstanding amount reconciled against its recoveries. @tier:T @owner:Finance @retention:84m-keep';


-- Name: nitaqat_grid; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.nitaqat_grid (
    id uuid NOT NULL,
    activity_code character varying(32) NOT NULL,
    activity_name_en text,
    activity_name_ar text,
    size_tier character varying(40) NOT NULL,
    band character varying(40) NOT NULL,
    grid_version character varying(40) NOT NULL,
    effective_from date NOT NULL,
    effective_to date,
    headcount_min integer NOT NULL,
    headcount_max integer,
    min_saudization_pct numeric(9,6) NOT NULL,
    max_saudization_pct numeric(9,6),
    source_reference text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT ck_nitaqat_grid__band CHECK (((band)::text = ANY ((ARRAY['Platinum'::character varying, 'HighGreen'::character varying, 'MidGreen'::character varying, 'LowGreen'::character varying, 'Red'::character varying])::text[]))),
    CONSTRAINT ck_nitaqat_grid__effective_range CHECK (((effective_to IS NULL) OR (effective_to >= effective_from))),
    CONSTRAINT ck_nitaqat_grid__headcount_range CHECK (((headcount_min >= 0) AND ((headcount_max IS NULL) OR (headcount_max >= headcount_min)))),
    CONSTRAINT ck_nitaqat_grid__saudization_range CHECK (((min_saudization_pct >= (0)::numeric) AND ((max_saudization_pct IS NULL) OR (max_saudization_pct > min_saudization_pct))))
);

ALTER TABLE ONLY public.nitaqat_grid FORCE ROW LEVEL SECURITY;


-- Name: TABLE nitaqat_grid; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.nitaqat_grid IS 'Seeds the MHRSD Nitaqat colour bands as Saudization percentage ranges per economic activity, establishment size tier and published grid version. @tier:R @owner:Compliance @retention:indefinite-keep';


-- Name: nitaqat_snapshots; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.nitaqat_snapshots (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    company_id uuid NOT NULL,
    as_of_date date NOT NULL,
    activity_code character varying(32) NOT NULL,
    size_tier character varying(40) NOT NULL,
    band character varying(40) NOT NULL,
    total_headcount integer DEFAULT 0 NOT NULL,
    saudi_weighted numeric(18,2) DEFAULT 0 NOT NULL,
    total_weighted numeric(18,2) DEFAULT 0 NOT NULL,
    achieved_pct numeric(9,6) DEFAULT 0 NOT NULL,
    grid_version character varying(40),
    rules_version character varying(40),
    employee_breakdown jsonb DEFAULT '[]'::jsonb NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    CONSTRAINT ck_nitaqat_snapshots__band CHECK (((band)::text = ANY ((ARRAY['Platinum'::character varying, 'HighGreen'::character varying, 'MidGreen'::character varying, 'LowGreen'::character varying, 'Red'::character varying])::text[])))
);

ALTER TABLE ONLY public.nitaqat_snapshots FORCE ROW LEVEL SECURITY;


-- Name: TABLE nitaqat_snapshots; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.nitaqat_snapshots IS 'Freezes an establishment Saudization standing on one date, with the weighted headcounts, achieved percentage, awarded band and the per-employee breakdown that the figure drills down to. @tier:C @owner:Compliance @retention:84m-keep';


-- Name: notification_deliveries; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.notification_deliveries (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    notification_id uuid NOT NULL,
    channel character varying(40) NOT NULL,
    status character varying(40) DEFAULT 'Queued'::character varying NOT NULL,
    destination text,
    attempts integer DEFAULT 0 NOT NULL,
    next_attempt_at timestamp with time zone,
    provider_message_id text,
    last_error text,
    sent_at timestamp with time zone,
    delivered_at timestamp with time zone,
    dead_lettered_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT chk_notification_deliveries_status CHECK (((status)::text = ANY ((ARRAY['Queued'::character varying, 'Sent'::character varying, 'Delivered'::character varying, 'Failed'::character varying, 'Suppressed'::character varying, 'DeadLettered'::character varying])::text[]))),
    CONSTRAINT ck_notification_deliveries__attempts_non_negative CHECK ((attempts >= 0)),
    CONSTRAINT ck_notification_deliveries__channel CHECK (((channel)::text = ANY ((ARRAY['Email'::character varying, 'Sms'::character varying, 'Push'::character varying])::text[])))
);

ALTER TABLE ONLY public.notification_deliveries FORCE ROW LEVEL SECURITY;


-- Name: TABLE notification_deliveries; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.notification_deliveries IS 'Tracks each outbound email, SMS or push attempt for a notification through its retries to delivery, suppression or the dead-letter terminal state. @tier:T @owner:Platform @retention:12m-purge';


-- Name: notifications; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.notifications (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    user_id uuid NOT NULL,
    category character varying(40) NOT NULL,
    title text NOT NULL,
    body text,
    link text,
    source_type character varying(40),
    source_id uuid,
    idempotency_key text NOT NULL,
    read_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid
);

ALTER TABLE ONLY public.notifications FORCE ROW LEVEL SECURITY;


-- Name: TABLE notifications; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.notifications IS 'Is one in-app inbox item for a user, deduplicated by idempotency key so a retried producer cannot notify twice. @tier:T @owner:Platform @retention:12m-purge';


-- Name: number_sequences; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.number_sequences (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    company_id uuid,
    scope_key character varying(40) NOT NULL,
    period_key character varying(16),
    reset_period character varying(40) DEFAULT 'None'::character varying NOT NULL,
    prefix character varying(16),
    pattern character varying(64),
    next_value bigint DEFAULT 1 NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT ck_number_sequences__next_value_positive CHECK ((next_value >= 1)),
    CONSTRAINT ck_number_sequences__reset_period CHECK (((reset_period)::text = ANY ((ARRAY['None'::character varying, 'Year'::character varying, 'Month'::character varying])::text[]))),
    CONSTRAINT ck_number_sequences__scope_key CHECK (((scope_key)::text = ANY ((ARRAY['employee_no'::character varying, 'letter_no'::character varying, 'run_no'::character varying, 'wps_batch_no'::character varying, 'settlement_no'::character varying, 'gosi_filing_no'::character varying, 'timesheet_no'::character varying])::text[])))
);

ALTER TABLE ONLY public.number_sequences FORCE ROW LEVEL SECURITY;


-- Name: TABLE number_sequences; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.number_sequences IS 'Allocates every human-facing number (employee, letter, run, WPS batch, settlement, GOSI filing, timesheet) with one UPDATE ... RETURNING, so no counter ever lives in a settings blob. @tier:T @owner:Platform';


-- Name: overtime_requests; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.overtime_requests (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    statutory_rule_id uuid,
    approval_request_id uuid,
    ot_type character varying(40) NOT NULL,
    payout character varying(40) DEFAULT 'Pay'::character varying NOT NULL,
    status character varying(40) DEFAULT 'Draft'::character varying NOT NULL,
    work_date date NOT NULL,
    overtime_minutes integer NOT NULL,
    reason text,
    basic_hourly_rate numeric(18,2) NOT NULL,
    multiplier numeric(9,6) NOT NULL,
    amount numeric(18,2) NOT NULL,
    rules_version character varying(40),
    decided_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT chk_overtime_requests_status CHECK (((status)::text = ANY ((ARRAY['Draft'::character varying, 'PendingApproval'::character varying, 'Approved'::character varying, 'Rejected'::character varying, 'Cancelled'::character varying, 'Paid'::character varying, 'ConvertedToCompOff'::character varying])::text[]))),
    CONSTRAINT ck_overtime_requests__minutes_positive CHECK ((overtime_minutes > 0)),
    CONSTRAINT ck_overtime_requests__multiplier_positive CHECK ((multiplier > (0)::numeric)),
    CONSTRAINT ck_overtime_requests__ot_type CHECK (((ot_type)::text = ANY ((ARRAY['Normal'::character varying, 'WeeklyOff'::character varying, 'PublicHoliday'::character varying, 'Ramadan'::character varying])::text[]))),
    CONSTRAINT ck_overtime_requests__payout CHECK (((payout)::text = ANY ((ARRAY['Pay'::character varying, 'CompOff'::character varying])::text[])))
);

ALTER TABLE ONLY public.overtime_requests FORCE ROW LEVEL SECURITY;


-- Name: TABLE overtime_requests; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.overtime_requests IS 'Records an overtime claim in minutes for one local working day with the hourly rate, statutory multiplier and amount frozen at the moment it was calculated. @tier:T @owner:HR @retention:84m-keep';


-- Name: pay_components; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.pay_components (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    code character varying(32) NOT NULL,
    kind character varying(40) NOT NULL,
    name_en text NOT NULL,
    name_ar text,
    gosi_contributory boolean DEFAULT false NOT NULL,
    eos_eligible boolean DEFAULT false NOT NULL,
    prorate boolean DEFAULT false NOT NULL,
    gl_driver character varying(64),
    is_system boolean DEFAULT false NOT NULL,
    is_active boolean DEFAULT true NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT ck_pay_components__kind CHECK (((kind)::text = ANY ((ARRAY['Earning'::character varying, 'Deduction'::character varying, 'EmployerContribution'::character varying, 'Info'::character varying])::text[])))
);

ALTER TABLE ONLY public.pay_components FORCE ROW LEVEL SECURITY;


-- Name: TABLE pay_components; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.pay_components IS 'The catalogue of everything that can appear as a line on a payslip, each declaring whether it is GOSI-contributory, EOS-eligible, prorated and which GL driver it posts through. @tier:T @owner:Finance';


-- Name: COLUMN pay_components.is_system; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.pay_components.is_system IS 'Seeded per tenant at provisioning: BASIC, HOUSING, TRANSPORT, OT, GOSI_ANN_EE/ER, SANED_EE/ER, OH_ER, LOAN, ADVANCE, UNPAID_LEAVE, ABSENCE. A tenant may add components but not remove these.';


-- Name: payroll_audit_logs; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.payroll_audit_logs (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    run_id uuid,
    entity character varying(64) NOT NULL,
    entity_id uuid,
    action character varying(64) NOT NULL,
    seq bigint NOT NULL,
    user_id uuid,
    correlation_id uuid,
    hash_algorithm character varying(40) DEFAULT 'sha256'::character varying NOT NULL,
    before jsonb,
    after jsonb,
    metadata jsonb,
    prev_hash text,
    entry_hash text NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT ck_payroll_audit_logs__seq_positive CHECK ((seq > 0))
);

ALTER TABLE ONLY public.payroll_audit_logs FORCE ROW LEVEL SECURITY;


-- Name: TABLE payroll_audit_logs; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.payroll_audit_logs IS 'Is the separate, trigger-protected, row-chained log of every payroll state change, where each entry hash commits to its predecessor so the money path carries an unbroken chain rather than a checkpoint. @tier:T @owner:Compliance @retention:indefinite-keep';


-- Name: payroll_inputs; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.payroll_inputs (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    company_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    pay_component_code character varying(32) NOT NULL,
    cost_center_id uuid,
    claimed_by_run_id uuid,
    consumed_run_id uuid,
    kind character varying(40) NOT NULL,
    status character varying(40) DEFAULT 'Pending'::character varying NOT NULL,
    target_run_type character varying(40),
    source_type character varying(40) NOT NULL,
    source_id uuid,
    revision integer DEFAULT 1 NOT NULL,
    run_year smallint NOT NULL,
    run_month smallint NOT NULL,
    covered_year smallint NOT NULL,
    covered_month smallint NOT NULL,
    entitled_amount numeric(18,2) NOT NULL,
    previously_settled_amount numeric(18,2) DEFAULT 0 NOT NULL,
    amount numeric(18,2) NOT NULL,
    gosi_basis_delta numeric(18,2) DEFAULT 0 NOT NULL,
    claimed_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT chk_payroll_inputs_status CHECK (((status)::text = ANY ((ARRAY['Pending'::character varying, 'Claimed'::character varying, 'Consumed'::character varying, 'Cancelled'::character varying])::text[]))),
    CONSTRAINT ck_payroll_inputs__amount_is_net_entitlement CHECK ((amount = (entitled_amount - previously_settled_amount))),
    CONSTRAINT ck_payroll_inputs__covered_month_range CHECK (((covered_month >= 1) AND (covered_month <= 12))),
    CONSTRAINT ck_payroll_inputs__kind CHECK (((kind)::text = ANY ((ARRAY['Adjustment'::character varying, 'Arrears'::character varying, 'Receivable'::character varying, 'Overtime'::character varying, 'UnpaidLeave'::character varying, 'Absence'::character varying, 'LeaveEncashment'::character varying, 'Bonus'::character varying])::text[]))),
    CONSTRAINT ck_payroll_inputs__revision_positive CHECK ((revision >= 1)),
    CONSTRAINT ck_payroll_inputs__run_month_range CHECK (((run_month >= 1) AND (run_month <= 12))),
    CONSTRAINT ck_payroll_inputs__source_id_presence CHECK (((((source_type)::text = ANY ((ARRAY['Manual'::character varying, 'Bonus'::character varying])::text[])) AND (source_id IS NULL)) OR (((source_type)::text <> ALL ((ARRAY['Manual'::character varying, 'Bonus'::character varying])::text[])) AND (source_id IS NOT NULL)))),
    CONSTRAINT ck_payroll_inputs__source_type CHECK (((source_type)::text = ANY ((ARRAY['Overtime'::character varying, 'Leave'::character varying, 'Attendance'::character varying, 'Timesheet'::character varying, 'Manual'::character varying, 'Opening'::character varying, 'Bonus'::character varying])::text[]))),
    CONSTRAINT ck_payroll_inputs__target_run_type CHECK (((target_run_type IS NULL) OR ((target_run_type)::text = ANY ((ARRAY['Regular'::character varying, 'OffCycle'::character varying, 'FinalSettlement'::character varying, 'Correction'::character varying, 'Opening'::character varying])::text[]))))
);

ALTER TABLE ONLY public.payroll_inputs FORCE ROW LEVEL SECURITY;


-- Name: TABLE payroll_inputs; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.payroll_inputs IS 'Variable pay waiting to be paid — adjustments, arrears, overtime, absence, encashment — each carrying the period it BELONGS to as well as the period it is paid in, so a backdated amount recalculates GOSI as data rather than as arithmetic in a service. @tier:C @owner:Finance @retention:Keep';


-- Name: COLUMN payroll_inputs.claimed_by_run_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.payroll_inputs.claimed_by_run_id IS 'SET NULL on delete so deleting a draft run releases the claim. A crashed run releases by predicate — status back to Pending where claimed_by_run_id points at a voided run — which is what makes a retry idempotent (§F).';


-- Name: COLUMN payroll_inputs.source_type; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.payroll_inputs.source_type IS 'Polymorphic pointer with source_id (§16): Overtime -> overtime_requests, Leave -> leave_requests, Attendance -> attendance_days, Timesheet -> timesheets, Opening -> background_jobs, Manual/Bonus -> NULL. A nightly sweep reports unresolvable rows.';


-- Name: COLUMN payroll_inputs.revision; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.payroll_inputs.revision IS 'Cancelling an input BUMPS this and inserts the replacement; it never reuses the key its replacement needs (§2.F, CONVENTIONS.md §11).';


-- Name: COLUMN payroll_inputs.gosi_basis_delta; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.payroll_inputs.gosi_basis_delta IS 'The contributory-wage change this backdated amount causes in the COVERED period. Stored so the GOSI recalculation is data, not arithmetic in a service (§2.F).';


-- Name: payroll_issues; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.payroll_issues (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    run_id uuid,
    employee_id uuid,
    override_by uuid,
    code character varying(64) NOT NULL,
    severity character varying(40) NOT NULL,
    gap_type character varying(40),
    message text NOT NULL,
    evidence jsonb,
    detected_at timestamp with time zone DEFAULT now() NOT NULL,
    resolved_at timestamp with time zone,
    override_reason text,
    override_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT chk_payroll_issues_severity CHECK (((severity)::text = ANY ((ARRAY['Block'::character varying, 'Warn'::character varying])::text[]))),
    CONSTRAINT ck_payroll_issues__block_never_overridden CHECK ((((severity)::text <> 'Block'::text) OR ((override_by IS NULL) AND (override_at IS NULL) AND (override_reason IS NULL))))
);

ALTER TABLE ONLY public.payroll_issues FORCE ROW LEVEL SECURITY;


-- Name: TABLE payroll_issues; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.payroll_issues IS 'Every validation finding and standing readiness gap that blocks or warns a payroll run, with the evidence behind it and — for warnings only — who waived it and why. @tier:C @owner:Finance @retention:Keep';


-- Name: COLUMN payroll_issues.run_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.payroll_issues.run_id IS 'NULL = a standing gap that blocks ANY run for this employee (GOSI_COHORT_UNKNOWN, IBAN_MISSING, ORG_ESTABLISHMENT_MISSING, SALARY_HELD). Non-NULL = a finding of one run, which dies with a draft run.';


-- Name: COLUMN payroll_issues.severity; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.payroll_issues.severity IS 'Block is NEVER overridable — enforced by CHECK, not by the UI (§2.F). Warn may be overridden with a recorded reason.';


-- Name: payroll_runs; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.payroll_runs (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    company_id uuid NOT NULL,
    parent_run_id uuid,
    source_import_job_id uuid,
    approval_request_id uuid,
    run_type character varying(40) NOT NULL,
    status character varying(40) DEFAULT 'Draft'::character varying NOT NULL,
    year smallint NOT NULL,
    month smallint NOT NULL,
    attendance_locked_range daterange,
    employee_count integer DEFAULT 0 NOT NULL,
    selected_employee_count integer DEFAULT 0 NOT NULL,
    total_gross numeric(18,2) DEFAULT 0 NOT NULL,
    total_deductions numeric(18,2) DEFAULT 0 NOT NULL,
    total_net numeric(18,2) DEFAULT 0 NOT NULL,
    total_employer_statutory numeric(18,2) DEFAULT 0 NOT NULL,
    rules_version character varying(40),
    source_system character varying(40),
    idempotency_key text,
    selection jsonb,
    calculated_at timestamp with time zone,
    approved_at timestamp with time zone,
    locked_at timestamp with time zone,
    void_reason text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT chk_payroll_runs_status CHECK (((status)::text = ANY ((ARRAY['Draft'::character varying, 'Processing'::character varying, 'Processed'::character varying, 'PendingFinanceReview'::character varying, 'Approved'::character varying, 'Completed'::character varying, 'Locked'::character varying, 'Paid'::character varying, 'Voided'::character varying])::text[]))),
    CONSTRAINT chk_payroll_runs_type CHECK (((run_type)::text = ANY ((ARRAY['Regular'::character varying, 'OffCycle'::character varying, 'FinalSettlement'::character varying, 'Correction'::character varying, 'Opening'::character varying])::text[]))),
    CONSTRAINT ck_payroll_runs__counts_nonnegative CHECK (((employee_count >= 0) AND (selected_employee_count >= 0))),
    CONSTRAINT ck_payroll_runs__lock_inside_period CHECK (((attendance_locked_range IS NULL) OR (attendance_locked_range <@ daterange(make_date((year)::integer, (month)::integer, 1), ((make_date((year)::integer, (month)::integer, 1) + '1 mon'::interval))::date, '[)'::text)))),
    CONSTRAINT ck_payroll_runs__month_range CHECK (((month >= 1) AND (month <= 12))),
    CONSTRAINT ck_payroll_runs__opening_never_paid CHECK ((((run_type)::text <> 'Opening'::text) OR ((status)::text <> 'Paid'::text))),
    CONSTRAINT ck_payroll_runs__parent_not_self CHECK (((parent_run_id IS NULL) OR (parent_run_id <> id)))
);

ALTER TABLE ONLY public.payroll_runs FORCE ROW LEVEL SECURITY;


-- Name: TABLE payroll_runs; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.payroll_runs IS 'One payroll execution for a company and period — regular, off-cycle, correction, final settlement or the mid-year Opening import — carrying its selection, its cached totals, the rules version it applied and the attendance range it locked. @tier:C @owner:Finance @retention:Keep';


-- Name: COLUMN payroll_runs.source_import_job_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.payroll_runs.source_import_job_id IS 'Opening-run provenance. FK deferred to the cross-domain constraints pass: background_jobs is domain R.';


-- Name: COLUMN payroll_runs.attendance_locked_range; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.payroll_runs.attendance_locked_range IS 'Authoritative for the attendance lock; attendance_days.locked_run_id is the per-row projection written in the same transaction (§11.6). CHECK-constrained to lie inside the run period. Set on Approved, cleared on Void.';


-- Name: COLUMN payroll_runs.selected_employee_count; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.payroll_runs.selected_employee_count IS 'The run''s exit condition: it leaves Processing only when slips + explicitly excluded = this count (§10.1, §19.5).';


-- Name: COLUMN payroll_runs.total_gross; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.payroll_runs.total_gross IS 'CACHE of SUM over included slips, reconciled by trg_run_totals only in the transaction that moves Processing -> Processed. While Processing, every total_* column is UNDEFINED and must not be displayed as authoritative (§11.2).';


-- Name: payroll_slip_lines; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.payroll_slip_lines (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    slip_id uuid NOT NULL,
    pay_component_code character varying(32) NOT NULL,
    statutory_rule_id uuid,
    statutory_rule_band_id uuid,
    payroll_input_id uuid,
    loan_installment_id uuid,
    cost_center_id uuid,
    kind character varying(40) NOT NULL,
    amount numeric(18,2) NOT NULL,
    quantity numeric(18,6),
    rate numeric(9,6),
    gosi_branch character varying(40),
    gosi_payer character varying(40),
    applied_contributory_wage numeric(18,2),
    rules_version character varying(40),
    gl_driver character varying(64),
    source_type character varying(40),
    source_system character varying(40),
    source_record_id text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    CONSTRAINT chk_payroll_slip_lines_kind CHECK (((kind)::text = ANY ((ARRAY['Earning'::character varying, 'Deduction'::character varying, 'EmployerContribution'::character varying, 'Info'::character varying])::text[])))
);

ALTER TABLE ONLY public.payroll_slip_lines FORCE ROW LEVEL SECURITY;


-- Name: TABLE payroll_slip_lines; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.payroll_slip_lines IS 'The single line table behind every payslip, freezing the component, the GOSI branch, payer, applied wage, rule and band that produced each amount, plus where the amount came from. @tier:C @owner:Finance @retention:84-months-from-RecordDate-then-Keep';


-- Name: COLUMN payroll_slip_lines.payroll_input_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.payroll_slip_lines.payroll_input_id IS 'The line points at the input; the input does NOT point back. That is how the second cycle was broken (§8.4).';


-- Name: COLUMN payroll_slip_lines.loan_installment_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.payroll_slip_lines.loan_installment_id IS 'Recovery evidence, and the surviving half of the broken payroll_slip_lines <-> loan_installments cycle (§8.4). FK deferred to the cross-domain pass: loan_installments is domain I.';


-- Name: COLUMN payroll_slip_lines.applied_contributory_wage; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.payroll_slip_lines.applied_contributory_wage IS 'The wage actually applied to THIS line''s GOSI branch, which differs whenever a branch has its own floor or cap. Renamed in revision 3 to end the ambiguity with the slip-level figure (§11.4).';


-- Name: COLUMN payroll_slip_lines.source_record_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.payroll_slip_lines.source_record_id IS 'Opening-run provenance alongside source_system: which row of which legacy system this opening amount came from (§2.F).';


-- Name: payroll_slips; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.payroll_slips (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    run_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    payslip_file_id uuid,
    template_id uuid,
    payslip_number character varying(40),
    inclusion_status character varying(40) DEFAULT 'Included'::character varying NOT NULL,
    paid_from date,
    paid_to date,
    paid_days numeric(9,2),
    period_days numeric(9,2),
    proration_denominator_days numeric(9,2),
    proration_basis character varying(40),
    proration_factor numeric(9,6),
    gross numeric(18,2) DEFAULT 0 NOT NULL,
    deductions numeric(18,2) DEFAULT 0 NOT NULL,
    net numeric(18,2) DEFAULT 0 NOT NULL,
    employee_statutory_total numeric(18,2) DEFAULT 0 NOT NULL,
    employer_statutory_total numeric(18,2) DEFAULT 0 NOT NULL,
    loan_deductions numeric(18,2) DEFAULT 0 NOT NULL,
    arrears_amount numeric(18,2) DEFAULT 0 NOT NULL,
    is_final_wage_month boolean DEFAULT false NOT NULL,
    ytd_gross numeric(18,2) DEFAULT 0 NOT NULL,
    ytd_deductions numeric(18,2) DEFAULT 0 NOT NULL,
    ytd_net numeric(18,2) DEFAULT 0 NOT NULL,
    ytd_employee_statutory numeric(18,2) DEFAULT 0 NOT NULL,
    ytd_employer_statutory numeric(18,2) DEFAULT 0 NOT NULL,
    ytd_contributory_wage numeric(18,2) DEFAULT 0 NOT NULL,
    employee_number character varying(32),
    employee_name text,
    department_name text,
    designation_name text,
    nationality_class character varying(40),
    gosi_cohort character varying(40),
    employer_gosi_registration_no character varying(20),
    iban character varying(34),
    bank_code character varying(16),
    gosi_base_policy character varying(40),
    full_basic numeric(18,2),
    full_housing numeric(18,2),
    full_transport numeric(18,2),
    contributory_wage numeric(18,2),
    payslip_sha256 text,
    template_version integer,
    language character varying(40),
    published_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT chk_payroll_slips_inclusion CHECK (((inclusion_status)::text = ANY ((ARRAY['Included'::character varying, 'ExcludedByFilter'::character varying, 'ExcludedByHold'::character varying, 'ExcludedByBlock'::character varying])::text[])))
);

ALTER TABLE ONLY public.payroll_slips FORCE ROW LEVEL SECURITY;


-- Name: TABLE payroll_slips; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.payroll_slips IS 'One frozen payslip per employee per run, holding its own identity, proration, GOSI and year-to-date witnesses so it can be reprinted and reconciled years later without joining a single live row. @tier:C @owner:Finance @retention:84-months-from-RecordDate-then-Keep';


-- Name: COLUMN payroll_slips.gross; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.payroll_slips.gross IS 'CACHE of the signed SUM of payroll_slip_lines of the matching kinds, reconciled by the deferred trg_slip_totals (§11.2). Lines are authoritative.';


-- Name: COLUMN payroll_slips.ytd_gross; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.payroll_slips.ytd_gross IS 'Year-to-date set. §19.4 H7 turns the YTD query into a single-row read of the prior slip; the Opening run seeds these as the go-live carry-forward (§2.F).';


-- Name: COLUMN payroll_slips.employee_name; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.payroll_slips.employee_name IS 'Identity snapshot. §2.F names these columns "name, department, designation"; spelled _name here so they cannot be mistaken for live joins.';


-- Name: COLUMN payroll_slips.employer_gosi_registration_no; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.payroll_slips.employer_gosi_registration_no IS 'Frozen snapshot, and the only copy of the registration number not bound by FK; companies.gosi_registration_no is the single owner (§11.5). It is what traces a slip to the return that carried it.';


-- Name: COLUMN payroll_slips.contributory_wage; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.payroll_slips.contributory_wage IS 'The PERIOD''s computed contributory wage. Deliberately a different fact from payroll_slip_lines.applied_contributory_wage (per GOSI branch) and from employee_gosi_registrations.registered_contributory_wage (what GOSI holds) — §11.4.';


-- Name: permission_grantor_records; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.permission_grantor_records (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    grantor_user_id uuid NOT NULL,
    granted_by_user_id uuid,
    revoked_by uuid,
    permission_scope text NOT NULL,
    can_sub_delegate boolean DEFAULT false NOT NULL,
    is_active boolean DEFAULT true NOT NULL,
    reason text,
    expires_at timestamp with time zone,
    revoked_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid
);

ALTER TABLE ONLY public.permission_grantor_records FORCE ROW LEVEL SECURITY;


-- Name: TABLE permission_grantor_records; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.permission_grantor_records IS 'Records that a named user MAY grant permissions over a stated scope, with or without sub-delegation, until a date and for a reason — the authority behind a grant, not the grant itself. @tier:T @owner:Platform';


-- Name: COLUMN permission_grantor_records.permission_scope; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.permission_grantor_records.permission_scope IS '''all'', a module prefix, or an explicit key list. Sub-delegation may only narrow the parent scope (§10.11), checked in AccessManagementService.';


-- Name: permissions; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.permissions (
    id uuid NOT NULL,
    code character varying(64) NOT NULL,
    module character varying(40) NOT NULL,
    description text,
    created_at timestamp with time zone DEFAULT now() NOT NULL
);

ALTER TABLE ONLY public.permissions FORCE ROW LEVEL SECURITY;


-- Name: TABLE permissions; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.permissions IS 'The platform permission catalogue, including the access.grant.* keys that govern delegated granting authority; retiring a permission is a migration, never a delete. @tier:R @owner:Platform @retention:Keep';


-- Name: platform_users; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.platform_users (
    id uuid NOT NULL,
    email text NOT NULL,
    status character varying(40) DEFAULT 'Active'::character varying NOT NULL,
    platform_role character varying(40) NOT NULL,
    full_name text NOT NULL,
    password_hash text,
    mfa_enabled boolean DEFAULT false NOT NULL,
    mfa_secret_encrypted text,
    mfa_recovery_hashes jsonb,
    failed_login_count integer DEFAULT 0 NOT NULL,
    lockout_end timestamp with time zone,
    last_login_at timestamp with time zone,
    deleted_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT chk_platform_users_status CHECK (((status)::text = ANY ((ARRAY['Active'::character varying, 'Suspended'::character varying, 'Disabled'::character varying])::text[]))),
    CONSTRAINT ck_platform_users__failed_login_count_nonnegative CHECK ((failed_login_count >= 0))
);

ALTER TABLE ONLY public.platform_users FORCE ROW LEVEL SECURITY;


-- Name: TABLE platform_users; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.platform_users IS 'Platform operators and their credentials, kept outside tenancy entirely so the operator surface is policed by grant rather than by a tenant filter. @tier:P @owner:Platform @retention:12-months-after-SoftDelete-then-Anonymise';


-- Name: COLUMN platform_users.email; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.platform_users.email IS 'Globally unique (no tenant to scope it by). Stored normalised by the application; see 001_extensions.sql on the citext question.';


-- Name: COLUMN platform_users.password_hash; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.platform_users.password_hash IS 'Secret column: REVOKE from kynex_ro by column privilege (§19.2).';


-- Name: public_holidays; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.public_holidays (
    id uuid NOT NULL,
    tenant_id uuid,
    calendar_code character varying(40) NOT NULL,
    holiday_date date NOT NULL,
    name text NOT NULL,
    is_paid boolean DEFAULT true NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid
);

ALTER TABLE ONLY public.public_holidays FORCE ROW LEVEL SECURITY;


-- Name: TABLE public_holidays; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.public_holidays IS 'Named non-working days per calendar code, with the platform KSA calendar carried as the tenant_id IS NULL rows that every tenant session can read. @tier:R/T @owner:HR';


-- Name: COLUMN public_holidays.holiday_date; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.public_holidays.holiday_date IS 'A local Gregorian date. Hijri occasions are named in `name` but never stored as Hijri — Hijri is always derived (§13.4).';


-- Name: retention_policies; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.retention_policies (
    id uuid NOT NULL,
    tenant_id uuid,
    entity_name character varying(63) NOT NULL,
    rule_key character varying(64) NOT NULL,
    trigger_event character varying(40) NOT NULL,
    disposition character varying(40) NOT NULL,
    effective_from date NOT NULL,
    effective_to date,
    legal_basis text NOT NULL,
    minimum_retention_months integer NOT NULL,
    owner_role character varying(40) NOT NULL,
    source_reference text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT chk_retention_policies_disposition CHECK (((disposition)::text = ANY ((ARRAY['Anonymise'::character varying, 'Purge'::character varying, 'Keep'::character varying])::text[]))),
    CONSTRAINT ck_retention_policies__effective_range CHECK (((effective_to IS NULL) OR (effective_to >= effective_from))),
    CONSTRAINT ck_retention_policies__minimum_nonnegative CHECK ((minimum_retention_months >= 0)),
    CONSTRAINT ck_retention_policies__trigger_event CHECK (((trigger_event)::text = ANY ((ARRAY['SoftDelete'::character varying, 'Separation'::character varying, 'RecordDate'::character varying, 'Expiry'::character varying])::text[])))
);

ALTER TABLE ONLY public.retention_policies FORCE ROW LEVEL SECURITY;


-- Name: TABLE retention_policies; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.retention_policies IS 'The PDPL retention matrix as data — one row per entity giving the legal basis, minimum period, trigger event and disposition the retention job reads instead of appsettings. @tier:R/T @owner:Compliance @retention:Keep';


-- Name: COLUMN retention_policies.tenant_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.retention_policies.tenant_id IS 'NULL = the platform default row, visible to every session under RLS shape (b). A tenant override row may only LENGTHEN the platform period (§12.4) — enforced by trigger, not by a table CHECK, because the rule needs a subquery.';


-- Name: COLUMN retention_policies.rule_key; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.retention_policies.rule_key IS 'Matches the C# RetentionRuleKeys constants and retention_purge_audits.rule_key (FK-free match, §Q).';


-- Name: retention_purge_audits; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.retention_purge_audits (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    job_id uuid,
    rule_key character varying(64) NOT NULL,
    entity character varying(64) NOT NULL,
    entity_id uuid,
    disposition character varying(40) NOT NULL,
    outcome character varying(40) NOT NULL,
    dry_run boolean DEFAULT false NOT NULL,
    retention_until date,
    correlation_id uuid,
    details jsonb,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    CONSTRAINT ck_retention_purge_audits__disposition CHECK (((disposition)::text = ANY ((ARRAY['Anonymise'::character varying, 'Purge'::character varying, 'Keep'::character varying])::text[]))),
    CONSTRAINT ck_retention_purge_audits__outcome CHECK (((outcome)::text = ANY ((ARRAY['Applied'::character varying, 'Skipped'::character varying, 'Failed'::character varying])::text[])))
);

ALTER TABLE ONLY public.retention_purge_audits FORCE ROW LEVEL SECURITY;


-- Name: TABLE retention_purge_audits; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.retention_purge_audits IS 'Is the append-only evidence that a PDPL retention rule ran, naming the rule, the record, the disposition applied and whether the run was a rehearsal. @tier:T @owner:Compliance @retention:indefinite-keep';


-- Name: role_permissions; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.role_permissions (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    role_id uuid NOT NULL,
    permission_code character varying(64) NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid
);

ALTER TABLE ONLY public.role_permissions FORCE ROW LEVEL SECURITY;


-- Name: TABLE role_permissions; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.role_permissions IS 'Grants one catalogue permission to one tenant role; the join that turns a role into an authorisation decision. @tier:T @owner:Platform';


-- Name: roles; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.roles (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    code character varying(64) NOT NULL,
    name text NOT NULL,
    is_system boolean DEFAULT false NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid
);

ALTER TABLE ONLY public.roles FORCE ROW LEVEL SECURITY;


-- Name: TABLE roles; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.roles IS 'Tenant-defined and system-seeded roles; a per-user permission override is modelled as a custom role rather than its own table (§5 decision 5). @tier:T @owner:Platform';


-- Name: shift_assignments; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.shift_assignments (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    shift_id uuid NOT NULL,
    effective_from date NOT NULL,
    effective_to date,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT ck_shift_assignments__effective_range CHECK (((effective_to IS NULL) OR (effective_to >= effective_from)))
);

ALTER TABLE ONLY public.shift_assignments FORCE ROW LEVEL SECURITY;


-- Name: TABLE shift_assignments; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.shift_assignments IS 'Rosters an employee onto a shift for an inclusive effective-dated period, with the database rejecting any overlapping assignment. @tier:T @owner:HR @retention:24m-purge';


-- Name: shifts; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.shifts (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    code character varying(32) NOT NULL,
    name text NOT NULL,
    start_time time without time zone NOT NULL,
    end_time time without time zone NOT NULL,
    break_minutes integer DEFAULT 0 NOT NULL,
    crosses_midnight boolean DEFAULT false NOT NULL,
    weekly_off_days text[] DEFAULT '{}'::text[] NOT NULL,
    rules jsonb DEFAULT '{}'::jsonb NOT NULL,
    is_active boolean DEFAULT true NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid
);

ALTER TABLE ONLY public.shifts FORCE ROW LEVEL SECURITY;


-- Name: TABLE shifts; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.shifts IS 'Defines a working shift with its start and end times, unpaid break, weekly off days and the grace, lateness and Ramadan rules the attendance engine applies to it. @tier:T @owner:HR @retention:tenant-lifecycle';


-- Name: statutory_rule_bands; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.statutory_rule_bands (
    id uuid NOT NULL,
    statutory_rule_id uuid NOT NULL,
    band_key character varying(64) NOT NULL,
    unit character varying(40) NOT NULL,
    band_order integer DEFAULT 0 NOT NULL,
    lower_bound numeric NOT NULL,
    upper_bound numeric,
    rate numeric(9,6),
    amount numeric(18,2),
    value_json jsonb,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT ck_statutory_rule_bands__bounds CHECK (((upper_bound IS NULL) OR (upper_bound > lower_bound))),
    CONSTRAINT ck_statutory_rule_bands__unit CHECK (((unit)::text = ANY ((ARRAY['ServiceYears'::character varying, 'SickDays'::character varying, 'ContributoryWage'::character varying])::text[])))
);

ALTER TABLE ONLY public.statutory_rule_bands FORCE ROW LEVEL SECURITY;


-- Name: TABLE statutory_rule_bands; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.statutory_rule_bands IS 'The banded form of a statutory rule — service-year, sick-day or contributory-wage ranges each carrying their own rate or amount — with the database guaranteeing the bands of one rule never overlap. @tier:R @owner:Compliance @retention:Keep';


-- Name: COLUMN statutory_rule_bands.unit; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.statutory_rule_bands.unit IS 'ServiceYears | SickDays | ContributoryWage — what lower_bound and upper_bound are measured in.';


-- Name: COLUMN statutory_rule_bands.upper_bound; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.statutory_rule_bands.upper_bound IS 'NULL = open-ended. The exclusion constraint reads it as numrange(lower, COALESCE(upper, ''infinity''), ''[)'') — inclusive lower, EXCLUSIVE upper, unlike the inclusive-inclusive date convention (§5).';


-- Name: statutory_rules; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.statutory_rules (
    id uuid NOT NULL,
    country_code character(2) NOT NULL,
    family character varying(40) NOT NULL,
    rule_key character varying(64) NOT NULL,
    nationality_class character varying(40) DEFAULT 'Any'::character varying NOT NULL,
    cohort character varying(40) DEFAULT 'Any'::character varying NOT NULL,
    gosi_branch character varying(40),
    payer character varying(40),
    effective_from date NOT NULL,
    effective_to date,
    rate numeric(9,6),
    wage_floor numeric(18,2),
    wage_cap numeric(18,2),
    value_json jsonb,
    rules_version character varying(40) NOT NULL,
    source_reference text,
    verified_by uuid,
    verified_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT ck_statutory_rules__cohort CHECK (((cohort)::text = ANY ((ARRAY['Legacy'::character varying, 'Entrant2024'::character varying, 'Any'::character varying])::text[]))),
    CONSTRAINT ck_statutory_rules__country_code_format CHECK ((country_code ~ '^[A-Z]{2}$'::text)),
    CONSTRAINT ck_statutory_rules__effective_range CHECK (((effective_to IS NULL) OR (effective_to >= effective_from))),
    CONSTRAINT ck_statutory_rules__family CHECK (((family)::text = ANY ((ARRAY['GOSI'::character varying, 'EOS'::character varying, 'Overtime'::character varying, 'Leave'::character varying, 'Nitaqat'::character varying, 'WPS'::character varying])::text[]))),
    CONSTRAINT ck_statutory_rules__nationality_class CHECK (((nationality_class)::text = ANY ((ARRAY['Saudi'::character varying, 'GCC'::character varying, 'NonSaudi'::character varying, 'Any'::character varying])::text[])))
);

ALTER TABLE ONLY public.statutory_rules FORCE ROW LEVEL SECURITY;


-- Name: TABLE statutory_rules; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.statutory_rules IS 'The single effective-dated source of every statutory rate and bound the payroll engine may apply — GOSI by cohort, branch and payer, EOS, overtime, leave pay, Nitaqat weights and WPS parameters — with the circular it came from and who verified it. @tier:R @owner:Compliance @retention:Keep';


-- Name: COLUMN statutory_rules.cohort; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.statutory_rules.cohort IS '''Legacy'' (Saudis first insured before 3 July 2024), ''Entrant2024'' (on or after), or ''Any''. Resolved from employees.gosi_first_registered_on; an unknown cohort BLOCKS the slip and is never defaulted (§2.E). [COUNSEL] confirms the ladder.';


-- Name: COLUMN statutory_rules.gosi_branch; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.statutory_rules.gosi_branch IS 'Closed set with no enumerated domain anywhere in revision 6 — left unconstrained pending a §9 entry. See the note above.';


-- Name: COLUMN statutory_rules.payer; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.statutory_rules.payer IS 'Closed set with no enumerated domain anywhere in revision 6 — left unconstrained pending a §9 entry.';


-- Name: COLUMN statutory_rules.rate; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.statutory_rules.rate IS 'numeric(9,6): the Entrant2024 annuities ladder steps by 0.5 percentage points, so two decimals on a percentage has no headroom (CONVENTIONS.md §4). Stored as 0.090000, not 9.';


-- Name: COLUMN statutory_rules.rules_version; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.statutory_rules.rules_version IS 'e.g. SA-GOSI-2025.07. Frozen onto every payroll_slip_line so a slip can be recomputed years later.';


-- Name: tenant_settings; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.tenant_settings (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    sections jsonb DEFAULT '{}'::jsonb NOT NULL,
    section_versions jsonb DEFAULT '{}'::jsonb NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid
);

ALTER TABLE ONLY public.tenant_settings FORCE ROW LEVEL SECURITY;


-- Name: TABLE tenant_settings; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.tenant_settings IS 'The single settings row per tenant, holding every configuration section as versioned JSON so two admins editing different sections never clobber each other. @tier:T @owner:Platform';


-- Name: COLUMN tenant_settings.sections; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.tenant_settings.sections IS 'Keys: general, hr, payroll, localization, branding, security, lookups, leave, loans, overtime, notification_templates, document_requirements, help_texts. Written with jsonb_set on one key, guarded by that key''s section_versions entry (§A).';


-- Name: tenants; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.tenants (
    id uuid NOT NULL,
    slug character varying(63) NOT NULL,
    status character varying(40) DEFAULT 'Active'::character varying NOT NULL,
    name text NOT NULL,
    timezone_id character varying(64) DEFAULT 'Asia/Riyadh'::character varying NOT NULL,
    plan_code character varying(40),
    plan_expires_at timestamp with time zone,
    enabled_modules text[] DEFAULT '{}'::text[] NOT NULL,
    plan_limits jsonb DEFAULT '{}'::jsonb NOT NULL,
    soft_deleted_at timestamp with time zone,
    purged_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT chk_tenants_status CHECK (((status)::text = ANY ((ARRAY['Active'::character varying, 'Suspended'::character varying, 'SoftDeleted'::character varying, 'Purged'::character varying])::text[]))),
    CONSTRAINT ck_tenants__slug_format CHECK (((slug)::text ~ '^[a-z0-9]([a-z0-9-]*[a-z0-9])?$'::text))
);

ALTER TABLE ONLY public.tenants FORCE ROW LEVEL SECURITY;


-- Name: TABLE tenants; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.tenants IS 'Customer account root: identity, plan gating, seat and module limits, timezone anchor and the tenancy soft-delete/purge lifecycle. @tier:P @owner:Platform @retention:3-months-after-SoftDelete-then-Purge';


-- Name: COLUMN tenants.timezone_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.tenants.timezone_id IS 'IANA id, validated against the runtime tz database on write. Anchors every business day (§13.4).';


-- Name: COLUMN tenants.plan_limits; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.tenants.plan_limits IS 'Authoritative seat/module limits (max_employees, max_users, max_companies). Nothing caches a seat count (§11.6).';


-- Name: timesheet_day_reconciliations; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.timesheet_day_reconciliations (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    timesheet_id uuid NOT NULL,
    attendance_day_id uuid,
    status character varying(40) DEFAULT 'Open'::character varying NOT NULL,
    work_date date NOT NULL,
    timesheet_minutes integer DEFAULT 0 NOT NULL,
    attendance_minutes integer DEFAULT 0 NOT NULL,
    variance_minutes integer DEFAULT 0 NOT NULL,
    explanation text,
    resolved_by uuid,
    resolved_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT chk_tsdr_status CHECK (((status)::text = ANY ((ARRAY['Open'::character varying, 'Explained'::character varying, 'Accepted'::character varying, 'Rejected'::character varying])::text[]))),
    CONSTRAINT ck_timesheet_day_reconciliations__minutes_non_negative CHECK (((timesheet_minutes >= 0) AND (attendance_minutes >= 0))),
    CONSTRAINT ck_timesheet_day_reconciliations__variance CHECK ((variance_minutes = (timesheet_minutes - attendance_minutes)))
);

ALTER TABLE ONLY public.timesheet_day_reconciliations FORCE ROW LEVEL SECURITY;


-- Name: TABLE timesheet_day_reconciliations; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.timesheet_day_reconciliations IS 'Compares the minutes an employee booked on a timesheet day against the minutes attendance computed for the same day and holds the variance until it is explained or accepted. @tier:C @owner:HR @retention:24m-purge';


-- Name: timesheet_entries; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.timesheet_entries (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    timesheet_id uuid NOT NULL,
    cost_center_id uuid,
    work_date date NOT NULL,
    minutes integer NOT NULL,
    project_code character varying(64),
    task text,
    billable boolean DEFAULT false NOT NULL,
    rate_source character varying(40),
    notes text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT ck_timesheet_entries__minutes_non_negative CHECK ((minutes >= 0))
)
PARTITION BY RANGE (work_date);

ALTER TABLE ONLY public.timesheet_entries FORCE ROW LEVEL SECURITY;


-- Name: TABLE timesheet_entries; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.timesheet_entries IS 'Logs minutes worked on one local day against a cost centre, project and task, and is the only place the project and client dimension of time is captured. @tier:C @owner:HR @retention:24m-purge';


-- Name: timesheet_entries_default; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.timesheet_entries_default (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    timesheet_id uuid NOT NULL,
    cost_center_id uuid,
    work_date date NOT NULL,
    minutes integer NOT NULL,
    project_code character varying(64),
    task text,
    billable boolean DEFAULT false NOT NULL,
    rate_source character varying(40),
    notes text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT ck_timesheet_entries__minutes_non_negative CHECK ((minutes >= 0))
);

ALTER TABLE ONLY public.timesheet_entries_default FORCE ROW LEVEL SECURITY;


-- Name: TABLE timesheet_entries_default; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.timesheet_entries_default IS 'DEFAULT catch-all (§19.3 rule 3). A row landing here is an incident: the month partition was missing. Alert on the FIRST row — recovery is DETACH CONCURRENTLY, create the month, batched INSERT…SELECT, then an ATTACH that takes ACCESS EXCLUSIVE and a full validation scan.';


-- Name: timesheet_entries_y2025m10; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.timesheet_entries_y2025m10 (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    timesheet_id uuid NOT NULL,
    cost_center_id uuid,
    work_date date NOT NULL,
    minutes integer NOT NULL,
    project_code character varying(64),
    task text,
    billable boolean DEFAULT false NOT NULL,
    rate_source character varying(40),
    notes text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT ck_timesheet_entries__minutes_non_negative CHECK ((minutes >= 0))
);

ALTER TABLE ONLY public.timesheet_entries_y2025m10 FORCE ROW LEVEL SECURITY;


-- Name: timesheet_entries_y2025m11; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.timesheet_entries_y2025m11 (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    timesheet_id uuid NOT NULL,
    cost_center_id uuid,
    work_date date NOT NULL,
    minutes integer NOT NULL,
    project_code character varying(64),
    task text,
    billable boolean DEFAULT false NOT NULL,
    rate_source character varying(40),
    notes text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT ck_timesheet_entries__minutes_non_negative CHECK ((minutes >= 0))
);

ALTER TABLE ONLY public.timesheet_entries_y2025m11 FORCE ROW LEVEL SECURITY;


-- Name: timesheet_entries_y2025m12; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.timesheet_entries_y2025m12 (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    timesheet_id uuid NOT NULL,
    cost_center_id uuid,
    work_date date NOT NULL,
    minutes integer NOT NULL,
    project_code character varying(64),
    task text,
    billable boolean DEFAULT false NOT NULL,
    rate_source character varying(40),
    notes text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT ck_timesheet_entries__minutes_non_negative CHECK ((minutes >= 0))
);

ALTER TABLE ONLY public.timesheet_entries_y2025m12 FORCE ROW LEVEL SECURITY;


-- Name: timesheet_entries_y2026m01; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.timesheet_entries_y2026m01 (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    timesheet_id uuid NOT NULL,
    cost_center_id uuid,
    work_date date NOT NULL,
    minutes integer NOT NULL,
    project_code character varying(64),
    task text,
    billable boolean DEFAULT false NOT NULL,
    rate_source character varying(40),
    notes text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT ck_timesheet_entries__minutes_non_negative CHECK ((minutes >= 0))
);

ALTER TABLE ONLY public.timesheet_entries_y2026m01 FORCE ROW LEVEL SECURITY;


-- Name: timesheet_entries_y2026m02; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.timesheet_entries_y2026m02 (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    timesheet_id uuid NOT NULL,
    cost_center_id uuid,
    work_date date NOT NULL,
    minutes integer NOT NULL,
    project_code character varying(64),
    task text,
    billable boolean DEFAULT false NOT NULL,
    rate_source character varying(40),
    notes text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT ck_timesheet_entries__minutes_non_negative CHECK ((minutes >= 0))
);

ALTER TABLE ONLY public.timesheet_entries_y2026m02 FORCE ROW LEVEL SECURITY;


-- Name: timesheet_entries_y2026m03; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.timesheet_entries_y2026m03 (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    timesheet_id uuid NOT NULL,
    cost_center_id uuid,
    work_date date NOT NULL,
    minutes integer NOT NULL,
    project_code character varying(64),
    task text,
    billable boolean DEFAULT false NOT NULL,
    rate_source character varying(40),
    notes text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT ck_timesheet_entries__minutes_non_negative CHECK ((minutes >= 0))
);

ALTER TABLE ONLY public.timesheet_entries_y2026m03 FORCE ROW LEVEL SECURITY;


-- Name: timesheet_entries_y2026m04; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.timesheet_entries_y2026m04 (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    timesheet_id uuid NOT NULL,
    cost_center_id uuid,
    work_date date NOT NULL,
    minutes integer NOT NULL,
    project_code character varying(64),
    task text,
    billable boolean DEFAULT false NOT NULL,
    rate_source character varying(40),
    notes text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT ck_timesheet_entries__minutes_non_negative CHECK ((minutes >= 0))
);

ALTER TABLE ONLY public.timesheet_entries_y2026m04 FORCE ROW LEVEL SECURITY;


-- Name: timesheet_entries_y2026m05; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.timesheet_entries_y2026m05 (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    timesheet_id uuid NOT NULL,
    cost_center_id uuid,
    work_date date NOT NULL,
    minutes integer NOT NULL,
    project_code character varying(64),
    task text,
    billable boolean DEFAULT false NOT NULL,
    rate_source character varying(40),
    notes text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT ck_timesheet_entries__minutes_non_negative CHECK ((minutes >= 0))
);

ALTER TABLE ONLY public.timesheet_entries_y2026m05 FORCE ROW LEVEL SECURITY;


-- Name: timesheet_entries_y2026m06; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.timesheet_entries_y2026m06 (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    timesheet_id uuid NOT NULL,
    cost_center_id uuid,
    work_date date NOT NULL,
    minutes integer NOT NULL,
    project_code character varying(64),
    task text,
    billable boolean DEFAULT false NOT NULL,
    rate_source character varying(40),
    notes text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT ck_timesheet_entries__minutes_non_negative CHECK ((minutes >= 0))
);

ALTER TABLE ONLY public.timesheet_entries_y2026m06 FORCE ROW LEVEL SECURITY;


-- Name: timesheet_entries_y2026m07; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.timesheet_entries_y2026m07 (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    timesheet_id uuid NOT NULL,
    cost_center_id uuid,
    work_date date NOT NULL,
    minutes integer NOT NULL,
    project_code character varying(64),
    task text,
    billable boolean DEFAULT false NOT NULL,
    rate_source character varying(40),
    notes text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT ck_timesheet_entries__minutes_non_negative CHECK ((minutes >= 0))
);

ALTER TABLE ONLY public.timesheet_entries_y2026m07 FORCE ROW LEVEL SECURITY;


-- Name: timesheet_entries_y2026m08; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.timesheet_entries_y2026m08 (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    timesheet_id uuid NOT NULL,
    cost_center_id uuid,
    work_date date NOT NULL,
    minutes integer NOT NULL,
    project_code character varying(64),
    task text,
    billable boolean DEFAULT false NOT NULL,
    rate_source character varying(40),
    notes text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT ck_timesheet_entries__minutes_non_negative CHECK ((minutes >= 0))
);

ALTER TABLE ONLY public.timesheet_entries_y2026m08 FORCE ROW LEVEL SECURITY;


-- Name: timesheet_entries_y2026m09; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.timesheet_entries_y2026m09 (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    timesheet_id uuid NOT NULL,
    cost_center_id uuid,
    work_date date NOT NULL,
    minutes integer NOT NULL,
    project_code character varying(64),
    task text,
    billable boolean DEFAULT false NOT NULL,
    rate_source character varying(40),
    notes text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT ck_timesheet_entries__minutes_non_negative CHECK ((minutes >= 0))
);

ALTER TABLE ONLY public.timesheet_entries_y2026m09 FORCE ROW LEVEL SECURITY;


-- Name: timesheet_entries_y2026m10; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.timesheet_entries_y2026m10 (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    timesheet_id uuid NOT NULL,
    cost_center_id uuid,
    work_date date NOT NULL,
    minutes integer NOT NULL,
    project_code character varying(64),
    task text,
    billable boolean DEFAULT false NOT NULL,
    rate_source character varying(40),
    notes text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT ck_timesheet_entries__minutes_non_negative CHECK ((minutes >= 0))
);

ALTER TABLE ONLY public.timesheet_entries_y2026m10 FORCE ROW LEVEL SECURITY;


-- Name: timesheet_entries_y2026m11; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.timesheet_entries_y2026m11 (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    timesheet_id uuid NOT NULL,
    cost_center_id uuid,
    work_date date NOT NULL,
    minutes integer NOT NULL,
    project_code character varying(64),
    task text,
    billable boolean DEFAULT false NOT NULL,
    rate_source character varying(40),
    notes text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT ck_timesheet_entries__minutes_non_negative CHECK ((minutes >= 0))
);

ALTER TABLE ONLY public.timesheet_entries_y2026m11 FORCE ROW LEVEL SECURITY;


-- Name: timesheet_entries_y2026m12; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.timesheet_entries_y2026m12 (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    timesheet_id uuid NOT NULL,
    cost_center_id uuid,
    work_date date NOT NULL,
    minutes integer NOT NULL,
    project_code character varying(64),
    task text,
    billable boolean DEFAULT false NOT NULL,
    rate_source character varying(40),
    notes text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT ck_timesheet_entries__minutes_non_negative CHECK ((minutes >= 0))
);

ALTER TABLE ONLY public.timesheet_entries_y2026m12 FORCE ROW LEVEL SECURITY;


-- Name: timesheets; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.timesheets (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    company_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    approval_request_id uuid,
    locked_run_id uuid,
    timesheet_number character varying(40),
    status character varying(40) DEFAULT 'Draft'::character varying NOT NULL,
    period_start date NOT NULL,
    period_end date NOT NULL,
    total_minutes integer DEFAULT 0 NOT NULL,
    submitted_at timestamp with time zone,
    decided_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT chk_timesheets_status CHECK (((status)::text = ANY ((ARRAY['Draft'::character varying, 'Submitted'::character varying, 'Approved'::character varying, 'Rejected'::character varying, 'Locked'::character varying])::text[]))),
    CONSTRAINT ck_timesheets__period CHECK ((period_end >= period_start)),
    CONSTRAINT ck_timesheets__total_minutes_non_negative CHECK ((total_minutes >= 0))
);

ALTER TABLE ONLY public.timesheets FORCE ROW LEVEL SECURITY;


-- Name: TABLE timesheets; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.timesheets IS 'Groups one employee logged working minutes for a period into a single submittable, approvable and lockable record. @tier:C @owner:HR @retention:24m-purge';


-- Name: user_roles; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.user_roles (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    user_id uuid NOT NULL,
    role_id uuid NOT NULL,
    scope_company_id uuid,
    scope_branch_id uuid,
    scope_department_id uuid,
    granted_by uuid,
    granted_at timestamp with time zone DEFAULT now() NOT NULL,
    expires_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid
);

ALTER TABLE ONLY public.user_roles FORCE ROW LEVEL SECURITY;


-- Name: TABLE user_roles; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.user_roles IS 'Grants a role to a user, optionally narrowed to one company, branch or department and optionally time-boxed; NULL scope columns mean the whole tenant. @tier:T @owner:Platform';


-- Name: COLUMN user_roles.granted_by; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.user_roles.granted_by IS 'Who made THIS grant. Who MAY grant is a different fact and lives in permission_grantor_records (§2.B).';


-- Name: users; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.users (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    employee_id uuid,
    normalized_email text NOT NULL,
    status character varying(40) DEFAULT 'Invited'::character varying NOT NULL,
    password_hash text,
    mfa_enabled boolean DEFAULT false NOT NULL,
    mfa_secret_encrypted text,
    mfa_recovery_hashes jsonb,
    failed_login_count integer DEFAULT 0 NOT NULL,
    lockout_end timestamp with time zone,
    notification_prefs jsonb DEFAULT '{}'::jsonb NOT NULL,
    last_login_at timestamp with time zone,
    deleted_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT chk_users_status CHECK (((status)::text = ANY ((ARRAY['Invited'::character varying, 'Active'::character varying, 'Suspended'::character varying, 'Locked'::character varying, 'Disabled'::character varying])::text[]))),
    CONSTRAINT ck_users__failed_login_count_nonnegative CHECK ((failed_login_count >= 0))
);

ALTER TABLE ONLY public.users FORCE ROW LEVEL SECURITY;


-- Name: TABLE users; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.users IS 'Every tenant-side login — staff and employee self-service — with its credential, MFA and lockout state, optionally bound one-to-one to an employee record. @tier:T @owner:Platform @retention:soft-delete-only';


-- Name: COLUMN users.normalized_email; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.users.normalized_email IS 'Unique per tenant, NOT globally: two tenants may legitimately share an address, which is why app.resolve_login() is keyed on (tenant, email) (§19.2 bypass surface 1).';


-- Name: COLUMN users.password_hash; Type: COMMENT; Schema: public; Owner: -

COMMENT ON COLUMN public.users.password_hash IS 'Secret column: REVOKE from kynex_ro by column privilege (§19.2). NULL until an invitation is consumed.';


-- Name: v_employee_current; Type: VIEW; Schema: public; Owner: -

CREATE VIEW public.v_employee_current WITH (security_invoker='true') AS
 SELECT e.tenant_id,
    e.id AS employee_id,
    e.employee_number,
    e.status,
    e.name_en,
    e.name_ar,
    e.work_email,
    e.joining_date,
    e.separation_date,
    asg.id AS assignment_id,
    asg.company_id,
    asg.branch_id,
    asg.department_id,
    asg.designation_id,
    asg.grade_id,
    asg.manager_employee_id,
    asg.cost_center_id,
    asg.employment_status,
    asg.pay_group,
    asg.effective_from AS assignment_effective_from,
    sal.id AS salary_id,
    sal.basic,
    sal.housing,
    sal.transport,
    sal.housing_in_kind,
    sal.effective_from AS salary_effective_from,
    bank.id AS bank_account_id,
    bank.iban,
    bank.bank_code,
    bank.payment_method
   FROM (((((public.employees e
     JOIN public.tenants t ON ((t.id = e.tenant_id)))
     CROSS JOIN LATERAL ( SELECT ((now() AT TIME ZONE t.timezone_id))::date AS d) today)
     LEFT JOIN LATERAL ( SELECT a.id,
            a.tenant_id,
            a.company_id,
            a.employee_id,
            a.branch_id,
            a.department_id,
            a.designation_id,
            a.grade_id,
            a.manager_employee_id,
            a.cost_center_id,
            a.approval_request_id,
            a.employment_status,
            a.pay_group,
            a.effective_from,
            a.effective_to,
            a.change_reason,
            a.created_at,
            a.created_by,
            a.updated_at,
            a.updated_by
           FROM public.employee_assignments a
          WHERE ((a.tenant_id = e.tenant_id) AND (a.employee_id = e.id) AND (a.effective_from <= today.d) AND ((a.effective_to IS NULL) OR (a.effective_to >= today.d)))
          ORDER BY a.effective_from DESC
         LIMIT 1) asg ON (true))
     LEFT JOIN LATERAL ( SELECT s.id,
            s.tenant_id,
            s.employee_id,
            s.approval_request_id,
            s.effective_from,
            s.effective_to,
            s.basic,
            s.housing,
            s.transport,
            s.housing_in_kind,
            s.components,
            s.change_reason,
            s.created_at,
            s.created_by,
            s.updated_at,
            s.updated_by
           FROM public.employee_salaries s
          WHERE ((s.tenant_id = e.tenant_id) AND (s.employee_id = e.id) AND (s.effective_from <= today.d) AND ((s.effective_to IS NULL) OR (s.effective_to >= today.d)))
          ORDER BY s.effective_from DESC
         LIMIT 1) sal ON (true))
     LEFT JOIN LATERAL ( SELECT b.id,
            b.tenant_id,
            b.employee_id,
            b.effective_from,
            b.effective_to,
            b.iban,
            b.bank_code,
            b.account_holder_name,
            b.payment_method,
            b.created_at,
            b.created_by,
            b.updated_at,
            b.updated_by
           FROM public.employee_bank_accounts b
          WHERE ((b.tenant_id = e.tenant_id) AND (b.employee_id = e.id) AND (b.effective_from <= today.d) AND ((b.effective_to IS NULL) OR (b.effective_to >= today.d)))
          ORDER BY b.effective_from DESC
         LIMIT 1) bank ON (true))
  WHERE (e.deleted_at IS NULL);


-- Name: VIEW v_employee_current; Type: COMMENT; Schema: public; Owner: -

COMMENT ON VIEW public.v_employee_current IS 'One row per live employee joined to the assignment, salary and bank row in force TODAY in the tenant''s own timezone, so no screen has to choose between employees.status and the effective-dated truth (§11.3). security_invoker = true: without it the view would read every tenant, its owner holding BYPASSRLS.';


-- Name: v_leave_balances; Type: VIEW; Schema: public; Owner: -

CREATE VIEW public.v_leave_balances WITH (security_invoker='true') AS
 SELECT tenant_id,
    employee_id,
    leave_type_id,
    sum(days) AS balance_days,
    sum(days) FILTER (WHERE ((entry_type)::text = 'Accrual'::text)) AS accrued_days,
    sum(days) FILTER (WHERE ((entry_type)::text = 'Debit'::text)) AS debited_days,
    count(*) AS movement_count,
    max(entry_date) AS last_movement_on
   FROM public.leave_ledger l
  GROUP BY tenant_id, employee_id, leave_type_id;


-- Name: VIEW v_leave_balances; Type: COMMENT; Schema: public; Owner: -

COMMENT ON VIEW public.v_leave_balances IS 'The only sanctioned way to read a leave balance (§11.6): a SUM over the append-only leave_ledger, which is authoritative. No table stores a balance. security_invoker = true, so the SUM is over the caller''s tenant only — without it the view would sum every tenant''s ledger, because its owner kynex_owner holds BYPASSRLS.';


-- Name: wps_batches; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.wps_batches (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    company_id uuid NOT NULL,
    run_id uuid NOT NULL,
    batch_number character varying(40) NOT NULL,
    status character varying(40) DEFAULT 'Generated'::character varying NOT NULL,
    format_version character varying(40) NOT NULL,
    employee_count integer DEFAULT 0 NOT NULL,
    total_amount numeric(18,2) DEFAULT 0 NOT NULL,
    submission_reference character varying(64),
    file_id uuid,
    file_sha256 text,
    resubmission_of_id uuid,
    generated_by uuid,
    submitted_at timestamp with time zone,
    acknowledged_at timestamp with time zone,
    rejected_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_at timestamp with time zone,
    updated_by uuid,
    CONSTRAINT chk_wps_batches_status CHECK (((status)::text = ANY ((ARRAY['Generated'::character varying, 'Submitted'::character varying, 'Accepted'::character varying, 'PartiallyRejected'::character varying, 'Rejected'::character varying, 'Superseded'::character varying])::text[]))),
    CONSTRAINT ck_wps_batches__counts_non_negative CHECK (((employee_count >= 0) AND (total_amount >= (0)::numeric)))
);

ALTER TABLE ONLY public.wps_batches FORCE ROW LEVEL SECURITY;


-- Name: TABLE wps_batches; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.wps_batches IS 'Holds one Wage Protection System SIF file per payroll run as generated, submitted and acknowledged by the bank, including its resubmission chain. @tier:C @owner:Finance @retention:84m-keep';


-- Name: wps_lines; Type: TABLE; Schema: public; Owner: -

CREATE TABLE public.wps_lines (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    batch_id uuid NOT NULL,
    slip_id uuid NOT NULL,
    employee_id uuid NOT NULL,
    id_number character varying(20),
    employee_number character varying(32) NOT NULL,
    iban character varying(34) NOT NULL,
    bank_code character varying(16),
    mol_id character varying(20),
    basic numeric(18,2) DEFAULT 0 NOT NULL,
    housing numeric(18,2) DEFAULT 0 NOT NULL,
    other_earnings numeric(18,2) DEFAULT 0 NOT NULL,
    deductions numeric(18,2) DEFAULT 0 NOT NULL,
    net numeric(18,2) DEFAULT 0 NOT NULL,
    bank_status character varying(40) DEFAULT 'Pending'::character varying NOT NULL,
    bank_reference text,
    confirmed_amount numeric(18,2),
    reason_code character varying(40),
    value_date date,
    confirmation_job_id uuid,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    CONSTRAINT chk_wps_lines_bank_status CHECK (((bank_status)::text = ANY ((ARRAY['Pending'::character varying, 'Paid'::character varying, 'Rejected'::character varying, 'Returned'::character varying, 'OnHold'::character varying])::text[])))
);

ALTER TABLE ONLY public.wps_lines FORCE ROW LEVEL SECURITY;


-- Name: TABLE wps_lines; Type: COMMENT; Schema: public; Owner: -

COMMENT ON TABLE public.wps_lines IS 'Records each employee payment line exactly as filed in a WPS SIF batch, frozen at submission, alongside the bank confirmation result returned for it. @tier:C @owner:Finance @retention:84m-keep';


-- Name: attendance_days_default; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days ATTACH PARTITION public.attendance_days_default DEFAULT;


-- Name: attendance_days_y2025m10; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days ATTACH PARTITION public.attendance_days_y2025m10 FOR VALUES FROM ('2025-10-01') TO ('2025-11-01');


-- Name: attendance_days_y2025m11; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days ATTACH PARTITION public.attendance_days_y2025m11 FOR VALUES FROM ('2025-11-01') TO ('2025-12-01');


-- Name: attendance_days_y2025m12; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days ATTACH PARTITION public.attendance_days_y2025m12 FOR VALUES FROM ('2025-12-01') TO ('2026-01-01');


-- Name: attendance_days_y2026m01; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days ATTACH PARTITION public.attendance_days_y2026m01 FOR VALUES FROM ('2026-01-01') TO ('2026-02-01');


-- Name: attendance_days_y2026m02; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days ATTACH PARTITION public.attendance_days_y2026m02 FOR VALUES FROM ('2026-02-01') TO ('2026-03-01');


-- Name: attendance_days_y2026m03; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days ATTACH PARTITION public.attendance_days_y2026m03 FOR VALUES FROM ('2026-03-01') TO ('2026-04-01');


-- Name: attendance_days_y2026m04; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days ATTACH PARTITION public.attendance_days_y2026m04 FOR VALUES FROM ('2026-04-01') TO ('2026-05-01');


-- Name: attendance_days_y2026m05; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days ATTACH PARTITION public.attendance_days_y2026m05 FOR VALUES FROM ('2026-05-01') TO ('2026-06-01');


-- Name: attendance_days_y2026m06; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days ATTACH PARTITION public.attendance_days_y2026m06 FOR VALUES FROM ('2026-06-01') TO ('2026-07-01');


-- Name: attendance_days_y2026m07; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days ATTACH PARTITION public.attendance_days_y2026m07 FOR VALUES FROM ('2026-07-01') TO ('2026-08-01');


-- Name: attendance_days_y2026m08; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days ATTACH PARTITION public.attendance_days_y2026m08 FOR VALUES FROM ('2026-08-01') TO ('2026-09-01');


-- Name: attendance_days_y2026m09; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days ATTACH PARTITION public.attendance_days_y2026m09 FOR VALUES FROM ('2026-09-01') TO ('2026-10-01');


-- Name: attendance_days_y2026m10; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days ATTACH PARTITION public.attendance_days_y2026m10 FOR VALUES FROM ('2026-10-01') TO ('2026-11-01');


-- Name: attendance_days_y2026m11; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days ATTACH PARTITION public.attendance_days_y2026m11 FOR VALUES FROM ('2026-11-01') TO ('2026-12-01');


-- Name: attendance_days_y2026m12; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days ATTACH PARTITION public.attendance_days_y2026m12 FOR VALUES FROM ('2026-12-01') TO ('2027-01-01');


-- Name: attendance_punches_default; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches ATTACH PARTITION public.attendance_punches_default DEFAULT;


-- Name: attendance_punches_y2025m10; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches ATTACH PARTITION public.attendance_punches_y2025m10 FOR VALUES FROM ('2025-10-01 00:00:00+00') TO ('2025-11-01 00:00:00+00');


-- Name: attendance_punches_y2025m11; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches ATTACH PARTITION public.attendance_punches_y2025m11 FOR VALUES FROM ('2025-11-01 00:00:00+00') TO ('2025-12-01 00:00:00+00');


-- Name: attendance_punches_y2025m12; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches ATTACH PARTITION public.attendance_punches_y2025m12 FOR VALUES FROM ('2025-12-01 00:00:00+00') TO ('2026-01-01 00:00:00+00');


-- Name: attendance_punches_y2026m01; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches ATTACH PARTITION public.attendance_punches_y2026m01 FOR VALUES FROM ('2026-01-01 00:00:00+00') TO ('2026-02-01 00:00:00+00');


-- Name: attendance_punches_y2026m02; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches ATTACH PARTITION public.attendance_punches_y2026m02 FOR VALUES FROM ('2026-02-01 00:00:00+00') TO ('2026-03-01 00:00:00+00');


-- Name: attendance_punches_y2026m03; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches ATTACH PARTITION public.attendance_punches_y2026m03 FOR VALUES FROM ('2026-03-01 00:00:00+00') TO ('2026-04-01 00:00:00+00');


-- Name: attendance_punches_y2026m04; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches ATTACH PARTITION public.attendance_punches_y2026m04 FOR VALUES FROM ('2026-04-01 00:00:00+00') TO ('2026-05-01 00:00:00+00');


-- Name: attendance_punches_y2026m05; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches ATTACH PARTITION public.attendance_punches_y2026m05 FOR VALUES FROM ('2026-05-01 00:00:00+00') TO ('2026-06-01 00:00:00+00');


-- Name: attendance_punches_y2026m06; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches ATTACH PARTITION public.attendance_punches_y2026m06 FOR VALUES FROM ('2026-06-01 00:00:00+00') TO ('2026-07-01 00:00:00+00');


-- Name: attendance_punches_y2026m07; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches ATTACH PARTITION public.attendance_punches_y2026m07 FOR VALUES FROM ('2026-07-01 00:00:00+00') TO ('2026-08-01 00:00:00+00');


-- Name: attendance_punches_y2026m08; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches ATTACH PARTITION public.attendance_punches_y2026m08 FOR VALUES FROM ('2026-08-01 00:00:00+00') TO ('2026-09-01 00:00:00+00');


-- Name: attendance_punches_y2026m09; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches ATTACH PARTITION public.attendance_punches_y2026m09 FOR VALUES FROM ('2026-09-01 00:00:00+00') TO ('2026-10-01 00:00:00+00');


-- Name: attendance_punches_y2026m10; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches ATTACH PARTITION public.attendance_punches_y2026m10 FOR VALUES FROM ('2026-10-01 00:00:00+00') TO ('2026-11-01 00:00:00+00');


-- Name: attendance_punches_y2026m11; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches ATTACH PARTITION public.attendance_punches_y2026m11 FOR VALUES FROM ('2026-11-01 00:00:00+00') TO ('2026-12-01 00:00:00+00');


-- Name: attendance_punches_y2026m12; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches ATTACH PARTITION public.attendance_punches_y2026m12 FOR VALUES FROM ('2026-12-01 00:00:00+00') TO ('2027-01-01 00:00:00+00');


-- Name: audit_logs_default; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs ATTACH PARTITION public.audit_logs_default DEFAULT;


-- Name: audit_logs_y2025m10; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs ATTACH PARTITION public.audit_logs_y2025m10 FOR VALUES FROM ('2025-10-01 00:00:00+00') TO ('2025-11-01 00:00:00+00');


-- Name: audit_logs_y2025m11; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs ATTACH PARTITION public.audit_logs_y2025m11 FOR VALUES FROM ('2025-11-01 00:00:00+00') TO ('2025-12-01 00:00:00+00');


-- Name: audit_logs_y2025m12; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs ATTACH PARTITION public.audit_logs_y2025m12 FOR VALUES FROM ('2025-12-01 00:00:00+00') TO ('2026-01-01 00:00:00+00');


-- Name: audit_logs_y2026m01; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs ATTACH PARTITION public.audit_logs_y2026m01 FOR VALUES FROM ('2026-01-01 00:00:00+00') TO ('2026-02-01 00:00:00+00');


-- Name: audit_logs_y2026m02; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs ATTACH PARTITION public.audit_logs_y2026m02 FOR VALUES FROM ('2026-02-01 00:00:00+00') TO ('2026-03-01 00:00:00+00');


-- Name: audit_logs_y2026m03; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs ATTACH PARTITION public.audit_logs_y2026m03 FOR VALUES FROM ('2026-03-01 00:00:00+00') TO ('2026-04-01 00:00:00+00');


-- Name: audit_logs_y2026m04; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs ATTACH PARTITION public.audit_logs_y2026m04 FOR VALUES FROM ('2026-04-01 00:00:00+00') TO ('2026-05-01 00:00:00+00');


-- Name: audit_logs_y2026m05; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs ATTACH PARTITION public.audit_logs_y2026m05 FOR VALUES FROM ('2026-05-01 00:00:00+00') TO ('2026-06-01 00:00:00+00');


-- Name: audit_logs_y2026m06; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs ATTACH PARTITION public.audit_logs_y2026m06 FOR VALUES FROM ('2026-06-01 00:00:00+00') TO ('2026-07-01 00:00:00+00');


-- Name: audit_logs_y2026m07; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs ATTACH PARTITION public.audit_logs_y2026m07 FOR VALUES FROM ('2026-07-01 00:00:00+00') TO ('2026-08-01 00:00:00+00');


-- Name: audit_logs_y2026m08; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs ATTACH PARTITION public.audit_logs_y2026m08 FOR VALUES FROM ('2026-08-01 00:00:00+00') TO ('2026-09-01 00:00:00+00');


-- Name: audit_logs_y2026m09; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs ATTACH PARTITION public.audit_logs_y2026m09 FOR VALUES FROM ('2026-09-01 00:00:00+00') TO ('2026-10-01 00:00:00+00');


-- Name: audit_logs_y2026m10; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs ATTACH PARTITION public.audit_logs_y2026m10 FOR VALUES FROM ('2026-10-01 00:00:00+00') TO ('2026-11-01 00:00:00+00');


-- Name: audit_logs_y2026m11; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs ATTACH PARTITION public.audit_logs_y2026m11 FOR VALUES FROM ('2026-11-01 00:00:00+00') TO ('2026-12-01 00:00:00+00');


-- Name: audit_logs_y2026m12; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs ATTACH PARTITION public.audit_logs_y2026m12 FOR VALUES FROM ('2026-12-01 00:00:00+00') TO ('2027-01-01 00:00:00+00');


-- Name: background_job_items_default; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items ATTACH PARTITION public.background_job_items_default DEFAULT;


-- Name: background_job_items_y2025m10; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items ATTACH PARTITION public.background_job_items_y2025m10 FOR VALUES FROM ('2025-10-01 00:00:00+00') TO ('2025-11-01 00:00:00+00');


-- Name: background_job_items_y2025m11; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items ATTACH PARTITION public.background_job_items_y2025m11 FOR VALUES FROM ('2025-11-01 00:00:00+00') TO ('2025-12-01 00:00:00+00');


-- Name: background_job_items_y2025m12; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items ATTACH PARTITION public.background_job_items_y2025m12 FOR VALUES FROM ('2025-12-01 00:00:00+00') TO ('2026-01-01 00:00:00+00');


-- Name: background_job_items_y2026m01; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items ATTACH PARTITION public.background_job_items_y2026m01 FOR VALUES FROM ('2026-01-01 00:00:00+00') TO ('2026-02-01 00:00:00+00');


-- Name: background_job_items_y2026m02; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items ATTACH PARTITION public.background_job_items_y2026m02 FOR VALUES FROM ('2026-02-01 00:00:00+00') TO ('2026-03-01 00:00:00+00');


-- Name: background_job_items_y2026m03; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items ATTACH PARTITION public.background_job_items_y2026m03 FOR VALUES FROM ('2026-03-01 00:00:00+00') TO ('2026-04-01 00:00:00+00');


-- Name: background_job_items_y2026m04; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items ATTACH PARTITION public.background_job_items_y2026m04 FOR VALUES FROM ('2026-04-01 00:00:00+00') TO ('2026-05-01 00:00:00+00');


-- Name: background_job_items_y2026m05; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items ATTACH PARTITION public.background_job_items_y2026m05 FOR VALUES FROM ('2026-05-01 00:00:00+00') TO ('2026-06-01 00:00:00+00');


-- Name: background_job_items_y2026m06; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items ATTACH PARTITION public.background_job_items_y2026m06 FOR VALUES FROM ('2026-06-01 00:00:00+00') TO ('2026-07-01 00:00:00+00');


-- Name: background_job_items_y2026m07; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items ATTACH PARTITION public.background_job_items_y2026m07 FOR VALUES FROM ('2026-07-01 00:00:00+00') TO ('2026-08-01 00:00:00+00');


-- Name: background_job_items_y2026m08; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items ATTACH PARTITION public.background_job_items_y2026m08 FOR VALUES FROM ('2026-08-01 00:00:00+00') TO ('2026-09-01 00:00:00+00');


-- Name: background_job_items_y2026m09; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items ATTACH PARTITION public.background_job_items_y2026m09 FOR VALUES FROM ('2026-09-01 00:00:00+00') TO ('2026-10-01 00:00:00+00');


-- Name: background_job_items_y2026m10; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items ATTACH PARTITION public.background_job_items_y2026m10 FOR VALUES FROM ('2026-10-01 00:00:00+00') TO ('2026-11-01 00:00:00+00');


-- Name: background_job_items_y2026m11; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items ATTACH PARTITION public.background_job_items_y2026m11 FOR VALUES FROM ('2026-11-01 00:00:00+00') TO ('2026-12-01 00:00:00+00');


-- Name: background_job_items_y2026m12; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items ATTACH PARTITION public.background_job_items_y2026m12 FOR VALUES FROM ('2026-12-01 00:00:00+00') TO ('2027-01-01 00:00:00+00');


-- Name: timesheet_entries_default; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries ATTACH PARTITION public.timesheet_entries_default DEFAULT;


-- Name: timesheet_entries_y2025m10; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries ATTACH PARTITION public.timesheet_entries_y2025m10 FOR VALUES FROM ('2025-10-01') TO ('2025-11-01');


-- Name: timesheet_entries_y2025m11; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries ATTACH PARTITION public.timesheet_entries_y2025m11 FOR VALUES FROM ('2025-11-01') TO ('2025-12-01');


-- Name: timesheet_entries_y2025m12; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries ATTACH PARTITION public.timesheet_entries_y2025m12 FOR VALUES FROM ('2025-12-01') TO ('2026-01-01');


-- Name: timesheet_entries_y2026m01; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries ATTACH PARTITION public.timesheet_entries_y2026m01 FOR VALUES FROM ('2026-01-01') TO ('2026-02-01');


-- Name: timesheet_entries_y2026m02; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries ATTACH PARTITION public.timesheet_entries_y2026m02 FOR VALUES FROM ('2026-02-01') TO ('2026-03-01');


-- Name: timesheet_entries_y2026m03; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries ATTACH PARTITION public.timesheet_entries_y2026m03 FOR VALUES FROM ('2026-03-01') TO ('2026-04-01');


-- Name: timesheet_entries_y2026m04; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries ATTACH PARTITION public.timesheet_entries_y2026m04 FOR VALUES FROM ('2026-04-01') TO ('2026-05-01');


-- Name: timesheet_entries_y2026m05; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries ATTACH PARTITION public.timesheet_entries_y2026m05 FOR VALUES FROM ('2026-05-01') TO ('2026-06-01');


-- Name: timesheet_entries_y2026m06; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries ATTACH PARTITION public.timesheet_entries_y2026m06 FOR VALUES FROM ('2026-06-01') TO ('2026-07-01');


-- Name: timesheet_entries_y2026m07; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries ATTACH PARTITION public.timesheet_entries_y2026m07 FOR VALUES FROM ('2026-07-01') TO ('2026-08-01');


-- Name: timesheet_entries_y2026m08; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries ATTACH PARTITION public.timesheet_entries_y2026m08 FOR VALUES FROM ('2026-08-01') TO ('2026-09-01');


-- Name: timesheet_entries_y2026m09; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries ATTACH PARTITION public.timesheet_entries_y2026m09 FOR VALUES FROM ('2026-09-01') TO ('2026-10-01');


-- Name: timesheet_entries_y2026m10; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries ATTACH PARTITION public.timesheet_entries_y2026m10 FOR VALUES FROM ('2026-10-01') TO ('2026-11-01');


-- Name: timesheet_entries_y2026m11; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries ATTACH PARTITION public.timesheet_entries_y2026m11 FOR VALUES FROM ('2026-11-01') TO ('2026-12-01');


-- Name: timesheet_entries_y2026m12; Type: TABLE ATTACH; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries ATTACH PARTITION public.timesheet_entries_y2026m12 FOR VALUES FROM ('2026-12-01') TO ('2027-01-01');


-- Name: rls_manifest rls_manifest_pkey; Type: CONSTRAINT; Schema: app; Owner: -

ALTER TABLE ONLY app.rls_manifest
    ADD CONSTRAINT rls_manifest_pkey PRIMARY KEY (relname);


-- Name: attendance_days pk_attendance_days; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days
    ADD CONSTRAINT pk_attendance_days PRIMARY KEY (id, work_date);


-- Name: attendance_days_default attendance_days_default_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days_default
    ADD CONSTRAINT attendance_days_default_pkey PRIMARY KEY (id, work_date);


-- Name: attendance_days uq_attendance_days__employee_work_date; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days
    ADD CONSTRAINT uq_attendance_days__employee_work_date UNIQUE (tenant_id, employee_id, work_date);


-- Name: attendance_days_default attendance_days_default_tenant_id_employee_id_work_date_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days_default
    ADD CONSTRAINT attendance_days_default_tenant_id_employee_id_work_date_key UNIQUE (tenant_id, employee_id, work_date);


-- Name: attendance_days uq_attendance_days__tenant_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days
    ADD CONSTRAINT uq_attendance_days__tenant_id UNIQUE (tenant_id, id, work_date);


-- Name: attendance_days_default attendance_days_default_tenant_id_id_work_date_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days_default
    ADD CONSTRAINT attendance_days_default_tenant_id_id_work_date_key UNIQUE (tenant_id, id, work_date);


-- Name: attendance_days_y2025m10 attendance_days_y2025m10_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days_y2025m10
    ADD CONSTRAINT attendance_days_y2025m10_pkey PRIMARY KEY (id, work_date);


-- Name: attendance_days_y2025m10 attendance_days_y2025m10_tenant_id_employee_id_work_date_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days_y2025m10
    ADD CONSTRAINT attendance_days_y2025m10_tenant_id_employee_id_work_date_key UNIQUE (tenant_id, employee_id, work_date);


-- Name: attendance_days_y2025m10 attendance_days_y2025m10_tenant_id_id_work_date_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days_y2025m10
    ADD CONSTRAINT attendance_days_y2025m10_tenant_id_id_work_date_key UNIQUE (tenant_id, id, work_date);


-- Name: attendance_days_y2025m11 attendance_days_y2025m11_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days_y2025m11
    ADD CONSTRAINT attendance_days_y2025m11_pkey PRIMARY KEY (id, work_date);


-- Name: attendance_days_y2025m11 attendance_days_y2025m11_tenant_id_employee_id_work_date_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days_y2025m11
    ADD CONSTRAINT attendance_days_y2025m11_tenant_id_employee_id_work_date_key UNIQUE (tenant_id, employee_id, work_date);


-- Name: attendance_days_y2025m11 attendance_days_y2025m11_tenant_id_id_work_date_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days_y2025m11
    ADD CONSTRAINT attendance_days_y2025m11_tenant_id_id_work_date_key UNIQUE (tenant_id, id, work_date);


-- Name: attendance_days_y2025m12 attendance_days_y2025m12_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days_y2025m12
    ADD CONSTRAINT attendance_days_y2025m12_pkey PRIMARY KEY (id, work_date);


-- Name: attendance_days_y2025m12 attendance_days_y2025m12_tenant_id_employee_id_work_date_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days_y2025m12
    ADD CONSTRAINT attendance_days_y2025m12_tenant_id_employee_id_work_date_key UNIQUE (tenant_id, employee_id, work_date);


-- Name: attendance_days_y2025m12 attendance_days_y2025m12_tenant_id_id_work_date_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days_y2025m12
    ADD CONSTRAINT attendance_days_y2025m12_tenant_id_id_work_date_key UNIQUE (tenant_id, id, work_date);


-- Name: attendance_days_y2026m01 attendance_days_y2026m01_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days_y2026m01
    ADD CONSTRAINT attendance_days_y2026m01_pkey PRIMARY KEY (id, work_date);


-- Name: attendance_days_y2026m01 attendance_days_y2026m01_tenant_id_employee_id_work_date_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days_y2026m01
    ADD CONSTRAINT attendance_days_y2026m01_tenant_id_employee_id_work_date_key UNIQUE (tenant_id, employee_id, work_date);


-- Name: attendance_days_y2026m01 attendance_days_y2026m01_tenant_id_id_work_date_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days_y2026m01
    ADD CONSTRAINT attendance_days_y2026m01_tenant_id_id_work_date_key UNIQUE (tenant_id, id, work_date);


-- Name: attendance_days_y2026m02 attendance_days_y2026m02_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days_y2026m02
    ADD CONSTRAINT attendance_days_y2026m02_pkey PRIMARY KEY (id, work_date);


-- Name: attendance_days_y2026m02 attendance_days_y2026m02_tenant_id_employee_id_work_date_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days_y2026m02
    ADD CONSTRAINT attendance_days_y2026m02_tenant_id_employee_id_work_date_key UNIQUE (tenant_id, employee_id, work_date);


-- Name: attendance_days_y2026m02 attendance_days_y2026m02_tenant_id_id_work_date_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days_y2026m02
    ADD CONSTRAINT attendance_days_y2026m02_tenant_id_id_work_date_key UNIQUE (tenant_id, id, work_date);


-- Name: attendance_days_y2026m03 attendance_days_y2026m03_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days_y2026m03
    ADD CONSTRAINT attendance_days_y2026m03_pkey PRIMARY KEY (id, work_date);


-- Name: attendance_days_y2026m03 attendance_days_y2026m03_tenant_id_employee_id_work_date_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days_y2026m03
    ADD CONSTRAINT attendance_days_y2026m03_tenant_id_employee_id_work_date_key UNIQUE (tenant_id, employee_id, work_date);


-- Name: attendance_days_y2026m03 attendance_days_y2026m03_tenant_id_id_work_date_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days_y2026m03
    ADD CONSTRAINT attendance_days_y2026m03_tenant_id_id_work_date_key UNIQUE (tenant_id, id, work_date);


-- Name: attendance_days_y2026m04 attendance_days_y2026m04_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days_y2026m04
    ADD CONSTRAINT attendance_days_y2026m04_pkey PRIMARY KEY (id, work_date);


-- Name: attendance_days_y2026m04 attendance_days_y2026m04_tenant_id_employee_id_work_date_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days_y2026m04
    ADD CONSTRAINT attendance_days_y2026m04_tenant_id_employee_id_work_date_key UNIQUE (tenant_id, employee_id, work_date);


-- Name: attendance_days_y2026m04 attendance_days_y2026m04_tenant_id_id_work_date_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days_y2026m04
    ADD CONSTRAINT attendance_days_y2026m04_tenant_id_id_work_date_key UNIQUE (tenant_id, id, work_date);


-- Name: attendance_days_y2026m05 attendance_days_y2026m05_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days_y2026m05
    ADD CONSTRAINT attendance_days_y2026m05_pkey PRIMARY KEY (id, work_date);


-- Name: attendance_days_y2026m05 attendance_days_y2026m05_tenant_id_employee_id_work_date_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days_y2026m05
    ADD CONSTRAINT attendance_days_y2026m05_tenant_id_employee_id_work_date_key UNIQUE (tenant_id, employee_id, work_date);


-- Name: attendance_days_y2026m05 attendance_days_y2026m05_tenant_id_id_work_date_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days_y2026m05
    ADD CONSTRAINT attendance_days_y2026m05_tenant_id_id_work_date_key UNIQUE (tenant_id, id, work_date);


-- Name: attendance_days_y2026m06 attendance_days_y2026m06_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days_y2026m06
    ADD CONSTRAINT attendance_days_y2026m06_pkey PRIMARY KEY (id, work_date);


-- Name: attendance_days_y2026m06 attendance_days_y2026m06_tenant_id_employee_id_work_date_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days_y2026m06
    ADD CONSTRAINT attendance_days_y2026m06_tenant_id_employee_id_work_date_key UNIQUE (tenant_id, employee_id, work_date);


-- Name: attendance_days_y2026m06 attendance_days_y2026m06_tenant_id_id_work_date_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days_y2026m06
    ADD CONSTRAINT attendance_days_y2026m06_tenant_id_id_work_date_key UNIQUE (tenant_id, id, work_date);


-- Name: attendance_days_y2026m07 attendance_days_y2026m07_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days_y2026m07
    ADD CONSTRAINT attendance_days_y2026m07_pkey PRIMARY KEY (id, work_date);


-- Name: attendance_days_y2026m07 attendance_days_y2026m07_tenant_id_employee_id_work_date_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days_y2026m07
    ADD CONSTRAINT attendance_days_y2026m07_tenant_id_employee_id_work_date_key UNIQUE (tenant_id, employee_id, work_date);


-- Name: attendance_days_y2026m07 attendance_days_y2026m07_tenant_id_id_work_date_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days_y2026m07
    ADD CONSTRAINT attendance_days_y2026m07_tenant_id_id_work_date_key UNIQUE (tenant_id, id, work_date);


-- Name: attendance_days_y2026m08 attendance_days_y2026m08_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days_y2026m08
    ADD CONSTRAINT attendance_days_y2026m08_pkey PRIMARY KEY (id, work_date);


-- Name: attendance_days_y2026m08 attendance_days_y2026m08_tenant_id_employee_id_work_date_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days_y2026m08
    ADD CONSTRAINT attendance_days_y2026m08_tenant_id_employee_id_work_date_key UNIQUE (tenant_id, employee_id, work_date);


-- Name: attendance_days_y2026m08 attendance_days_y2026m08_tenant_id_id_work_date_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days_y2026m08
    ADD CONSTRAINT attendance_days_y2026m08_tenant_id_id_work_date_key UNIQUE (tenant_id, id, work_date);


-- Name: attendance_days_y2026m09 attendance_days_y2026m09_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days_y2026m09
    ADD CONSTRAINT attendance_days_y2026m09_pkey PRIMARY KEY (id, work_date);


-- Name: attendance_days_y2026m09 attendance_days_y2026m09_tenant_id_employee_id_work_date_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days_y2026m09
    ADD CONSTRAINT attendance_days_y2026m09_tenant_id_employee_id_work_date_key UNIQUE (tenant_id, employee_id, work_date);


-- Name: attendance_days_y2026m09 attendance_days_y2026m09_tenant_id_id_work_date_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days_y2026m09
    ADD CONSTRAINT attendance_days_y2026m09_tenant_id_id_work_date_key UNIQUE (tenant_id, id, work_date);


-- Name: attendance_days_y2026m10 attendance_days_y2026m10_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days_y2026m10
    ADD CONSTRAINT attendance_days_y2026m10_pkey PRIMARY KEY (id, work_date);


-- Name: attendance_days_y2026m10 attendance_days_y2026m10_tenant_id_employee_id_work_date_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days_y2026m10
    ADD CONSTRAINT attendance_days_y2026m10_tenant_id_employee_id_work_date_key UNIQUE (tenant_id, employee_id, work_date);


-- Name: attendance_days_y2026m10 attendance_days_y2026m10_tenant_id_id_work_date_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days_y2026m10
    ADD CONSTRAINT attendance_days_y2026m10_tenant_id_id_work_date_key UNIQUE (tenant_id, id, work_date);


-- Name: attendance_days_y2026m11 attendance_days_y2026m11_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days_y2026m11
    ADD CONSTRAINT attendance_days_y2026m11_pkey PRIMARY KEY (id, work_date);


-- Name: attendance_days_y2026m11 attendance_days_y2026m11_tenant_id_employee_id_work_date_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days_y2026m11
    ADD CONSTRAINT attendance_days_y2026m11_tenant_id_employee_id_work_date_key UNIQUE (tenant_id, employee_id, work_date);


-- Name: attendance_days_y2026m11 attendance_days_y2026m11_tenant_id_id_work_date_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days_y2026m11
    ADD CONSTRAINT attendance_days_y2026m11_tenant_id_id_work_date_key UNIQUE (tenant_id, id, work_date);


-- Name: attendance_days_y2026m12 attendance_days_y2026m12_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days_y2026m12
    ADD CONSTRAINT attendance_days_y2026m12_pkey PRIMARY KEY (id, work_date);


-- Name: attendance_days_y2026m12 attendance_days_y2026m12_tenant_id_employee_id_work_date_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days_y2026m12
    ADD CONSTRAINT attendance_days_y2026m12_tenant_id_employee_id_work_date_key UNIQUE (tenant_id, employee_id, work_date);


-- Name: attendance_days_y2026m12 attendance_days_y2026m12_tenant_id_id_work_date_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_days_y2026m12
    ADD CONSTRAINT attendance_days_y2026m12_tenant_id_id_work_date_key UNIQUE (tenant_id, id, work_date);


-- Name: attendance_punches pk_attendance_punches; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches
    ADD CONSTRAINT pk_attendance_punches PRIMARY KEY (id, occurred_at);


-- Name: attendance_punches_default attendance_punches_default_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_default
    ADD CONSTRAINT attendance_punches_default_pkey PRIMARY KEY (id, occurred_at);


-- Name: attendance_punches uq_attendance_punches__device_external_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches
    ADD CONSTRAINT uq_attendance_punches__device_external_id UNIQUE (tenant_id, device_id, external_id, occurred_at);


-- Name: attendance_punches_default attendance_punches_default_tenant_id_device_id_external_id__key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_default
    ADD CONSTRAINT attendance_punches_default_tenant_id_device_id_external_id__key UNIQUE (tenant_id, device_id, external_id, occurred_at);


-- Name: attendance_punches uq_attendance_punches__tenant_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches
    ADD CONSTRAINT uq_attendance_punches__tenant_id UNIQUE (tenant_id, id, occurred_at);


-- Name: attendance_punches_default attendance_punches_default_tenant_id_id_occurred_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_default
    ADD CONSTRAINT attendance_punches_default_tenant_id_id_occurred_at_key UNIQUE (tenant_id, id, occurred_at);


-- Name: attendance_punches uq_attendance_punches__idempotency_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches
    ADD CONSTRAINT uq_attendance_punches__idempotency_key UNIQUE (tenant_id, idempotency_key, occurred_at);


-- Name: attendance_punches_default attendance_punches_default_tenant_id_idempotency_key_occurr_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_default
    ADD CONSTRAINT attendance_punches_default_tenant_id_idempotency_key_occurr_key UNIQUE (tenant_id, idempotency_key, occurred_at);


-- Name: attendance_punches_y2025m10 attendance_punches_y2025m10_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2025m10
    ADD CONSTRAINT attendance_punches_y2025m10_pkey PRIMARY KEY (id, occurred_at);


-- Name: attendance_punches_y2025m10 attendance_punches_y2025m10_tenant_id_device_id_external_id_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2025m10
    ADD CONSTRAINT attendance_punches_y2025m10_tenant_id_device_id_external_id_key UNIQUE (tenant_id, device_id, external_id, occurred_at);


-- Name: attendance_punches_y2025m10 attendance_punches_y2025m10_tenant_id_id_occurred_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2025m10
    ADD CONSTRAINT attendance_punches_y2025m10_tenant_id_id_occurred_at_key UNIQUE (tenant_id, id, occurred_at);


-- Name: attendance_punches_y2025m10 attendance_punches_y2025m10_tenant_id_idempotency_key_occur_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2025m10
    ADD CONSTRAINT attendance_punches_y2025m10_tenant_id_idempotency_key_occur_key UNIQUE (tenant_id, idempotency_key, occurred_at);


-- Name: attendance_punches_y2025m11 attendance_punches_y2025m11_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2025m11
    ADD CONSTRAINT attendance_punches_y2025m11_pkey PRIMARY KEY (id, occurred_at);


-- Name: attendance_punches_y2025m11 attendance_punches_y2025m11_tenant_id_device_id_external_id_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2025m11
    ADD CONSTRAINT attendance_punches_y2025m11_tenant_id_device_id_external_id_key UNIQUE (tenant_id, device_id, external_id, occurred_at);


-- Name: attendance_punches_y2025m11 attendance_punches_y2025m11_tenant_id_id_occurred_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2025m11
    ADD CONSTRAINT attendance_punches_y2025m11_tenant_id_id_occurred_at_key UNIQUE (tenant_id, id, occurred_at);


-- Name: attendance_punches_y2025m11 attendance_punches_y2025m11_tenant_id_idempotency_key_occur_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2025m11
    ADD CONSTRAINT attendance_punches_y2025m11_tenant_id_idempotency_key_occur_key UNIQUE (tenant_id, idempotency_key, occurred_at);


-- Name: attendance_punches_y2025m12 attendance_punches_y2025m12_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2025m12
    ADD CONSTRAINT attendance_punches_y2025m12_pkey PRIMARY KEY (id, occurred_at);


-- Name: attendance_punches_y2025m12 attendance_punches_y2025m12_tenant_id_device_id_external_id_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2025m12
    ADD CONSTRAINT attendance_punches_y2025m12_tenant_id_device_id_external_id_key UNIQUE (tenant_id, device_id, external_id, occurred_at);


-- Name: attendance_punches_y2025m12 attendance_punches_y2025m12_tenant_id_id_occurred_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2025m12
    ADD CONSTRAINT attendance_punches_y2025m12_tenant_id_id_occurred_at_key UNIQUE (tenant_id, id, occurred_at);


-- Name: attendance_punches_y2025m12 attendance_punches_y2025m12_tenant_id_idempotency_key_occur_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2025m12
    ADD CONSTRAINT attendance_punches_y2025m12_tenant_id_idempotency_key_occur_key UNIQUE (tenant_id, idempotency_key, occurred_at);


-- Name: attendance_punches_y2026m01 attendance_punches_y2026m01_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2026m01
    ADD CONSTRAINT attendance_punches_y2026m01_pkey PRIMARY KEY (id, occurred_at);


-- Name: attendance_punches_y2026m01 attendance_punches_y2026m01_tenant_id_device_id_external_id_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2026m01
    ADD CONSTRAINT attendance_punches_y2026m01_tenant_id_device_id_external_id_key UNIQUE (tenant_id, device_id, external_id, occurred_at);


-- Name: attendance_punches_y2026m01 attendance_punches_y2026m01_tenant_id_id_occurred_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2026m01
    ADD CONSTRAINT attendance_punches_y2026m01_tenant_id_id_occurred_at_key UNIQUE (tenant_id, id, occurred_at);


-- Name: attendance_punches_y2026m01 attendance_punches_y2026m01_tenant_id_idempotency_key_occur_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2026m01
    ADD CONSTRAINT attendance_punches_y2026m01_tenant_id_idempotency_key_occur_key UNIQUE (tenant_id, idempotency_key, occurred_at);


-- Name: attendance_punches_y2026m02 attendance_punches_y2026m02_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2026m02
    ADD CONSTRAINT attendance_punches_y2026m02_pkey PRIMARY KEY (id, occurred_at);


-- Name: attendance_punches_y2026m02 attendance_punches_y2026m02_tenant_id_device_id_external_id_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2026m02
    ADD CONSTRAINT attendance_punches_y2026m02_tenant_id_device_id_external_id_key UNIQUE (tenant_id, device_id, external_id, occurred_at);


-- Name: attendance_punches_y2026m02 attendance_punches_y2026m02_tenant_id_id_occurred_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2026m02
    ADD CONSTRAINT attendance_punches_y2026m02_tenant_id_id_occurred_at_key UNIQUE (tenant_id, id, occurred_at);


-- Name: attendance_punches_y2026m02 attendance_punches_y2026m02_tenant_id_idempotency_key_occur_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2026m02
    ADD CONSTRAINT attendance_punches_y2026m02_tenant_id_idempotency_key_occur_key UNIQUE (tenant_id, idempotency_key, occurred_at);


-- Name: attendance_punches_y2026m03 attendance_punches_y2026m03_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2026m03
    ADD CONSTRAINT attendance_punches_y2026m03_pkey PRIMARY KEY (id, occurred_at);


-- Name: attendance_punches_y2026m03 attendance_punches_y2026m03_tenant_id_device_id_external_id_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2026m03
    ADD CONSTRAINT attendance_punches_y2026m03_tenant_id_device_id_external_id_key UNIQUE (tenant_id, device_id, external_id, occurred_at);


-- Name: attendance_punches_y2026m03 attendance_punches_y2026m03_tenant_id_id_occurred_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2026m03
    ADD CONSTRAINT attendance_punches_y2026m03_tenant_id_id_occurred_at_key UNIQUE (tenant_id, id, occurred_at);


-- Name: attendance_punches_y2026m03 attendance_punches_y2026m03_tenant_id_idempotency_key_occur_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2026m03
    ADD CONSTRAINT attendance_punches_y2026m03_tenant_id_idempotency_key_occur_key UNIQUE (tenant_id, idempotency_key, occurred_at);


-- Name: attendance_punches_y2026m04 attendance_punches_y2026m04_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2026m04
    ADD CONSTRAINT attendance_punches_y2026m04_pkey PRIMARY KEY (id, occurred_at);


-- Name: attendance_punches_y2026m04 attendance_punches_y2026m04_tenant_id_device_id_external_id_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2026m04
    ADD CONSTRAINT attendance_punches_y2026m04_tenant_id_device_id_external_id_key UNIQUE (tenant_id, device_id, external_id, occurred_at);


-- Name: attendance_punches_y2026m04 attendance_punches_y2026m04_tenant_id_id_occurred_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2026m04
    ADD CONSTRAINT attendance_punches_y2026m04_tenant_id_id_occurred_at_key UNIQUE (tenant_id, id, occurred_at);


-- Name: attendance_punches_y2026m04 attendance_punches_y2026m04_tenant_id_idempotency_key_occur_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2026m04
    ADD CONSTRAINT attendance_punches_y2026m04_tenant_id_idempotency_key_occur_key UNIQUE (tenant_id, idempotency_key, occurred_at);


-- Name: attendance_punches_y2026m05 attendance_punches_y2026m05_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2026m05
    ADD CONSTRAINT attendance_punches_y2026m05_pkey PRIMARY KEY (id, occurred_at);


-- Name: attendance_punches_y2026m05 attendance_punches_y2026m05_tenant_id_device_id_external_id_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2026m05
    ADD CONSTRAINT attendance_punches_y2026m05_tenant_id_device_id_external_id_key UNIQUE (tenant_id, device_id, external_id, occurred_at);


-- Name: attendance_punches_y2026m05 attendance_punches_y2026m05_tenant_id_id_occurred_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2026m05
    ADD CONSTRAINT attendance_punches_y2026m05_tenant_id_id_occurred_at_key UNIQUE (tenant_id, id, occurred_at);


-- Name: attendance_punches_y2026m05 attendance_punches_y2026m05_tenant_id_idempotency_key_occur_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2026m05
    ADD CONSTRAINT attendance_punches_y2026m05_tenant_id_idempotency_key_occur_key UNIQUE (tenant_id, idempotency_key, occurred_at);


-- Name: attendance_punches_y2026m06 attendance_punches_y2026m06_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2026m06
    ADD CONSTRAINT attendance_punches_y2026m06_pkey PRIMARY KEY (id, occurred_at);


-- Name: attendance_punches_y2026m06 attendance_punches_y2026m06_tenant_id_device_id_external_id_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2026m06
    ADD CONSTRAINT attendance_punches_y2026m06_tenant_id_device_id_external_id_key UNIQUE (tenant_id, device_id, external_id, occurred_at);


-- Name: attendance_punches_y2026m06 attendance_punches_y2026m06_tenant_id_id_occurred_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2026m06
    ADD CONSTRAINT attendance_punches_y2026m06_tenant_id_id_occurred_at_key UNIQUE (tenant_id, id, occurred_at);


-- Name: attendance_punches_y2026m06 attendance_punches_y2026m06_tenant_id_idempotency_key_occur_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2026m06
    ADD CONSTRAINT attendance_punches_y2026m06_tenant_id_idempotency_key_occur_key UNIQUE (tenant_id, idempotency_key, occurred_at);


-- Name: attendance_punches_y2026m07 attendance_punches_y2026m07_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2026m07
    ADD CONSTRAINT attendance_punches_y2026m07_pkey PRIMARY KEY (id, occurred_at);


-- Name: attendance_punches_y2026m07 attendance_punches_y2026m07_tenant_id_device_id_external_id_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2026m07
    ADD CONSTRAINT attendance_punches_y2026m07_tenant_id_device_id_external_id_key UNIQUE (tenant_id, device_id, external_id, occurred_at);


-- Name: attendance_punches_y2026m07 attendance_punches_y2026m07_tenant_id_id_occurred_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2026m07
    ADD CONSTRAINT attendance_punches_y2026m07_tenant_id_id_occurred_at_key UNIQUE (tenant_id, id, occurred_at);


-- Name: attendance_punches_y2026m07 attendance_punches_y2026m07_tenant_id_idempotency_key_occur_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2026m07
    ADD CONSTRAINT attendance_punches_y2026m07_tenant_id_idempotency_key_occur_key UNIQUE (tenant_id, idempotency_key, occurred_at);


-- Name: attendance_punches_y2026m08 attendance_punches_y2026m08_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2026m08
    ADD CONSTRAINT attendance_punches_y2026m08_pkey PRIMARY KEY (id, occurred_at);


-- Name: attendance_punches_y2026m08 attendance_punches_y2026m08_tenant_id_device_id_external_id_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2026m08
    ADD CONSTRAINT attendance_punches_y2026m08_tenant_id_device_id_external_id_key UNIQUE (tenant_id, device_id, external_id, occurred_at);


-- Name: attendance_punches_y2026m08 attendance_punches_y2026m08_tenant_id_id_occurred_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2026m08
    ADD CONSTRAINT attendance_punches_y2026m08_tenant_id_id_occurred_at_key UNIQUE (tenant_id, id, occurred_at);


-- Name: attendance_punches_y2026m08 attendance_punches_y2026m08_tenant_id_idempotency_key_occur_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2026m08
    ADD CONSTRAINT attendance_punches_y2026m08_tenant_id_idempotency_key_occur_key UNIQUE (tenant_id, idempotency_key, occurred_at);


-- Name: attendance_punches_y2026m09 attendance_punches_y2026m09_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2026m09
    ADD CONSTRAINT attendance_punches_y2026m09_pkey PRIMARY KEY (id, occurred_at);


-- Name: attendance_punches_y2026m09 attendance_punches_y2026m09_tenant_id_device_id_external_id_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2026m09
    ADD CONSTRAINT attendance_punches_y2026m09_tenant_id_device_id_external_id_key UNIQUE (tenant_id, device_id, external_id, occurred_at);


-- Name: attendance_punches_y2026m09 attendance_punches_y2026m09_tenant_id_id_occurred_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2026m09
    ADD CONSTRAINT attendance_punches_y2026m09_tenant_id_id_occurred_at_key UNIQUE (tenant_id, id, occurred_at);


-- Name: attendance_punches_y2026m09 attendance_punches_y2026m09_tenant_id_idempotency_key_occur_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2026m09
    ADD CONSTRAINT attendance_punches_y2026m09_tenant_id_idempotency_key_occur_key UNIQUE (tenant_id, idempotency_key, occurred_at);


-- Name: attendance_punches_y2026m10 attendance_punches_y2026m10_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2026m10
    ADD CONSTRAINT attendance_punches_y2026m10_pkey PRIMARY KEY (id, occurred_at);


-- Name: attendance_punches_y2026m10 attendance_punches_y2026m10_tenant_id_device_id_external_id_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2026m10
    ADD CONSTRAINT attendance_punches_y2026m10_tenant_id_device_id_external_id_key UNIQUE (tenant_id, device_id, external_id, occurred_at);


-- Name: attendance_punches_y2026m10 attendance_punches_y2026m10_tenant_id_id_occurred_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2026m10
    ADD CONSTRAINT attendance_punches_y2026m10_tenant_id_id_occurred_at_key UNIQUE (tenant_id, id, occurred_at);


-- Name: attendance_punches_y2026m10 attendance_punches_y2026m10_tenant_id_idempotency_key_occur_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2026m10
    ADD CONSTRAINT attendance_punches_y2026m10_tenant_id_idempotency_key_occur_key UNIQUE (tenant_id, idempotency_key, occurred_at);


-- Name: attendance_punches_y2026m11 attendance_punches_y2026m11_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2026m11
    ADD CONSTRAINT attendance_punches_y2026m11_pkey PRIMARY KEY (id, occurred_at);


-- Name: attendance_punches_y2026m11 attendance_punches_y2026m11_tenant_id_device_id_external_id_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2026m11
    ADD CONSTRAINT attendance_punches_y2026m11_tenant_id_device_id_external_id_key UNIQUE (tenant_id, device_id, external_id, occurred_at);


-- Name: attendance_punches_y2026m11 attendance_punches_y2026m11_tenant_id_id_occurred_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2026m11
    ADD CONSTRAINT attendance_punches_y2026m11_tenant_id_id_occurred_at_key UNIQUE (tenant_id, id, occurred_at);


-- Name: attendance_punches_y2026m11 attendance_punches_y2026m11_tenant_id_idempotency_key_occur_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2026m11
    ADD CONSTRAINT attendance_punches_y2026m11_tenant_id_idempotency_key_occur_key UNIQUE (tenant_id, idempotency_key, occurred_at);


-- Name: attendance_punches_y2026m12 attendance_punches_y2026m12_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2026m12
    ADD CONSTRAINT attendance_punches_y2026m12_pkey PRIMARY KEY (id, occurred_at);


-- Name: attendance_punches_y2026m12 attendance_punches_y2026m12_tenant_id_device_id_external_id_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2026m12
    ADD CONSTRAINT attendance_punches_y2026m12_tenant_id_device_id_external_id_key UNIQUE (tenant_id, device_id, external_id, occurred_at);


-- Name: attendance_punches_y2026m12 attendance_punches_y2026m12_tenant_id_id_occurred_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2026m12
    ADD CONSTRAINT attendance_punches_y2026m12_tenant_id_id_occurred_at_key UNIQUE (tenant_id, id, occurred_at);


-- Name: attendance_punches_y2026m12 attendance_punches_y2026m12_tenant_id_idempotency_key_occur_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_punches_y2026m12
    ADD CONSTRAINT attendance_punches_y2026m12_tenant_id_idempotency_key_occur_key UNIQUE (tenant_id, idempotency_key, occurred_at);


-- Name: audit_logs uq_audit_logs__chain_seq; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs
    ADD CONSTRAINT uq_audit_logs__chain_seq UNIQUE (chain_key, seq, created_at);


-- Name: audit_logs_default audit_logs_default_chain_key_seq_created_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs_default
    ADD CONSTRAINT audit_logs_default_chain_key_seq_created_at_key UNIQUE (chain_key, seq, created_at);


-- Name: audit_logs pk_audit_logs; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs
    ADD CONSTRAINT pk_audit_logs PRIMARY KEY (id, created_at);


-- Name: audit_logs_default audit_logs_default_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs_default
    ADD CONSTRAINT audit_logs_default_pkey PRIMARY KEY (id, created_at);


-- Name: audit_logs uq_audit_logs__tenant_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs
    ADD CONSTRAINT uq_audit_logs__tenant_id UNIQUE (tenant_id, id, created_at);


-- Name: audit_logs_default audit_logs_default_tenant_id_id_created_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs_default
    ADD CONSTRAINT audit_logs_default_tenant_id_id_created_at_key UNIQUE (tenant_id, id, created_at);


-- Name: audit_logs_y2025m10 audit_logs_y2025m10_chain_key_seq_created_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs_y2025m10
    ADD CONSTRAINT audit_logs_y2025m10_chain_key_seq_created_at_key UNIQUE (chain_key, seq, created_at);


-- Name: audit_logs_y2025m10 audit_logs_y2025m10_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs_y2025m10
    ADD CONSTRAINT audit_logs_y2025m10_pkey PRIMARY KEY (id, created_at);


-- Name: audit_logs_y2025m10 audit_logs_y2025m10_tenant_id_id_created_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs_y2025m10
    ADD CONSTRAINT audit_logs_y2025m10_tenant_id_id_created_at_key UNIQUE (tenant_id, id, created_at);


-- Name: audit_logs_y2025m11 audit_logs_y2025m11_chain_key_seq_created_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs_y2025m11
    ADD CONSTRAINT audit_logs_y2025m11_chain_key_seq_created_at_key UNIQUE (chain_key, seq, created_at);


-- Name: audit_logs_y2025m11 audit_logs_y2025m11_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs_y2025m11
    ADD CONSTRAINT audit_logs_y2025m11_pkey PRIMARY KEY (id, created_at);


-- Name: audit_logs_y2025m11 audit_logs_y2025m11_tenant_id_id_created_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs_y2025m11
    ADD CONSTRAINT audit_logs_y2025m11_tenant_id_id_created_at_key UNIQUE (tenant_id, id, created_at);


-- Name: audit_logs_y2025m12 audit_logs_y2025m12_chain_key_seq_created_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs_y2025m12
    ADD CONSTRAINT audit_logs_y2025m12_chain_key_seq_created_at_key UNIQUE (chain_key, seq, created_at);


-- Name: audit_logs_y2025m12 audit_logs_y2025m12_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs_y2025m12
    ADD CONSTRAINT audit_logs_y2025m12_pkey PRIMARY KEY (id, created_at);


-- Name: audit_logs_y2025m12 audit_logs_y2025m12_tenant_id_id_created_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs_y2025m12
    ADD CONSTRAINT audit_logs_y2025m12_tenant_id_id_created_at_key UNIQUE (tenant_id, id, created_at);


-- Name: audit_logs_y2026m01 audit_logs_y2026m01_chain_key_seq_created_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs_y2026m01
    ADD CONSTRAINT audit_logs_y2026m01_chain_key_seq_created_at_key UNIQUE (chain_key, seq, created_at);


-- Name: audit_logs_y2026m01 audit_logs_y2026m01_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs_y2026m01
    ADD CONSTRAINT audit_logs_y2026m01_pkey PRIMARY KEY (id, created_at);


-- Name: audit_logs_y2026m01 audit_logs_y2026m01_tenant_id_id_created_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs_y2026m01
    ADD CONSTRAINT audit_logs_y2026m01_tenant_id_id_created_at_key UNIQUE (tenant_id, id, created_at);


-- Name: audit_logs_y2026m02 audit_logs_y2026m02_chain_key_seq_created_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs_y2026m02
    ADD CONSTRAINT audit_logs_y2026m02_chain_key_seq_created_at_key UNIQUE (chain_key, seq, created_at);


-- Name: audit_logs_y2026m02 audit_logs_y2026m02_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs_y2026m02
    ADD CONSTRAINT audit_logs_y2026m02_pkey PRIMARY KEY (id, created_at);


-- Name: audit_logs_y2026m02 audit_logs_y2026m02_tenant_id_id_created_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs_y2026m02
    ADD CONSTRAINT audit_logs_y2026m02_tenant_id_id_created_at_key UNIQUE (tenant_id, id, created_at);


-- Name: audit_logs_y2026m03 audit_logs_y2026m03_chain_key_seq_created_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs_y2026m03
    ADD CONSTRAINT audit_logs_y2026m03_chain_key_seq_created_at_key UNIQUE (chain_key, seq, created_at);


-- Name: audit_logs_y2026m03 audit_logs_y2026m03_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs_y2026m03
    ADD CONSTRAINT audit_logs_y2026m03_pkey PRIMARY KEY (id, created_at);


-- Name: audit_logs_y2026m03 audit_logs_y2026m03_tenant_id_id_created_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs_y2026m03
    ADD CONSTRAINT audit_logs_y2026m03_tenant_id_id_created_at_key UNIQUE (tenant_id, id, created_at);


-- Name: audit_logs_y2026m04 audit_logs_y2026m04_chain_key_seq_created_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs_y2026m04
    ADD CONSTRAINT audit_logs_y2026m04_chain_key_seq_created_at_key UNIQUE (chain_key, seq, created_at);


-- Name: audit_logs_y2026m04 audit_logs_y2026m04_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs_y2026m04
    ADD CONSTRAINT audit_logs_y2026m04_pkey PRIMARY KEY (id, created_at);


-- Name: audit_logs_y2026m04 audit_logs_y2026m04_tenant_id_id_created_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs_y2026m04
    ADD CONSTRAINT audit_logs_y2026m04_tenant_id_id_created_at_key UNIQUE (tenant_id, id, created_at);


-- Name: audit_logs_y2026m05 audit_logs_y2026m05_chain_key_seq_created_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs_y2026m05
    ADD CONSTRAINT audit_logs_y2026m05_chain_key_seq_created_at_key UNIQUE (chain_key, seq, created_at);


-- Name: audit_logs_y2026m05 audit_logs_y2026m05_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs_y2026m05
    ADD CONSTRAINT audit_logs_y2026m05_pkey PRIMARY KEY (id, created_at);


-- Name: audit_logs_y2026m05 audit_logs_y2026m05_tenant_id_id_created_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs_y2026m05
    ADD CONSTRAINT audit_logs_y2026m05_tenant_id_id_created_at_key UNIQUE (tenant_id, id, created_at);


-- Name: audit_logs_y2026m06 audit_logs_y2026m06_chain_key_seq_created_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs_y2026m06
    ADD CONSTRAINT audit_logs_y2026m06_chain_key_seq_created_at_key UNIQUE (chain_key, seq, created_at);


-- Name: audit_logs_y2026m06 audit_logs_y2026m06_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs_y2026m06
    ADD CONSTRAINT audit_logs_y2026m06_pkey PRIMARY KEY (id, created_at);


-- Name: audit_logs_y2026m06 audit_logs_y2026m06_tenant_id_id_created_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs_y2026m06
    ADD CONSTRAINT audit_logs_y2026m06_tenant_id_id_created_at_key UNIQUE (tenant_id, id, created_at);


-- Name: audit_logs_y2026m07 audit_logs_y2026m07_chain_key_seq_created_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs_y2026m07
    ADD CONSTRAINT audit_logs_y2026m07_chain_key_seq_created_at_key UNIQUE (chain_key, seq, created_at);


-- Name: audit_logs_y2026m07 audit_logs_y2026m07_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs_y2026m07
    ADD CONSTRAINT audit_logs_y2026m07_pkey PRIMARY KEY (id, created_at);


-- Name: audit_logs_y2026m07 audit_logs_y2026m07_tenant_id_id_created_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs_y2026m07
    ADD CONSTRAINT audit_logs_y2026m07_tenant_id_id_created_at_key UNIQUE (tenant_id, id, created_at);


-- Name: audit_logs_y2026m08 audit_logs_y2026m08_chain_key_seq_created_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs_y2026m08
    ADD CONSTRAINT audit_logs_y2026m08_chain_key_seq_created_at_key UNIQUE (chain_key, seq, created_at);


-- Name: audit_logs_y2026m08 audit_logs_y2026m08_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs_y2026m08
    ADD CONSTRAINT audit_logs_y2026m08_pkey PRIMARY KEY (id, created_at);


-- Name: audit_logs_y2026m08 audit_logs_y2026m08_tenant_id_id_created_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs_y2026m08
    ADD CONSTRAINT audit_logs_y2026m08_tenant_id_id_created_at_key UNIQUE (tenant_id, id, created_at);


-- Name: audit_logs_y2026m09 audit_logs_y2026m09_chain_key_seq_created_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs_y2026m09
    ADD CONSTRAINT audit_logs_y2026m09_chain_key_seq_created_at_key UNIQUE (chain_key, seq, created_at);


-- Name: audit_logs_y2026m09 audit_logs_y2026m09_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs_y2026m09
    ADD CONSTRAINT audit_logs_y2026m09_pkey PRIMARY KEY (id, created_at);


-- Name: audit_logs_y2026m09 audit_logs_y2026m09_tenant_id_id_created_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs_y2026m09
    ADD CONSTRAINT audit_logs_y2026m09_tenant_id_id_created_at_key UNIQUE (tenant_id, id, created_at);


-- Name: audit_logs_y2026m10 audit_logs_y2026m10_chain_key_seq_created_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs_y2026m10
    ADD CONSTRAINT audit_logs_y2026m10_chain_key_seq_created_at_key UNIQUE (chain_key, seq, created_at);


-- Name: audit_logs_y2026m10 audit_logs_y2026m10_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs_y2026m10
    ADD CONSTRAINT audit_logs_y2026m10_pkey PRIMARY KEY (id, created_at);


-- Name: audit_logs_y2026m10 audit_logs_y2026m10_tenant_id_id_created_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs_y2026m10
    ADD CONSTRAINT audit_logs_y2026m10_tenant_id_id_created_at_key UNIQUE (tenant_id, id, created_at);


-- Name: audit_logs_y2026m11 audit_logs_y2026m11_chain_key_seq_created_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs_y2026m11
    ADD CONSTRAINT audit_logs_y2026m11_chain_key_seq_created_at_key UNIQUE (chain_key, seq, created_at);


-- Name: audit_logs_y2026m11 audit_logs_y2026m11_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs_y2026m11
    ADD CONSTRAINT audit_logs_y2026m11_pkey PRIMARY KEY (id, created_at);


-- Name: audit_logs_y2026m11 audit_logs_y2026m11_tenant_id_id_created_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs_y2026m11
    ADD CONSTRAINT audit_logs_y2026m11_tenant_id_id_created_at_key UNIQUE (tenant_id, id, created_at);


-- Name: audit_logs_y2026m12 audit_logs_y2026m12_chain_key_seq_created_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs_y2026m12
    ADD CONSTRAINT audit_logs_y2026m12_chain_key_seq_created_at_key UNIQUE (chain_key, seq, created_at);


-- Name: audit_logs_y2026m12 audit_logs_y2026m12_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs_y2026m12
    ADD CONSTRAINT audit_logs_y2026m12_pkey PRIMARY KEY (id, created_at);


-- Name: audit_logs_y2026m12 audit_logs_y2026m12_tenant_id_id_created_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.audit_logs_y2026m12
    ADD CONSTRAINT audit_logs_y2026m12_tenant_id_id_created_at_key UNIQUE (tenant_id, id, created_at);


-- Name: background_job_items pk_background_job_items; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items
    ADD CONSTRAINT pk_background_job_items PRIMARY KEY (id, created_at);


-- Name: background_job_items_default background_job_items_default_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items_default
    ADD CONSTRAINT background_job_items_default_pkey PRIMARY KEY (id, created_at);


-- Name: background_job_items uq_background_job_items__tenant_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items
    ADD CONSTRAINT uq_background_job_items__tenant_id UNIQUE (tenant_id, id, created_at);


-- Name: background_job_items_default background_job_items_default_tenant_id_id_created_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items_default
    ADD CONSTRAINT background_job_items_default_tenant_id_id_created_at_key UNIQUE (tenant_id, id, created_at);


-- Name: background_job_items_y2025m10 background_job_items_y2025m10_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items_y2025m10
    ADD CONSTRAINT background_job_items_y2025m10_pkey PRIMARY KEY (id, created_at);


-- Name: background_job_items_y2025m10 background_job_items_y2025m10_tenant_id_id_created_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items_y2025m10
    ADD CONSTRAINT background_job_items_y2025m10_tenant_id_id_created_at_key UNIQUE (tenant_id, id, created_at);


-- Name: background_job_items_y2025m11 background_job_items_y2025m11_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items_y2025m11
    ADD CONSTRAINT background_job_items_y2025m11_pkey PRIMARY KEY (id, created_at);


-- Name: background_job_items_y2025m11 background_job_items_y2025m11_tenant_id_id_created_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items_y2025m11
    ADD CONSTRAINT background_job_items_y2025m11_tenant_id_id_created_at_key UNIQUE (tenant_id, id, created_at);


-- Name: background_job_items_y2025m12 background_job_items_y2025m12_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items_y2025m12
    ADD CONSTRAINT background_job_items_y2025m12_pkey PRIMARY KEY (id, created_at);


-- Name: background_job_items_y2025m12 background_job_items_y2025m12_tenant_id_id_created_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items_y2025m12
    ADD CONSTRAINT background_job_items_y2025m12_tenant_id_id_created_at_key UNIQUE (tenant_id, id, created_at);


-- Name: background_job_items_y2026m01 background_job_items_y2026m01_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items_y2026m01
    ADD CONSTRAINT background_job_items_y2026m01_pkey PRIMARY KEY (id, created_at);


-- Name: background_job_items_y2026m01 background_job_items_y2026m01_tenant_id_id_created_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items_y2026m01
    ADD CONSTRAINT background_job_items_y2026m01_tenant_id_id_created_at_key UNIQUE (tenant_id, id, created_at);


-- Name: background_job_items_y2026m02 background_job_items_y2026m02_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items_y2026m02
    ADD CONSTRAINT background_job_items_y2026m02_pkey PRIMARY KEY (id, created_at);


-- Name: background_job_items_y2026m02 background_job_items_y2026m02_tenant_id_id_created_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items_y2026m02
    ADD CONSTRAINT background_job_items_y2026m02_tenant_id_id_created_at_key UNIQUE (tenant_id, id, created_at);


-- Name: background_job_items_y2026m03 background_job_items_y2026m03_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items_y2026m03
    ADD CONSTRAINT background_job_items_y2026m03_pkey PRIMARY KEY (id, created_at);


-- Name: background_job_items_y2026m03 background_job_items_y2026m03_tenant_id_id_created_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items_y2026m03
    ADD CONSTRAINT background_job_items_y2026m03_tenant_id_id_created_at_key UNIQUE (tenant_id, id, created_at);


-- Name: background_job_items_y2026m04 background_job_items_y2026m04_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items_y2026m04
    ADD CONSTRAINT background_job_items_y2026m04_pkey PRIMARY KEY (id, created_at);


-- Name: background_job_items_y2026m04 background_job_items_y2026m04_tenant_id_id_created_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items_y2026m04
    ADD CONSTRAINT background_job_items_y2026m04_tenant_id_id_created_at_key UNIQUE (tenant_id, id, created_at);


-- Name: background_job_items_y2026m05 background_job_items_y2026m05_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items_y2026m05
    ADD CONSTRAINT background_job_items_y2026m05_pkey PRIMARY KEY (id, created_at);


-- Name: background_job_items_y2026m05 background_job_items_y2026m05_tenant_id_id_created_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items_y2026m05
    ADD CONSTRAINT background_job_items_y2026m05_tenant_id_id_created_at_key UNIQUE (tenant_id, id, created_at);


-- Name: background_job_items_y2026m06 background_job_items_y2026m06_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items_y2026m06
    ADD CONSTRAINT background_job_items_y2026m06_pkey PRIMARY KEY (id, created_at);


-- Name: background_job_items_y2026m06 background_job_items_y2026m06_tenant_id_id_created_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items_y2026m06
    ADD CONSTRAINT background_job_items_y2026m06_tenant_id_id_created_at_key UNIQUE (tenant_id, id, created_at);


-- Name: background_job_items_y2026m07 background_job_items_y2026m07_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items_y2026m07
    ADD CONSTRAINT background_job_items_y2026m07_pkey PRIMARY KEY (id, created_at);


-- Name: background_job_items_y2026m07 background_job_items_y2026m07_tenant_id_id_created_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items_y2026m07
    ADD CONSTRAINT background_job_items_y2026m07_tenant_id_id_created_at_key UNIQUE (tenant_id, id, created_at);


-- Name: background_job_items_y2026m08 background_job_items_y2026m08_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items_y2026m08
    ADD CONSTRAINT background_job_items_y2026m08_pkey PRIMARY KEY (id, created_at);


-- Name: background_job_items_y2026m08 background_job_items_y2026m08_tenant_id_id_created_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items_y2026m08
    ADD CONSTRAINT background_job_items_y2026m08_tenant_id_id_created_at_key UNIQUE (tenant_id, id, created_at);


-- Name: background_job_items_y2026m09 background_job_items_y2026m09_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items_y2026m09
    ADD CONSTRAINT background_job_items_y2026m09_pkey PRIMARY KEY (id, created_at);


-- Name: background_job_items_y2026m09 background_job_items_y2026m09_tenant_id_id_created_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items_y2026m09
    ADD CONSTRAINT background_job_items_y2026m09_tenant_id_id_created_at_key UNIQUE (tenant_id, id, created_at);


-- Name: background_job_items_y2026m10 background_job_items_y2026m10_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items_y2026m10
    ADD CONSTRAINT background_job_items_y2026m10_pkey PRIMARY KEY (id, created_at);


-- Name: background_job_items_y2026m10 background_job_items_y2026m10_tenant_id_id_created_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items_y2026m10
    ADD CONSTRAINT background_job_items_y2026m10_tenant_id_id_created_at_key UNIQUE (tenant_id, id, created_at);


-- Name: background_job_items_y2026m11 background_job_items_y2026m11_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items_y2026m11
    ADD CONSTRAINT background_job_items_y2026m11_pkey PRIMARY KEY (id, created_at);


-- Name: background_job_items_y2026m11 background_job_items_y2026m11_tenant_id_id_created_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items_y2026m11
    ADD CONSTRAINT background_job_items_y2026m11_tenant_id_id_created_at_key UNIQUE (tenant_id, id, created_at);


-- Name: background_job_items_y2026m12 background_job_items_y2026m12_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items_y2026m12
    ADD CONSTRAINT background_job_items_y2026m12_pkey PRIMARY KEY (id, created_at);


-- Name: background_job_items_y2026m12 background_job_items_y2026m12_tenant_id_id_created_at_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.background_job_items_y2026m12
    ADD CONSTRAINT background_job_items_y2026m12_tenant_id_id_created_at_key UNIQUE (tenant_id, id, created_at);


-- Name: company_pay_policies ex_company_pay_policies__policy_no_overlap; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.company_pay_policies
    ADD CONSTRAINT ex_company_pay_policies__policy_no_overlap EXCLUDE USING gist (tenant_id WITH =, company_id WITH =, policy_key WITH =, COALESCE(pay_component_code, ''::character varying) WITH =, daterange(effective_from, COALESCE(effective_to, 'infinity'::date), '[]'::text) WITH &&);


-- Name: employee_assignments ex_employee_assignments__employee_no_overlap; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.employee_assignments
    ADD CONSTRAINT ex_employee_assignments__employee_no_overlap EXCLUDE USING gist (tenant_id WITH =, employee_id WITH =, daterange(effective_from, COALESCE(effective_to, 'infinity'::date), '[]'::text) WITH &&);


-- Name: employee_bank_accounts ex_employee_bank_accounts__employee_no_overlap; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.employee_bank_accounts
    ADD CONSTRAINT ex_employee_bank_accounts__employee_no_overlap EXCLUDE USING gist (tenant_id WITH =, employee_id WITH =, daterange(effective_from, COALESCE(effective_to, 'infinity'::date), '[]'::text) WITH &&);


-- Name: employee_contracts ex_employee_contracts__employee_no_overlap; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.employee_contracts
    ADD CONSTRAINT ex_employee_contracts__employee_no_overlap EXCLUDE USING gist (tenant_id WITH =, employee_id WITH =, daterange(effective_from, COALESCE(effective_to, 'infinity'::date), '[]'::text) WITH &&);


-- Name: employee_gosi_registrations ex_employee_gosi_registrations__employee_no_overlap; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.employee_gosi_registrations
    ADD CONSTRAINT ex_employee_gosi_registrations__employee_no_overlap EXCLUDE USING gist (tenant_id WITH =, employee_id WITH =, daterange(effective_from, COALESCE(effective_to, 'infinity'::date), '[]'::text) WITH &&);


-- Name: employee_salaries ex_employee_salaries__employee_no_overlap; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.employee_salaries
    ADD CONSTRAINT ex_employee_salaries__employee_no_overlap EXCLUDE USING gist (tenant_id WITH =, employee_id WITH =, daterange(effective_from, COALESCE(effective_to, 'infinity'::date), '[]'::text) WITH &&);


-- Name: nitaqat_grid ex_nitaqat_grid__saudization_band_no_overlap; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.nitaqat_grid
    ADD CONSTRAINT ex_nitaqat_grid__saudization_band_no_overlap EXCLUDE USING gist (activity_code WITH =, size_tier WITH =, grid_version WITH =, numrange(min_saudization_pct, COALESCE(max_saudization_pct, 'Infinity'::numeric), '[)'::text) WITH &&);


-- Name: retention_policies ex_retention_policies__entity_no_overlap; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.retention_policies
    ADD CONSTRAINT ex_retention_policies__entity_no_overlap EXCLUDE USING gist (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid) WITH =, entity_name WITH =, daterange(effective_from, COALESCE(effective_to, 'infinity'::date), '[]'::text) WITH &&);


-- Name: shift_assignments ex_shift_assignments__employee_no_overlap; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.shift_assignments
    ADD CONSTRAINT ex_shift_assignments__employee_no_overlap EXCLUDE USING gist (tenant_id WITH =, employee_id WITH =, daterange(effective_from, COALESCE(effective_to, 'infinity'::date), '[]'::text) WITH &&);


-- Name: statutory_rule_bands ex_statutory_rule_bands__band_no_overlap; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.statutory_rule_bands
    ADD CONSTRAINT ex_statutory_rule_bands__band_no_overlap EXCLUDE USING gist (statutory_rule_id WITH =, numrange(lower_bound, COALESCE(upper_bound, 'Infinity'::numeric), '[)'::text) WITH &&);


-- Name: statutory_rules ex_statutory_rules__rule_no_overlap; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.statutory_rules
    ADD CONSTRAINT ex_statutory_rules__rule_no_overlap EXCLUDE USING gist (country_code WITH =, family WITH =, rule_key WITH =, nationality_class WITH =, cohort WITH =, COALESCE(gosi_branch, ''::character varying) WITH =, COALESCE(payer, ''::character varying) WITH =, daterange(effective_from, COALESCE(effective_to, 'infinity'::date), '[]'::text) WITH &&);


-- Name: approval_actions pk_approval_actions; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.approval_actions
    ADD CONSTRAINT pk_approval_actions PRIMARY KEY (id);


-- Name: approval_delegations pk_approval_delegations; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.approval_delegations
    ADD CONSTRAINT pk_approval_delegations PRIMARY KEY (id);


-- Name: approval_requests pk_approval_requests; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.approval_requests
    ADD CONSTRAINT pk_approval_requests PRIMARY KEY (id);


-- Name: approval_workflows pk_approval_workflows; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.approval_workflows
    ADD CONSTRAINT pk_approval_workflows PRIMARY KEY (id);


-- Name: attendance_devices pk_attendance_devices; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_devices
    ADD CONSTRAINT pk_attendance_devices PRIMARY KEY (id);


-- Name: auth_sessions pk_auth_sessions; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.auth_sessions
    ADD CONSTRAINT pk_auth_sessions PRIMARY KEY (id);


-- Name: auth_tokens pk_auth_tokens; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.auth_tokens
    ADD CONSTRAINT pk_auth_tokens PRIMARY KEY (id);


-- Name: background_jobs pk_background_jobs; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.background_jobs
    ADD CONSTRAINT pk_background_jobs PRIMARY KEY (id);


-- Name: branches pk_branches; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.branches
    ADD CONSTRAINT pk_branches PRIMARY KEY (id);


-- Name: companies pk_companies; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.companies
    ADD CONSTRAINT pk_companies PRIMARY KEY (id);


-- Name: company_pay_policies pk_company_pay_policies; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.company_pay_policies
    ADD CONSTRAINT pk_company_pay_policies PRIMARY KEY (id);


-- Name: cost_centers pk_cost_centers; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.cost_centers
    ADD CONSTRAINT pk_cost_centers PRIMARY KEY (id);


-- Name: data_protection_keys pk_data_protection_keys; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.data_protection_keys
    ADD CONSTRAINT pk_data_protection_keys PRIMARY KEY (id);


-- Name: departments pk_departments; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.departments
    ADD CONSTRAINT pk_departments PRIMARY KEY (id);


-- Name: designations pk_designations; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.designations
    ADD CONSTRAINT pk_designations PRIMARY KEY (id);


-- Name: document_templates pk_document_templates; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.document_templates
    ADD CONSTRAINT pk_document_templates PRIMARY KEY (id);


-- Name: employee_assignments pk_employee_assignments; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.employee_assignments
    ADD CONSTRAINT pk_employee_assignments PRIMARY KEY (id);


-- Name: employee_bank_accounts pk_employee_bank_accounts; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.employee_bank_accounts
    ADD CONSTRAINT pk_employee_bank_accounts PRIMARY KEY (id);


-- Name: employee_contracts pk_employee_contracts; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.employee_contracts
    ADD CONSTRAINT pk_employee_contracts PRIMARY KEY (id);


-- Name: employee_documents pk_employee_documents; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.employee_documents
    ADD CONSTRAINT pk_employee_documents PRIMARY KEY (id);


-- Name: employee_gosi_registrations pk_employee_gosi_registrations; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.employee_gosi_registrations
    ADD CONSTRAINT pk_employee_gosi_registrations PRIMARY KEY (id);


-- Name: employee_salaries pk_employee_salaries; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.employee_salaries
    ADD CONSTRAINT pk_employee_salaries PRIMARY KEY (id);


-- Name: employees pk_employees; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.employees
    ADD CONSTRAINT pk_employees PRIMARY KEY (id);


-- Name: eos_calculations pk_eos_calculations; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.eos_calculations
    ADD CONSTRAINT pk_eos_calculations PRIMARY KEY (id);


-- Name: files pk_files; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.files
    ADD CONSTRAINT pk_files PRIMARY KEY (id);


-- Name: final_settlement_lines pk_final_settlement_lines; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.final_settlement_lines
    ADD CONSTRAINT pk_final_settlement_lines PRIMARY KEY (id);


-- Name: final_settlements pk_final_settlements; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.final_settlements
    ADD CONSTRAINT pk_final_settlements PRIMARY KEY (id);


-- Name: gl_journal_lines pk_gl_journal_lines; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.gl_journal_lines
    ADD CONSTRAINT pk_gl_journal_lines PRIMARY KEY (id);


-- Name: gl_journals pk_gl_journals; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.gl_journals
    ADD CONSTRAINT pk_gl_journals PRIMARY KEY (id);


-- Name: gl_mappings pk_gl_mappings; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.gl_mappings
    ADD CONSTRAINT pk_gl_mappings PRIMARY KEY (id);


-- Name: gl_period_closes pk_gl_period_closes; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.gl_period_closes
    ADD CONSTRAINT pk_gl_period_closes PRIMARY KEY (id);


-- Name: gosi_filings pk_gosi_filings; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.gosi_filings
    ADD CONSTRAINT pk_gosi_filings PRIMARY KEY (id);


-- Name: grades pk_grades; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.grades
    ADD CONSTRAINT pk_grades PRIMARY KEY (id);


-- Name: leave_ledger pk_leave_ledger; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.leave_ledger
    ADD CONSTRAINT pk_leave_ledger PRIMARY KEY (id);


-- Name: leave_requests pk_leave_requests; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.leave_requests
    ADD CONSTRAINT pk_leave_requests PRIMARY KEY (id);


-- Name: leave_types pk_leave_types; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.leave_types
    ADD CONSTRAINT pk_leave_types PRIMARY KEY (id);


-- Name: loan_installments pk_loan_installments; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.loan_installments
    ADD CONSTRAINT pk_loan_installments PRIMARY KEY (id);


-- Name: loans pk_loans; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.loans
    ADD CONSTRAINT pk_loans PRIMARY KEY (id);


-- Name: nitaqat_grid pk_nitaqat_grid; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.nitaqat_grid
    ADD CONSTRAINT pk_nitaqat_grid PRIMARY KEY (id);


-- Name: nitaqat_snapshots pk_nitaqat_snapshots; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.nitaqat_snapshots
    ADD CONSTRAINT pk_nitaqat_snapshots PRIMARY KEY (id);


-- Name: notification_deliveries pk_notification_deliveries; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.notification_deliveries
    ADD CONSTRAINT pk_notification_deliveries PRIMARY KEY (id);


-- Name: notifications pk_notifications; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.notifications
    ADD CONSTRAINT pk_notifications PRIMARY KEY (id);


-- Name: number_sequences pk_number_sequences; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.number_sequences
    ADD CONSTRAINT pk_number_sequences PRIMARY KEY (id);


-- Name: overtime_requests pk_overtime_requests; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.overtime_requests
    ADD CONSTRAINT pk_overtime_requests PRIMARY KEY (id);


-- Name: pay_components pk_pay_components; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.pay_components
    ADD CONSTRAINT pk_pay_components PRIMARY KEY (id);


-- Name: payroll_audit_logs pk_payroll_audit_logs; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.payroll_audit_logs
    ADD CONSTRAINT pk_payroll_audit_logs PRIMARY KEY (id);


-- Name: payroll_inputs pk_payroll_inputs; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.payroll_inputs
    ADD CONSTRAINT pk_payroll_inputs PRIMARY KEY (id);


-- Name: payroll_issues pk_payroll_issues; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.payroll_issues
    ADD CONSTRAINT pk_payroll_issues PRIMARY KEY (id);


-- Name: payroll_runs pk_payroll_runs; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.payroll_runs
    ADD CONSTRAINT pk_payroll_runs PRIMARY KEY (id);


-- Name: payroll_slip_lines pk_payroll_slip_lines; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.payroll_slip_lines
    ADD CONSTRAINT pk_payroll_slip_lines PRIMARY KEY (id);


-- Name: payroll_slips pk_payroll_slips; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.payroll_slips
    ADD CONSTRAINT pk_payroll_slips PRIMARY KEY (id);


-- Name: permission_grantor_records pk_permission_grantor_records; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.permission_grantor_records
    ADD CONSTRAINT pk_permission_grantor_records PRIMARY KEY (id);


-- Name: permissions pk_permissions; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.permissions
    ADD CONSTRAINT pk_permissions PRIMARY KEY (id);


-- Name: platform_users pk_platform_users; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.platform_users
    ADD CONSTRAINT pk_platform_users PRIMARY KEY (id);


-- Name: public_holidays pk_public_holidays; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.public_holidays
    ADD CONSTRAINT pk_public_holidays PRIMARY KEY (id);


-- Name: retention_policies pk_retention_policies; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.retention_policies
    ADD CONSTRAINT pk_retention_policies PRIMARY KEY (id);


-- Name: retention_purge_audits pk_retention_purge_audits; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.retention_purge_audits
    ADD CONSTRAINT pk_retention_purge_audits PRIMARY KEY (id);


-- Name: role_permissions pk_role_permissions; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.role_permissions
    ADD CONSTRAINT pk_role_permissions PRIMARY KEY (id);


-- Name: roles pk_roles; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.roles
    ADD CONSTRAINT pk_roles PRIMARY KEY (id);


-- Name: shift_assignments pk_shift_assignments; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.shift_assignments
    ADD CONSTRAINT pk_shift_assignments PRIMARY KEY (id);


-- Name: shifts pk_shifts; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.shifts
    ADD CONSTRAINT pk_shifts PRIMARY KEY (id);


-- Name: statutory_rule_bands pk_statutory_rule_bands; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.statutory_rule_bands
    ADD CONSTRAINT pk_statutory_rule_bands PRIMARY KEY (id);


-- Name: statutory_rules pk_statutory_rules; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.statutory_rules
    ADD CONSTRAINT pk_statutory_rules PRIMARY KEY (id);


-- Name: tenant_settings pk_tenant_settings; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.tenant_settings
    ADD CONSTRAINT pk_tenant_settings PRIMARY KEY (id);


-- Name: tenants pk_tenants; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.tenants
    ADD CONSTRAINT pk_tenants PRIMARY KEY (id);


-- Name: timesheet_day_reconciliations pk_timesheet_day_reconciliations; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_day_reconciliations
    ADD CONSTRAINT pk_timesheet_day_reconciliations PRIMARY KEY (id);


-- Name: timesheet_entries pk_timesheet_entries; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries
    ADD CONSTRAINT pk_timesheet_entries PRIMARY KEY (id, work_date);


-- Name: timesheets pk_timesheets; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheets
    ADD CONSTRAINT pk_timesheets PRIMARY KEY (id);


-- Name: user_roles pk_user_roles; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.user_roles
    ADD CONSTRAINT pk_user_roles PRIMARY KEY (id);


-- Name: users pk_users; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.users
    ADD CONSTRAINT pk_users PRIMARY KEY (id);


-- Name: wps_batches pk_wps_batches; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.wps_batches
    ADD CONSTRAINT pk_wps_batches PRIMARY KEY (id);


-- Name: wps_lines pk_wps_lines; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.wps_lines
    ADD CONSTRAINT pk_wps_lines PRIMARY KEY (id);


-- Name: timesheet_entries_default timesheet_entries_default_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries_default
    ADD CONSTRAINT timesheet_entries_default_pkey PRIMARY KEY (id, work_date);


-- Name: timesheet_entries uq_timesheet_entries__tenant_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries
    ADD CONSTRAINT uq_timesheet_entries__tenant_id UNIQUE (tenant_id, id, work_date);


-- Name: timesheet_entries_default timesheet_entries_default_tenant_id_id_work_date_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries_default
    ADD CONSTRAINT timesheet_entries_default_tenant_id_id_work_date_key UNIQUE (tenant_id, id, work_date);


-- Name: timesheet_entries_y2025m10 timesheet_entries_y2025m10_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries_y2025m10
    ADD CONSTRAINT timesheet_entries_y2025m10_pkey PRIMARY KEY (id, work_date);


-- Name: timesheet_entries_y2025m10 timesheet_entries_y2025m10_tenant_id_id_work_date_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries_y2025m10
    ADD CONSTRAINT timesheet_entries_y2025m10_tenant_id_id_work_date_key UNIQUE (tenant_id, id, work_date);


-- Name: timesheet_entries_y2025m11 timesheet_entries_y2025m11_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries_y2025m11
    ADD CONSTRAINT timesheet_entries_y2025m11_pkey PRIMARY KEY (id, work_date);


-- Name: timesheet_entries_y2025m11 timesheet_entries_y2025m11_tenant_id_id_work_date_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries_y2025m11
    ADD CONSTRAINT timesheet_entries_y2025m11_tenant_id_id_work_date_key UNIQUE (tenant_id, id, work_date);


-- Name: timesheet_entries_y2025m12 timesheet_entries_y2025m12_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries_y2025m12
    ADD CONSTRAINT timesheet_entries_y2025m12_pkey PRIMARY KEY (id, work_date);


-- Name: timesheet_entries_y2025m12 timesheet_entries_y2025m12_tenant_id_id_work_date_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries_y2025m12
    ADD CONSTRAINT timesheet_entries_y2025m12_tenant_id_id_work_date_key UNIQUE (tenant_id, id, work_date);


-- Name: timesheet_entries_y2026m01 timesheet_entries_y2026m01_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries_y2026m01
    ADD CONSTRAINT timesheet_entries_y2026m01_pkey PRIMARY KEY (id, work_date);


-- Name: timesheet_entries_y2026m01 timesheet_entries_y2026m01_tenant_id_id_work_date_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries_y2026m01
    ADD CONSTRAINT timesheet_entries_y2026m01_tenant_id_id_work_date_key UNIQUE (tenant_id, id, work_date);


-- Name: timesheet_entries_y2026m02 timesheet_entries_y2026m02_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries_y2026m02
    ADD CONSTRAINT timesheet_entries_y2026m02_pkey PRIMARY KEY (id, work_date);


-- Name: timesheet_entries_y2026m02 timesheet_entries_y2026m02_tenant_id_id_work_date_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries_y2026m02
    ADD CONSTRAINT timesheet_entries_y2026m02_tenant_id_id_work_date_key UNIQUE (tenant_id, id, work_date);


-- Name: timesheet_entries_y2026m03 timesheet_entries_y2026m03_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries_y2026m03
    ADD CONSTRAINT timesheet_entries_y2026m03_pkey PRIMARY KEY (id, work_date);


-- Name: timesheet_entries_y2026m03 timesheet_entries_y2026m03_tenant_id_id_work_date_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries_y2026m03
    ADD CONSTRAINT timesheet_entries_y2026m03_tenant_id_id_work_date_key UNIQUE (tenant_id, id, work_date);


-- Name: timesheet_entries_y2026m04 timesheet_entries_y2026m04_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries_y2026m04
    ADD CONSTRAINT timesheet_entries_y2026m04_pkey PRIMARY KEY (id, work_date);


-- Name: timesheet_entries_y2026m04 timesheet_entries_y2026m04_tenant_id_id_work_date_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries_y2026m04
    ADD CONSTRAINT timesheet_entries_y2026m04_tenant_id_id_work_date_key UNIQUE (tenant_id, id, work_date);


-- Name: timesheet_entries_y2026m05 timesheet_entries_y2026m05_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries_y2026m05
    ADD CONSTRAINT timesheet_entries_y2026m05_pkey PRIMARY KEY (id, work_date);


-- Name: timesheet_entries_y2026m05 timesheet_entries_y2026m05_tenant_id_id_work_date_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries_y2026m05
    ADD CONSTRAINT timesheet_entries_y2026m05_tenant_id_id_work_date_key UNIQUE (tenant_id, id, work_date);


-- Name: timesheet_entries_y2026m06 timesheet_entries_y2026m06_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries_y2026m06
    ADD CONSTRAINT timesheet_entries_y2026m06_pkey PRIMARY KEY (id, work_date);


-- Name: timesheet_entries_y2026m06 timesheet_entries_y2026m06_tenant_id_id_work_date_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries_y2026m06
    ADD CONSTRAINT timesheet_entries_y2026m06_tenant_id_id_work_date_key UNIQUE (tenant_id, id, work_date);


-- Name: timesheet_entries_y2026m07 timesheet_entries_y2026m07_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries_y2026m07
    ADD CONSTRAINT timesheet_entries_y2026m07_pkey PRIMARY KEY (id, work_date);


-- Name: timesheet_entries_y2026m07 timesheet_entries_y2026m07_tenant_id_id_work_date_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries_y2026m07
    ADD CONSTRAINT timesheet_entries_y2026m07_tenant_id_id_work_date_key UNIQUE (tenant_id, id, work_date);


-- Name: timesheet_entries_y2026m08 timesheet_entries_y2026m08_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries_y2026m08
    ADD CONSTRAINT timesheet_entries_y2026m08_pkey PRIMARY KEY (id, work_date);


-- Name: timesheet_entries_y2026m08 timesheet_entries_y2026m08_tenant_id_id_work_date_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries_y2026m08
    ADD CONSTRAINT timesheet_entries_y2026m08_tenant_id_id_work_date_key UNIQUE (tenant_id, id, work_date);


-- Name: timesheet_entries_y2026m09 timesheet_entries_y2026m09_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries_y2026m09
    ADD CONSTRAINT timesheet_entries_y2026m09_pkey PRIMARY KEY (id, work_date);


-- Name: timesheet_entries_y2026m09 timesheet_entries_y2026m09_tenant_id_id_work_date_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries_y2026m09
    ADD CONSTRAINT timesheet_entries_y2026m09_tenant_id_id_work_date_key UNIQUE (tenant_id, id, work_date);


-- Name: timesheet_entries_y2026m10 timesheet_entries_y2026m10_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries_y2026m10
    ADD CONSTRAINT timesheet_entries_y2026m10_pkey PRIMARY KEY (id, work_date);


-- Name: timesheet_entries_y2026m10 timesheet_entries_y2026m10_tenant_id_id_work_date_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries_y2026m10
    ADD CONSTRAINT timesheet_entries_y2026m10_tenant_id_id_work_date_key UNIQUE (tenant_id, id, work_date);


-- Name: timesheet_entries_y2026m11 timesheet_entries_y2026m11_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries_y2026m11
    ADD CONSTRAINT timesheet_entries_y2026m11_pkey PRIMARY KEY (id, work_date);


-- Name: timesheet_entries_y2026m11 timesheet_entries_y2026m11_tenant_id_id_work_date_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries_y2026m11
    ADD CONSTRAINT timesheet_entries_y2026m11_tenant_id_id_work_date_key UNIQUE (tenant_id, id, work_date);


-- Name: timesheet_entries_y2026m12 timesheet_entries_y2026m12_pkey; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries_y2026m12
    ADD CONSTRAINT timesheet_entries_y2026m12_pkey PRIMARY KEY (id, work_date);


-- Name: timesheet_entries_y2026m12 timesheet_entries_y2026m12_tenant_id_id_work_date_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_entries_y2026m12
    ADD CONSTRAINT timesheet_entries_y2026m12_tenant_id_id_work_date_key UNIQUE (tenant_id, id, work_date);


-- Name: approval_actions uq_approval_actions__tenant_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.approval_actions
    ADD CONSTRAINT uq_approval_actions__tenant_id UNIQUE (tenant_id, id);


-- Name: approval_delegations uq_approval_delegations__tenant_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.approval_delegations
    ADD CONSTRAINT uq_approval_delegations__tenant_id UNIQUE (tenant_id, id);


-- Name: approval_requests uq_approval_requests__tenant_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.approval_requests
    ADD CONSTRAINT uq_approval_requests__tenant_id UNIQUE (tenant_id, id);


-- Name: approval_workflows uq_approval_workflows__request_type; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.approval_workflows
    ADD CONSTRAINT uq_approval_workflows__request_type UNIQUE NULLS NOT DISTINCT (tenant_id, company_id, request_type);


-- Name: approval_workflows uq_approval_workflows__tenant_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.approval_workflows
    ADD CONSTRAINT uq_approval_workflows__tenant_id UNIQUE (tenant_id, id);


-- Name: attendance_devices uq_attendance_devices__serial; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_devices
    ADD CONSTRAINT uq_attendance_devices__serial UNIQUE (tenant_id, serial);


-- Name: attendance_devices uq_attendance_devices__tenant_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_devices
    ADD CONSTRAINT uq_attendance_devices__tenant_id UNIQUE (tenant_id, id);


-- Name: auth_sessions uq_auth_sessions__tenant_id_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.auth_sessions
    ADD CONSTRAINT uq_auth_sessions__tenant_id_id UNIQUE NULLS NOT DISTINCT (tenant_id, id);


-- Name: auth_tokens uq_auth_tokens__tenant_id_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.auth_tokens
    ADD CONSTRAINT uq_auth_tokens__tenant_id_id UNIQUE NULLS NOT DISTINCT (tenant_id, id);


-- Name: auth_tokens uq_auth_tokens__token_hash; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.auth_tokens
    ADD CONSTRAINT uq_auth_tokens__token_hash UNIQUE (token_hash);


-- Name: background_jobs uq_background_jobs__idempotency_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.background_jobs
    ADD CONSTRAINT uq_background_jobs__idempotency_key UNIQUE NULLS NOT DISTINCT (tenant_id, idempotency_key);


-- Name: background_jobs uq_background_jobs__tenant_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.background_jobs
    ADD CONSTRAINT uq_background_jobs__tenant_id UNIQUE (tenant_id, id);


-- Name: branches uq_branches__tenant_id_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.branches
    ADD CONSTRAINT uq_branches__tenant_id_id UNIQUE (tenant_id, id);


-- Name: companies uq_companies__tenant_id_gosi_registration_no; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.companies
    ADD CONSTRAINT uq_companies__tenant_id_gosi_registration_no UNIQUE (tenant_id, gosi_registration_no);


-- Name: companies uq_companies__tenant_id_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.companies
    ADD CONSTRAINT uq_companies__tenant_id_id UNIQUE (tenant_id, id);


-- Name: company_pay_policies uq_company_pay_policies__tenant_id_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.company_pay_policies
    ADD CONSTRAINT uq_company_pay_policies__tenant_id_id UNIQUE (tenant_id, id);


-- Name: cost_centers uq_cost_centers__company_id_code; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.cost_centers
    ADD CONSTRAINT uq_cost_centers__company_id_code UNIQUE (tenant_id, company_id, code);


-- Name: cost_centers uq_cost_centers__tenant_id_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.cost_centers
    ADD CONSTRAINT uq_cost_centers__tenant_id_id UNIQUE (tenant_id, id);


-- Name: departments uq_departments__tenant_id_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.departments
    ADD CONSTRAINT uq_departments__tenant_id_id UNIQUE (tenant_id, id);


-- Name: designations uq_designations__tenant_id_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.designations
    ADD CONSTRAINT uq_designations__tenant_id_id UNIQUE (tenant_id, id);


-- Name: document_templates uq_document_templates__code_version; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.document_templates
    ADD CONSTRAINT uq_document_templates__code_version UNIQUE NULLS NOT DISTINCT (tenant_id, company_id, kind, code, version);


-- Name: document_templates uq_document_templates__tenant_id_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.document_templates
    ADD CONSTRAINT uq_document_templates__tenant_id_id UNIQUE (tenant_id, id);


-- Name: employee_assignments uq_employee_assignments__tenant_id_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.employee_assignments
    ADD CONSTRAINT uq_employee_assignments__tenant_id_id UNIQUE (tenant_id, id);


-- Name: employee_bank_accounts uq_employee_bank_accounts__tenant_id_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.employee_bank_accounts
    ADD CONSTRAINT uq_employee_bank_accounts__tenant_id_id UNIQUE (tenant_id, id);


-- Name: employee_contracts uq_employee_contracts__tenant_id_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.employee_contracts
    ADD CONSTRAINT uq_employee_contracts__tenant_id_id UNIQUE (tenant_id, id);


-- Name: employee_documents uq_employee_documents__tenant_id_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.employee_documents
    ADD CONSTRAINT uq_employee_documents__tenant_id_id UNIQUE (tenant_id, id);


-- Name: employee_gosi_registrations uq_employee_gosi_registrations__tenant_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.employee_gosi_registrations
    ADD CONSTRAINT uq_employee_gosi_registrations__tenant_id UNIQUE (tenant_id, id);


-- Name: employee_salaries uq_employee_salaries__tenant_id_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.employee_salaries
    ADD CONSTRAINT uq_employee_salaries__tenant_id_id UNIQUE (tenant_id, id);


-- Name: employees uq_employees__tenant_id_employee_number; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.employees
    ADD CONSTRAINT uq_employees__tenant_id_employee_number UNIQUE (tenant_id, employee_number);


-- Name: employees uq_employees__tenant_id_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.employees
    ADD CONSTRAINT uq_employees__tenant_id_id UNIQUE (tenant_id, id);


-- Name: eos_calculations uq_eos_calculations__tenant_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.eos_calculations
    ADD CONSTRAINT uq_eos_calculations__tenant_id UNIQUE (tenant_id, id);


-- Name: files uq_files__storage_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.files
    ADD CONSTRAINT uq_files__storage_key UNIQUE (storage_key);


-- Name: files uq_files__tenant_id_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.files
    ADD CONSTRAINT uq_files__tenant_id_id UNIQUE (tenant_id, id);


-- Name: final_settlement_lines uq_final_settlement_lines__tenant_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.final_settlement_lines
    ADD CONSTRAINT uq_final_settlement_lines__tenant_id UNIQUE (tenant_id, id);


-- Name: final_settlements uq_final_settlements__settlement_number; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.final_settlements
    ADD CONSTRAINT uq_final_settlements__settlement_number UNIQUE (tenant_id, settlement_number);


-- Name: final_settlements uq_final_settlements__tenant_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.final_settlements
    ADD CONSTRAINT uq_final_settlements__tenant_id UNIQUE (tenant_id, id);


-- Name: gl_journal_lines uq_gl_journal_lines__tenant_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.gl_journal_lines
    ADD CONSTRAINT uq_gl_journal_lines__tenant_id UNIQUE (tenant_id, id);


-- Name: gl_journals uq_gl_journals__idempotency_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.gl_journals
    ADD CONSTRAINT uq_gl_journals__idempotency_key UNIQUE (tenant_id, idempotency_key);


-- Name: gl_journals uq_gl_journals__source; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.gl_journals
    ADD CONSTRAINT uq_gl_journals__source UNIQUE NULLS NOT DISTINCT (tenant_id, source_type, source_id, reversal_of_id);


-- Name: gl_journals uq_gl_journals__tenant_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.gl_journals
    ADD CONSTRAINT uq_gl_journals__tenant_id UNIQUE (tenant_id, id);


-- Name: gl_mappings uq_gl_mappings__driver_scope; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.gl_mappings
    ADD CONSTRAINT uq_gl_mappings__driver_scope UNIQUE NULLS NOT DISTINCT (tenant_id, company_id, gl_driver, cost_center_id);


-- Name: gl_mappings uq_gl_mappings__tenant_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.gl_mappings
    ADD CONSTRAINT uq_gl_mappings__tenant_id UNIQUE (tenant_id, id);


-- Name: gl_period_closes uq_gl_period_closes__period; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.gl_period_closes
    ADD CONSTRAINT uq_gl_period_closes__period UNIQUE (tenant_id, company_id, year, month);


-- Name: gl_period_closes uq_gl_period_closes__tenant_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.gl_period_closes
    ADD CONSTRAINT uq_gl_period_closes__tenant_id UNIQUE (tenant_id, id);


-- Name: gosi_filings uq_gosi_filings__period; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.gosi_filings
    ADD CONSTRAINT uq_gosi_filings__period UNIQUE (tenant_id, company_id, gosi_registration_no, year, month, revision);


-- Name: gosi_filings uq_gosi_filings__tenant_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.gosi_filings
    ADD CONSTRAINT uq_gosi_filings__tenant_id UNIQUE (tenant_id, id);


-- Name: grades uq_grades__tenant_id_code; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.grades
    ADD CONSTRAINT uq_grades__tenant_id_code UNIQUE (tenant_id, code);


-- Name: grades uq_grades__tenant_id_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.grades
    ADD CONSTRAINT uq_grades__tenant_id_id UNIQUE (tenant_id, id);


-- Name: leave_ledger uq_leave_ledger__idempotency_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.leave_ledger
    ADD CONSTRAINT uq_leave_ledger__idempotency_key UNIQUE (tenant_id, idempotency_key);


-- Name: leave_ledger uq_leave_ledger__tenant_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.leave_ledger
    ADD CONSTRAINT uq_leave_ledger__tenant_id UNIQUE (tenant_id, id);


-- Name: leave_requests uq_leave_requests__tenant_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.leave_requests
    ADD CONSTRAINT uq_leave_requests__tenant_id UNIQUE (tenant_id, id);


-- Name: leave_types uq_leave_types__code; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.leave_types
    ADD CONSTRAINT uq_leave_types__code UNIQUE (tenant_id, code);


-- Name: leave_types uq_leave_types__tenant_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.leave_types
    ADD CONSTRAINT uq_leave_types__tenant_id UNIQUE (tenant_id, id);


-- Name: loan_installments uq_loan_installments__loan_id_number; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.loan_installments
    ADD CONSTRAINT uq_loan_installments__loan_id_number UNIQUE (tenant_id, loan_id, installment_number);


-- Name: loan_installments uq_loan_installments__tenant_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.loan_installments
    ADD CONSTRAINT uq_loan_installments__tenant_id UNIQUE (tenant_id, id);


-- Name: loans uq_loans__tenant_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.loans
    ADD CONSTRAINT uq_loans__tenant_id UNIQUE (tenant_id, id);


-- Name: nitaqat_grid uq_nitaqat_grid__band; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.nitaqat_grid
    ADD CONSTRAINT uq_nitaqat_grid__band UNIQUE (activity_code, size_tier, grid_version, band, effective_from);


-- Name: nitaqat_snapshots uq_nitaqat_snapshots__as_of; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.nitaqat_snapshots
    ADD CONSTRAINT uq_nitaqat_snapshots__as_of UNIQUE (tenant_id, company_id, as_of_date);


-- Name: nitaqat_snapshots uq_nitaqat_snapshots__tenant_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.nitaqat_snapshots
    ADD CONSTRAINT uq_nitaqat_snapshots__tenant_id UNIQUE (tenant_id, id);


-- Name: notification_deliveries uq_notification_deliveries__tenant_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.notification_deliveries
    ADD CONSTRAINT uq_notification_deliveries__tenant_id UNIQUE (tenant_id, id);


-- Name: notifications uq_notifications__idempotency_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.notifications
    ADD CONSTRAINT uq_notifications__idempotency_key UNIQUE (tenant_id, idempotency_key);


-- Name: notifications uq_notifications__tenant_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.notifications
    ADD CONSTRAINT uq_notifications__tenant_id UNIQUE (tenant_id, id);


-- Name: number_sequences uq_number_sequences__scope; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.number_sequences
    ADD CONSTRAINT uq_number_sequences__scope UNIQUE NULLS NOT DISTINCT (tenant_id, company_id, scope_key, period_key);


-- Name: number_sequences uq_number_sequences__tenant_id_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.number_sequences
    ADD CONSTRAINT uq_number_sequences__tenant_id_id UNIQUE (tenant_id, id);


-- Name: overtime_requests uq_overtime_requests__tenant_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.overtime_requests
    ADD CONSTRAINT uq_overtime_requests__tenant_id UNIQUE (tenant_id, id);


-- Name: pay_components uq_pay_components__tenant_id_code; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.pay_components
    ADD CONSTRAINT uq_pay_components__tenant_id_code UNIQUE (tenant_id, code);


-- Name: pay_components uq_pay_components__tenant_id_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.pay_components
    ADD CONSTRAINT uq_pay_components__tenant_id_id UNIQUE (tenant_id, id);


-- Name: payroll_audit_logs uq_payroll_audit_logs__entry_hash; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.payroll_audit_logs
    ADD CONSTRAINT uq_payroll_audit_logs__entry_hash UNIQUE (tenant_id, entry_hash);


-- Name: payroll_audit_logs uq_payroll_audit_logs__seq; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.payroll_audit_logs
    ADD CONSTRAINT uq_payroll_audit_logs__seq UNIQUE (tenant_id, seq);


-- Name: payroll_audit_logs uq_payroll_audit_logs__tenant_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.payroll_audit_logs
    ADD CONSTRAINT uq_payroll_audit_logs__tenant_id UNIQUE (tenant_id, id);


-- Name: payroll_inputs uq_payroll_inputs__source_period_revision; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.payroll_inputs
    ADD CONSTRAINT uq_payroll_inputs__source_period_revision UNIQUE NULLS NOT DISTINCT (tenant_id, source_type, source_id, covered_year, covered_month, pay_component_code, revision);


-- Name: payroll_inputs uq_payroll_inputs__tenant_id_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.payroll_inputs
    ADD CONSTRAINT uq_payroll_inputs__tenant_id_id UNIQUE (tenant_id, id);


-- Name: payroll_issues uq_payroll_issues__tenant_id_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.payroll_issues
    ADD CONSTRAINT uq_payroll_issues__tenant_id_id UNIQUE (tenant_id, id);


-- Name: payroll_runs uq_payroll_runs__idempotency_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.payroll_runs
    ADD CONSTRAINT uq_payroll_runs__idempotency_key UNIQUE (tenant_id, idempotency_key);


-- Name: payroll_runs uq_payroll_runs__tenant_id_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.payroll_runs
    ADD CONSTRAINT uq_payroll_runs__tenant_id_id UNIQUE (tenant_id, id);


-- Name: payroll_slip_lines uq_payroll_slip_lines__tenant_id_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.payroll_slip_lines
    ADD CONSTRAINT uq_payroll_slip_lines__tenant_id_id UNIQUE (tenant_id, id);


-- Name: payroll_slips uq_payroll_slips__run_id_employee_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.payroll_slips
    ADD CONSTRAINT uq_payroll_slips__run_id_employee_id UNIQUE (tenant_id, run_id, employee_id);


-- Name: payroll_slips uq_payroll_slips__tenant_id_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.payroll_slips
    ADD CONSTRAINT uq_payroll_slips__tenant_id_id UNIQUE (tenant_id, id);


-- Name: permission_grantor_records uq_permission_grantor_records__tenant_id_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.permission_grantor_records
    ADD CONSTRAINT uq_permission_grantor_records__tenant_id_id UNIQUE (tenant_id, id);


-- Name: permissions uq_permissions__code; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.permissions
    ADD CONSTRAINT uq_permissions__code UNIQUE (code);


-- Name: platform_users uq_platform_users__email; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.platform_users
    ADD CONSTRAINT uq_platform_users__email UNIQUE (email);


-- Name: public_holidays uq_public_holidays__calendar_date; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.public_holidays
    ADD CONSTRAINT uq_public_holidays__calendar_date UNIQUE NULLS NOT DISTINCT (tenant_id, calendar_code, holiday_date);


-- Name: public_holidays uq_public_holidays__tenant_id_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.public_holidays
    ADD CONSTRAINT uq_public_holidays__tenant_id_id UNIQUE NULLS NOT DISTINCT (tenant_id, id);


-- Name: retention_policies uq_retention_policies__tenant_id_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.retention_policies
    ADD CONSTRAINT uq_retention_policies__tenant_id_id UNIQUE NULLS NOT DISTINCT (tenant_id, id);


-- Name: retention_purge_audits uq_retention_purge_audits__tenant_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.retention_purge_audits
    ADD CONSTRAINT uq_retention_purge_audits__tenant_id UNIQUE (tenant_id, id);


-- Name: role_permissions uq_role_permissions__role_id_permission_code; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.role_permissions
    ADD CONSTRAINT uq_role_permissions__role_id_permission_code UNIQUE (tenant_id, role_id, permission_code);


-- Name: role_permissions uq_role_permissions__tenant_id_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.role_permissions
    ADD CONSTRAINT uq_role_permissions__tenant_id_id UNIQUE (tenant_id, id);


-- Name: roles uq_roles__tenant_id_code; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.roles
    ADD CONSTRAINT uq_roles__tenant_id_code UNIQUE (tenant_id, code);


-- Name: roles uq_roles__tenant_id_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.roles
    ADD CONSTRAINT uq_roles__tenant_id_id UNIQUE (tenant_id, id);


-- Name: shift_assignments uq_shift_assignments__tenant_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.shift_assignments
    ADD CONSTRAINT uq_shift_assignments__tenant_id UNIQUE (tenant_id, id);


-- Name: shifts uq_shifts__code; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.shifts
    ADD CONSTRAINT uq_shifts__code UNIQUE (tenant_id, code);


-- Name: shifts uq_shifts__tenant_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.shifts
    ADD CONSTRAINT uq_shifts__tenant_id UNIQUE (tenant_id, id);


-- Name: statutory_rule_bands uq_statutory_rule_bands__rule_band_key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.statutory_rule_bands
    ADD CONSTRAINT uq_statutory_rule_bands__rule_band_key UNIQUE (statutory_rule_id, band_key);


-- Name: statutory_rules uq_statutory_rules__key; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.statutory_rules
    ADD CONSTRAINT uq_statutory_rules__key UNIQUE NULLS NOT DISTINCT (country_code, family, rule_key, nationality_class, cohort, gosi_branch, payer, effective_from);


-- Name: tenant_settings uq_tenant_settings__tenant_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.tenant_settings
    ADD CONSTRAINT uq_tenant_settings__tenant_id UNIQUE (tenant_id);


-- Name: tenant_settings uq_tenant_settings__tenant_id_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.tenant_settings
    ADD CONSTRAINT uq_tenant_settings__tenant_id_id UNIQUE (tenant_id, id);


-- Name: tenants uq_tenants__slug; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.tenants
    ADD CONSTRAINT uq_tenants__slug UNIQUE (slug);


-- Name: timesheet_day_reconciliations uq_timesheet_day_reconciliations__day; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_day_reconciliations
    ADD CONSTRAINT uq_timesheet_day_reconciliations__day UNIQUE (tenant_id, timesheet_id, work_date);


-- Name: timesheet_day_reconciliations uq_timesheet_day_reconciliations__tenant_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_day_reconciliations
    ADD CONSTRAINT uq_timesheet_day_reconciliations__tenant_id UNIQUE (tenant_id, id);


-- Name: timesheets uq_timesheets__employee_period; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheets
    ADD CONSTRAINT uq_timesheets__employee_period UNIQUE (tenant_id, employee_id, period_start);


-- Name: timesheets uq_timesheets__tenant_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheets
    ADD CONSTRAINT uq_timesheets__tenant_id UNIQUE (tenant_id, id);


-- Name: timesheets uq_timesheets__timesheet_number; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheets
    ADD CONSTRAINT uq_timesheets__timesheet_number UNIQUE (tenant_id, timesheet_number);


-- Name: user_roles uq_user_roles__tenant_id_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.user_roles
    ADD CONSTRAINT uq_user_roles__tenant_id_id UNIQUE (tenant_id, id);


-- Name: users uq_users__tenant_id_employee_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.users
    ADD CONSTRAINT uq_users__tenant_id_employee_id UNIQUE (tenant_id, employee_id);


-- Name: users uq_users__tenant_id_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.users
    ADD CONSTRAINT uq_users__tenant_id_id UNIQUE (tenant_id, id);


-- Name: users uq_users__tenant_id_normalized_email; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.users
    ADD CONSTRAINT uq_users__tenant_id_normalized_email UNIQUE (tenant_id, normalized_email);


-- Name: wps_batches uq_wps_batches__batch_number; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.wps_batches
    ADD CONSTRAINT uq_wps_batches__batch_number UNIQUE (tenant_id, batch_number);


-- Name: wps_batches uq_wps_batches__tenant_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.wps_batches
    ADD CONSTRAINT uq_wps_batches__tenant_id UNIQUE (tenant_id, id);


-- Name: wps_lines uq_wps_lines__batch_id_slip_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.wps_lines
    ADD CONSTRAINT uq_wps_lines__batch_id_slip_id UNIQUE (tenant_id, batch_id, slip_id);


-- Name: wps_lines uq_wps_lines__tenant_id; Type: CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.wps_lines
    ADD CONSTRAINT uq_wps_lines__tenant_id UNIQUE (tenant_id, id);


-- Name: ix_attendance_days__locked_run_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_attendance_days__locked_run_id ON ONLY public.attendance_days USING btree (tenant_id, locked_run_id);


-- Name: INDEX ix_attendance_days__locked_run_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_attendance_days__locked_run_id IS '§19.4 rule 2: FK cover for attendance_days(tenant_id, locked_run_id) -> payroll_runs, ON DELETE SET NULL.';


-- Name: attendance_days_default_tenant_id_locked_run_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_days_default_tenant_id_locked_run_id_idx ON public.attendance_days_default USING btree (tenant_id, locked_run_id);


-- Name: ix_attendance_days__shift_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_attendance_days__shift_id ON ONLY public.attendance_days USING btree (tenant_id, shift_id);


-- Name: INDEX ix_attendance_days__shift_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_attendance_days__shift_id IS '§19.4 rule 2: FK cover for attendance_days(tenant_id, shift_id) -> shifts, ON DELETE SET NULL.';


-- Name: attendance_days_default_tenant_id_shift_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_days_default_tenant_id_shift_id_idx ON public.attendance_days_default USING btree (tenant_id, shift_id);


-- Name: ix_attendance_days__tenant_work_date_status; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_attendance_days__tenant_work_date_status ON ONLY public.attendance_days USING btree (tenant_id, work_date, status);


-- Name: INDEX ix_attendance_days__tenant_work_date_status; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_attendance_days__tenant_work_date_status IS 'H2 dashboard summary (DashboardController.cs:250): today''s present/absent/leave counts for a tenant.';


-- Name: attendance_days_default_tenant_id_work_date_status_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_days_default_tenant_id_work_date_status_idx ON public.attendance_days_default USING btree (tenant_id, work_date, status);


-- Name: attendance_days_y2025m10_tenant_id_locked_run_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_days_y2025m10_tenant_id_locked_run_id_idx ON public.attendance_days_y2025m10 USING btree (tenant_id, locked_run_id);


-- Name: attendance_days_y2025m10_tenant_id_shift_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_days_y2025m10_tenant_id_shift_id_idx ON public.attendance_days_y2025m10 USING btree (tenant_id, shift_id);


-- Name: attendance_days_y2025m10_tenant_id_work_date_status_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_days_y2025m10_tenant_id_work_date_status_idx ON public.attendance_days_y2025m10 USING btree (tenant_id, work_date, status);


-- Name: attendance_days_y2025m11_tenant_id_locked_run_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_days_y2025m11_tenant_id_locked_run_id_idx ON public.attendance_days_y2025m11 USING btree (tenant_id, locked_run_id);


-- Name: attendance_days_y2025m11_tenant_id_shift_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_days_y2025m11_tenant_id_shift_id_idx ON public.attendance_days_y2025m11 USING btree (tenant_id, shift_id);


-- Name: attendance_days_y2025m11_tenant_id_work_date_status_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_days_y2025m11_tenant_id_work_date_status_idx ON public.attendance_days_y2025m11 USING btree (tenant_id, work_date, status);


-- Name: attendance_days_y2025m12_tenant_id_locked_run_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_days_y2025m12_tenant_id_locked_run_id_idx ON public.attendance_days_y2025m12 USING btree (tenant_id, locked_run_id);


-- Name: attendance_days_y2025m12_tenant_id_shift_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_days_y2025m12_tenant_id_shift_id_idx ON public.attendance_days_y2025m12 USING btree (tenant_id, shift_id);


-- Name: attendance_days_y2025m12_tenant_id_work_date_status_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_days_y2025m12_tenant_id_work_date_status_idx ON public.attendance_days_y2025m12 USING btree (tenant_id, work_date, status);


-- Name: attendance_days_y2026m01_tenant_id_locked_run_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_days_y2026m01_tenant_id_locked_run_id_idx ON public.attendance_days_y2026m01 USING btree (tenant_id, locked_run_id);


-- Name: attendance_days_y2026m01_tenant_id_shift_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_days_y2026m01_tenant_id_shift_id_idx ON public.attendance_days_y2026m01 USING btree (tenant_id, shift_id);


-- Name: attendance_days_y2026m01_tenant_id_work_date_status_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_days_y2026m01_tenant_id_work_date_status_idx ON public.attendance_days_y2026m01 USING btree (tenant_id, work_date, status);


-- Name: attendance_days_y2026m02_tenant_id_locked_run_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_days_y2026m02_tenant_id_locked_run_id_idx ON public.attendance_days_y2026m02 USING btree (tenant_id, locked_run_id);


-- Name: attendance_days_y2026m02_tenant_id_shift_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_days_y2026m02_tenant_id_shift_id_idx ON public.attendance_days_y2026m02 USING btree (tenant_id, shift_id);


-- Name: attendance_days_y2026m02_tenant_id_work_date_status_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_days_y2026m02_tenant_id_work_date_status_idx ON public.attendance_days_y2026m02 USING btree (tenant_id, work_date, status);


-- Name: attendance_days_y2026m03_tenant_id_locked_run_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_days_y2026m03_tenant_id_locked_run_id_idx ON public.attendance_days_y2026m03 USING btree (tenant_id, locked_run_id);


-- Name: attendance_days_y2026m03_tenant_id_shift_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_days_y2026m03_tenant_id_shift_id_idx ON public.attendance_days_y2026m03 USING btree (tenant_id, shift_id);


-- Name: attendance_days_y2026m03_tenant_id_work_date_status_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_days_y2026m03_tenant_id_work_date_status_idx ON public.attendance_days_y2026m03 USING btree (tenant_id, work_date, status);


-- Name: attendance_days_y2026m04_tenant_id_locked_run_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_days_y2026m04_tenant_id_locked_run_id_idx ON public.attendance_days_y2026m04 USING btree (tenant_id, locked_run_id);


-- Name: attendance_days_y2026m04_tenant_id_shift_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_days_y2026m04_tenant_id_shift_id_idx ON public.attendance_days_y2026m04 USING btree (tenant_id, shift_id);


-- Name: attendance_days_y2026m04_tenant_id_work_date_status_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_days_y2026m04_tenant_id_work_date_status_idx ON public.attendance_days_y2026m04 USING btree (tenant_id, work_date, status);


-- Name: attendance_days_y2026m05_tenant_id_locked_run_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_days_y2026m05_tenant_id_locked_run_id_idx ON public.attendance_days_y2026m05 USING btree (tenant_id, locked_run_id);


-- Name: attendance_days_y2026m05_tenant_id_shift_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_days_y2026m05_tenant_id_shift_id_idx ON public.attendance_days_y2026m05 USING btree (tenant_id, shift_id);


-- Name: attendance_days_y2026m05_tenant_id_work_date_status_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_days_y2026m05_tenant_id_work_date_status_idx ON public.attendance_days_y2026m05 USING btree (tenant_id, work_date, status);


-- Name: attendance_days_y2026m06_tenant_id_locked_run_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_days_y2026m06_tenant_id_locked_run_id_idx ON public.attendance_days_y2026m06 USING btree (tenant_id, locked_run_id);


-- Name: attendance_days_y2026m06_tenant_id_shift_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_days_y2026m06_tenant_id_shift_id_idx ON public.attendance_days_y2026m06 USING btree (tenant_id, shift_id);


-- Name: attendance_days_y2026m06_tenant_id_work_date_status_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_days_y2026m06_tenant_id_work_date_status_idx ON public.attendance_days_y2026m06 USING btree (tenant_id, work_date, status);


-- Name: attendance_days_y2026m07_tenant_id_locked_run_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_days_y2026m07_tenant_id_locked_run_id_idx ON public.attendance_days_y2026m07 USING btree (tenant_id, locked_run_id);


-- Name: attendance_days_y2026m07_tenant_id_shift_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_days_y2026m07_tenant_id_shift_id_idx ON public.attendance_days_y2026m07 USING btree (tenant_id, shift_id);


-- Name: attendance_days_y2026m07_tenant_id_work_date_status_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_days_y2026m07_tenant_id_work_date_status_idx ON public.attendance_days_y2026m07 USING btree (tenant_id, work_date, status);


-- Name: attendance_days_y2026m08_tenant_id_locked_run_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_days_y2026m08_tenant_id_locked_run_id_idx ON public.attendance_days_y2026m08 USING btree (tenant_id, locked_run_id);


-- Name: attendance_days_y2026m08_tenant_id_shift_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_days_y2026m08_tenant_id_shift_id_idx ON public.attendance_days_y2026m08 USING btree (tenant_id, shift_id);


-- Name: attendance_days_y2026m08_tenant_id_work_date_status_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_days_y2026m08_tenant_id_work_date_status_idx ON public.attendance_days_y2026m08 USING btree (tenant_id, work_date, status);


-- Name: attendance_days_y2026m09_tenant_id_locked_run_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_days_y2026m09_tenant_id_locked_run_id_idx ON public.attendance_days_y2026m09 USING btree (tenant_id, locked_run_id);


-- Name: attendance_days_y2026m09_tenant_id_shift_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_days_y2026m09_tenant_id_shift_id_idx ON public.attendance_days_y2026m09 USING btree (tenant_id, shift_id);


-- Name: attendance_days_y2026m09_tenant_id_work_date_status_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_days_y2026m09_tenant_id_work_date_status_idx ON public.attendance_days_y2026m09 USING btree (tenant_id, work_date, status);


-- Name: attendance_days_y2026m10_tenant_id_locked_run_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_days_y2026m10_tenant_id_locked_run_id_idx ON public.attendance_days_y2026m10 USING btree (tenant_id, locked_run_id);


-- Name: attendance_days_y2026m10_tenant_id_shift_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_days_y2026m10_tenant_id_shift_id_idx ON public.attendance_days_y2026m10 USING btree (tenant_id, shift_id);


-- Name: attendance_days_y2026m10_tenant_id_work_date_status_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_days_y2026m10_tenant_id_work_date_status_idx ON public.attendance_days_y2026m10 USING btree (tenant_id, work_date, status);


-- Name: attendance_days_y2026m11_tenant_id_locked_run_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_days_y2026m11_tenant_id_locked_run_id_idx ON public.attendance_days_y2026m11 USING btree (tenant_id, locked_run_id);


-- Name: attendance_days_y2026m11_tenant_id_shift_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_days_y2026m11_tenant_id_shift_id_idx ON public.attendance_days_y2026m11 USING btree (tenant_id, shift_id);


-- Name: attendance_days_y2026m11_tenant_id_work_date_status_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_days_y2026m11_tenant_id_work_date_status_idx ON public.attendance_days_y2026m11 USING btree (tenant_id, work_date, status);


-- Name: attendance_days_y2026m12_tenant_id_locked_run_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_days_y2026m12_tenant_id_locked_run_id_idx ON public.attendance_days_y2026m12 USING btree (tenant_id, locked_run_id);


-- Name: attendance_days_y2026m12_tenant_id_shift_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_days_y2026m12_tenant_id_shift_id_idx ON public.attendance_days_y2026m12 USING btree (tenant_id, shift_id);


-- Name: attendance_days_y2026m12_tenant_id_work_date_status_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_days_y2026m12_tenant_id_work_date_status_idx ON public.attendance_days_y2026m12 USING btree (tenant_id, work_date, status);


-- Name: ix_attendance_punches__approval_request_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_attendance_punches__approval_request_id ON ONLY public.attendance_punches USING btree (tenant_id, approval_request_id);


-- Name: INDEX ix_attendance_punches__approval_request_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_attendance_punches__approval_request_id IS '§19.4 rule 2: FK cover for attendance_punches(tenant_id, approval_request_id) -> approval_requests, ON DELETE RESTRICT.';


-- Name: attendance_punches_default_tenant_id_approval_request_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_punches_default_tenant_id_approval_request_id_idx ON public.attendance_punches_default USING btree (tenant_id, approval_request_id);


-- Name: ix_attendance_punches__employee_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_attendance_punches__employee_id ON ONLY public.attendance_punches USING btree (tenant_id, employee_id);


-- Name: INDEX ix_attendance_punches__employee_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_attendance_punches__employee_id IS '§19.4 rule 2: FK cover for attendance_punches(tenant_id, employee_id) -> employees, ON DELETE RESTRICT.';


-- Name: attendance_punches_default_tenant_id_employee_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_punches_default_tenant_id_employee_id_idx ON public.attendance_punches_default USING btree (tenant_id, employee_id);


-- Name: ix_attendance_punches__employee_occurred_at; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_attendance_punches__employee_occurred_at ON ONLY public.attendance_punches USING btree (tenant_id, employee_id, occurred_at);


-- Name: INDEX ix_attendance_punches__employee_occurred_at; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_attendance_punches__employee_occurred_at IS 'H8 attendance sweep: a day''s punches per employee. Propagates per partition once 030 range-partitions attendance_punches on occurred_at. Also the FK cover for attendance_punches.employee_id.';


-- Name: attendance_punches_default_tenant_id_employee_id_occurred_a_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_punches_default_tenant_id_employee_id_occurred_a_idx ON public.attendance_punches_default USING btree (tenant_id, employee_id, occurred_at);


-- Name: attendance_punches_y2025m10_tenant_id_approval_request_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_punches_y2025m10_tenant_id_approval_request_id_idx ON public.attendance_punches_y2025m10 USING btree (tenant_id, approval_request_id);


-- Name: attendance_punches_y2025m10_tenant_id_employee_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_punches_y2025m10_tenant_id_employee_id_idx ON public.attendance_punches_y2025m10 USING btree (tenant_id, employee_id);


-- Name: attendance_punches_y2025m10_tenant_id_employee_id_occurred__idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_punches_y2025m10_tenant_id_employee_id_occurred__idx ON public.attendance_punches_y2025m10 USING btree (tenant_id, employee_id, occurred_at);


-- Name: attendance_punches_y2025m11_tenant_id_approval_request_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_punches_y2025m11_tenant_id_approval_request_id_idx ON public.attendance_punches_y2025m11 USING btree (tenant_id, approval_request_id);


-- Name: attendance_punches_y2025m11_tenant_id_employee_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_punches_y2025m11_tenant_id_employee_id_idx ON public.attendance_punches_y2025m11 USING btree (tenant_id, employee_id);


-- Name: attendance_punches_y2025m11_tenant_id_employee_id_occurred__idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_punches_y2025m11_tenant_id_employee_id_occurred__idx ON public.attendance_punches_y2025m11 USING btree (tenant_id, employee_id, occurred_at);


-- Name: attendance_punches_y2025m12_tenant_id_approval_request_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_punches_y2025m12_tenant_id_approval_request_id_idx ON public.attendance_punches_y2025m12 USING btree (tenant_id, approval_request_id);


-- Name: attendance_punches_y2025m12_tenant_id_employee_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_punches_y2025m12_tenant_id_employee_id_idx ON public.attendance_punches_y2025m12 USING btree (tenant_id, employee_id);


-- Name: attendance_punches_y2025m12_tenant_id_employee_id_occurred__idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_punches_y2025m12_tenant_id_employee_id_occurred__idx ON public.attendance_punches_y2025m12 USING btree (tenant_id, employee_id, occurred_at);


-- Name: attendance_punches_y2026m01_tenant_id_approval_request_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_punches_y2026m01_tenant_id_approval_request_id_idx ON public.attendance_punches_y2026m01 USING btree (tenant_id, approval_request_id);


-- Name: attendance_punches_y2026m01_tenant_id_employee_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_punches_y2026m01_tenant_id_employee_id_idx ON public.attendance_punches_y2026m01 USING btree (tenant_id, employee_id);


-- Name: attendance_punches_y2026m01_tenant_id_employee_id_occurred__idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_punches_y2026m01_tenant_id_employee_id_occurred__idx ON public.attendance_punches_y2026m01 USING btree (tenant_id, employee_id, occurred_at);


-- Name: attendance_punches_y2026m02_tenant_id_approval_request_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_punches_y2026m02_tenant_id_approval_request_id_idx ON public.attendance_punches_y2026m02 USING btree (tenant_id, approval_request_id);


-- Name: attendance_punches_y2026m02_tenant_id_employee_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_punches_y2026m02_tenant_id_employee_id_idx ON public.attendance_punches_y2026m02 USING btree (tenant_id, employee_id);


-- Name: attendance_punches_y2026m02_tenant_id_employee_id_occurred__idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_punches_y2026m02_tenant_id_employee_id_occurred__idx ON public.attendance_punches_y2026m02 USING btree (tenant_id, employee_id, occurred_at);


-- Name: attendance_punches_y2026m03_tenant_id_approval_request_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_punches_y2026m03_tenant_id_approval_request_id_idx ON public.attendance_punches_y2026m03 USING btree (tenant_id, approval_request_id);


-- Name: attendance_punches_y2026m03_tenant_id_employee_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_punches_y2026m03_tenant_id_employee_id_idx ON public.attendance_punches_y2026m03 USING btree (tenant_id, employee_id);


-- Name: attendance_punches_y2026m03_tenant_id_employee_id_occurred__idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_punches_y2026m03_tenant_id_employee_id_occurred__idx ON public.attendance_punches_y2026m03 USING btree (tenant_id, employee_id, occurred_at);


-- Name: attendance_punches_y2026m04_tenant_id_approval_request_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_punches_y2026m04_tenant_id_approval_request_id_idx ON public.attendance_punches_y2026m04 USING btree (tenant_id, approval_request_id);


-- Name: attendance_punches_y2026m04_tenant_id_employee_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_punches_y2026m04_tenant_id_employee_id_idx ON public.attendance_punches_y2026m04 USING btree (tenant_id, employee_id);


-- Name: attendance_punches_y2026m04_tenant_id_employee_id_occurred__idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_punches_y2026m04_tenant_id_employee_id_occurred__idx ON public.attendance_punches_y2026m04 USING btree (tenant_id, employee_id, occurred_at);


-- Name: attendance_punches_y2026m05_tenant_id_approval_request_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_punches_y2026m05_tenant_id_approval_request_id_idx ON public.attendance_punches_y2026m05 USING btree (tenant_id, approval_request_id);


-- Name: attendance_punches_y2026m05_tenant_id_employee_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_punches_y2026m05_tenant_id_employee_id_idx ON public.attendance_punches_y2026m05 USING btree (tenant_id, employee_id);


-- Name: attendance_punches_y2026m05_tenant_id_employee_id_occurred__idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_punches_y2026m05_tenant_id_employee_id_occurred__idx ON public.attendance_punches_y2026m05 USING btree (tenant_id, employee_id, occurred_at);


-- Name: attendance_punches_y2026m06_tenant_id_approval_request_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_punches_y2026m06_tenant_id_approval_request_id_idx ON public.attendance_punches_y2026m06 USING btree (tenant_id, approval_request_id);


-- Name: attendance_punches_y2026m06_tenant_id_employee_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_punches_y2026m06_tenant_id_employee_id_idx ON public.attendance_punches_y2026m06 USING btree (tenant_id, employee_id);


-- Name: attendance_punches_y2026m06_tenant_id_employee_id_occurred__idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_punches_y2026m06_tenant_id_employee_id_occurred__idx ON public.attendance_punches_y2026m06 USING btree (tenant_id, employee_id, occurred_at);


-- Name: attendance_punches_y2026m07_tenant_id_approval_request_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_punches_y2026m07_tenant_id_approval_request_id_idx ON public.attendance_punches_y2026m07 USING btree (tenant_id, approval_request_id);


-- Name: attendance_punches_y2026m07_tenant_id_employee_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_punches_y2026m07_tenant_id_employee_id_idx ON public.attendance_punches_y2026m07 USING btree (tenant_id, employee_id);


-- Name: attendance_punches_y2026m07_tenant_id_employee_id_occurred__idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_punches_y2026m07_tenant_id_employee_id_occurred__idx ON public.attendance_punches_y2026m07 USING btree (tenant_id, employee_id, occurred_at);


-- Name: attendance_punches_y2026m08_tenant_id_approval_request_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_punches_y2026m08_tenant_id_approval_request_id_idx ON public.attendance_punches_y2026m08 USING btree (tenant_id, approval_request_id);


-- Name: attendance_punches_y2026m08_tenant_id_employee_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_punches_y2026m08_tenant_id_employee_id_idx ON public.attendance_punches_y2026m08 USING btree (tenant_id, employee_id);


-- Name: attendance_punches_y2026m08_tenant_id_employee_id_occurred__idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_punches_y2026m08_tenant_id_employee_id_occurred__idx ON public.attendance_punches_y2026m08 USING btree (tenant_id, employee_id, occurred_at);


-- Name: attendance_punches_y2026m09_tenant_id_approval_request_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_punches_y2026m09_tenant_id_approval_request_id_idx ON public.attendance_punches_y2026m09 USING btree (tenant_id, approval_request_id);


-- Name: attendance_punches_y2026m09_tenant_id_employee_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_punches_y2026m09_tenant_id_employee_id_idx ON public.attendance_punches_y2026m09 USING btree (tenant_id, employee_id);


-- Name: attendance_punches_y2026m09_tenant_id_employee_id_occurred__idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_punches_y2026m09_tenant_id_employee_id_occurred__idx ON public.attendance_punches_y2026m09 USING btree (tenant_id, employee_id, occurred_at);


-- Name: attendance_punches_y2026m10_tenant_id_approval_request_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_punches_y2026m10_tenant_id_approval_request_id_idx ON public.attendance_punches_y2026m10 USING btree (tenant_id, approval_request_id);


-- Name: attendance_punches_y2026m10_tenant_id_employee_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_punches_y2026m10_tenant_id_employee_id_idx ON public.attendance_punches_y2026m10 USING btree (tenant_id, employee_id);


-- Name: attendance_punches_y2026m10_tenant_id_employee_id_occurred__idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_punches_y2026m10_tenant_id_employee_id_occurred__idx ON public.attendance_punches_y2026m10 USING btree (tenant_id, employee_id, occurred_at);


-- Name: attendance_punches_y2026m11_tenant_id_approval_request_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_punches_y2026m11_tenant_id_approval_request_id_idx ON public.attendance_punches_y2026m11 USING btree (tenant_id, approval_request_id);


-- Name: attendance_punches_y2026m11_tenant_id_employee_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_punches_y2026m11_tenant_id_employee_id_idx ON public.attendance_punches_y2026m11 USING btree (tenant_id, employee_id);


-- Name: attendance_punches_y2026m11_tenant_id_employee_id_occurred__idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_punches_y2026m11_tenant_id_employee_id_occurred__idx ON public.attendance_punches_y2026m11 USING btree (tenant_id, employee_id, occurred_at);


-- Name: attendance_punches_y2026m12_tenant_id_approval_request_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_punches_y2026m12_tenant_id_approval_request_id_idx ON public.attendance_punches_y2026m12 USING btree (tenant_id, approval_request_id);


-- Name: attendance_punches_y2026m12_tenant_id_employee_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_punches_y2026m12_tenant_id_employee_id_idx ON public.attendance_punches_y2026m12 USING btree (tenant_id, employee_id);


-- Name: attendance_punches_y2026m12_tenant_id_employee_id_occurred__idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX attendance_punches_y2026m12_tenant_id_employee_id_occurred__idx ON public.attendance_punches_y2026m12 USING btree (tenant_id, employee_id, occurred_at);


-- Name: ix_audit_logs__correlation_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_audit_logs__correlation_id ON ONLY public.audit_logs USING btree (tenant_id, correlation_id);


-- Name: INDEX ix_audit_logs__correlation_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_audit_logs__correlation_id IS '§14 audit: every row written by one request or job. The (chain_key, seq) verification order §19.4 also names is already uq_audit_logs__chain_seq, which leads with exactly those two columns.';


-- Name: audit_logs_default_tenant_id_correlation_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX audit_logs_default_tenant_id_correlation_id_idx ON public.audit_logs_default USING btree (tenant_id, correlation_id);


-- Name: audit_logs_y2025m10_tenant_id_correlation_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX audit_logs_y2025m10_tenant_id_correlation_id_idx ON public.audit_logs_y2025m10 USING btree (tenant_id, correlation_id);


-- Name: audit_logs_y2025m11_tenant_id_correlation_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX audit_logs_y2025m11_tenant_id_correlation_id_idx ON public.audit_logs_y2025m11 USING btree (tenant_id, correlation_id);


-- Name: audit_logs_y2025m12_tenant_id_correlation_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX audit_logs_y2025m12_tenant_id_correlation_id_idx ON public.audit_logs_y2025m12 USING btree (tenant_id, correlation_id);


-- Name: audit_logs_y2026m01_tenant_id_correlation_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX audit_logs_y2026m01_tenant_id_correlation_id_idx ON public.audit_logs_y2026m01 USING btree (tenant_id, correlation_id);


-- Name: audit_logs_y2026m02_tenant_id_correlation_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX audit_logs_y2026m02_tenant_id_correlation_id_idx ON public.audit_logs_y2026m02 USING btree (tenant_id, correlation_id);


-- Name: audit_logs_y2026m03_tenant_id_correlation_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX audit_logs_y2026m03_tenant_id_correlation_id_idx ON public.audit_logs_y2026m03 USING btree (tenant_id, correlation_id);


-- Name: audit_logs_y2026m04_tenant_id_correlation_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX audit_logs_y2026m04_tenant_id_correlation_id_idx ON public.audit_logs_y2026m04 USING btree (tenant_id, correlation_id);


-- Name: audit_logs_y2026m05_tenant_id_correlation_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX audit_logs_y2026m05_tenant_id_correlation_id_idx ON public.audit_logs_y2026m05 USING btree (tenant_id, correlation_id);


-- Name: audit_logs_y2026m06_tenant_id_correlation_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX audit_logs_y2026m06_tenant_id_correlation_id_idx ON public.audit_logs_y2026m06 USING btree (tenant_id, correlation_id);


-- Name: audit_logs_y2026m07_tenant_id_correlation_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX audit_logs_y2026m07_tenant_id_correlation_id_idx ON public.audit_logs_y2026m07 USING btree (tenant_id, correlation_id);


-- Name: audit_logs_y2026m08_tenant_id_correlation_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX audit_logs_y2026m08_tenant_id_correlation_id_idx ON public.audit_logs_y2026m08 USING btree (tenant_id, correlation_id);


-- Name: audit_logs_y2026m09_tenant_id_correlation_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX audit_logs_y2026m09_tenant_id_correlation_id_idx ON public.audit_logs_y2026m09 USING btree (tenant_id, correlation_id);


-- Name: audit_logs_y2026m10_tenant_id_correlation_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX audit_logs_y2026m10_tenant_id_correlation_id_idx ON public.audit_logs_y2026m10 USING btree (tenant_id, correlation_id);


-- Name: audit_logs_y2026m11_tenant_id_correlation_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX audit_logs_y2026m11_tenant_id_correlation_id_idx ON public.audit_logs_y2026m11 USING btree (tenant_id, correlation_id);


-- Name: audit_logs_y2026m12_tenant_id_correlation_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX audit_logs_y2026m12_tenant_id_correlation_id_idx ON public.audit_logs_y2026m12 USING btree (tenant_id, correlation_id);


-- Name: ix_background_job_items__job_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_background_job_items__job_id ON ONLY public.background_job_items USING btree (job_id);


-- Name: INDEX ix_background_job_items__job_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_background_job_items__job_id IS '§19.4 rule 2: FK cover for background_job_items(job_id) -> background_jobs, ON DELETE CASCADE.';


-- Name: background_job_items_default_job_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX background_job_items_default_job_id_idx ON public.background_job_items_default USING btree (job_id);


-- Name: background_job_items_y2025m10_job_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX background_job_items_y2025m10_job_id_idx ON public.background_job_items_y2025m10 USING btree (job_id);


-- Name: background_job_items_y2025m11_job_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX background_job_items_y2025m11_job_id_idx ON public.background_job_items_y2025m11 USING btree (job_id);


-- Name: background_job_items_y2025m12_job_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX background_job_items_y2025m12_job_id_idx ON public.background_job_items_y2025m12 USING btree (job_id);


-- Name: background_job_items_y2026m01_job_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX background_job_items_y2026m01_job_id_idx ON public.background_job_items_y2026m01 USING btree (job_id);


-- Name: background_job_items_y2026m02_job_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX background_job_items_y2026m02_job_id_idx ON public.background_job_items_y2026m02 USING btree (job_id);


-- Name: background_job_items_y2026m03_job_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX background_job_items_y2026m03_job_id_idx ON public.background_job_items_y2026m03 USING btree (job_id);


-- Name: background_job_items_y2026m04_job_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX background_job_items_y2026m04_job_id_idx ON public.background_job_items_y2026m04 USING btree (job_id);


-- Name: background_job_items_y2026m05_job_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX background_job_items_y2026m05_job_id_idx ON public.background_job_items_y2026m05 USING btree (job_id);


-- Name: background_job_items_y2026m06_job_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX background_job_items_y2026m06_job_id_idx ON public.background_job_items_y2026m06 USING btree (job_id);


-- Name: background_job_items_y2026m07_job_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX background_job_items_y2026m07_job_id_idx ON public.background_job_items_y2026m07 USING btree (job_id);


-- Name: background_job_items_y2026m08_job_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX background_job_items_y2026m08_job_id_idx ON public.background_job_items_y2026m08 USING btree (job_id);


-- Name: background_job_items_y2026m09_job_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX background_job_items_y2026m09_job_id_idx ON public.background_job_items_y2026m09 USING btree (job_id);


-- Name: background_job_items_y2026m10_job_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX background_job_items_y2026m10_job_id_idx ON public.background_job_items_y2026m10 USING btree (job_id);


-- Name: background_job_items_y2026m11_job_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX background_job_items_y2026m11_job_id_idx ON public.background_job_items_y2026m11 USING btree (job_id);


-- Name: background_job_items_y2026m12_job_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX background_job_items_y2026m12_job_id_idx ON public.background_job_items_y2026m12 USING btree (job_id);


-- Name: ix_approval_actions__actor_user_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_approval_actions__actor_user_id ON public.approval_actions USING btree (tenant_id, actor_user_id);


-- Name: INDEX ix_approval_actions__actor_user_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_approval_actions__actor_user_id IS '§19.4 rule 2: FK cover for approval_actions(tenant_id, actor_user_id) -> users, ON DELETE RESTRICT.';


-- Name: ix_approval_actions__on_behalf_of_user_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_approval_actions__on_behalf_of_user_id ON public.approval_actions USING btree (tenant_id, on_behalf_of_user_id);


-- Name: INDEX ix_approval_actions__on_behalf_of_user_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_approval_actions__on_behalf_of_user_id IS '§19.4 rule 2: FK cover for approval_actions(tenant_id, on_behalf_of_user_id) -> users, ON DELETE SET NULL.';


-- Name: ix_approval_actions__request_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_approval_actions__request_id ON public.approval_actions USING btree (tenant_id, request_id);


-- Name: INDEX ix_approval_actions__request_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_approval_actions__request_id IS '§19.4 rule 2: FK cover for approval_actions(tenant_id, request_id) -> approval_requests, ON DELETE RESTRICT.';


-- Name: ix_approval_delegations__delegate_user_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_approval_delegations__delegate_user_id ON public.approval_delegations USING btree (tenant_id, delegate_user_id);


-- Name: INDEX ix_approval_delegations__delegate_user_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_approval_delegations__delegate_user_id IS '§19.4 rule 2: FK cover for approval_delegations(tenant_id, delegate_user_id) -> users, ON DELETE CASCADE.';


-- Name: ix_approval_delegations__delegator_user_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_approval_delegations__delegator_user_id ON public.approval_delegations USING btree (tenant_id, delegator_user_id);


-- Name: INDEX ix_approval_delegations__delegator_user_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_approval_delegations__delegator_user_id IS '§19.4 rule 2: FK cover for approval_delegations(tenant_id, delegator_user_id) -> users, ON DELETE CASCADE.';


-- Name: ix_approval_requests__employee_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_approval_requests__employee_id ON public.approval_requests USING btree (tenant_id, employee_id);


-- Name: INDEX ix_approval_requests__employee_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_approval_requests__employee_id IS '§19.4 rule 2: FK cover for approval_requests(tenant_id, employee_id) -> employees, ON DELETE RESTRICT.';


-- Name: ix_approval_requests__inbox_employee; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_approval_requests__inbox_employee ON public.approval_requests USING btree (tenant_id, current_approver_employee_id, due_at, created_at DESC) WHERE ((status)::text = 'Pending'::text);


-- Name: INDEX ix_approval_requests__inbox_employee; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_approval_requests__inbox_employee IS 'H4 approvals inbox, the current_approver_employee_id twin: an approver identified as an employee rather than a user account.';


-- Name: ix_approval_requests__inbox_user; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_approval_requests__inbox_user ON public.approval_requests USING btree (tenant_id, current_approver_user_id, due_at, created_at DESC) WHERE ((status)::text = 'Pending'::text);


-- Name: INDEX ix_approval_requests__inbox_user; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_approval_requests__inbox_user IS 'H4 approvals inbox by approver user (ApprovalWorkflowService.cs:94). Partial on status=''Pending''; due_at ASC NULLS LAST matches the port''s sort exactly.';


-- Name: ix_approval_requests__overdue; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_approval_requests__overdue ON public.approval_requests USING btree (tenant_id, due_at) WHERE (((status)::text = 'Pending'::text) AND (due_at IS NOT NULL));


-- Name: INDEX ix_approval_requests__overdue; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_approval_requests__overdue IS 'H4 overdue sweep and the §10.2 Pending -> Expired transition at due_at.';


-- Name: ix_approval_requests__requester_user_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_approval_requests__requester_user_id ON public.approval_requests USING btree (tenant_id, requester_user_id);


-- Name: INDEX ix_approval_requests__requester_user_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_approval_requests__requester_user_id IS '§19.4 rule 2: FK cover for approval_requests(tenant_id, requester_user_id) -> users, ON DELETE RESTRICT.';


-- Name: ix_approval_requests__workflow_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_approval_requests__workflow_id ON public.approval_requests USING btree (tenant_id, workflow_id);


-- Name: INDEX ix_approval_requests__workflow_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_approval_requests__workflow_id IS '§19.4 rule 2: FK cover for approval_requests(tenant_id, workflow_id) -> approval_workflows, ON DELETE RESTRICT.';


-- Name: ix_attendance_devices__branch_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_attendance_devices__branch_id ON public.attendance_devices USING btree (tenant_id, branch_id);


-- Name: INDEX ix_attendance_devices__branch_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_attendance_devices__branch_id IS '§19.4 rule 2: FK cover for attendance_devices(tenant_id, branch_id) -> branches, ON DELETE RESTRICT.';


-- Name: ix_auth_sessions__platform_user_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_auth_sessions__platform_user_id ON public.auth_sessions USING btree (platform_user_id);


-- Name: INDEX ix_auth_sessions__platform_user_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_auth_sessions__platform_user_id IS '§19.4 rule 2: FK cover for auth_sessions(platform_user_id) -> platform_users, ON DELETE CASCADE.';


-- Name: ix_auth_sessions__user_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_auth_sessions__user_id ON public.auth_sessions USING btree (tenant_id, user_id);


-- Name: INDEX ix_auth_sessions__user_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_auth_sessions__user_id IS '§19.4 rule 2: FK cover for auth_sessions(tenant_id, user_id) -> users, ON DELETE CASCADE.';


-- Name: ix_auth_tokens__platform_user_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_auth_tokens__platform_user_id ON public.auth_tokens USING btree (platform_user_id);


-- Name: INDEX ix_auth_tokens__platform_user_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_auth_tokens__platform_user_id IS '§19.4 rule 2: FK cover for auth_tokens(platform_user_id) -> platform_users, ON DELETE CASCADE.';


-- Name: ix_auth_tokens__user_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_auth_tokens__user_id ON public.auth_tokens USING btree (tenant_id, user_id);


-- Name: INDEX ix_auth_tokens__user_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_auth_tokens__user_id IS '§19.4 rule 2: FK cover for auth_tokens(tenant_id, user_id) -> users, ON DELETE CASCADE.';


-- Name: ix_background_jobs__queue; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_background_jobs__queue ON public.background_jobs USING btree (status, scheduled_at) WHERE ((status)::text = ANY ((ARRAY['Queued'::character varying, 'Leased'::character varying, 'Running'::character varying])::text[]));


-- Name: INDEX ix_background_jobs__queue; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_background_jobs__queue IS 'Workflow: the job poller. THE QUEUE EXCEPTION to §19.4 rule 1 — the poller runs as kynex_job across every tenant and background_jobs.tenant_id is nullable for platform jobs, so a tenant-leading index would be useless to it. §19.4 prints this index as (status, next_attempt_at); background_jobs has no next_attempt_at column — the due-time column the design actually built is scheduled_at, used here.';


-- Name: ix_background_jobs__source_file_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_background_jobs__source_file_id ON public.background_jobs USING btree (source_file_id);


-- Name: INDEX ix_background_jobs__source_file_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_background_jobs__source_file_id IS '§19.4 rule 2: FK cover for background_jobs(source_file_id) -> files, ON DELETE SET NULL.';


-- Name: ix_branches__company_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_branches__company_id ON public.branches USING btree (tenant_id, company_id);


-- Name: INDEX ix_branches__company_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_branches__company_id IS '§19.4 rule 2: FK cover for branches(tenant_id, company_id) -> companies, ON DELETE RESTRICT.';


-- Name: ix_company_pay_policies__pay_component_code; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_company_pay_policies__pay_component_code ON public.company_pay_policies USING btree (tenant_id, pay_component_code);


-- Name: INDEX ix_company_pay_policies__pay_component_code; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_company_pay_policies__pay_component_code IS '§19.4 rule 2: FK cover for company_pay_policies(tenant_id, pay_component_code) -> pay_components, ON DELETE RESTRICT.';


-- Name: ix_departments__company_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_departments__company_id ON public.departments USING btree (tenant_id, company_id);


-- Name: INDEX ix_departments__company_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_departments__company_id IS '§19.4 rule 2: FK cover for departments(tenant_id, company_id) -> companies, ON DELETE RESTRICT.';


-- Name: ix_departments__cost_center_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_departments__cost_center_id ON public.departments USING btree (tenant_id, cost_center_id);


-- Name: INDEX ix_departments__cost_center_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_departments__cost_center_id IS '§19.4 rule 2: FK cover for departments(tenant_id, cost_center_id) -> cost_centers, ON DELETE SET NULL.';


-- Name: ix_employee_assignments__approval_request_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_employee_assignments__approval_request_id ON public.employee_assignments USING btree (tenant_id, approval_request_id);


-- Name: INDEX ix_employee_assignments__approval_request_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_employee_assignments__approval_request_id IS '§19.4 rule 2: FK cover for employee_assignments(tenant_id, approval_request_id) -> approval_requests, ON DELETE RESTRICT.';


-- Name: ix_employee_assignments__as_of; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_employee_assignments__as_of ON public.employee_assignments USING btree (tenant_id, employee_id, effective_from DESC);


-- Name: INDEX ix_employee_assignments__as_of; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_employee_assignments__as_of IS 'H6 payroll run: the as-of assignment read, which §11.3 makes authoritative for "was this person employed on date D". Same pairing with the gist EXCLUDE.';


-- Name: ix_employee_assignments__branch_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_employee_assignments__branch_id ON public.employee_assignments USING btree (tenant_id, branch_id);


-- Name: INDEX ix_employee_assignments__branch_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_employee_assignments__branch_id IS '§19.4 rule 2: FK cover for employee_assignments(tenant_id, branch_id) -> branches, ON DELETE RESTRICT.';


-- Name: ix_employee_assignments__company_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_employee_assignments__company_id ON public.employee_assignments USING btree (tenant_id, company_id);


-- Name: INDEX ix_employee_assignments__company_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_employee_assignments__company_id IS '§19.4 rule 2: FK cover for employee_assignments(tenant_id, company_id) -> companies, ON DELETE RESTRICT.';


-- Name: ix_employee_assignments__cost_center_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_employee_assignments__cost_center_id ON public.employee_assignments USING btree (tenant_id, cost_center_id);


-- Name: INDEX ix_employee_assignments__cost_center_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_employee_assignments__cost_center_id IS '§19.4 rule 2: FK cover for employee_assignments(tenant_id, cost_center_id) -> cost_centers, ON DELETE RESTRICT.';


-- Name: ix_employee_assignments__department_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_employee_assignments__department_id ON public.employee_assignments USING btree (tenant_id, department_id);


-- Name: INDEX ix_employee_assignments__department_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_employee_assignments__department_id IS '§19.4 rule 2: FK cover for employee_assignments(tenant_id, department_id) -> departments, ON DELETE RESTRICT.';


-- Name: ix_employee_assignments__designation_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_employee_assignments__designation_id ON public.employee_assignments USING btree (tenant_id, designation_id);


-- Name: INDEX ix_employee_assignments__designation_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_employee_assignments__designation_id IS '§19.4 rule 2: FK cover for employee_assignments(tenant_id, designation_id) -> designations, ON DELETE RESTRICT.';


-- Name: ix_employee_assignments__grade_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_employee_assignments__grade_id ON public.employee_assignments USING btree (tenant_id, grade_id);


-- Name: INDEX ix_employee_assignments__grade_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_employee_assignments__grade_id IS '§19.4 rule 2: FK cover for employee_assignments(tenant_id, grade_id) -> grades, ON DELETE RESTRICT.';


-- Name: ix_employee_assignments__manager_employee_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_employee_assignments__manager_employee_id ON public.employee_assignments USING btree (tenant_id, manager_employee_id) WHERE (effective_to IS NULL);


-- Name: INDEX ix_employee_assignments__manager_employee_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_employee_assignments__manager_employee_id IS 'H5 recursive manager walk over employee_assignments.manager_employee_id, partial on the current assignment (effective_to IS NULL) per §19.4 rule 4. Also the FK cover for that column.';


-- Name: ix_employee_contracts__document_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_employee_contracts__document_id ON public.employee_contracts USING btree (tenant_id, document_id);


-- Name: INDEX ix_employee_contracts__document_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_employee_contracts__document_id IS '§19.4 rule 2: FK cover for employee_contracts(tenant_id, document_id) -> employee_documents, ON DELETE SET NULL.';


-- Name: ix_employee_documents__active_expiry; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_employee_documents__active_expiry ON public.employee_documents USING btree (tenant_id, expiry_date) WHERE ((status)::text = 'Active'::text);


-- Name: INDEX ix_employee_documents__active_expiry; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_employee_documents__active_expiry IS 'Lifecycle: the Iqama/passport/work-permit expiry alerting sweep, which only ever looks at Active documents.';


-- Name: ix_employee_documents__employee_doc_type; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_employee_documents__employee_doc_type ON public.employee_documents USING btree (tenant_id, employee_id, doc_type);


-- Name: INDEX ix_employee_documents__employee_doc_type; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_employee_documents__employee_doc_type IS 'H3 dashboard KPIs (:1104): document completeness per employee, after the correlated subquery becomes a GROUP BY.';


-- Name: ix_employee_documents__employee_expiry; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_employee_documents__employee_expiry ON public.employee_documents USING btree (tenant_id, employee_id, expiry_date);


-- Name: INDEX ix_employee_documents__employee_expiry; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_employee_documents__employee_expiry IS 'H10 ESS dashboard: my documents and what expires next.';


-- Name: ix_employee_documents__employee_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_employee_documents__employee_id ON public.employee_documents USING btree (tenant_id, employee_id);


-- Name: INDEX ix_employee_documents__employee_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_employee_documents__employee_id IS '§19.4 rule 2: FK cover for employee_documents(tenant_id, employee_id) -> employees, ON DELETE CASCADE.';


-- Name: ix_employee_documents__expiry_date; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_employee_documents__expiry_date ON public.employee_documents USING btree (tenant_id, expiry_date);


-- Name: INDEX ix_employee_documents__expiry_date; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_employee_documents__expiry_date IS 'H3 dashboard KPIs (:1150): documents expiring inside the window, all statuses.';


-- Name: ix_employee_documents__file_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_employee_documents__file_id ON public.employee_documents USING btree (tenant_id, file_id);


-- Name: INDEX ix_employee_documents__file_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_employee_documents__file_id IS '§19.4 rule 2: FK cover for employee_documents(tenant_id, file_id) -> files, ON DELETE RESTRICT.';


-- Name: ix_employee_documents__leave_request_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_employee_documents__leave_request_id ON public.employee_documents USING btree (tenant_id, leave_request_id);


-- Name: INDEX ix_employee_documents__leave_request_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_employee_documents__leave_request_id IS '§19.4 rule 2: FK cover for employee_documents(tenant_id, leave_request_id) -> leave_requests, ON DELETE SET NULL.';


-- Name: ix_employee_documents__supersedes_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_employee_documents__supersedes_id ON public.employee_documents USING btree (tenant_id, supersedes_id);


-- Name: INDEX ix_employee_documents__supersedes_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_employee_documents__supersedes_id IS '§19.4 rule 2: FK cover for employee_documents(tenant_id, supersedes_id) -> employee_documents, ON DELETE SET NULL.';


-- Name: ix_employee_gosi_registrations__company_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_employee_gosi_registrations__company_id ON public.employee_gosi_registrations USING btree (tenant_id, company_id);


-- Name: INDEX ix_employee_gosi_registrations__company_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_employee_gosi_registrations__company_id IS '§19.4 rule 2: FK cover for employee_gosi_registrations(tenant_id, company_id) -> companies, ON DELETE RESTRICT.';


-- Name: ix_employee_gosi_registrations__gosi_registration_no; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_employee_gosi_registrations__gosi_registration_no ON public.employee_gosi_registrations USING btree (tenant_id, gosi_registration_no);


-- Name: INDEX ix_employee_gosi_registrations__gosi_registration_no; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_employee_gosi_registrations__gosi_registration_no IS '§19.4 rule 2: FK cover for employee_gosi_registrations(tenant_id, gosi_registration_no) -> companies, ON DELETE RESTRICT.';


-- Name: ix_employee_salaries__approval_request_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_employee_salaries__approval_request_id ON public.employee_salaries USING btree (tenant_id, approval_request_id);


-- Name: INDEX ix_employee_salaries__approval_request_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_employee_salaries__approval_request_id IS '§19.4 rule 2: FK cover for employee_salaries(tenant_id, approval_request_id) -> approval_requests, ON DELETE RESTRICT.';


-- Name: ix_employee_salaries__as_of; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_employee_salaries__as_of ON public.employee_salaries USING btree (tenant_id, employee_id, effective_from DESC);


-- Name: INDEX ix_employee_salaries__as_of; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_employee_salaries__as_of IS 'H6 payroll run: the as-of salary read. Sits beside ex_employee_salaries__employee_no_overlap — the gist EXCLUDE guarantees there is exactly one row for a date, this btree finds it.';


-- Name: ix_employees__search_trgm; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_employees__search_trgm ON public.employees USING gin (((((((((COALESCE(employee_number, ''::character varying))::text || ' '::text) || COALESCE(name_en, ''::text)) || ' '::text) || COALESCE(name_ar, ''::text)) || ' '::text) || COALESCE(work_email, ''::text))) public.gin_trgm_ops) WHERE ((status)::text <> 'Archived'::text);


-- Name: INDEX ix_employees__search_trgm; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_employees__search_trgm IS 'H1 employee search. pg_trgm GIN over the four searched columns concatenated, so the five OR''d leading-wildcard LIKEs of EmployeesController.cs:86 become one index scan. Partial on status <> ''Archived'' because an archived employee is never a search hit. tenant_id is NOT the leading key here (a GIN index has no leading key); RLS still filters, and the trigram candidate set is small.';


-- Name: ix_employees__tenant_status_name_en; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_employees__tenant_status_name_en ON public.employees USING btree (tenant_id, status, name_en);


-- Name: INDEX ix_employees__tenant_status_name_en; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_employees__tenant_status_name_en IS 'H1 employee list, unsearched default page, and the keyset paging that replaces OFFSET in the port.';


-- Name: ix_eos_calculations__employee_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_eos_calculations__employee_id ON public.eos_calculations USING btree (tenant_id, employee_id);


-- Name: INDEX ix_eos_calculations__employee_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_eos_calculations__employee_id IS '§19.4 rule 2: FK cover for eos_calculations(tenant_id, employee_id) -> employees, ON DELETE RESTRICT.';


-- Name: ix_eos_calculations__settlement_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_eos_calculations__settlement_id ON public.eos_calculations USING btree (tenant_id, settlement_id);


-- Name: INDEX ix_eos_calculations__settlement_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_eos_calculations__settlement_id IS '§19.4 rule 2: FK cover for eos_calculations(tenant_id, settlement_id) -> final_settlements, ON DELETE SET NULL.';


-- Name: ix_files__pending_purge; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_files__pending_purge ON public.files USING btree (purge_state) WHERE ((purge_state)::text = 'PendingPurge'::text);


-- Name: INDEX ix_files__pending_purge; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_files__pending_purge IS 'Lifecycle: the §12.3 blob purge worklist. THE QUEUE EXCEPTION to §19.4 rule 1 — the retention job walks tenants in bounded batches (§19.5) and needs the global worklist first.';


-- Name: ix_files__uploaded_by; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_files__uploaded_by ON public.files USING btree (tenant_id, uploaded_by);


-- Name: INDEX ix_files__uploaded_by; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_files__uploaded_by IS '§19.4 rule 2: FK cover for files(tenant_id, uploaded_by) -> users, ON DELETE SET NULL.';


-- Name: ix_final_settlement_lines__loan_installment_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_final_settlement_lines__loan_installment_id ON public.final_settlement_lines USING btree (tenant_id, loan_installment_id);


-- Name: INDEX ix_final_settlement_lines__loan_installment_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_final_settlement_lines__loan_installment_id IS '§19.4 rule 2: FK cover for final_settlement_lines(tenant_id, loan_installment_id) -> loan_installments, ON DELETE RESTRICT.';


-- Name: ix_final_settlement_lines__settlement_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_final_settlement_lines__settlement_id ON public.final_settlement_lines USING btree (tenant_id, settlement_id);


-- Name: INDEX ix_final_settlement_lines__settlement_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_final_settlement_lines__settlement_id IS '§19.4 rule 2: FK cover for final_settlement_lines(tenant_id, settlement_id) -> final_settlements, ON DELETE CASCADE.';


-- Name: ix_final_settlements__approval_request_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_final_settlements__approval_request_id ON public.final_settlements USING btree (tenant_id, approval_request_id);


-- Name: INDEX ix_final_settlements__approval_request_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_final_settlements__approval_request_id IS '§19.4 rule 2: FK cover for final_settlements(tenant_id, approval_request_id) -> approval_requests, ON DELETE RESTRICT.';


-- Name: ix_final_settlements__company_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_final_settlements__company_id ON public.final_settlements USING btree (tenant_id, company_id);


-- Name: INDEX ix_final_settlements__company_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_final_settlements__company_id IS '§19.4 rule 2: FK cover for final_settlements(tenant_id, company_id) -> companies, ON DELETE RESTRICT.';


-- Name: ix_final_settlements__employee_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_final_settlements__employee_id ON public.final_settlements USING btree (tenant_id, employee_id);


-- Name: INDEX ix_final_settlements__employee_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_final_settlements__employee_id IS '§19.4 rule 2: FK cover for final_settlements(tenant_id, employee_id) -> employees, ON DELETE RESTRICT.';


-- Name: ix_final_settlements__paid_via_run_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_final_settlements__paid_via_run_id ON public.final_settlements USING btree (tenant_id, paid_via_run_id);


-- Name: INDEX ix_final_settlements__paid_via_run_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_final_settlements__paid_via_run_id IS '§19.4 rule 2: FK cover for final_settlements(tenant_id, paid_via_run_id) -> payroll_runs, ON DELETE RESTRICT.';


-- Name: ix_gl_journal_lines__cost_center_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_gl_journal_lines__cost_center_id ON public.gl_journal_lines USING btree (tenant_id, cost_center_id);


-- Name: INDEX ix_gl_journal_lines__cost_center_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_gl_journal_lines__cost_center_id IS '§19.4 rule 2: FK cover for gl_journal_lines(tenant_id, cost_center_id) -> cost_centers, ON DELETE RESTRICT.';


-- Name: ix_gl_journal_lines__journal_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_gl_journal_lines__journal_id ON public.gl_journal_lines USING btree (tenant_id, journal_id);


-- Name: INDEX ix_gl_journal_lines__journal_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_gl_journal_lines__journal_id IS '§19.4 rule 2: FK cover for gl_journal_lines(tenant_id, journal_id) -> gl_journals, ON DELETE CASCADE.';


-- Name: ix_gl_journals__company_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_gl_journals__company_id ON public.gl_journals USING btree (tenant_id, company_id);


-- Name: INDEX ix_gl_journals__company_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_gl_journals__company_id IS '§19.4 rule 2: FK cover for gl_journals(tenant_id, company_id) -> companies, ON DELETE RESTRICT.';


-- Name: ix_gl_journals__file_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_gl_journals__file_id ON public.gl_journals USING btree (tenant_id, file_id);


-- Name: INDEX ix_gl_journals__file_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_gl_journals__file_id IS '§19.4 rule 2: FK cover for gl_journals(tenant_id, file_id) -> files, ON DELETE RESTRICT.';


-- Name: ix_gl_journals__reversal_of_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_gl_journals__reversal_of_id ON public.gl_journals USING btree (tenant_id, reversal_of_id);


-- Name: INDEX ix_gl_journals__reversal_of_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_gl_journals__reversal_of_id IS '§19.4 rule 2: FK cover for gl_journals(tenant_id, reversal_of_id) -> gl_journals, ON DELETE RESTRICT.';


-- Name: ix_gl_mappings__cost_center_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_gl_mappings__cost_center_id ON public.gl_mappings USING btree (tenant_id, cost_center_id);


-- Name: INDEX ix_gl_mappings__cost_center_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_gl_mappings__cost_center_id IS '§19.4 rule 2: FK cover for gl_mappings(tenant_id, cost_center_id) -> cost_centers, ON DELETE RESTRICT.';


-- Name: ix_gosi_filings__file_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_gosi_filings__file_id ON public.gosi_filings USING btree (tenant_id, file_id);


-- Name: INDEX ix_gosi_filings__file_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_gosi_filings__file_id IS '§19.4 rule 2: FK cover for gosi_filings(tenant_id, file_id) -> files, ON DELETE RESTRICT.';


-- Name: ix_gosi_filings__gosi_registration_no; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_gosi_filings__gosi_registration_no ON public.gosi_filings USING btree (tenant_id, gosi_registration_no);


-- Name: INDEX ix_gosi_filings__gosi_registration_no; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_gosi_filings__gosi_registration_no IS '§19.4 rule 2: FK cover for gosi_filings(tenant_id, gosi_registration_no) -> companies, ON DELETE RESTRICT.';


-- Name: ix_gosi_filings__period; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_gosi_filings__period ON public.gosi_filings USING btree (tenant_id, company_id, year, month);


-- Name: INDEX ix_gosi_filings__period; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_gosi_filings__period IS 'Workflow: the filing for a company and period across revisions. uq_gosi_filings__period leads with gosi_registration_no before year and month, so it cannot serve this lookup.';


-- Name: ix_leave_ledger__balance; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_leave_ledger__balance ON public.leave_ledger USING btree (tenant_id, employee_id, leave_type_id) INCLUDE (days);


-- Name: INDEX ix_leave_ledger__balance; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_leave_ledger__balance IS 'H12 v_leave_balances: INCLUDE (days) makes the SUM index-only, since §11.6 stores no balance anywhere. Also the FK cover for leave_ledger.employee_id. If p95 exceeds 50 ms at 270k rows per tenant the fallback is a trigger-maintained balance row — §19.4 leaves that to measurement, so it is not here.';


-- Name: ix_leave_ledger__employee_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_leave_ledger__employee_id ON public.leave_ledger USING btree (tenant_id, employee_id);


-- Name: INDEX ix_leave_ledger__employee_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_leave_ledger__employee_id IS '§19.4 rule 2: FK cover for leave_ledger(tenant_id, employee_id) -> employees, ON DELETE RESTRICT.';


-- Name: ix_leave_ledger__leave_type_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_leave_ledger__leave_type_id ON public.leave_ledger USING btree (tenant_id, leave_type_id);


-- Name: INDEX ix_leave_ledger__leave_type_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_leave_ledger__leave_type_id IS '§19.4 rule 2: FK cover for leave_ledger(tenant_id, leave_type_id) -> leave_types, ON DELETE RESTRICT.';


-- Name: ix_leave_requests__approval_request_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_leave_requests__approval_request_id ON public.leave_requests USING btree (tenant_id, approval_request_id);


-- Name: INDEX ix_leave_requests__approval_request_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_leave_requests__approval_request_id IS '§19.4 rule 2: FK cover for leave_requests(tenant_id, approval_request_id) -> approval_requests, ON DELETE RESTRICT.';


-- Name: ix_leave_requests__approved_span; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_leave_requests__approved_span ON public.leave_requests USING gist (tenant_id, employee_id, daterange(start_date, end_date, '[]'::text)) WHERE ((status)::text = 'Approved'::text);


-- Name: INDEX ix_leave_requests__approved_span; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_leave_requests__approved_span IS 'H8 attendance sweep: "was this employee on approved leave on date D". gist over the inclusive-inclusive daterange of the §1 effective-dating convention; needs btree_gist for the two scalar keys.';


-- Name: ix_leave_requests__employee_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_leave_requests__employee_id ON public.leave_requests USING btree (tenant_id, employee_id);


-- Name: INDEX ix_leave_requests__employee_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_leave_requests__employee_id IS '§19.4 rule 2: FK cover for leave_requests(tenant_id, employee_id) -> employees, ON DELETE RESTRICT.';


-- Name: ix_leave_requests__leave_type_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_leave_requests__leave_type_id ON public.leave_requests USING btree (tenant_id, leave_type_id);


-- Name: INDEX ix_leave_requests__leave_type_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_leave_requests__leave_type_id IS '§19.4 rule 2: FK cover for leave_requests(tenant_id, leave_type_id) -> leave_types, ON DELETE RESTRICT.';


-- Name: ix_leave_requests__pending; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_leave_requests__pending ON public.leave_requests USING btree (tenant_id, status) WHERE ((status)::text = 'PendingApproval'::text);


-- Name: INDEX ix_leave_requests__pending; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_leave_requests__pending IS 'H3 dashboard KPIs: the pending-leave count. §19.4 rule 4 partial — the pending subset is tiny next to the table. H10''s LIKE ''%Pending%'' becomes this status set in the port.';


-- Name: ix_loans__active_recoverable; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_loans__active_recoverable ON public.loans USING btree (tenant_id, employee_id) WHERE (((status)::text = 'Active'::text) AND (outstanding > (0)::numeric));


-- Name: INDEX ix_loans__active_recoverable; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_loans__active_recoverable IS 'H6 payroll run: loans still being recovered. Partial, so a tenant''s settled loan history does not sit in the index the run scans every month.';


-- Name: ix_loans__approval_request_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_loans__approval_request_id ON public.loans USING btree (tenant_id, approval_request_id);


-- Name: INDEX ix_loans__approval_request_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_loans__approval_request_id IS '§19.4 rule 2: FK cover for loans(tenant_id, approval_request_id) -> approval_requests, ON DELETE RESTRICT.';


-- Name: ix_loans__employee_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_loans__employee_id ON public.loans USING btree (tenant_id, employee_id);


-- Name: INDEX ix_loans__employee_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_loans__employee_id IS '§19.4 rule 2: FK cover for loans(tenant_id, employee_id) -> employees, ON DELETE RESTRICT.';


-- Name: ix_notification_deliveries__notification_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_notification_deliveries__notification_id ON public.notification_deliveries USING btree (tenant_id, notification_id);


-- Name: INDEX ix_notification_deliveries__notification_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_notification_deliveries__notification_id IS '§19.4 rule 2: FK cover for notification_deliveries(tenant_id, notification_id) -> notifications, ON DELETE CASCADE.';


-- Name: ix_notification_deliveries__retry; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_notification_deliveries__retry ON public.notification_deliveries USING btree (status, next_attempt_at) WHERE ((status)::text = ANY ((ARRAY['Queued'::character varying, 'Failed'::character varying])::text[]));


-- Name: INDEX ix_notification_deliveries__retry; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_notification_deliveries__retry IS 'Workflow: the delivery retry poller. THE QUEUE EXCEPTION to §19.4 rule 1, same reason as the job queue.';


-- Name: ix_notifications__unread; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_notifications__unread ON public.notifications USING btree (tenant_id, user_id, created_at DESC) WHERE (read_at IS NULL);


-- Name: INDEX ix_notifications__unread; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_notifications__unread IS 'H10 ESS dashboard: the unread bell count and list. §19.4 rule 4 partial on read_at IS NULL — the read tail is the overwhelming majority of the table and is never in this query.';


-- Name: ix_notifications__user_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_notifications__user_id ON public.notifications USING btree (tenant_id, user_id);


-- Name: INDEX ix_notifications__user_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_notifications__user_id IS '§19.4 rule 2: FK cover for notifications(tenant_id, user_id) -> users, ON DELETE CASCADE.';


-- Name: ix_overtime_requests__approval_request_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_overtime_requests__approval_request_id ON public.overtime_requests USING btree (tenant_id, approval_request_id);


-- Name: INDEX ix_overtime_requests__approval_request_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_overtime_requests__approval_request_id IS '§19.4 rule 2: FK cover for overtime_requests(tenant_id, approval_request_id) -> approval_requests, ON DELETE RESTRICT.';


-- Name: ix_overtime_requests__employee_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_overtime_requests__employee_id ON public.overtime_requests USING btree (tenant_id, employee_id);


-- Name: INDEX ix_overtime_requests__employee_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_overtime_requests__employee_id IS '§19.4 rule 2: FK cover for overtime_requests(tenant_id, employee_id) -> employees, ON DELETE RESTRICT.';


-- Name: ix_overtime_requests__work_date_status; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_overtime_requests__work_date_status ON public.overtime_requests USING btree (tenant_id, work_date, status);


-- Name: INDEX ix_overtime_requests__work_date_status; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_overtime_requests__work_date_status IS 'H6 payroll run: approved overtime for the period being paid.';


-- Name: ix_payroll_inputs__claimable; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_payroll_inputs__claimable ON public.payroll_inputs USING btree (tenant_id, company_id, run_year, run_month, employee_id) WHERE ((status)::text = 'Pending'::text);


-- Name: INDEX ix_payroll_inputs__claimable; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_payroll_inputs__claimable IS 'H6 payroll run: the single-statement UPDATE ... RETURNING claim of §10.10, which reads exactly the Pending rows for a company and period. §19.4 rule 4 partial.';


-- Name: ix_payroll_inputs__claimed_by_run_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_payroll_inputs__claimed_by_run_id ON public.payroll_inputs USING btree (tenant_id, claimed_by_run_id);


-- Name: INDEX ix_payroll_inputs__claimed_by_run_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_payroll_inputs__claimed_by_run_id IS '§19.4 rule 2: FK cover for payroll_inputs(tenant_id, claimed_by_run_id) -> payroll_runs, ON DELETE SET NULL.';


-- Name: ix_payroll_inputs__company_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_payroll_inputs__company_id ON public.payroll_inputs USING btree (tenant_id, company_id);


-- Name: INDEX ix_payroll_inputs__company_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_payroll_inputs__company_id IS '§19.4 rule 2: FK cover for payroll_inputs(tenant_id, company_id) -> companies, ON DELETE RESTRICT.';


-- Name: ix_payroll_inputs__consumed_run_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_payroll_inputs__consumed_run_id ON public.payroll_inputs USING btree (tenant_id, consumed_run_id);


-- Name: INDEX ix_payroll_inputs__consumed_run_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_payroll_inputs__consumed_run_id IS '§19.4 rule 2: FK cover for payroll_inputs(tenant_id, consumed_run_id) -> payroll_runs, ON DELETE RESTRICT.';


-- Name: ix_payroll_inputs__cost_center_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_payroll_inputs__cost_center_id ON public.payroll_inputs USING btree (tenant_id, cost_center_id);


-- Name: INDEX ix_payroll_inputs__cost_center_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_payroll_inputs__cost_center_id IS '§19.4 rule 2: FK cover for payroll_inputs(tenant_id, cost_center_id) -> cost_centers, ON DELETE RESTRICT.';


-- Name: ix_payroll_inputs__employee_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_payroll_inputs__employee_id ON public.payroll_inputs USING btree (tenant_id, employee_id);


-- Name: INDEX ix_payroll_inputs__employee_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_payroll_inputs__employee_id IS '§19.4 rule 2: FK cover for payroll_inputs(tenant_id, employee_id) -> employees, ON DELETE RESTRICT.';


-- Name: ix_payroll_inputs__pay_component_code; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_payroll_inputs__pay_component_code ON public.payroll_inputs USING btree (tenant_id, pay_component_code);


-- Name: INDEX ix_payroll_inputs__pay_component_code; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_payroll_inputs__pay_component_code IS '§19.4 rule 2: FK cover for payroll_inputs(tenant_id, pay_component_code) -> pay_components, ON DELETE RESTRICT.';


-- Name: ix_payroll_issues__employee_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_payroll_issues__employee_id ON public.payroll_issues USING btree (tenant_id, employee_id);


-- Name: INDEX ix_payroll_issues__employee_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_payroll_issues__employee_id IS '§19.4 rule 2: FK cover for payroll_issues(tenant_id, employee_id) -> employees, ON DELETE RESTRICT.';


-- Name: ix_payroll_issues__employee_standing; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_payroll_issues__employee_standing ON public.payroll_issues USING btree (tenant_id, employee_id) WHERE (run_id IS NULL);


-- Name: INDEX ix_payroll_issues__employee_standing; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_payroll_issues__employee_standing IS 'Workflow: standing issues against an employee that belong to no run. Also the FK cover for payroll_issues.employee_id on the subset that query reads.';


-- Name: ix_payroll_issues__run_blocks; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_payroll_issues__run_blocks ON public.payroll_issues USING btree (tenant_id, run_id) WHERE ((severity)::text = 'Block'::text);


-- Name: INDEX ix_payroll_issues__run_blocks; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_payroll_issues__run_blocks IS 'Workflow: "can this run be approved" — the Block issues of a run. §19.4 rule 4 partial.';


-- Name: ix_payroll_issues__run_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_payroll_issues__run_id ON public.payroll_issues USING btree (tenant_id, run_id);


-- Name: INDEX ix_payroll_issues__run_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_payroll_issues__run_id IS '§19.4 rule 2: FK cover for payroll_issues(tenant_id, run_id) -> payroll_runs, ON DELETE CASCADE.';


-- Name: ix_payroll_runs__approval_request_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_payroll_runs__approval_request_id ON public.payroll_runs USING btree (tenant_id, approval_request_id);


-- Name: INDEX ix_payroll_runs__approval_request_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_payroll_runs__approval_request_id IS '§19.4 rule 2: FK cover for payroll_runs(tenant_id, approval_request_id) -> approval_requests, ON DELETE RESTRICT.';


-- Name: ix_payroll_runs__parent_run_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_payroll_runs__parent_run_id ON public.payroll_runs USING btree (tenant_id, parent_run_id);


-- Name: INDEX ix_payroll_runs__parent_run_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_payroll_runs__parent_run_id IS '§19.4 rule 2: FK cover for payroll_runs(tenant_id, parent_run_id) -> payroll_runs, ON DELETE RESTRICT.';


-- Name: ix_payroll_runs__period; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_payroll_runs__period ON public.payroll_runs USING btree (tenant_id, company_id, year, month, status);


-- Name: INDEX ix_payroll_runs__period; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_payroll_runs__period IS 'H7 payroll YTD and every period lookup: the prior run for a company, year and month.';


-- Name: ix_payroll_runs__source_import_job_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_payroll_runs__source_import_job_id ON public.payroll_runs USING btree (tenant_id, source_import_job_id);


-- Name: INDEX ix_payroll_runs__source_import_job_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_payroll_runs__source_import_job_id IS '§19.4 rule 2: FK cover for payroll_runs(tenant_id, source_import_job_id) -> background_jobs, ON DELETE SET NULL.';


-- Name: ix_payroll_slip_lines__cost_center_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_payroll_slip_lines__cost_center_id ON public.payroll_slip_lines USING btree (tenant_id, cost_center_id);


-- Name: INDEX ix_payroll_slip_lines__cost_center_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_payroll_slip_lines__cost_center_id IS '§19.4 rule 2: FK cover for payroll_slip_lines(tenant_id, cost_center_id) -> cost_centers, ON DELETE RESTRICT.';


-- Name: ix_payroll_slip_lines__loan_installment_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_payroll_slip_lines__loan_installment_id ON public.payroll_slip_lines USING btree (tenant_id, loan_installment_id);


-- Name: INDEX ix_payroll_slip_lines__loan_installment_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_payroll_slip_lines__loan_installment_id IS '§19.4 rule 2: FK cover for payroll_slip_lines(tenant_id, loan_installment_id) -> loan_installments, ON DELETE RESTRICT.';


-- Name: ix_payroll_slip_lines__pay_component_code; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_payroll_slip_lines__pay_component_code ON public.payroll_slip_lines USING btree (tenant_id, pay_component_code);


-- Name: INDEX ix_payroll_slip_lines__pay_component_code; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_payroll_slip_lines__pay_component_code IS '§19.4 rule 2: FK cover for payroll_slip_lines(tenant_id, pay_component_code) -> pay_components, ON DELETE RESTRICT.';


-- Name: ix_payroll_slip_lines__payroll_input_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_payroll_slip_lines__payroll_input_id ON public.payroll_slip_lines USING btree (tenant_id, payroll_input_id);


-- Name: INDEX ix_payroll_slip_lines__payroll_input_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_payroll_slip_lines__payroll_input_id IS '§19.4 rule 2: FK cover for payroll_slip_lines(tenant_id, payroll_input_id) -> payroll_inputs, ON DELETE RESTRICT.';


-- Name: ix_payroll_slip_lines__slip_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_payroll_slip_lines__slip_id ON public.payroll_slip_lines USING btree (tenant_id, slip_id);


-- Name: INDEX ix_payroll_slip_lines__slip_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_payroll_slip_lines__slip_id IS '§19.4 rule 2: FK cover for payroll_slip_lines(tenant_id, slip_id) -> payroll_slips, ON DELETE CASCADE.';


-- Name: ix_payroll_slips__employee_run; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_payroll_slips__employee_run ON public.payroll_slips USING btree (tenant_id, employee_id, run_id DESC);


-- Name: INDEX ix_payroll_slips__employee_run; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_payroll_slips__employee_run IS 'H10 ESS dashboard: my latest payslips, newest first. Also the FK cover for payroll_slips.employee_id.';


-- Name: ix_payroll_slips__payslip_file_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_payroll_slips__payslip_file_id ON public.payroll_slips USING btree (tenant_id, payslip_file_id);


-- Name: INDEX ix_payroll_slips__payslip_file_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_payroll_slips__payslip_file_id IS '§19.4 rule 2: FK cover for payroll_slips(tenant_id, payslip_file_id) -> files, ON DELETE RESTRICT.';


-- Name: ix_permission_grantor_records__grantor_user_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_permission_grantor_records__grantor_user_id ON public.permission_grantor_records USING btree (tenant_id, grantor_user_id);


-- Name: INDEX ix_permission_grantor_records__grantor_user_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_permission_grantor_records__grantor_user_id IS '§19.4 rule 2: FK cover for permission_grantor_records(tenant_id, grantor_user_id) -> users, ON DELETE CASCADE.';


-- Name: ix_permission_grantor_records__revoked_by; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_permission_grantor_records__revoked_by ON public.permission_grantor_records USING btree (tenant_id, revoked_by);


-- Name: INDEX ix_permission_grantor_records__revoked_by; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_permission_grantor_records__revoked_by IS '§19.4 rule 2: FK cover for permission_grantor_records(tenant_id, revoked_by) -> users, ON DELETE RESTRICT.';


-- Name: ix_public_holidays__calendar_date; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_public_holidays__calendar_date ON public.public_holidays USING btree (calendar_code, holiday_date);


-- Name: INDEX ix_public_holidays__calendar_date; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_public_holidays__calendar_date IS 'H8 attendance sweep: the holiday calendar, loaded once per sweep. THE QUEUE EXCEPTION to §19.4 rule 1 — public_holidays.tenant_id is nullable because a NULL row is the shared national calendar, so a tenant-leading index would not find it.';


-- Name: ix_shift_assignments__shift_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_shift_assignments__shift_id ON public.shift_assignments USING btree (tenant_id, shift_id);


-- Name: INDEX ix_shift_assignments__shift_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_shift_assignments__shift_id IS '§19.4 rule 2: FK cover for shift_assignments(tenant_id, shift_id) -> shifts, ON DELETE RESTRICT.';


-- Name: ix_timesheet_day_reconciliations__attendance_day_id_work_date; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_timesheet_day_reconciliations__attendance_day_id_work_date ON public.timesheet_day_reconciliations USING btree (tenant_id, attendance_day_id, work_date);


-- Name: INDEX ix_timesheet_day_reconciliations__attendance_day_id_work_date; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_timesheet_day_reconciliations__attendance_day_id_work_date IS '§19.4 rule 2: FK cover for timesheet_day_reconciliations(tenant_id, attendance_day_id, work_date) -> attendance_days, ON DELETE RESTRICT.';


-- Name: ix_timesheet_entries__cost_center_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_timesheet_entries__cost_center_id ON ONLY public.timesheet_entries USING btree (tenant_id, cost_center_id);


-- Name: INDEX ix_timesheet_entries__cost_center_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_timesheet_entries__cost_center_id IS '§19.4 rule 2: FK cover for timesheet_entries(tenant_id, cost_center_id) -> cost_centers, ON DELETE RESTRICT.';


-- Name: ix_timesheet_entries__timesheet_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_timesheet_entries__timesheet_id ON ONLY public.timesheet_entries USING btree (tenant_id, timesheet_id);


-- Name: INDEX ix_timesheet_entries__timesheet_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_timesheet_entries__timesheet_id IS '§19.4 rule 2: FK cover for timesheet_entries(tenant_id, timesheet_id) -> timesheets, ON DELETE CASCADE.';


-- Name: ix_timesheets__approval_request_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_timesheets__approval_request_id ON public.timesheets USING btree (tenant_id, approval_request_id);


-- Name: INDEX ix_timesheets__approval_request_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_timesheets__approval_request_id IS '§19.4 rule 2: FK cover for timesheets(tenant_id, approval_request_id) -> approval_requests, ON DELETE RESTRICT.';


-- Name: ix_timesheets__company_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_timesheets__company_id ON public.timesheets USING btree (tenant_id, company_id);


-- Name: INDEX ix_timesheets__company_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_timesheets__company_id IS '§19.4 rule 2: FK cover for timesheets(tenant_id, company_id) -> companies, ON DELETE RESTRICT.';


-- Name: ix_timesheets__locked_run_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_timesheets__locked_run_id ON public.timesheets USING btree (tenant_id, locked_run_id);


-- Name: INDEX ix_timesheets__locked_run_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_timesheets__locked_run_id IS '§19.4 rule 2: FK cover for timesheets(tenant_id, locked_run_id) -> payroll_runs, ON DELETE SET NULL.';


-- Name: ix_user_roles__granted_by; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_user_roles__granted_by ON public.user_roles USING btree (tenant_id, granted_by);


-- Name: INDEX ix_user_roles__granted_by; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_user_roles__granted_by IS '§19.4 rule 2: FK cover for user_roles(tenant_id, granted_by) -> users, ON DELETE SET NULL.';


-- Name: ix_user_roles__role_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_user_roles__role_id ON public.user_roles USING btree (tenant_id, role_id);


-- Name: INDEX ix_user_roles__role_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_user_roles__role_id IS '§19.4 rule 2: FK cover for user_roles(tenant_id, role_id) -> roles, ON DELETE RESTRICT.';


-- Name: ix_user_roles__scope_branch_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_user_roles__scope_branch_id ON public.user_roles USING btree (tenant_id, scope_branch_id);


-- Name: INDEX ix_user_roles__scope_branch_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_user_roles__scope_branch_id IS '§19.4 rule 2: FK cover for user_roles(tenant_id, scope_branch_id) -> branches, ON DELETE RESTRICT.';


-- Name: ix_user_roles__scope_company_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_user_roles__scope_company_id ON public.user_roles USING btree (tenant_id, scope_company_id);


-- Name: INDEX ix_user_roles__scope_company_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_user_roles__scope_company_id IS '§19.4 rule 2: FK cover for user_roles(tenant_id, scope_company_id) -> companies, ON DELETE RESTRICT.';


-- Name: ix_user_roles__scope_department_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_user_roles__scope_department_id ON public.user_roles USING btree (tenant_id, scope_department_id);


-- Name: INDEX ix_user_roles__scope_department_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_user_roles__scope_department_id IS '§19.4 rule 2: FK cover for user_roles(tenant_id, scope_department_id) -> departments, ON DELETE RESTRICT.';


-- Name: ix_user_roles__user_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_user_roles__user_id ON public.user_roles USING btree (tenant_id, user_id);


-- Name: INDEX ix_user_roles__user_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_user_roles__user_id IS '§19.4 rule 2: FK cover for user_roles(tenant_id, user_id) -> users, ON DELETE CASCADE.';


-- Name: ix_wps_batches__company_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_wps_batches__company_id ON public.wps_batches USING btree (tenant_id, company_id);


-- Name: INDEX ix_wps_batches__company_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_wps_batches__company_id IS '§19.4 rule 2: FK cover for wps_batches(tenant_id, company_id) -> companies, ON DELETE RESTRICT.';


-- Name: ix_wps_batches__file_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_wps_batches__file_id ON public.wps_batches USING btree (tenant_id, file_id);


-- Name: INDEX ix_wps_batches__file_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_wps_batches__file_id IS '§19.4 rule 2: FK cover for wps_batches(tenant_id, file_id) -> files, ON DELETE RESTRICT.';


-- Name: ix_wps_batches__generated_by; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_wps_batches__generated_by ON public.wps_batches USING btree (tenant_id, generated_by);


-- Name: INDEX ix_wps_batches__generated_by; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_wps_batches__generated_by IS '§19.4 rule 2: FK cover for wps_batches(tenant_id, generated_by) -> users, ON DELETE SET NULL.';


-- Name: ix_wps_batches__resubmission_of_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_wps_batches__resubmission_of_id ON public.wps_batches USING btree (tenant_id, resubmission_of_id);


-- Name: INDEX ix_wps_batches__resubmission_of_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_wps_batches__resubmission_of_id IS '§19.4 rule 2: FK cover for wps_batches(tenant_id, resubmission_of_id) -> wps_batches, ON DELETE RESTRICT.';


-- Name: ix_wps_batches__run_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_wps_batches__run_id ON public.wps_batches USING btree (tenant_id, run_id);


-- Name: INDEX ix_wps_batches__run_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_wps_batches__run_id IS '§19.4 rule 2: FK cover for wps_batches(tenant_id, run_id) -> payroll_runs, ON DELETE RESTRICT.';


-- Name: ix_wps_lines__batch_bank_status; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_wps_lines__batch_bank_status ON public.wps_lines USING btree (tenant_id, batch_id, bank_status);


-- Name: INDEX ix_wps_lines__batch_bank_status; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_wps_lines__batch_bank_status IS 'Workflow: the bank confirmation importer, and the §10.1 Paid -> Completed test that every wps_lines.bank_status in a run is terminal. Also the FK cover for wps_lines.batch_id.';


-- Name: ix_wps_lines__confirmation_job_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_wps_lines__confirmation_job_id ON public.wps_lines USING btree (confirmation_job_id);


-- Name: INDEX ix_wps_lines__confirmation_job_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_wps_lines__confirmation_job_id IS '§19.4 rule 2: FK cover for wps_lines(confirmation_job_id) -> background_jobs, ON DELETE SET NULL.';


-- Name: ix_wps_lines__employee_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_wps_lines__employee_id ON public.wps_lines USING btree (tenant_id, employee_id);


-- Name: INDEX ix_wps_lines__employee_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_wps_lines__employee_id IS '§19.4 rule 2: FK cover for wps_lines(tenant_id, employee_id) -> employees, ON DELETE RESTRICT.';


-- Name: ix_wps_lines__slip_id; Type: INDEX; Schema: public; Owner: -

CREATE INDEX ix_wps_lines__slip_id ON public.wps_lines USING btree (tenant_id, slip_id);


-- Name: INDEX ix_wps_lines__slip_id; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.ix_wps_lines__slip_id IS '§19.4 rule 2: FK cover for wps_lines(tenant_id, slip_id) -> payroll_slips, ON DELETE RESTRICT.';


-- Name: timesheet_entries_default_tenant_id_cost_center_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX timesheet_entries_default_tenant_id_cost_center_id_idx ON public.timesheet_entries_default USING btree (tenant_id, cost_center_id);


-- Name: timesheet_entries_default_tenant_id_timesheet_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX timesheet_entries_default_tenant_id_timesheet_id_idx ON public.timesheet_entries_default USING btree (tenant_id, timesheet_id);


-- Name: timesheet_entries_y2025m10_tenant_id_cost_center_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX timesheet_entries_y2025m10_tenant_id_cost_center_id_idx ON public.timesheet_entries_y2025m10 USING btree (tenant_id, cost_center_id);


-- Name: timesheet_entries_y2025m10_tenant_id_timesheet_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX timesheet_entries_y2025m10_tenant_id_timesheet_id_idx ON public.timesheet_entries_y2025m10 USING btree (tenant_id, timesheet_id);


-- Name: timesheet_entries_y2025m11_tenant_id_cost_center_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX timesheet_entries_y2025m11_tenant_id_cost_center_id_idx ON public.timesheet_entries_y2025m11 USING btree (tenant_id, cost_center_id);


-- Name: timesheet_entries_y2025m11_tenant_id_timesheet_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX timesheet_entries_y2025m11_tenant_id_timesheet_id_idx ON public.timesheet_entries_y2025m11 USING btree (tenant_id, timesheet_id);


-- Name: timesheet_entries_y2025m12_tenant_id_cost_center_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX timesheet_entries_y2025m12_tenant_id_cost_center_id_idx ON public.timesheet_entries_y2025m12 USING btree (tenant_id, cost_center_id);


-- Name: timesheet_entries_y2025m12_tenant_id_timesheet_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX timesheet_entries_y2025m12_tenant_id_timesheet_id_idx ON public.timesheet_entries_y2025m12 USING btree (tenant_id, timesheet_id);


-- Name: timesheet_entries_y2026m01_tenant_id_cost_center_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX timesheet_entries_y2026m01_tenant_id_cost_center_id_idx ON public.timesheet_entries_y2026m01 USING btree (tenant_id, cost_center_id);


-- Name: timesheet_entries_y2026m01_tenant_id_timesheet_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX timesheet_entries_y2026m01_tenant_id_timesheet_id_idx ON public.timesheet_entries_y2026m01 USING btree (tenant_id, timesheet_id);


-- Name: timesheet_entries_y2026m02_tenant_id_cost_center_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX timesheet_entries_y2026m02_tenant_id_cost_center_id_idx ON public.timesheet_entries_y2026m02 USING btree (tenant_id, cost_center_id);


-- Name: timesheet_entries_y2026m02_tenant_id_timesheet_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX timesheet_entries_y2026m02_tenant_id_timesheet_id_idx ON public.timesheet_entries_y2026m02 USING btree (tenant_id, timesheet_id);


-- Name: timesheet_entries_y2026m03_tenant_id_cost_center_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX timesheet_entries_y2026m03_tenant_id_cost_center_id_idx ON public.timesheet_entries_y2026m03 USING btree (tenant_id, cost_center_id);


-- Name: timesheet_entries_y2026m03_tenant_id_timesheet_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX timesheet_entries_y2026m03_tenant_id_timesheet_id_idx ON public.timesheet_entries_y2026m03 USING btree (tenant_id, timesheet_id);


-- Name: timesheet_entries_y2026m04_tenant_id_cost_center_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX timesheet_entries_y2026m04_tenant_id_cost_center_id_idx ON public.timesheet_entries_y2026m04 USING btree (tenant_id, cost_center_id);


-- Name: timesheet_entries_y2026m04_tenant_id_timesheet_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX timesheet_entries_y2026m04_tenant_id_timesheet_id_idx ON public.timesheet_entries_y2026m04 USING btree (tenant_id, timesheet_id);


-- Name: timesheet_entries_y2026m05_tenant_id_cost_center_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX timesheet_entries_y2026m05_tenant_id_cost_center_id_idx ON public.timesheet_entries_y2026m05 USING btree (tenant_id, cost_center_id);


-- Name: timesheet_entries_y2026m05_tenant_id_timesheet_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX timesheet_entries_y2026m05_tenant_id_timesheet_id_idx ON public.timesheet_entries_y2026m05 USING btree (tenant_id, timesheet_id);


-- Name: timesheet_entries_y2026m06_tenant_id_cost_center_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX timesheet_entries_y2026m06_tenant_id_cost_center_id_idx ON public.timesheet_entries_y2026m06 USING btree (tenant_id, cost_center_id);


-- Name: timesheet_entries_y2026m06_tenant_id_timesheet_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX timesheet_entries_y2026m06_tenant_id_timesheet_id_idx ON public.timesheet_entries_y2026m06 USING btree (tenant_id, timesheet_id);


-- Name: timesheet_entries_y2026m07_tenant_id_cost_center_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX timesheet_entries_y2026m07_tenant_id_cost_center_id_idx ON public.timesheet_entries_y2026m07 USING btree (tenant_id, cost_center_id);


-- Name: timesheet_entries_y2026m07_tenant_id_timesheet_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX timesheet_entries_y2026m07_tenant_id_timesheet_id_idx ON public.timesheet_entries_y2026m07 USING btree (tenant_id, timesheet_id);


-- Name: timesheet_entries_y2026m08_tenant_id_cost_center_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX timesheet_entries_y2026m08_tenant_id_cost_center_id_idx ON public.timesheet_entries_y2026m08 USING btree (tenant_id, cost_center_id);


-- Name: timesheet_entries_y2026m08_tenant_id_timesheet_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX timesheet_entries_y2026m08_tenant_id_timesheet_id_idx ON public.timesheet_entries_y2026m08 USING btree (tenant_id, timesheet_id);


-- Name: timesheet_entries_y2026m09_tenant_id_cost_center_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX timesheet_entries_y2026m09_tenant_id_cost_center_id_idx ON public.timesheet_entries_y2026m09 USING btree (tenant_id, cost_center_id);


-- Name: timesheet_entries_y2026m09_tenant_id_timesheet_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX timesheet_entries_y2026m09_tenant_id_timesheet_id_idx ON public.timesheet_entries_y2026m09 USING btree (tenant_id, timesheet_id);


-- Name: timesheet_entries_y2026m10_tenant_id_cost_center_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX timesheet_entries_y2026m10_tenant_id_cost_center_id_idx ON public.timesheet_entries_y2026m10 USING btree (tenant_id, cost_center_id);


-- Name: timesheet_entries_y2026m10_tenant_id_timesheet_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX timesheet_entries_y2026m10_tenant_id_timesheet_id_idx ON public.timesheet_entries_y2026m10 USING btree (tenant_id, timesheet_id);


-- Name: timesheet_entries_y2026m11_tenant_id_cost_center_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX timesheet_entries_y2026m11_tenant_id_cost_center_id_idx ON public.timesheet_entries_y2026m11 USING btree (tenant_id, cost_center_id);


-- Name: timesheet_entries_y2026m11_tenant_id_timesheet_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX timesheet_entries_y2026m11_tenant_id_timesheet_id_idx ON public.timesheet_entries_y2026m11 USING btree (tenant_id, timesheet_id);


-- Name: timesheet_entries_y2026m12_tenant_id_cost_center_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX timesheet_entries_y2026m12_tenant_id_cost_center_id_idx ON public.timesheet_entries_y2026m12 USING btree (tenant_id, cost_center_id);


-- Name: timesheet_entries_y2026m12_tenant_id_timesheet_id_idx; Type: INDEX; Schema: public; Owner: -

CREATE INDEX timesheet_entries_y2026m12_tenant_id_timesheet_id_idx ON public.timesheet_entries_y2026m12 USING btree (tenant_id, timesheet_id);


-- Name: uq_auth_sessions__subject_device; Type: INDEX; Schema: public; Owner: -

CREATE UNIQUE INDEX uq_auth_sessions__subject_device ON public.auth_sessions USING btree (subject_kind, COALESCE(user_id, platform_user_id), device_id);


-- Name: INDEX uq_auth_sessions__subject_device; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.uq_auth_sessions__subject_device IS 'Enforces §2.B: one live session row per (subject kind, subject, device), so a re-login replaces a device''s session rather than accumulating one per sign-in. COALESCE, not a bare column pair, because a NULL operand would exempt the row from the constraint entirely. Also serves the refresh path''s lookup by device.';


-- Name: uq_payroll_runs__period_regular_opening; Type: INDEX; Schema: public; Owner: -

CREATE UNIQUE INDEX uq_payroll_runs__period_regular_opening ON public.payroll_runs USING btree (tenant_id, company_id, year, month, run_type) WHERE (((run_type)::text = ANY ((ARRAY['Regular'::character varying, 'Opening'::character varying])::text[])) AND ((status)::text <> 'Voided'::text));


-- Name: INDEX uq_payroll_runs__period_regular_opening; Type: COMMENT; Schema: public; Owner: -

COMMENT ON INDEX public.uq_payroll_runs__period_regular_opening IS 'Enforces §2.F: at most one live Regular run and one live Opening run per company and period. Partial on status <> ''Voided'' so a voided run can be re-run for the same month. Also serves the payroll dashboard''s lookup of the current run for a company and period.';


-- Name: attendance_days_default_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_attendance_days ATTACH PARTITION public.attendance_days_default_pkey;


-- Name: attendance_days_default_tenant_id_employee_id_work_date_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_days__employee_work_date ATTACH PARTITION public.attendance_days_default_tenant_id_employee_id_work_date_key;


-- Name: attendance_days_default_tenant_id_id_work_date_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_days__tenant_id ATTACH PARTITION public.attendance_days_default_tenant_id_id_work_date_key;


-- Name: attendance_days_default_tenant_id_locked_run_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_days__locked_run_id ATTACH PARTITION public.attendance_days_default_tenant_id_locked_run_id_idx;


-- Name: attendance_days_default_tenant_id_shift_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_days__shift_id ATTACH PARTITION public.attendance_days_default_tenant_id_shift_id_idx;


-- Name: attendance_days_default_tenant_id_work_date_status_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_days__tenant_work_date_status ATTACH PARTITION public.attendance_days_default_tenant_id_work_date_status_idx;


-- Name: attendance_days_y2025m10_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_attendance_days ATTACH PARTITION public.attendance_days_y2025m10_pkey;


-- Name: attendance_days_y2025m10_tenant_id_employee_id_work_date_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_days__employee_work_date ATTACH PARTITION public.attendance_days_y2025m10_tenant_id_employee_id_work_date_key;


-- Name: attendance_days_y2025m10_tenant_id_id_work_date_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_days__tenant_id ATTACH PARTITION public.attendance_days_y2025m10_tenant_id_id_work_date_key;


-- Name: attendance_days_y2025m10_tenant_id_locked_run_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_days__locked_run_id ATTACH PARTITION public.attendance_days_y2025m10_tenant_id_locked_run_id_idx;


-- Name: attendance_days_y2025m10_tenant_id_shift_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_days__shift_id ATTACH PARTITION public.attendance_days_y2025m10_tenant_id_shift_id_idx;


-- Name: attendance_days_y2025m10_tenant_id_work_date_status_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_days__tenant_work_date_status ATTACH PARTITION public.attendance_days_y2025m10_tenant_id_work_date_status_idx;


-- Name: attendance_days_y2025m11_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_attendance_days ATTACH PARTITION public.attendance_days_y2025m11_pkey;


-- Name: attendance_days_y2025m11_tenant_id_employee_id_work_date_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_days__employee_work_date ATTACH PARTITION public.attendance_days_y2025m11_tenant_id_employee_id_work_date_key;


-- Name: attendance_days_y2025m11_tenant_id_id_work_date_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_days__tenant_id ATTACH PARTITION public.attendance_days_y2025m11_tenant_id_id_work_date_key;


-- Name: attendance_days_y2025m11_tenant_id_locked_run_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_days__locked_run_id ATTACH PARTITION public.attendance_days_y2025m11_tenant_id_locked_run_id_idx;


-- Name: attendance_days_y2025m11_tenant_id_shift_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_days__shift_id ATTACH PARTITION public.attendance_days_y2025m11_tenant_id_shift_id_idx;


-- Name: attendance_days_y2025m11_tenant_id_work_date_status_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_days__tenant_work_date_status ATTACH PARTITION public.attendance_days_y2025m11_tenant_id_work_date_status_idx;


-- Name: attendance_days_y2025m12_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_attendance_days ATTACH PARTITION public.attendance_days_y2025m12_pkey;


-- Name: attendance_days_y2025m12_tenant_id_employee_id_work_date_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_days__employee_work_date ATTACH PARTITION public.attendance_days_y2025m12_tenant_id_employee_id_work_date_key;


-- Name: attendance_days_y2025m12_tenant_id_id_work_date_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_days__tenant_id ATTACH PARTITION public.attendance_days_y2025m12_tenant_id_id_work_date_key;


-- Name: attendance_days_y2025m12_tenant_id_locked_run_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_days__locked_run_id ATTACH PARTITION public.attendance_days_y2025m12_tenant_id_locked_run_id_idx;


-- Name: attendance_days_y2025m12_tenant_id_shift_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_days__shift_id ATTACH PARTITION public.attendance_days_y2025m12_tenant_id_shift_id_idx;


-- Name: attendance_days_y2025m12_tenant_id_work_date_status_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_days__tenant_work_date_status ATTACH PARTITION public.attendance_days_y2025m12_tenant_id_work_date_status_idx;


-- Name: attendance_days_y2026m01_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_attendance_days ATTACH PARTITION public.attendance_days_y2026m01_pkey;


-- Name: attendance_days_y2026m01_tenant_id_employee_id_work_date_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_days__employee_work_date ATTACH PARTITION public.attendance_days_y2026m01_tenant_id_employee_id_work_date_key;


-- Name: attendance_days_y2026m01_tenant_id_id_work_date_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_days__tenant_id ATTACH PARTITION public.attendance_days_y2026m01_tenant_id_id_work_date_key;


-- Name: attendance_days_y2026m01_tenant_id_locked_run_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_days__locked_run_id ATTACH PARTITION public.attendance_days_y2026m01_tenant_id_locked_run_id_idx;


-- Name: attendance_days_y2026m01_tenant_id_shift_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_days__shift_id ATTACH PARTITION public.attendance_days_y2026m01_tenant_id_shift_id_idx;


-- Name: attendance_days_y2026m01_tenant_id_work_date_status_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_days__tenant_work_date_status ATTACH PARTITION public.attendance_days_y2026m01_tenant_id_work_date_status_idx;


-- Name: attendance_days_y2026m02_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_attendance_days ATTACH PARTITION public.attendance_days_y2026m02_pkey;


-- Name: attendance_days_y2026m02_tenant_id_employee_id_work_date_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_days__employee_work_date ATTACH PARTITION public.attendance_days_y2026m02_tenant_id_employee_id_work_date_key;


-- Name: attendance_days_y2026m02_tenant_id_id_work_date_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_days__tenant_id ATTACH PARTITION public.attendance_days_y2026m02_tenant_id_id_work_date_key;


-- Name: attendance_days_y2026m02_tenant_id_locked_run_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_days__locked_run_id ATTACH PARTITION public.attendance_days_y2026m02_tenant_id_locked_run_id_idx;


-- Name: attendance_days_y2026m02_tenant_id_shift_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_days__shift_id ATTACH PARTITION public.attendance_days_y2026m02_tenant_id_shift_id_idx;


-- Name: attendance_days_y2026m02_tenant_id_work_date_status_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_days__tenant_work_date_status ATTACH PARTITION public.attendance_days_y2026m02_tenant_id_work_date_status_idx;


-- Name: attendance_days_y2026m03_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_attendance_days ATTACH PARTITION public.attendance_days_y2026m03_pkey;


-- Name: attendance_days_y2026m03_tenant_id_employee_id_work_date_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_days__employee_work_date ATTACH PARTITION public.attendance_days_y2026m03_tenant_id_employee_id_work_date_key;


-- Name: attendance_days_y2026m03_tenant_id_id_work_date_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_days__tenant_id ATTACH PARTITION public.attendance_days_y2026m03_tenant_id_id_work_date_key;


-- Name: attendance_days_y2026m03_tenant_id_locked_run_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_days__locked_run_id ATTACH PARTITION public.attendance_days_y2026m03_tenant_id_locked_run_id_idx;


-- Name: attendance_days_y2026m03_tenant_id_shift_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_days__shift_id ATTACH PARTITION public.attendance_days_y2026m03_tenant_id_shift_id_idx;


-- Name: attendance_days_y2026m03_tenant_id_work_date_status_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_days__tenant_work_date_status ATTACH PARTITION public.attendance_days_y2026m03_tenant_id_work_date_status_idx;


-- Name: attendance_days_y2026m04_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_attendance_days ATTACH PARTITION public.attendance_days_y2026m04_pkey;


-- Name: attendance_days_y2026m04_tenant_id_employee_id_work_date_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_days__employee_work_date ATTACH PARTITION public.attendance_days_y2026m04_tenant_id_employee_id_work_date_key;


-- Name: attendance_days_y2026m04_tenant_id_id_work_date_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_days__tenant_id ATTACH PARTITION public.attendance_days_y2026m04_tenant_id_id_work_date_key;


-- Name: attendance_days_y2026m04_tenant_id_locked_run_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_days__locked_run_id ATTACH PARTITION public.attendance_days_y2026m04_tenant_id_locked_run_id_idx;


-- Name: attendance_days_y2026m04_tenant_id_shift_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_days__shift_id ATTACH PARTITION public.attendance_days_y2026m04_tenant_id_shift_id_idx;


-- Name: attendance_days_y2026m04_tenant_id_work_date_status_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_days__tenant_work_date_status ATTACH PARTITION public.attendance_days_y2026m04_tenant_id_work_date_status_idx;


-- Name: attendance_days_y2026m05_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_attendance_days ATTACH PARTITION public.attendance_days_y2026m05_pkey;


-- Name: attendance_days_y2026m05_tenant_id_employee_id_work_date_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_days__employee_work_date ATTACH PARTITION public.attendance_days_y2026m05_tenant_id_employee_id_work_date_key;


-- Name: attendance_days_y2026m05_tenant_id_id_work_date_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_days__tenant_id ATTACH PARTITION public.attendance_days_y2026m05_tenant_id_id_work_date_key;


-- Name: attendance_days_y2026m05_tenant_id_locked_run_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_days__locked_run_id ATTACH PARTITION public.attendance_days_y2026m05_tenant_id_locked_run_id_idx;


-- Name: attendance_days_y2026m05_tenant_id_shift_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_days__shift_id ATTACH PARTITION public.attendance_days_y2026m05_tenant_id_shift_id_idx;


-- Name: attendance_days_y2026m05_tenant_id_work_date_status_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_days__tenant_work_date_status ATTACH PARTITION public.attendance_days_y2026m05_tenant_id_work_date_status_idx;


-- Name: attendance_days_y2026m06_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_attendance_days ATTACH PARTITION public.attendance_days_y2026m06_pkey;


-- Name: attendance_days_y2026m06_tenant_id_employee_id_work_date_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_days__employee_work_date ATTACH PARTITION public.attendance_days_y2026m06_tenant_id_employee_id_work_date_key;


-- Name: attendance_days_y2026m06_tenant_id_id_work_date_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_days__tenant_id ATTACH PARTITION public.attendance_days_y2026m06_tenant_id_id_work_date_key;


-- Name: attendance_days_y2026m06_tenant_id_locked_run_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_days__locked_run_id ATTACH PARTITION public.attendance_days_y2026m06_tenant_id_locked_run_id_idx;


-- Name: attendance_days_y2026m06_tenant_id_shift_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_days__shift_id ATTACH PARTITION public.attendance_days_y2026m06_tenant_id_shift_id_idx;


-- Name: attendance_days_y2026m06_tenant_id_work_date_status_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_days__tenant_work_date_status ATTACH PARTITION public.attendance_days_y2026m06_tenant_id_work_date_status_idx;


-- Name: attendance_days_y2026m07_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_attendance_days ATTACH PARTITION public.attendance_days_y2026m07_pkey;


-- Name: attendance_days_y2026m07_tenant_id_employee_id_work_date_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_days__employee_work_date ATTACH PARTITION public.attendance_days_y2026m07_tenant_id_employee_id_work_date_key;


-- Name: attendance_days_y2026m07_tenant_id_id_work_date_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_days__tenant_id ATTACH PARTITION public.attendance_days_y2026m07_tenant_id_id_work_date_key;


-- Name: attendance_days_y2026m07_tenant_id_locked_run_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_days__locked_run_id ATTACH PARTITION public.attendance_days_y2026m07_tenant_id_locked_run_id_idx;


-- Name: attendance_days_y2026m07_tenant_id_shift_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_days__shift_id ATTACH PARTITION public.attendance_days_y2026m07_tenant_id_shift_id_idx;


-- Name: attendance_days_y2026m07_tenant_id_work_date_status_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_days__tenant_work_date_status ATTACH PARTITION public.attendance_days_y2026m07_tenant_id_work_date_status_idx;


-- Name: attendance_days_y2026m08_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_attendance_days ATTACH PARTITION public.attendance_days_y2026m08_pkey;


-- Name: attendance_days_y2026m08_tenant_id_employee_id_work_date_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_days__employee_work_date ATTACH PARTITION public.attendance_days_y2026m08_tenant_id_employee_id_work_date_key;


-- Name: attendance_days_y2026m08_tenant_id_id_work_date_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_days__tenant_id ATTACH PARTITION public.attendance_days_y2026m08_tenant_id_id_work_date_key;


-- Name: attendance_days_y2026m08_tenant_id_locked_run_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_days__locked_run_id ATTACH PARTITION public.attendance_days_y2026m08_tenant_id_locked_run_id_idx;


-- Name: attendance_days_y2026m08_tenant_id_shift_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_days__shift_id ATTACH PARTITION public.attendance_days_y2026m08_tenant_id_shift_id_idx;


-- Name: attendance_days_y2026m08_tenant_id_work_date_status_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_days__tenant_work_date_status ATTACH PARTITION public.attendance_days_y2026m08_tenant_id_work_date_status_idx;


-- Name: attendance_days_y2026m09_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_attendance_days ATTACH PARTITION public.attendance_days_y2026m09_pkey;


-- Name: attendance_days_y2026m09_tenant_id_employee_id_work_date_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_days__employee_work_date ATTACH PARTITION public.attendance_days_y2026m09_tenant_id_employee_id_work_date_key;


-- Name: attendance_days_y2026m09_tenant_id_id_work_date_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_days__tenant_id ATTACH PARTITION public.attendance_days_y2026m09_tenant_id_id_work_date_key;


-- Name: attendance_days_y2026m09_tenant_id_locked_run_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_days__locked_run_id ATTACH PARTITION public.attendance_days_y2026m09_tenant_id_locked_run_id_idx;


-- Name: attendance_days_y2026m09_tenant_id_shift_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_days__shift_id ATTACH PARTITION public.attendance_days_y2026m09_tenant_id_shift_id_idx;


-- Name: attendance_days_y2026m09_tenant_id_work_date_status_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_days__tenant_work_date_status ATTACH PARTITION public.attendance_days_y2026m09_tenant_id_work_date_status_idx;


-- Name: attendance_days_y2026m10_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_attendance_days ATTACH PARTITION public.attendance_days_y2026m10_pkey;


-- Name: attendance_days_y2026m10_tenant_id_employee_id_work_date_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_days__employee_work_date ATTACH PARTITION public.attendance_days_y2026m10_tenant_id_employee_id_work_date_key;


-- Name: attendance_days_y2026m10_tenant_id_id_work_date_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_days__tenant_id ATTACH PARTITION public.attendance_days_y2026m10_tenant_id_id_work_date_key;


-- Name: attendance_days_y2026m10_tenant_id_locked_run_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_days__locked_run_id ATTACH PARTITION public.attendance_days_y2026m10_tenant_id_locked_run_id_idx;


-- Name: attendance_days_y2026m10_tenant_id_shift_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_days__shift_id ATTACH PARTITION public.attendance_days_y2026m10_tenant_id_shift_id_idx;


-- Name: attendance_days_y2026m10_tenant_id_work_date_status_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_days__tenant_work_date_status ATTACH PARTITION public.attendance_days_y2026m10_tenant_id_work_date_status_idx;


-- Name: attendance_days_y2026m11_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_attendance_days ATTACH PARTITION public.attendance_days_y2026m11_pkey;


-- Name: attendance_days_y2026m11_tenant_id_employee_id_work_date_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_days__employee_work_date ATTACH PARTITION public.attendance_days_y2026m11_tenant_id_employee_id_work_date_key;


-- Name: attendance_days_y2026m11_tenant_id_id_work_date_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_days__tenant_id ATTACH PARTITION public.attendance_days_y2026m11_tenant_id_id_work_date_key;


-- Name: attendance_days_y2026m11_tenant_id_locked_run_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_days__locked_run_id ATTACH PARTITION public.attendance_days_y2026m11_tenant_id_locked_run_id_idx;


-- Name: attendance_days_y2026m11_tenant_id_shift_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_days__shift_id ATTACH PARTITION public.attendance_days_y2026m11_tenant_id_shift_id_idx;


-- Name: attendance_days_y2026m11_tenant_id_work_date_status_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_days__tenant_work_date_status ATTACH PARTITION public.attendance_days_y2026m11_tenant_id_work_date_status_idx;


-- Name: attendance_days_y2026m12_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_attendance_days ATTACH PARTITION public.attendance_days_y2026m12_pkey;


-- Name: attendance_days_y2026m12_tenant_id_employee_id_work_date_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_days__employee_work_date ATTACH PARTITION public.attendance_days_y2026m12_tenant_id_employee_id_work_date_key;


-- Name: attendance_days_y2026m12_tenant_id_id_work_date_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_days__tenant_id ATTACH PARTITION public.attendance_days_y2026m12_tenant_id_id_work_date_key;


-- Name: attendance_days_y2026m12_tenant_id_locked_run_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_days__locked_run_id ATTACH PARTITION public.attendance_days_y2026m12_tenant_id_locked_run_id_idx;


-- Name: attendance_days_y2026m12_tenant_id_shift_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_days__shift_id ATTACH PARTITION public.attendance_days_y2026m12_tenant_id_shift_id_idx;


-- Name: attendance_days_y2026m12_tenant_id_work_date_status_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_days__tenant_work_date_status ATTACH PARTITION public.attendance_days_y2026m12_tenant_id_work_date_status_idx;


-- Name: attendance_punches_default_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_attendance_punches ATTACH PARTITION public.attendance_punches_default_pkey;


-- Name: attendance_punches_default_tenant_id_approval_request_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_punches__approval_request_id ATTACH PARTITION public.attendance_punches_default_tenant_id_approval_request_id_idx;


-- Name: attendance_punches_default_tenant_id_device_id_external_id__key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_punches__device_external_id ATTACH PARTITION public.attendance_punches_default_tenant_id_device_id_external_id__key;


-- Name: attendance_punches_default_tenant_id_employee_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_punches__employee_id ATTACH PARTITION public.attendance_punches_default_tenant_id_employee_id_idx;


-- Name: attendance_punches_default_tenant_id_employee_id_occurred_a_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_punches__employee_occurred_at ATTACH PARTITION public.attendance_punches_default_tenant_id_employee_id_occurred_a_idx;


-- Name: attendance_punches_default_tenant_id_id_occurred_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_punches__tenant_id ATTACH PARTITION public.attendance_punches_default_tenant_id_id_occurred_at_key;


-- Name: attendance_punches_default_tenant_id_idempotency_key_occurr_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_punches__idempotency_key ATTACH PARTITION public.attendance_punches_default_tenant_id_idempotency_key_occurr_key;


-- Name: attendance_punches_y2025m10_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_attendance_punches ATTACH PARTITION public.attendance_punches_y2025m10_pkey;


-- Name: attendance_punches_y2025m10_tenant_id_approval_request_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_punches__approval_request_id ATTACH PARTITION public.attendance_punches_y2025m10_tenant_id_approval_request_id_idx;


-- Name: attendance_punches_y2025m10_tenant_id_device_id_external_id_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_punches__device_external_id ATTACH PARTITION public.attendance_punches_y2025m10_tenant_id_device_id_external_id_key;


-- Name: attendance_punches_y2025m10_tenant_id_employee_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_punches__employee_id ATTACH PARTITION public.attendance_punches_y2025m10_tenant_id_employee_id_idx;


-- Name: attendance_punches_y2025m10_tenant_id_employee_id_occurred__idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_punches__employee_occurred_at ATTACH PARTITION public.attendance_punches_y2025m10_tenant_id_employee_id_occurred__idx;


-- Name: attendance_punches_y2025m10_tenant_id_id_occurred_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_punches__tenant_id ATTACH PARTITION public.attendance_punches_y2025m10_tenant_id_id_occurred_at_key;


-- Name: attendance_punches_y2025m10_tenant_id_idempotency_key_occur_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_punches__idempotency_key ATTACH PARTITION public.attendance_punches_y2025m10_tenant_id_idempotency_key_occur_key;


-- Name: attendance_punches_y2025m11_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_attendance_punches ATTACH PARTITION public.attendance_punches_y2025m11_pkey;


-- Name: attendance_punches_y2025m11_tenant_id_approval_request_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_punches__approval_request_id ATTACH PARTITION public.attendance_punches_y2025m11_tenant_id_approval_request_id_idx;


-- Name: attendance_punches_y2025m11_tenant_id_device_id_external_id_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_punches__device_external_id ATTACH PARTITION public.attendance_punches_y2025m11_tenant_id_device_id_external_id_key;


-- Name: attendance_punches_y2025m11_tenant_id_employee_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_punches__employee_id ATTACH PARTITION public.attendance_punches_y2025m11_tenant_id_employee_id_idx;


-- Name: attendance_punches_y2025m11_tenant_id_employee_id_occurred__idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_punches__employee_occurred_at ATTACH PARTITION public.attendance_punches_y2025m11_tenant_id_employee_id_occurred__idx;


-- Name: attendance_punches_y2025m11_tenant_id_id_occurred_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_punches__tenant_id ATTACH PARTITION public.attendance_punches_y2025m11_tenant_id_id_occurred_at_key;


-- Name: attendance_punches_y2025m11_tenant_id_idempotency_key_occur_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_punches__idempotency_key ATTACH PARTITION public.attendance_punches_y2025m11_tenant_id_idempotency_key_occur_key;


-- Name: attendance_punches_y2025m12_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_attendance_punches ATTACH PARTITION public.attendance_punches_y2025m12_pkey;


-- Name: attendance_punches_y2025m12_tenant_id_approval_request_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_punches__approval_request_id ATTACH PARTITION public.attendance_punches_y2025m12_tenant_id_approval_request_id_idx;


-- Name: attendance_punches_y2025m12_tenant_id_device_id_external_id_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_punches__device_external_id ATTACH PARTITION public.attendance_punches_y2025m12_tenant_id_device_id_external_id_key;


-- Name: attendance_punches_y2025m12_tenant_id_employee_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_punches__employee_id ATTACH PARTITION public.attendance_punches_y2025m12_tenant_id_employee_id_idx;


-- Name: attendance_punches_y2025m12_tenant_id_employee_id_occurred__idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_punches__employee_occurred_at ATTACH PARTITION public.attendance_punches_y2025m12_tenant_id_employee_id_occurred__idx;


-- Name: attendance_punches_y2025m12_tenant_id_id_occurred_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_punches__tenant_id ATTACH PARTITION public.attendance_punches_y2025m12_tenant_id_id_occurred_at_key;


-- Name: attendance_punches_y2025m12_tenant_id_idempotency_key_occur_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_punches__idempotency_key ATTACH PARTITION public.attendance_punches_y2025m12_tenant_id_idempotency_key_occur_key;


-- Name: attendance_punches_y2026m01_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_attendance_punches ATTACH PARTITION public.attendance_punches_y2026m01_pkey;


-- Name: attendance_punches_y2026m01_tenant_id_approval_request_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_punches__approval_request_id ATTACH PARTITION public.attendance_punches_y2026m01_tenant_id_approval_request_id_idx;


-- Name: attendance_punches_y2026m01_tenant_id_device_id_external_id_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_punches__device_external_id ATTACH PARTITION public.attendance_punches_y2026m01_tenant_id_device_id_external_id_key;


-- Name: attendance_punches_y2026m01_tenant_id_employee_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_punches__employee_id ATTACH PARTITION public.attendance_punches_y2026m01_tenant_id_employee_id_idx;


-- Name: attendance_punches_y2026m01_tenant_id_employee_id_occurred__idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_punches__employee_occurred_at ATTACH PARTITION public.attendance_punches_y2026m01_tenant_id_employee_id_occurred__idx;


-- Name: attendance_punches_y2026m01_tenant_id_id_occurred_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_punches__tenant_id ATTACH PARTITION public.attendance_punches_y2026m01_tenant_id_id_occurred_at_key;


-- Name: attendance_punches_y2026m01_tenant_id_idempotency_key_occur_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_punches__idempotency_key ATTACH PARTITION public.attendance_punches_y2026m01_tenant_id_idempotency_key_occur_key;


-- Name: attendance_punches_y2026m02_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_attendance_punches ATTACH PARTITION public.attendance_punches_y2026m02_pkey;


-- Name: attendance_punches_y2026m02_tenant_id_approval_request_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_punches__approval_request_id ATTACH PARTITION public.attendance_punches_y2026m02_tenant_id_approval_request_id_idx;


-- Name: attendance_punches_y2026m02_tenant_id_device_id_external_id_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_punches__device_external_id ATTACH PARTITION public.attendance_punches_y2026m02_tenant_id_device_id_external_id_key;


-- Name: attendance_punches_y2026m02_tenant_id_employee_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_punches__employee_id ATTACH PARTITION public.attendance_punches_y2026m02_tenant_id_employee_id_idx;


-- Name: attendance_punches_y2026m02_tenant_id_employee_id_occurred__idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_punches__employee_occurred_at ATTACH PARTITION public.attendance_punches_y2026m02_tenant_id_employee_id_occurred__idx;


-- Name: attendance_punches_y2026m02_tenant_id_id_occurred_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_punches__tenant_id ATTACH PARTITION public.attendance_punches_y2026m02_tenant_id_id_occurred_at_key;


-- Name: attendance_punches_y2026m02_tenant_id_idempotency_key_occur_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_punches__idempotency_key ATTACH PARTITION public.attendance_punches_y2026m02_tenant_id_idempotency_key_occur_key;


-- Name: attendance_punches_y2026m03_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_attendance_punches ATTACH PARTITION public.attendance_punches_y2026m03_pkey;


-- Name: attendance_punches_y2026m03_tenant_id_approval_request_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_punches__approval_request_id ATTACH PARTITION public.attendance_punches_y2026m03_tenant_id_approval_request_id_idx;


-- Name: attendance_punches_y2026m03_tenant_id_device_id_external_id_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_punches__device_external_id ATTACH PARTITION public.attendance_punches_y2026m03_tenant_id_device_id_external_id_key;


-- Name: attendance_punches_y2026m03_tenant_id_employee_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_punches__employee_id ATTACH PARTITION public.attendance_punches_y2026m03_tenant_id_employee_id_idx;


-- Name: attendance_punches_y2026m03_tenant_id_employee_id_occurred__idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_punches__employee_occurred_at ATTACH PARTITION public.attendance_punches_y2026m03_tenant_id_employee_id_occurred__idx;


-- Name: attendance_punches_y2026m03_tenant_id_id_occurred_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_punches__tenant_id ATTACH PARTITION public.attendance_punches_y2026m03_tenant_id_id_occurred_at_key;


-- Name: attendance_punches_y2026m03_tenant_id_idempotency_key_occur_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_punches__idempotency_key ATTACH PARTITION public.attendance_punches_y2026m03_tenant_id_idempotency_key_occur_key;


-- Name: attendance_punches_y2026m04_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_attendance_punches ATTACH PARTITION public.attendance_punches_y2026m04_pkey;


-- Name: attendance_punches_y2026m04_tenant_id_approval_request_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_punches__approval_request_id ATTACH PARTITION public.attendance_punches_y2026m04_tenant_id_approval_request_id_idx;


-- Name: attendance_punches_y2026m04_tenant_id_device_id_external_id_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_punches__device_external_id ATTACH PARTITION public.attendance_punches_y2026m04_tenant_id_device_id_external_id_key;


-- Name: attendance_punches_y2026m04_tenant_id_employee_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_punches__employee_id ATTACH PARTITION public.attendance_punches_y2026m04_tenant_id_employee_id_idx;


-- Name: attendance_punches_y2026m04_tenant_id_employee_id_occurred__idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_punches__employee_occurred_at ATTACH PARTITION public.attendance_punches_y2026m04_tenant_id_employee_id_occurred__idx;


-- Name: attendance_punches_y2026m04_tenant_id_id_occurred_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_punches__tenant_id ATTACH PARTITION public.attendance_punches_y2026m04_tenant_id_id_occurred_at_key;


-- Name: attendance_punches_y2026m04_tenant_id_idempotency_key_occur_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_punches__idempotency_key ATTACH PARTITION public.attendance_punches_y2026m04_tenant_id_idempotency_key_occur_key;


-- Name: attendance_punches_y2026m05_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_attendance_punches ATTACH PARTITION public.attendance_punches_y2026m05_pkey;


-- Name: attendance_punches_y2026m05_tenant_id_approval_request_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_punches__approval_request_id ATTACH PARTITION public.attendance_punches_y2026m05_tenant_id_approval_request_id_idx;


-- Name: attendance_punches_y2026m05_tenant_id_device_id_external_id_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_punches__device_external_id ATTACH PARTITION public.attendance_punches_y2026m05_tenant_id_device_id_external_id_key;


-- Name: attendance_punches_y2026m05_tenant_id_employee_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_punches__employee_id ATTACH PARTITION public.attendance_punches_y2026m05_tenant_id_employee_id_idx;


-- Name: attendance_punches_y2026m05_tenant_id_employee_id_occurred__idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_punches__employee_occurred_at ATTACH PARTITION public.attendance_punches_y2026m05_tenant_id_employee_id_occurred__idx;


-- Name: attendance_punches_y2026m05_tenant_id_id_occurred_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_punches__tenant_id ATTACH PARTITION public.attendance_punches_y2026m05_tenant_id_id_occurred_at_key;


-- Name: attendance_punches_y2026m05_tenant_id_idempotency_key_occur_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_punches__idempotency_key ATTACH PARTITION public.attendance_punches_y2026m05_tenant_id_idempotency_key_occur_key;


-- Name: attendance_punches_y2026m06_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_attendance_punches ATTACH PARTITION public.attendance_punches_y2026m06_pkey;


-- Name: attendance_punches_y2026m06_tenant_id_approval_request_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_punches__approval_request_id ATTACH PARTITION public.attendance_punches_y2026m06_tenant_id_approval_request_id_idx;


-- Name: attendance_punches_y2026m06_tenant_id_device_id_external_id_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_punches__device_external_id ATTACH PARTITION public.attendance_punches_y2026m06_tenant_id_device_id_external_id_key;


-- Name: attendance_punches_y2026m06_tenant_id_employee_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_punches__employee_id ATTACH PARTITION public.attendance_punches_y2026m06_tenant_id_employee_id_idx;


-- Name: attendance_punches_y2026m06_tenant_id_employee_id_occurred__idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_punches__employee_occurred_at ATTACH PARTITION public.attendance_punches_y2026m06_tenant_id_employee_id_occurred__idx;


-- Name: attendance_punches_y2026m06_tenant_id_id_occurred_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_punches__tenant_id ATTACH PARTITION public.attendance_punches_y2026m06_tenant_id_id_occurred_at_key;


-- Name: attendance_punches_y2026m06_tenant_id_idempotency_key_occur_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_punches__idempotency_key ATTACH PARTITION public.attendance_punches_y2026m06_tenant_id_idempotency_key_occur_key;


-- Name: attendance_punches_y2026m07_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_attendance_punches ATTACH PARTITION public.attendance_punches_y2026m07_pkey;


-- Name: attendance_punches_y2026m07_tenant_id_approval_request_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_punches__approval_request_id ATTACH PARTITION public.attendance_punches_y2026m07_tenant_id_approval_request_id_idx;


-- Name: attendance_punches_y2026m07_tenant_id_device_id_external_id_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_punches__device_external_id ATTACH PARTITION public.attendance_punches_y2026m07_tenant_id_device_id_external_id_key;


-- Name: attendance_punches_y2026m07_tenant_id_employee_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_punches__employee_id ATTACH PARTITION public.attendance_punches_y2026m07_tenant_id_employee_id_idx;


-- Name: attendance_punches_y2026m07_tenant_id_employee_id_occurred__idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_punches__employee_occurred_at ATTACH PARTITION public.attendance_punches_y2026m07_tenant_id_employee_id_occurred__idx;


-- Name: attendance_punches_y2026m07_tenant_id_id_occurred_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_punches__tenant_id ATTACH PARTITION public.attendance_punches_y2026m07_tenant_id_id_occurred_at_key;


-- Name: attendance_punches_y2026m07_tenant_id_idempotency_key_occur_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_punches__idempotency_key ATTACH PARTITION public.attendance_punches_y2026m07_tenant_id_idempotency_key_occur_key;


-- Name: attendance_punches_y2026m08_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_attendance_punches ATTACH PARTITION public.attendance_punches_y2026m08_pkey;


-- Name: attendance_punches_y2026m08_tenant_id_approval_request_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_punches__approval_request_id ATTACH PARTITION public.attendance_punches_y2026m08_tenant_id_approval_request_id_idx;


-- Name: attendance_punches_y2026m08_tenant_id_device_id_external_id_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_punches__device_external_id ATTACH PARTITION public.attendance_punches_y2026m08_tenant_id_device_id_external_id_key;


-- Name: attendance_punches_y2026m08_tenant_id_employee_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_punches__employee_id ATTACH PARTITION public.attendance_punches_y2026m08_tenant_id_employee_id_idx;


-- Name: attendance_punches_y2026m08_tenant_id_employee_id_occurred__idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_punches__employee_occurred_at ATTACH PARTITION public.attendance_punches_y2026m08_tenant_id_employee_id_occurred__idx;


-- Name: attendance_punches_y2026m08_tenant_id_id_occurred_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_punches__tenant_id ATTACH PARTITION public.attendance_punches_y2026m08_tenant_id_id_occurred_at_key;


-- Name: attendance_punches_y2026m08_tenant_id_idempotency_key_occur_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_punches__idempotency_key ATTACH PARTITION public.attendance_punches_y2026m08_tenant_id_idempotency_key_occur_key;


-- Name: attendance_punches_y2026m09_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_attendance_punches ATTACH PARTITION public.attendance_punches_y2026m09_pkey;


-- Name: attendance_punches_y2026m09_tenant_id_approval_request_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_punches__approval_request_id ATTACH PARTITION public.attendance_punches_y2026m09_tenant_id_approval_request_id_idx;


-- Name: attendance_punches_y2026m09_tenant_id_device_id_external_id_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_punches__device_external_id ATTACH PARTITION public.attendance_punches_y2026m09_tenant_id_device_id_external_id_key;


-- Name: attendance_punches_y2026m09_tenant_id_employee_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_punches__employee_id ATTACH PARTITION public.attendance_punches_y2026m09_tenant_id_employee_id_idx;


-- Name: attendance_punches_y2026m09_tenant_id_employee_id_occurred__idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_punches__employee_occurred_at ATTACH PARTITION public.attendance_punches_y2026m09_tenant_id_employee_id_occurred__idx;


-- Name: attendance_punches_y2026m09_tenant_id_id_occurred_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_punches__tenant_id ATTACH PARTITION public.attendance_punches_y2026m09_tenant_id_id_occurred_at_key;


-- Name: attendance_punches_y2026m09_tenant_id_idempotency_key_occur_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_punches__idempotency_key ATTACH PARTITION public.attendance_punches_y2026m09_tenant_id_idempotency_key_occur_key;


-- Name: attendance_punches_y2026m10_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_attendance_punches ATTACH PARTITION public.attendance_punches_y2026m10_pkey;


-- Name: attendance_punches_y2026m10_tenant_id_approval_request_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_punches__approval_request_id ATTACH PARTITION public.attendance_punches_y2026m10_tenant_id_approval_request_id_idx;


-- Name: attendance_punches_y2026m10_tenant_id_device_id_external_id_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_punches__device_external_id ATTACH PARTITION public.attendance_punches_y2026m10_tenant_id_device_id_external_id_key;


-- Name: attendance_punches_y2026m10_tenant_id_employee_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_punches__employee_id ATTACH PARTITION public.attendance_punches_y2026m10_tenant_id_employee_id_idx;


-- Name: attendance_punches_y2026m10_tenant_id_employee_id_occurred__idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_punches__employee_occurred_at ATTACH PARTITION public.attendance_punches_y2026m10_tenant_id_employee_id_occurred__idx;


-- Name: attendance_punches_y2026m10_tenant_id_id_occurred_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_punches__tenant_id ATTACH PARTITION public.attendance_punches_y2026m10_tenant_id_id_occurred_at_key;


-- Name: attendance_punches_y2026m10_tenant_id_idempotency_key_occur_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_punches__idempotency_key ATTACH PARTITION public.attendance_punches_y2026m10_tenant_id_idempotency_key_occur_key;


-- Name: attendance_punches_y2026m11_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_attendance_punches ATTACH PARTITION public.attendance_punches_y2026m11_pkey;


-- Name: attendance_punches_y2026m11_tenant_id_approval_request_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_punches__approval_request_id ATTACH PARTITION public.attendance_punches_y2026m11_tenant_id_approval_request_id_idx;


-- Name: attendance_punches_y2026m11_tenant_id_device_id_external_id_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_punches__device_external_id ATTACH PARTITION public.attendance_punches_y2026m11_tenant_id_device_id_external_id_key;


-- Name: attendance_punches_y2026m11_tenant_id_employee_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_punches__employee_id ATTACH PARTITION public.attendance_punches_y2026m11_tenant_id_employee_id_idx;


-- Name: attendance_punches_y2026m11_tenant_id_employee_id_occurred__idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_punches__employee_occurred_at ATTACH PARTITION public.attendance_punches_y2026m11_tenant_id_employee_id_occurred__idx;


-- Name: attendance_punches_y2026m11_tenant_id_id_occurred_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_punches__tenant_id ATTACH PARTITION public.attendance_punches_y2026m11_tenant_id_id_occurred_at_key;


-- Name: attendance_punches_y2026m11_tenant_id_idempotency_key_occur_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_punches__idempotency_key ATTACH PARTITION public.attendance_punches_y2026m11_tenant_id_idempotency_key_occur_key;


-- Name: attendance_punches_y2026m12_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_attendance_punches ATTACH PARTITION public.attendance_punches_y2026m12_pkey;


-- Name: attendance_punches_y2026m12_tenant_id_approval_request_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_punches__approval_request_id ATTACH PARTITION public.attendance_punches_y2026m12_tenant_id_approval_request_id_idx;


-- Name: attendance_punches_y2026m12_tenant_id_device_id_external_id_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_punches__device_external_id ATTACH PARTITION public.attendance_punches_y2026m12_tenant_id_device_id_external_id_key;


-- Name: attendance_punches_y2026m12_tenant_id_employee_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_punches__employee_id ATTACH PARTITION public.attendance_punches_y2026m12_tenant_id_employee_id_idx;


-- Name: attendance_punches_y2026m12_tenant_id_employee_id_occurred__idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_attendance_punches__employee_occurred_at ATTACH PARTITION public.attendance_punches_y2026m12_tenant_id_employee_id_occurred__idx;


-- Name: attendance_punches_y2026m12_tenant_id_id_occurred_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_punches__tenant_id ATTACH PARTITION public.attendance_punches_y2026m12_tenant_id_id_occurred_at_key;


-- Name: attendance_punches_y2026m12_tenant_id_idempotency_key_occur_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_attendance_punches__idempotency_key ATTACH PARTITION public.attendance_punches_y2026m12_tenant_id_idempotency_key_occur_key;


-- Name: audit_logs_default_chain_key_seq_created_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_audit_logs__chain_seq ATTACH PARTITION public.audit_logs_default_chain_key_seq_created_at_key;


-- Name: audit_logs_default_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_audit_logs ATTACH PARTITION public.audit_logs_default_pkey;


-- Name: audit_logs_default_tenant_id_correlation_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_audit_logs__correlation_id ATTACH PARTITION public.audit_logs_default_tenant_id_correlation_id_idx;


-- Name: audit_logs_default_tenant_id_id_created_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_audit_logs__tenant_id ATTACH PARTITION public.audit_logs_default_tenant_id_id_created_at_key;


-- Name: audit_logs_y2025m10_chain_key_seq_created_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_audit_logs__chain_seq ATTACH PARTITION public.audit_logs_y2025m10_chain_key_seq_created_at_key;


-- Name: audit_logs_y2025m10_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_audit_logs ATTACH PARTITION public.audit_logs_y2025m10_pkey;


-- Name: audit_logs_y2025m10_tenant_id_correlation_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_audit_logs__correlation_id ATTACH PARTITION public.audit_logs_y2025m10_tenant_id_correlation_id_idx;


-- Name: audit_logs_y2025m10_tenant_id_id_created_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_audit_logs__tenant_id ATTACH PARTITION public.audit_logs_y2025m10_tenant_id_id_created_at_key;


-- Name: audit_logs_y2025m11_chain_key_seq_created_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_audit_logs__chain_seq ATTACH PARTITION public.audit_logs_y2025m11_chain_key_seq_created_at_key;


-- Name: audit_logs_y2025m11_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_audit_logs ATTACH PARTITION public.audit_logs_y2025m11_pkey;


-- Name: audit_logs_y2025m11_tenant_id_correlation_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_audit_logs__correlation_id ATTACH PARTITION public.audit_logs_y2025m11_tenant_id_correlation_id_idx;


-- Name: audit_logs_y2025m11_tenant_id_id_created_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_audit_logs__tenant_id ATTACH PARTITION public.audit_logs_y2025m11_tenant_id_id_created_at_key;


-- Name: audit_logs_y2025m12_chain_key_seq_created_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_audit_logs__chain_seq ATTACH PARTITION public.audit_logs_y2025m12_chain_key_seq_created_at_key;


-- Name: audit_logs_y2025m12_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_audit_logs ATTACH PARTITION public.audit_logs_y2025m12_pkey;


-- Name: audit_logs_y2025m12_tenant_id_correlation_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_audit_logs__correlation_id ATTACH PARTITION public.audit_logs_y2025m12_tenant_id_correlation_id_idx;


-- Name: audit_logs_y2025m12_tenant_id_id_created_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_audit_logs__tenant_id ATTACH PARTITION public.audit_logs_y2025m12_tenant_id_id_created_at_key;


-- Name: audit_logs_y2026m01_chain_key_seq_created_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_audit_logs__chain_seq ATTACH PARTITION public.audit_logs_y2026m01_chain_key_seq_created_at_key;


-- Name: audit_logs_y2026m01_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_audit_logs ATTACH PARTITION public.audit_logs_y2026m01_pkey;


-- Name: audit_logs_y2026m01_tenant_id_correlation_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_audit_logs__correlation_id ATTACH PARTITION public.audit_logs_y2026m01_tenant_id_correlation_id_idx;


-- Name: audit_logs_y2026m01_tenant_id_id_created_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_audit_logs__tenant_id ATTACH PARTITION public.audit_logs_y2026m01_tenant_id_id_created_at_key;


-- Name: audit_logs_y2026m02_chain_key_seq_created_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_audit_logs__chain_seq ATTACH PARTITION public.audit_logs_y2026m02_chain_key_seq_created_at_key;


-- Name: audit_logs_y2026m02_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_audit_logs ATTACH PARTITION public.audit_logs_y2026m02_pkey;


-- Name: audit_logs_y2026m02_tenant_id_correlation_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_audit_logs__correlation_id ATTACH PARTITION public.audit_logs_y2026m02_tenant_id_correlation_id_idx;


-- Name: audit_logs_y2026m02_tenant_id_id_created_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_audit_logs__tenant_id ATTACH PARTITION public.audit_logs_y2026m02_tenant_id_id_created_at_key;


-- Name: audit_logs_y2026m03_chain_key_seq_created_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_audit_logs__chain_seq ATTACH PARTITION public.audit_logs_y2026m03_chain_key_seq_created_at_key;


-- Name: audit_logs_y2026m03_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_audit_logs ATTACH PARTITION public.audit_logs_y2026m03_pkey;


-- Name: audit_logs_y2026m03_tenant_id_correlation_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_audit_logs__correlation_id ATTACH PARTITION public.audit_logs_y2026m03_tenant_id_correlation_id_idx;


-- Name: audit_logs_y2026m03_tenant_id_id_created_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_audit_logs__tenant_id ATTACH PARTITION public.audit_logs_y2026m03_tenant_id_id_created_at_key;


-- Name: audit_logs_y2026m04_chain_key_seq_created_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_audit_logs__chain_seq ATTACH PARTITION public.audit_logs_y2026m04_chain_key_seq_created_at_key;


-- Name: audit_logs_y2026m04_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_audit_logs ATTACH PARTITION public.audit_logs_y2026m04_pkey;


-- Name: audit_logs_y2026m04_tenant_id_correlation_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_audit_logs__correlation_id ATTACH PARTITION public.audit_logs_y2026m04_tenant_id_correlation_id_idx;


-- Name: audit_logs_y2026m04_tenant_id_id_created_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_audit_logs__tenant_id ATTACH PARTITION public.audit_logs_y2026m04_tenant_id_id_created_at_key;


-- Name: audit_logs_y2026m05_chain_key_seq_created_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_audit_logs__chain_seq ATTACH PARTITION public.audit_logs_y2026m05_chain_key_seq_created_at_key;


-- Name: audit_logs_y2026m05_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_audit_logs ATTACH PARTITION public.audit_logs_y2026m05_pkey;


-- Name: audit_logs_y2026m05_tenant_id_correlation_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_audit_logs__correlation_id ATTACH PARTITION public.audit_logs_y2026m05_tenant_id_correlation_id_idx;


-- Name: audit_logs_y2026m05_tenant_id_id_created_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_audit_logs__tenant_id ATTACH PARTITION public.audit_logs_y2026m05_tenant_id_id_created_at_key;


-- Name: audit_logs_y2026m06_chain_key_seq_created_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_audit_logs__chain_seq ATTACH PARTITION public.audit_logs_y2026m06_chain_key_seq_created_at_key;


-- Name: audit_logs_y2026m06_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_audit_logs ATTACH PARTITION public.audit_logs_y2026m06_pkey;


-- Name: audit_logs_y2026m06_tenant_id_correlation_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_audit_logs__correlation_id ATTACH PARTITION public.audit_logs_y2026m06_tenant_id_correlation_id_idx;


-- Name: audit_logs_y2026m06_tenant_id_id_created_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_audit_logs__tenant_id ATTACH PARTITION public.audit_logs_y2026m06_tenant_id_id_created_at_key;


-- Name: audit_logs_y2026m07_chain_key_seq_created_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_audit_logs__chain_seq ATTACH PARTITION public.audit_logs_y2026m07_chain_key_seq_created_at_key;


-- Name: audit_logs_y2026m07_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_audit_logs ATTACH PARTITION public.audit_logs_y2026m07_pkey;


-- Name: audit_logs_y2026m07_tenant_id_correlation_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_audit_logs__correlation_id ATTACH PARTITION public.audit_logs_y2026m07_tenant_id_correlation_id_idx;


-- Name: audit_logs_y2026m07_tenant_id_id_created_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_audit_logs__tenant_id ATTACH PARTITION public.audit_logs_y2026m07_tenant_id_id_created_at_key;


-- Name: audit_logs_y2026m08_chain_key_seq_created_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_audit_logs__chain_seq ATTACH PARTITION public.audit_logs_y2026m08_chain_key_seq_created_at_key;


-- Name: audit_logs_y2026m08_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_audit_logs ATTACH PARTITION public.audit_logs_y2026m08_pkey;


-- Name: audit_logs_y2026m08_tenant_id_correlation_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_audit_logs__correlation_id ATTACH PARTITION public.audit_logs_y2026m08_tenant_id_correlation_id_idx;


-- Name: audit_logs_y2026m08_tenant_id_id_created_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_audit_logs__tenant_id ATTACH PARTITION public.audit_logs_y2026m08_tenant_id_id_created_at_key;


-- Name: audit_logs_y2026m09_chain_key_seq_created_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_audit_logs__chain_seq ATTACH PARTITION public.audit_logs_y2026m09_chain_key_seq_created_at_key;


-- Name: audit_logs_y2026m09_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_audit_logs ATTACH PARTITION public.audit_logs_y2026m09_pkey;


-- Name: audit_logs_y2026m09_tenant_id_correlation_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_audit_logs__correlation_id ATTACH PARTITION public.audit_logs_y2026m09_tenant_id_correlation_id_idx;


-- Name: audit_logs_y2026m09_tenant_id_id_created_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_audit_logs__tenant_id ATTACH PARTITION public.audit_logs_y2026m09_tenant_id_id_created_at_key;


-- Name: audit_logs_y2026m10_chain_key_seq_created_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_audit_logs__chain_seq ATTACH PARTITION public.audit_logs_y2026m10_chain_key_seq_created_at_key;


-- Name: audit_logs_y2026m10_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_audit_logs ATTACH PARTITION public.audit_logs_y2026m10_pkey;


-- Name: audit_logs_y2026m10_tenant_id_correlation_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_audit_logs__correlation_id ATTACH PARTITION public.audit_logs_y2026m10_tenant_id_correlation_id_idx;


-- Name: audit_logs_y2026m10_tenant_id_id_created_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_audit_logs__tenant_id ATTACH PARTITION public.audit_logs_y2026m10_tenant_id_id_created_at_key;


-- Name: audit_logs_y2026m11_chain_key_seq_created_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_audit_logs__chain_seq ATTACH PARTITION public.audit_logs_y2026m11_chain_key_seq_created_at_key;


-- Name: audit_logs_y2026m11_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_audit_logs ATTACH PARTITION public.audit_logs_y2026m11_pkey;


-- Name: audit_logs_y2026m11_tenant_id_correlation_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_audit_logs__correlation_id ATTACH PARTITION public.audit_logs_y2026m11_tenant_id_correlation_id_idx;


-- Name: audit_logs_y2026m11_tenant_id_id_created_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_audit_logs__tenant_id ATTACH PARTITION public.audit_logs_y2026m11_tenant_id_id_created_at_key;


-- Name: audit_logs_y2026m12_chain_key_seq_created_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_audit_logs__chain_seq ATTACH PARTITION public.audit_logs_y2026m12_chain_key_seq_created_at_key;


-- Name: audit_logs_y2026m12_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_audit_logs ATTACH PARTITION public.audit_logs_y2026m12_pkey;


-- Name: audit_logs_y2026m12_tenant_id_correlation_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_audit_logs__correlation_id ATTACH PARTITION public.audit_logs_y2026m12_tenant_id_correlation_id_idx;


-- Name: audit_logs_y2026m12_tenant_id_id_created_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_audit_logs__tenant_id ATTACH PARTITION public.audit_logs_y2026m12_tenant_id_id_created_at_key;


-- Name: background_job_items_default_job_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_background_job_items__job_id ATTACH PARTITION public.background_job_items_default_job_id_idx;


-- Name: background_job_items_default_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_background_job_items ATTACH PARTITION public.background_job_items_default_pkey;


-- Name: background_job_items_default_tenant_id_id_created_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_background_job_items__tenant_id ATTACH PARTITION public.background_job_items_default_tenant_id_id_created_at_key;


-- Name: background_job_items_y2025m10_job_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_background_job_items__job_id ATTACH PARTITION public.background_job_items_y2025m10_job_id_idx;


-- Name: background_job_items_y2025m10_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_background_job_items ATTACH PARTITION public.background_job_items_y2025m10_pkey;


-- Name: background_job_items_y2025m10_tenant_id_id_created_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_background_job_items__tenant_id ATTACH PARTITION public.background_job_items_y2025m10_tenant_id_id_created_at_key;


-- Name: background_job_items_y2025m11_job_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_background_job_items__job_id ATTACH PARTITION public.background_job_items_y2025m11_job_id_idx;


-- Name: background_job_items_y2025m11_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_background_job_items ATTACH PARTITION public.background_job_items_y2025m11_pkey;


-- Name: background_job_items_y2025m11_tenant_id_id_created_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_background_job_items__tenant_id ATTACH PARTITION public.background_job_items_y2025m11_tenant_id_id_created_at_key;


-- Name: background_job_items_y2025m12_job_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_background_job_items__job_id ATTACH PARTITION public.background_job_items_y2025m12_job_id_idx;


-- Name: background_job_items_y2025m12_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_background_job_items ATTACH PARTITION public.background_job_items_y2025m12_pkey;


-- Name: background_job_items_y2025m12_tenant_id_id_created_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_background_job_items__tenant_id ATTACH PARTITION public.background_job_items_y2025m12_tenant_id_id_created_at_key;


-- Name: background_job_items_y2026m01_job_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_background_job_items__job_id ATTACH PARTITION public.background_job_items_y2026m01_job_id_idx;


-- Name: background_job_items_y2026m01_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_background_job_items ATTACH PARTITION public.background_job_items_y2026m01_pkey;


-- Name: background_job_items_y2026m01_tenant_id_id_created_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_background_job_items__tenant_id ATTACH PARTITION public.background_job_items_y2026m01_tenant_id_id_created_at_key;


-- Name: background_job_items_y2026m02_job_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_background_job_items__job_id ATTACH PARTITION public.background_job_items_y2026m02_job_id_idx;


-- Name: background_job_items_y2026m02_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_background_job_items ATTACH PARTITION public.background_job_items_y2026m02_pkey;


-- Name: background_job_items_y2026m02_tenant_id_id_created_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_background_job_items__tenant_id ATTACH PARTITION public.background_job_items_y2026m02_tenant_id_id_created_at_key;


-- Name: background_job_items_y2026m03_job_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_background_job_items__job_id ATTACH PARTITION public.background_job_items_y2026m03_job_id_idx;


-- Name: background_job_items_y2026m03_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_background_job_items ATTACH PARTITION public.background_job_items_y2026m03_pkey;


-- Name: background_job_items_y2026m03_tenant_id_id_created_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_background_job_items__tenant_id ATTACH PARTITION public.background_job_items_y2026m03_tenant_id_id_created_at_key;


-- Name: background_job_items_y2026m04_job_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_background_job_items__job_id ATTACH PARTITION public.background_job_items_y2026m04_job_id_idx;


-- Name: background_job_items_y2026m04_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_background_job_items ATTACH PARTITION public.background_job_items_y2026m04_pkey;


-- Name: background_job_items_y2026m04_tenant_id_id_created_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_background_job_items__tenant_id ATTACH PARTITION public.background_job_items_y2026m04_tenant_id_id_created_at_key;


-- Name: background_job_items_y2026m05_job_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_background_job_items__job_id ATTACH PARTITION public.background_job_items_y2026m05_job_id_idx;


-- Name: background_job_items_y2026m05_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_background_job_items ATTACH PARTITION public.background_job_items_y2026m05_pkey;


-- Name: background_job_items_y2026m05_tenant_id_id_created_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_background_job_items__tenant_id ATTACH PARTITION public.background_job_items_y2026m05_tenant_id_id_created_at_key;


-- Name: background_job_items_y2026m06_job_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_background_job_items__job_id ATTACH PARTITION public.background_job_items_y2026m06_job_id_idx;


-- Name: background_job_items_y2026m06_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_background_job_items ATTACH PARTITION public.background_job_items_y2026m06_pkey;


-- Name: background_job_items_y2026m06_tenant_id_id_created_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_background_job_items__tenant_id ATTACH PARTITION public.background_job_items_y2026m06_tenant_id_id_created_at_key;


-- Name: background_job_items_y2026m07_job_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_background_job_items__job_id ATTACH PARTITION public.background_job_items_y2026m07_job_id_idx;


-- Name: background_job_items_y2026m07_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_background_job_items ATTACH PARTITION public.background_job_items_y2026m07_pkey;


-- Name: background_job_items_y2026m07_tenant_id_id_created_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_background_job_items__tenant_id ATTACH PARTITION public.background_job_items_y2026m07_tenant_id_id_created_at_key;


-- Name: background_job_items_y2026m08_job_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_background_job_items__job_id ATTACH PARTITION public.background_job_items_y2026m08_job_id_idx;


-- Name: background_job_items_y2026m08_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_background_job_items ATTACH PARTITION public.background_job_items_y2026m08_pkey;


-- Name: background_job_items_y2026m08_tenant_id_id_created_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_background_job_items__tenant_id ATTACH PARTITION public.background_job_items_y2026m08_tenant_id_id_created_at_key;


-- Name: background_job_items_y2026m09_job_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_background_job_items__job_id ATTACH PARTITION public.background_job_items_y2026m09_job_id_idx;


-- Name: background_job_items_y2026m09_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_background_job_items ATTACH PARTITION public.background_job_items_y2026m09_pkey;


-- Name: background_job_items_y2026m09_tenant_id_id_created_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_background_job_items__tenant_id ATTACH PARTITION public.background_job_items_y2026m09_tenant_id_id_created_at_key;


-- Name: background_job_items_y2026m10_job_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_background_job_items__job_id ATTACH PARTITION public.background_job_items_y2026m10_job_id_idx;


-- Name: background_job_items_y2026m10_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_background_job_items ATTACH PARTITION public.background_job_items_y2026m10_pkey;


-- Name: background_job_items_y2026m10_tenant_id_id_created_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_background_job_items__tenant_id ATTACH PARTITION public.background_job_items_y2026m10_tenant_id_id_created_at_key;


-- Name: background_job_items_y2026m11_job_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_background_job_items__job_id ATTACH PARTITION public.background_job_items_y2026m11_job_id_idx;


-- Name: background_job_items_y2026m11_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_background_job_items ATTACH PARTITION public.background_job_items_y2026m11_pkey;


-- Name: background_job_items_y2026m11_tenant_id_id_created_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_background_job_items__tenant_id ATTACH PARTITION public.background_job_items_y2026m11_tenant_id_id_created_at_key;


-- Name: background_job_items_y2026m12_job_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_background_job_items__job_id ATTACH PARTITION public.background_job_items_y2026m12_job_id_idx;


-- Name: background_job_items_y2026m12_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_background_job_items ATTACH PARTITION public.background_job_items_y2026m12_pkey;


-- Name: background_job_items_y2026m12_tenant_id_id_created_at_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_background_job_items__tenant_id ATTACH PARTITION public.background_job_items_y2026m12_tenant_id_id_created_at_key;


-- Name: timesheet_entries_default_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_timesheet_entries ATTACH PARTITION public.timesheet_entries_default_pkey;


-- Name: timesheet_entries_default_tenant_id_cost_center_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_timesheet_entries__cost_center_id ATTACH PARTITION public.timesheet_entries_default_tenant_id_cost_center_id_idx;


-- Name: timesheet_entries_default_tenant_id_id_work_date_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_timesheet_entries__tenant_id ATTACH PARTITION public.timesheet_entries_default_tenant_id_id_work_date_key;


-- Name: timesheet_entries_default_tenant_id_timesheet_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_timesheet_entries__timesheet_id ATTACH PARTITION public.timesheet_entries_default_tenant_id_timesheet_id_idx;


-- Name: timesheet_entries_y2025m10_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_timesheet_entries ATTACH PARTITION public.timesheet_entries_y2025m10_pkey;


-- Name: timesheet_entries_y2025m10_tenant_id_cost_center_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_timesheet_entries__cost_center_id ATTACH PARTITION public.timesheet_entries_y2025m10_tenant_id_cost_center_id_idx;


-- Name: timesheet_entries_y2025m10_tenant_id_id_work_date_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_timesheet_entries__tenant_id ATTACH PARTITION public.timesheet_entries_y2025m10_tenant_id_id_work_date_key;


-- Name: timesheet_entries_y2025m10_tenant_id_timesheet_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_timesheet_entries__timesheet_id ATTACH PARTITION public.timesheet_entries_y2025m10_tenant_id_timesheet_id_idx;


-- Name: timesheet_entries_y2025m11_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_timesheet_entries ATTACH PARTITION public.timesheet_entries_y2025m11_pkey;


-- Name: timesheet_entries_y2025m11_tenant_id_cost_center_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_timesheet_entries__cost_center_id ATTACH PARTITION public.timesheet_entries_y2025m11_tenant_id_cost_center_id_idx;


-- Name: timesheet_entries_y2025m11_tenant_id_id_work_date_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_timesheet_entries__tenant_id ATTACH PARTITION public.timesheet_entries_y2025m11_tenant_id_id_work_date_key;


-- Name: timesheet_entries_y2025m11_tenant_id_timesheet_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_timesheet_entries__timesheet_id ATTACH PARTITION public.timesheet_entries_y2025m11_tenant_id_timesheet_id_idx;


-- Name: timesheet_entries_y2025m12_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_timesheet_entries ATTACH PARTITION public.timesheet_entries_y2025m12_pkey;


-- Name: timesheet_entries_y2025m12_tenant_id_cost_center_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_timesheet_entries__cost_center_id ATTACH PARTITION public.timesheet_entries_y2025m12_tenant_id_cost_center_id_idx;


-- Name: timesheet_entries_y2025m12_tenant_id_id_work_date_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_timesheet_entries__tenant_id ATTACH PARTITION public.timesheet_entries_y2025m12_tenant_id_id_work_date_key;


-- Name: timesheet_entries_y2025m12_tenant_id_timesheet_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_timesheet_entries__timesheet_id ATTACH PARTITION public.timesheet_entries_y2025m12_tenant_id_timesheet_id_idx;


-- Name: timesheet_entries_y2026m01_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_timesheet_entries ATTACH PARTITION public.timesheet_entries_y2026m01_pkey;


-- Name: timesheet_entries_y2026m01_tenant_id_cost_center_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_timesheet_entries__cost_center_id ATTACH PARTITION public.timesheet_entries_y2026m01_tenant_id_cost_center_id_idx;


-- Name: timesheet_entries_y2026m01_tenant_id_id_work_date_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_timesheet_entries__tenant_id ATTACH PARTITION public.timesheet_entries_y2026m01_tenant_id_id_work_date_key;


-- Name: timesheet_entries_y2026m01_tenant_id_timesheet_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_timesheet_entries__timesheet_id ATTACH PARTITION public.timesheet_entries_y2026m01_tenant_id_timesheet_id_idx;


-- Name: timesheet_entries_y2026m02_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_timesheet_entries ATTACH PARTITION public.timesheet_entries_y2026m02_pkey;


-- Name: timesheet_entries_y2026m02_tenant_id_cost_center_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_timesheet_entries__cost_center_id ATTACH PARTITION public.timesheet_entries_y2026m02_tenant_id_cost_center_id_idx;


-- Name: timesheet_entries_y2026m02_tenant_id_id_work_date_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_timesheet_entries__tenant_id ATTACH PARTITION public.timesheet_entries_y2026m02_tenant_id_id_work_date_key;


-- Name: timesheet_entries_y2026m02_tenant_id_timesheet_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_timesheet_entries__timesheet_id ATTACH PARTITION public.timesheet_entries_y2026m02_tenant_id_timesheet_id_idx;


-- Name: timesheet_entries_y2026m03_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_timesheet_entries ATTACH PARTITION public.timesheet_entries_y2026m03_pkey;


-- Name: timesheet_entries_y2026m03_tenant_id_cost_center_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_timesheet_entries__cost_center_id ATTACH PARTITION public.timesheet_entries_y2026m03_tenant_id_cost_center_id_idx;


-- Name: timesheet_entries_y2026m03_tenant_id_id_work_date_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_timesheet_entries__tenant_id ATTACH PARTITION public.timesheet_entries_y2026m03_tenant_id_id_work_date_key;


-- Name: timesheet_entries_y2026m03_tenant_id_timesheet_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_timesheet_entries__timesheet_id ATTACH PARTITION public.timesheet_entries_y2026m03_tenant_id_timesheet_id_idx;


-- Name: timesheet_entries_y2026m04_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_timesheet_entries ATTACH PARTITION public.timesheet_entries_y2026m04_pkey;


-- Name: timesheet_entries_y2026m04_tenant_id_cost_center_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_timesheet_entries__cost_center_id ATTACH PARTITION public.timesheet_entries_y2026m04_tenant_id_cost_center_id_idx;


-- Name: timesheet_entries_y2026m04_tenant_id_id_work_date_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_timesheet_entries__tenant_id ATTACH PARTITION public.timesheet_entries_y2026m04_tenant_id_id_work_date_key;


-- Name: timesheet_entries_y2026m04_tenant_id_timesheet_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_timesheet_entries__timesheet_id ATTACH PARTITION public.timesheet_entries_y2026m04_tenant_id_timesheet_id_idx;


-- Name: timesheet_entries_y2026m05_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_timesheet_entries ATTACH PARTITION public.timesheet_entries_y2026m05_pkey;


-- Name: timesheet_entries_y2026m05_tenant_id_cost_center_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_timesheet_entries__cost_center_id ATTACH PARTITION public.timesheet_entries_y2026m05_tenant_id_cost_center_id_idx;


-- Name: timesheet_entries_y2026m05_tenant_id_id_work_date_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_timesheet_entries__tenant_id ATTACH PARTITION public.timesheet_entries_y2026m05_tenant_id_id_work_date_key;


-- Name: timesheet_entries_y2026m05_tenant_id_timesheet_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_timesheet_entries__timesheet_id ATTACH PARTITION public.timesheet_entries_y2026m05_tenant_id_timesheet_id_idx;


-- Name: timesheet_entries_y2026m06_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_timesheet_entries ATTACH PARTITION public.timesheet_entries_y2026m06_pkey;


-- Name: timesheet_entries_y2026m06_tenant_id_cost_center_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_timesheet_entries__cost_center_id ATTACH PARTITION public.timesheet_entries_y2026m06_tenant_id_cost_center_id_idx;


-- Name: timesheet_entries_y2026m06_tenant_id_id_work_date_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_timesheet_entries__tenant_id ATTACH PARTITION public.timesheet_entries_y2026m06_tenant_id_id_work_date_key;


-- Name: timesheet_entries_y2026m06_tenant_id_timesheet_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_timesheet_entries__timesheet_id ATTACH PARTITION public.timesheet_entries_y2026m06_tenant_id_timesheet_id_idx;


-- Name: timesheet_entries_y2026m07_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_timesheet_entries ATTACH PARTITION public.timesheet_entries_y2026m07_pkey;


-- Name: timesheet_entries_y2026m07_tenant_id_cost_center_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_timesheet_entries__cost_center_id ATTACH PARTITION public.timesheet_entries_y2026m07_tenant_id_cost_center_id_idx;


-- Name: timesheet_entries_y2026m07_tenant_id_id_work_date_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_timesheet_entries__tenant_id ATTACH PARTITION public.timesheet_entries_y2026m07_tenant_id_id_work_date_key;


-- Name: timesheet_entries_y2026m07_tenant_id_timesheet_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_timesheet_entries__timesheet_id ATTACH PARTITION public.timesheet_entries_y2026m07_tenant_id_timesheet_id_idx;


-- Name: timesheet_entries_y2026m08_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_timesheet_entries ATTACH PARTITION public.timesheet_entries_y2026m08_pkey;


-- Name: timesheet_entries_y2026m08_tenant_id_cost_center_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_timesheet_entries__cost_center_id ATTACH PARTITION public.timesheet_entries_y2026m08_tenant_id_cost_center_id_idx;


-- Name: timesheet_entries_y2026m08_tenant_id_id_work_date_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_timesheet_entries__tenant_id ATTACH PARTITION public.timesheet_entries_y2026m08_tenant_id_id_work_date_key;


-- Name: timesheet_entries_y2026m08_tenant_id_timesheet_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_timesheet_entries__timesheet_id ATTACH PARTITION public.timesheet_entries_y2026m08_tenant_id_timesheet_id_idx;


-- Name: timesheet_entries_y2026m09_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_timesheet_entries ATTACH PARTITION public.timesheet_entries_y2026m09_pkey;


-- Name: timesheet_entries_y2026m09_tenant_id_cost_center_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_timesheet_entries__cost_center_id ATTACH PARTITION public.timesheet_entries_y2026m09_tenant_id_cost_center_id_idx;


-- Name: timesheet_entries_y2026m09_tenant_id_id_work_date_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_timesheet_entries__tenant_id ATTACH PARTITION public.timesheet_entries_y2026m09_tenant_id_id_work_date_key;


-- Name: timesheet_entries_y2026m09_tenant_id_timesheet_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_timesheet_entries__timesheet_id ATTACH PARTITION public.timesheet_entries_y2026m09_tenant_id_timesheet_id_idx;


-- Name: timesheet_entries_y2026m10_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_timesheet_entries ATTACH PARTITION public.timesheet_entries_y2026m10_pkey;


-- Name: timesheet_entries_y2026m10_tenant_id_cost_center_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_timesheet_entries__cost_center_id ATTACH PARTITION public.timesheet_entries_y2026m10_tenant_id_cost_center_id_idx;


-- Name: timesheet_entries_y2026m10_tenant_id_id_work_date_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_timesheet_entries__tenant_id ATTACH PARTITION public.timesheet_entries_y2026m10_tenant_id_id_work_date_key;


-- Name: timesheet_entries_y2026m10_tenant_id_timesheet_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_timesheet_entries__timesheet_id ATTACH PARTITION public.timesheet_entries_y2026m10_tenant_id_timesheet_id_idx;


-- Name: timesheet_entries_y2026m11_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_timesheet_entries ATTACH PARTITION public.timesheet_entries_y2026m11_pkey;


-- Name: timesheet_entries_y2026m11_tenant_id_cost_center_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_timesheet_entries__cost_center_id ATTACH PARTITION public.timesheet_entries_y2026m11_tenant_id_cost_center_id_idx;


-- Name: timesheet_entries_y2026m11_tenant_id_id_work_date_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_timesheet_entries__tenant_id ATTACH PARTITION public.timesheet_entries_y2026m11_tenant_id_id_work_date_key;


-- Name: timesheet_entries_y2026m11_tenant_id_timesheet_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_timesheet_entries__timesheet_id ATTACH PARTITION public.timesheet_entries_y2026m11_tenant_id_timesheet_id_idx;


-- Name: timesheet_entries_y2026m12_pkey; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.pk_timesheet_entries ATTACH PARTITION public.timesheet_entries_y2026m12_pkey;


-- Name: timesheet_entries_y2026m12_tenant_id_cost_center_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_timesheet_entries__cost_center_id ATTACH PARTITION public.timesheet_entries_y2026m12_tenant_id_cost_center_id_idx;


-- Name: timesheet_entries_y2026m12_tenant_id_id_work_date_key; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.uq_timesheet_entries__tenant_id ATTACH PARTITION public.timesheet_entries_y2026m12_tenant_id_id_work_date_key;


-- Name: timesheet_entries_y2026m12_tenant_id_timesheet_id_idx; Type: INDEX ATTACH; Schema: public; Owner: -

ALTER INDEX public.ix_timesheet_entries__timesheet_id ATTACH PARTITION public.timesheet_entries_y2026m12_tenant_id_timesheet_id_idx;


-- Name: audit_logs trg_audit_logs_append_only; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_audit_logs_append_only BEFORE DELETE OR UPDATE ON public.audit_logs FOR EACH ROW EXECUTE FUNCTION public.fn_audit_logs_append_only();


-- Name: background_jobs trg_background_jobs_cascade_guard; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_background_jobs_cascade_guard BEFORE DELETE ON public.background_jobs FOR EACH ROW EXECUTE FUNCTION public.fn_cascade_parent_guard('Queued', 'Succeeded', 'Failed', 'Cancelled');


-- Name: employees trg_employees_cascade_guard; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_employees_cascade_guard BEFORE DELETE ON public.employees FOR EACH ROW EXECUTE FUNCTION public.fn_cascade_parent_guard('Draft');


-- Name: eos_calculations trg_eos_calculations_frozen; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_eos_calculations_frozen BEFORE DELETE OR UPDATE ON public.eos_calculations FOR EACH ROW EXECUTE FUNCTION public.fn_eos_calculation_frozen();


-- Name: final_settlement_lines trg_final_settlement_lines_frozen; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_final_settlement_lines_frozen BEFORE DELETE OR UPDATE ON public.final_settlement_lines FOR EACH ROW EXECUTE FUNCTION public.fn_settlement_line_frozen();


-- Name: final_settlements trg_final_settlements_cascade_guard; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_final_settlements_cascade_guard BEFORE DELETE ON public.final_settlements FOR EACH ROW EXECUTE FUNCTION public.fn_cascade_parent_guard('Draft', 'Cancelled');


-- Name: gl_journals trg_gl_balance; Type: TRIGGER; Schema: public; Owner: -

CREATE CONSTRAINT TRIGGER trg_gl_balance AFTER INSERT OR UPDATE ON public.gl_journals DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.fn_gl_balance();


-- Name: gl_journal_lines trg_gl_balance_lines; Type: TRIGGER; Schema: public; Owner: -

CREATE CONSTRAINT TRIGGER trg_gl_balance_lines AFTER INSERT OR DELETE OR UPDATE ON public.gl_journal_lines DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.fn_gl_balance();


-- Name: gl_journals trg_gl_journals_cascade_guard; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_gl_journals_cascade_guard BEFORE DELETE ON public.gl_journals FOR EACH ROW EXECUTE FUNCTION public.fn_cascade_parent_guard('Draft');


-- Name: gosi_filings trg_gosi_filing_totals; Type: TRIGGER; Schema: public; Owner: -

CREATE CONSTRAINT TRIGGER trg_gosi_filing_totals AFTER INSERT OR UPDATE ON public.gosi_filings DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.fn_gosi_filing_totals();


-- Name: gosi_filings trg_gosi_filings_frozen; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_gosi_filings_frozen BEFORE DELETE OR UPDATE ON public.gosi_filings FOR EACH ROW EXECUTE FUNCTION public.fn_gosi_filing_frozen();


-- Name: leave_ledger trg_leave_ledger_append_only; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_leave_ledger_append_only BEFORE DELETE OR UPDATE ON public.leave_ledger FOR EACH ROW EXECUTE FUNCTION public.fn_append_only();


-- Name: loans trg_loan_outstanding; Type: TRIGGER; Schema: public; Owner: -

CREATE CONSTRAINT TRIGGER trg_loan_outstanding AFTER INSERT OR UPDATE ON public.loans DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.fn_loan_outstanding();


-- Name: loan_installments trg_loan_outstanding_installments; Type: TRIGGER; Schema: public; Owner: -

CREATE CONSTRAINT TRIGGER trg_loan_outstanding_installments AFTER INSERT OR DELETE OR UPDATE ON public.loan_installments DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.fn_loan_outstanding();


-- Name: loans trg_loans_cascade_guard; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_loans_cascade_guard BEFORE DELETE ON public.loans FOR EACH ROW EXECUTE FUNCTION public.fn_cascade_parent_guard('PendingApproval', 'Rejected');


-- Name: notifications trg_notifications_cascade_guard; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_notifications_cascade_guard BEFORE DELETE ON public.notifications FOR EACH ROW EXECUTE FUNCTION public.fn_notifications_cascade_guard();


-- Name: payroll_audit_logs trg_payroll_audit_logs_append_only; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_payroll_audit_logs_append_only BEFORE DELETE OR UPDATE ON public.payroll_audit_logs FOR EACH ROW EXECUTE FUNCTION public.fn_append_only();


-- Name: payroll_runs trg_payroll_run_release_claims; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_payroll_run_release_claims AFTER UPDATE ON public.payroll_runs FOR EACH ROW WHEN ((((old.status)::text = 'Processing'::text) AND ((new.status)::text = 'Draft'::text))) EXECUTE FUNCTION public.fn_payroll_run_release_claims();


-- Name: payroll_runs trg_payroll_run_transition; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_payroll_run_transition BEFORE UPDATE ON public.payroll_runs FOR EACH ROW EXECUTE FUNCTION public.fn_payroll_run_transition();


-- Name: payroll_runs trg_payroll_runs_cascade_guard; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_payroll_runs_cascade_guard BEFORE DELETE ON public.payroll_runs FOR EACH ROW EXECUTE FUNCTION public.fn_cascade_parent_guard('Draft', 'Voided');


-- Name: payroll_slip_lines trg_payroll_slip_lines_frozen; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_payroll_slip_lines_frozen BEFORE DELETE OR UPDATE ON public.payroll_slip_lines FOR EACH ROW EXECUTE FUNCTION public.fn_payroll_slip_frozen();


-- Name: payroll_slips trg_payroll_slips_cascade_guard; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_payroll_slips_cascade_guard BEFORE DELETE ON public.payroll_slips FOR EACH ROW EXECUTE FUNCTION public.fn_payroll_slips_cascade_guard();


-- Name: payroll_slips trg_payroll_slips_frozen; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_payroll_slips_frozen BEFORE DELETE OR UPDATE ON public.payroll_slips FOR EACH ROW EXECUTE FUNCTION public.fn_payroll_slip_frozen();


-- Name: retention_purge_audits trg_retention_purge_audits_append_only; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_retention_purge_audits_append_only BEFORE DELETE OR UPDATE ON public.retention_purge_audits FOR EACH ROW EXECUTE FUNCTION public.fn_append_only();


-- Name: approval_actions trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.approval_actions FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: approval_delegations trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.approval_delegations FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: approval_requests trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.approval_requests FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: approval_workflows trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.approval_workflows FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: attendance_days trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.attendance_days FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: attendance_devices trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.attendance_devices FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: auth_sessions trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.auth_sessions FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: auth_tokens trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.auth_tokens FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: background_jobs trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.background_jobs FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: branches trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.branches FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: companies trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.companies FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: company_pay_policies trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.company_pay_policies FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: cost_centers trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.cost_centers FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: data_protection_keys trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.data_protection_keys FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: departments trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.departments FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: designations trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.designations FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: document_templates trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.document_templates FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: employee_assignments trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.employee_assignments FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: employee_bank_accounts trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.employee_bank_accounts FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: employee_contracts trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.employee_contracts FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: employee_documents trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.employee_documents FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: employee_gosi_registrations trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.employee_gosi_registrations FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: employee_salaries trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.employee_salaries FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: employees trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.employees FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: eos_calculations trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.eos_calculations FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: files trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.files FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: final_settlements trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.final_settlements FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: gl_journals trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.gl_journals FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: gl_mappings trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.gl_mappings FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: gl_period_closes trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.gl_period_closes FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: gosi_filings trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.gosi_filings FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: grades trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.grades FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: leave_requests trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.leave_requests FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: leave_types trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.leave_types FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: loan_installments trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.loan_installments FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: loans trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.loans FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: notification_deliveries trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.notification_deliveries FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: notifications trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.notifications FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: number_sequences trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.number_sequences FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: overtime_requests trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.overtime_requests FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: pay_components trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.pay_components FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: payroll_inputs trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.payroll_inputs FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: payroll_issues trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.payroll_issues FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: payroll_runs trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.payroll_runs FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: payroll_slips trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.payroll_slips FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: permission_grantor_records trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.permission_grantor_records FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: platform_users trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.platform_users FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: public_holidays trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.public_holidays FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: retention_policies trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.retention_policies FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: role_permissions trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.role_permissions FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: roles trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.roles FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: shift_assignments trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.shift_assignments FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: shifts trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.shifts FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: tenant_settings trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.tenant_settings FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: tenants trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.tenants FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: timesheet_day_reconciliations trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.timesheet_day_reconciliations FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: timesheet_entries trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.timesheet_entries FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: timesheets trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.timesheets FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: user_roles trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.user_roles FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: users trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.users FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: wps_batches trg_row_stamp; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON public.wps_batches FOR EACH ROW EXECUTE FUNCTION public.fn_row_stamp();


-- Name: payroll_runs trg_run_totals; Type: TRIGGER; Schema: public; Owner: -

CREATE CONSTRAINT TRIGGER trg_run_totals AFTER UPDATE ON public.payroll_runs DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN ((((old.status)::text = 'Processing'::text) AND ((new.status)::text = 'Processed'::text))) EXECUTE FUNCTION public.fn_run_totals();


-- Name: final_settlements trg_settlement_totals; Type: TRIGGER; Schema: public; Owner: -

CREATE CONSTRAINT TRIGGER trg_settlement_totals AFTER INSERT OR UPDATE ON public.final_settlements DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.fn_settlement_totals();


-- Name: final_settlement_lines trg_settlement_totals_lines; Type: TRIGGER; Schema: public; Owner: -

CREATE CONSTRAINT TRIGGER trg_settlement_totals_lines AFTER INSERT OR DELETE OR UPDATE ON public.final_settlement_lines DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.fn_settlement_totals();


-- Name: payroll_slips trg_slip_totals; Type: TRIGGER; Schema: public; Owner: -

CREATE CONSTRAINT TRIGGER trg_slip_totals AFTER INSERT OR UPDATE ON public.payroll_slips DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.fn_slip_totals();


-- Name: payroll_slip_lines trg_slip_totals_lines; Type: TRIGGER; Schema: public; Owner: -

CREATE CONSTRAINT TRIGGER trg_slip_totals_lines AFTER INSERT OR DELETE OR UPDATE ON public.payroll_slip_lines DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.fn_slip_totals();


-- Name: statutory_rules trg_statutory_rules_cascade_guard; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_statutory_rules_cascade_guard BEFORE DELETE ON public.statutory_rules FOR EACH ROW EXECUTE FUNCTION public.fn_statutory_rules_cascade_guard();


-- Name: timesheets trg_timesheet_minutes; Type: TRIGGER; Schema: public; Owner: -

CREATE CONSTRAINT TRIGGER trg_timesheet_minutes AFTER INSERT OR UPDATE ON public.timesheets DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.fn_timesheet_minutes();


-- Name: timesheet_entries trg_timesheet_minutes_entries; Type: TRIGGER; Schema: public; Owner: -

CREATE CONSTRAINT TRIGGER trg_timesheet_minutes_entries AFTER INSERT OR DELETE OR UPDATE ON public.timesheet_entries DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.fn_timesheet_minutes();


-- Name: timesheets trg_timesheets_cascade_guard; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_timesheets_cascade_guard BEFORE DELETE ON public.timesheets FOR EACH ROW EXECUTE FUNCTION public.fn_cascade_parent_guard('Draft', 'Rejected');


-- Name: wps_batches trg_wps_batches_cascade_guard; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_wps_batches_cascade_guard BEFORE DELETE ON public.wps_batches FOR EACH ROW EXECUTE FUNCTION public.fn_cascade_parent_guard('Generated');


-- Name: wps_lines trg_wps_lines_frozen; Type: TRIGGER; Schema: public; Owner: -

CREATE TRIGGER trg_wps_lines_frozen BEFORE DELETE OR UPDATE ON public.wps_lines FOR EACH ROW EXECUTE FUNCTION public.fn_wps_line_frozen();


-- Name: wps_batches trg_wps_totals; Type: TRIGGER; Schema: public; Owner: -

CREATE CONSTRAINT TRIGGER trg_wps_totals AFTER INSERT OR UPDATE ON public.wps_batches DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.fn_wps_totals();


-- Name: wps_lines trg_wps_totals_lines; Type: TRIGGER; Schema: public; Owner: -

CREATE CONSTRAINT TRIGGER trg_wps_totals_lines AFTER INSERT OR DELETE OR UPDATE ON public.wps_lines DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.fn_wps_totals();


-- Name: approval_actions fk_approval_actions__actor_user_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.approval_actions
    ADD CONSTRAINT fk_approval_actions__actor_user_id FOREIGN KEY (tenant_id, actor_user_id) REFERENCES public.users(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: approval_actions fk_approval_actions__on_behalf_of_user_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.approval_actions
    ADD CONSTRAINT fk_approval_actions__on_behalf_of_user_id FOREIGN KEY (tenant_id, on_behalf_of_user_id) REFERENCES public.users(tenant_id, id) ON UPDATE RESTRICT ON DELETE SET NULL (on_behalf_of_user_id);


-- Name: approval_actions fk_approval_actions__request_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.approval_actions
    ADD CONSTRAINT fk_approval_actions__request_id FOREIGN KEY (tenant_id, request_id) REFERENCES public.approval_requests(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: approval_delegations fk_approval_delegations__delegate_user_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.approval_delegations
    ADD CONSTRAINT fk_approval_delegations__delegate_user_id FOREIGN KEY (tenant_id, delegate_user_id) REFERENCES public.users(tenant_id, id) ON UPDATE RESTRICT ON DELETE CASCADE;


-- Name: approval_delegations fk_approval_delegations__delegator_user_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.approval_delegations
    ADD CONSTRAINT fk_approval_delegations__delegator_user_id FOREIGN KEY (tenant_id, delegator_user_id) REFERENCES public.users(tenant_id, id) ON UPDATE RESTRICT ON DELETE CASCADE;


-- Name: approval_requests fk_approval_requests__employee_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.approval_requests
    ADD CONSTRAINT fk_approval_requests__employee_id FOREIGN KEY (tenant_id, employee_id) REFERENCES public.employees(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: approval_requests fk_approval_requests__requester_user_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.approval_requests
    ADD CONSTRAINT fk_approval_requests__requester_user_id FOREIGN KEY (tenant_id, requester_user_id) REFERENCES public.users(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: approval_requests fk_approval_requests__workflow_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.approval_requests
    ADD CONSTRAINT fk_approval_requests__workflow_id FOREIGN KEY (tenant_id, workflow_id) REFERENCES public.approval_workflows(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: approval_workflows fk_approval_workflows__company_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.approval_workflows
    ADD CONSTRAINT fk_approval_workflows__company_id FOREIGN KEY (tenant_id, company_id) REFERENCES public.companies(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: attendance_days fk_attendance_days__employee_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE public.attendance_days
    ADD CONSTRAINT fk_attendance_days__employee_id FOREIGN KEY (tenant_id, employee_id) REFERENCES public.employees(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: attendance_days fk_attendance_days__locked_run_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE public.attendance_days
    ADD CONSTRAINT fk_attendance_days__locked_run_id FOREIGN KEY (tenant_id, locked_run_id) REFERENCES public.payroll_runs(tenant_id, id) ON UPDATE RESTRICT ON DELETE SET NULL (locked_run_id);


-- Name: attendance_days fk_attendance_days__shift_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE public.attendance_days
    ADD CONSTRAINT fk_attendance_days__shift_id FOREIGN KEY (tenant_id, shift_id) REFERENCES public.shifts(tenant_id, id) ON UPDATE RESTRICT ON DELETE SET NULL (shift_id);


-- Name: attendance_devices fk_attendance_devices__branch_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.attendance_devices
    ADD CONSTRAINT fk_attendance_devices__branch_id FOREIGN KEY (tenant_id, branch_id) REFERENCES public.branches(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: attendance_punches fk_attendance_punches__approval_request_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE public.attendance_punches
    ADD CONSTRAINT fk_attendance_punches__approval_request_id FOREIGN KEY (tenant_id, approval_request_id) REFERENCES public.approval_requests(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: attendance_punches fk_attendance_punches__device_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE public.attendance_punches
    ADD CONSTRAINT fk_attendance_punches__device_id FOREIGN KEY (tenant_id, device_id) REFERENCES public.attendance_devices(tenant_id, id) ON UPDATE RESTRICT ON DELETE SET NULL (device_id);


-- Name: attendance_punches fk_attendance_punches__employee_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE public.attendance_punches
    ADD CONSTRAINT fk_attendance_punches__employee_id FOREIGN KEY (tenant_id, employee_id) REFERENCES public.employees(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: auth_sessions fk_auth_sessions__platform_user_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.auth_sessions
    ADD CONSTRAINT fk_auth_sessions__platform_user_id FOREIGN KEY (platform_user_id) REFERENCES public.platform_users(id) ON UPDATE RESTRICT ON DELETE CASCADE;


-- Name: auth_sessions fk_auth_sessions__user_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.auth_sessions
    ADD CONSTRAINT fk_auth_sessions__user_id FOREIGN KEY (tenant_id, user_id) REFERENCES public.users(tenant_id, id) ON UPDATE RESTRICT ON DELETE CASCADE;


-- Name: auth_tokens fk_auth_tokens__platform_user_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.auth_tokens
    ADD CONSTRAINT fk_auth_tokens__platform_user_id FOREIGN KEY (platform_user_id) REFERENCES public.platform_users(id) ON UPDATE RESTRICT ON DELETE CASCADE;


-- Name: auth_tokens fk_auth_tokens__user_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.auth_tokens
    ADD CONSTRAINT fk_auth_tokens__user_id FOREIGN KEY (tenant_id, user_id) REFERENCES public.users(tenant_id, id) ON UPDATE RESTRICT ON DELETE CASCADE;


-- Name: background_job_items fk_background_job_items__job_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE public.background_job_items
    ADD CONSTRAINT fk_background_job_items__job_id FOREIGN KEY (job_id) REFERENCES public.background_jobs(id) ON UPDATE RESTRICT ON DELETE CASCADE;


-- Name: background_jobs fk_background_jobs__source_file_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.background_jobs
    ADD CONSTRAINT fk_background_jobs__source_file_id FOREIGN KEY (source_file_id) REFERENCES public.files(id) ON UPDATE RESTRICT ON DELETE SET NULL;


-- Name: background_jobs fk_background_jobs__tenant_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.background_jobs
    ADD CONSTRAINT fk_background_jobs__tenant_id FOREIGN KEY (tenant_id) REFERENCES public.tenants(id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: branches fk_branches__company_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.branches
    ADD CONSTRAINT fk_branches__company_id FOREIGN KEY (tenant_id, company_id) REFERENCES public.companies(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: companies fk_companies__tenant_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.companies
    ADD CONSTRAINT fk_companies__tenant_id FOREIGN KEY (tenant_id) REFERENCES public.tenants(id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: company_pay_policies fk_company_pay_policies__company_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.company_pay_policies
    ADD CONSTRAINT fk_company_pay_policies__company_id FOREIGN KEY (tenant_id, company_id) REFERENCES public.companies(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: company_pay_policies fk_company_pay_policies__pay_component_code; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.company_pay_policies
    ADD CONSTRAINT fk_company_pay_policies__pay_component_code FOREIGN KEY (tenant_id, pay_component_code) REFERENCES public.pay_components(tenant_id, code) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: cost_centers fk_cost_centers__company_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.cost_centers
    ADD CONSTRAINT fk_cost_centers__company_id FOREIGN KEY (tenant_id, company_id) REFERENCES public.companies(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: cost_centers fk_cost_centers__parent_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.cost_centers
    ADD CONSTRAINT fk_cost_centers__parent_id FOREIGN KEY (tenant_id, parent_id) REFERENCES public.cost_centers(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: departments fk_departments__company_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.departments
    ADD CONSTRAINT fk_departments__company_id FOREIGN KEY (tenant_id, company_id) REFERENCES public.companies(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: departments fk_departments__cost_center_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.departments
    ADD CONSTRAINT fk_departments__cost_center_id FOREIGN KEY (tenant_id, cost_center_id) REFERENCES public.cost_centers(tenant_id, id) ON UPDATE RESTRICT ON DELETE SET NULL (cost_center_id);


-- Name: departments fk_departments__parent_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.departments
    ADD CONSTRAINT fk_departments__parent_id FOREIGN KEY (tenant_id, parent_id) REFERENCES public.departments(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: designations fk_designations__tenant_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.designations
    ADD CONSTRAINT fk_designations__tenant_id FOREIGN KEY (tenant_id) REFERENCES public.tenants(id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: document_templates fk_document_templates__company_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.document_templates
    ADD CONSTRAINT fk_document_templates__company_id FOREIGN KEY (tenant_id, company_id) REFERENCES public.companies(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: employee_assignments fk_employee_assignments__approval_request_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.employee_assignments
    ADD CONSTRAINT fk_employee_assignments__approval_request_id FOREIGN KEY (tenant_id, approval_request_id) REFERENCES public.approval_requests(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: CONSTRAINT fk_employee_assignments__approval_request_id ON employee_assignments; Type: COMMENT; Schema: public; Owner: -

COMMENT ON CONSTRAINT fk_employee_assignments__approval_request_id ON public.employee_assignments IS '§8.2 row 41. RESTRICT: the approval is the assignment''s only state (§11.1).';


-- Name: employee_assignments fk_employee_assignments__branch_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.employee_assignments
    ADD CONSTRAINT fk_employee_assignments__branch_id FOREIGN KEY (tenant_id, branch_id) REFERENCES public.branches(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: employee_assignments fk_employee_assignments__company_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.employee_assignments
    ADD CONSTRAINT fk_employee_assignments__company_id FOREIGN KEY (tenant_id, company_id) REFERENCES public.companies(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: employee_assignments fk_employee_assignments__cost_center_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.employee_assignments
    ADD CONSTRAINT fk_employee_assignments__cost_center_id FOREIGN KEY (tenant_id, cost_center_id) REFERENCES public.cost_centers(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: employee_assignments fk_employee_assignments__department_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.employee_assignments
    ADD CONSTRAINT fk_employee_assignments__department_id FOREIGN KEY (tenant_id, department_id) REFERENCES public.departments(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: employee_assignments fk_employee_assignments__designation_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.employee_assignments
    ADD CONSTRAINT fk_employee_assignments__designation_id FOREIGN KEY (tenant_id, designation_id) REFERENCES public.designations(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: employee_assignments fk_employee_assignments__employee_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.employee_assignments
    ADD CONSTRAINT fk_employee_assignments__employee_id FOREIGN KEY (tenant_id, employee_id) REFERENCES public.employees(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: employee_assignments fk_employee_assignments__grade_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.employee_assignments
    ADD CONSTRAINT fk_employee_assignments__grade_id FOREIGN KEY (tenant_id, grade_id) REFERENCES public.grades(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: employee_assignments fk_employee_assignments__manager_employee_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.employee_assignments
    ADD CONSTRAINT fk_employee_assignments__manager_employee_id FOREIGN KEY (tenant_id, manager_employee_id) REFERENCES public.employees(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: employee_bank_accounts fk_employee_bank_accounts__employee_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.employee_bank_accounts
    ADD CONSTRAINT fk_employee_bank_accounts__employee_id FOREIGN KEY (tenant_id, employee_id) REFERENCES public.employees(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: employee_contracts fk_employee_contracts__document_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.employee_contracts
    ADD CONSTRAINT fk_employee_contracts__document_id FOREIGN KEY (tenant_id, document_id) REFERENCES public.employee_documents(tenant_id, id) ON UPDATE RESTRICT ON DELETE SET NULL (document_id);


-- Name: employee_contracts fk_employee_contracts__employee_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.employee_contracts
    ADD CONSTRAINT fk_employee_contracts__employee_id FOREIGN KEY (tenant_id, employee_id) REFERENCES public.employees(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: employee_documents fk_employee_documents__employee_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.employee_documents
    ADD CONSTRAINT fk_employee_documents__employee_id FOREIGN KEY (tenant_id, employee_id) REFERENCES public.employees(tenant_id, id) ON UPDATE RESTRICT ON DELETE CASCADE;


-- Name: employee_documents fk_employee_documents__file_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.employee_documents
    ADD CONSTRAINT fk_employee_documents__file_id FOREIGN KEY (tenant_id, file_id) REFERENCES public.files(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: employee_documents fk_employee_documents__leave_request_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.employee_documents
    ADD CONSTRAINT fk_employee_documents__leave_request_id FOREIGN KEY (tenant_id, leave_request_id) REFERENCES public.leave_requests(tenant_id, id) ON UPDATE RESTRICT ON DELETE SET NULL (leave_request_id);


-- Name: CONSTRAINT fk_employee_documents__leave_request_id ON employee_documents; Type: COMMENT; Schema: public; Owner: -

COMMENT ON CONSTRAINT fk_employee_documents__leave_request_id ON public.employee_documents IS '§8.2 row 51. SET NULL (leave_request_id) only — a bare SET NULL would null tenant_id, which is NOT NULL.';


-- Name: employee_documents fk_employee_documents__supersedes_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.employee_documents
    ADD CONSTRAINT fk_employee_documents__supersedes_id FOREIGN KEY (tenant_id, supersedes_id) REFERENCES public.employee_documents(tenant_id, id) ON UPDATE RESTRICT ON DELETE SET NULL (supersedes_id);


-- Name: employee_documents fk_employee_documents__template_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.employee_documents
    ADD CONSTRAINT fk_employee_documents__template_id FOREIGN KEY (tenant_id, template_id) REFERENCES public.document_templates(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: employee_gosi_registrations fk_employee_gosi_registrations__company_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.employee_gosi_registrations
    ADD CONSTRAINT fk_employee_gosi_registrations__company_id FOREIGN KEY (tenant_id, company_id) REFERENCES public.companies(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: employee_gosi_registrations fk_employee_gosi_registrations__employee_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.employee_gosi_registrations
    ADD CONSTRAINT fk_employee_gosi_registrations__employee_id FOREIGN KEY (tenant_id, employee_id) REFERENCES public.employees(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: employee_gosi_registrations fk_employee_gosi_registrations__gosi_registration_no; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.employee_gosi_registrations
    ADD CONSTRAINT fk_employee_gosi_registrations__gosi_registration_no FOREIGN KEY (tenant_id, gosi_registration_no) REFERENCES public.companies(tenant_id, gosi_registration_no) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: employee_salaries fk_employee_salaries__approval_request_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.employee_salaries
    ADD CONSTRAINT fk_employee_salaries__approval_request_id FOREIGN KEY (tenant_id, approval_request_id) REFERENCES public.approval_requests(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: CONSTRAINT fk_employee_salaries__approval_request_id ON employee_salaries; Type: COMMENT; Schema: public; Owner: -

COMMENT ON CONSTRAINT fk_employee_salaries__approval_request_id ON public.employee_salaries IS '§8.2 row 43. RESTRICT: the approval is the salary change''s only state (§11.1).';


-- Name: employee_salaries fk_employee_salaries__employee_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.employee_salaries
    ADD CONSTRAINT fk_employee_salaries__employee_id FOREIGN KEY (tenant_id, employee_id) REFERENCES public.employees(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: employees fk_employees__tenant_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.employees
    ADD CONSTRAINT fk_employees__tenant_id FOREIGN KEY (tenant_id) REFERENCES public.tenants(id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: eos_calculations fk_eos_calculations__employee_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.eos_calculations
    ADD CONSTRAINT fk_eos_calculations__employee_id FOREIGN KEY (tenant_id, employee_id) REFERENCES public.employees(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: eos_calculations fk_eos_calculations__settlement_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.eos_calculations
    ADD CONSTRAINT fk_eos_calculations__settlement_id FOREIGN KEY (tenant_id, settlement_id) REFERENCES public.final_settlements(tenant_id, id) ON UPDATE RESTRICT ON DELETE SET NULL (settlement_id);


-- Name: files fk_files__tenant_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.files
    ADD CONSTRAINT fk_files__tenant_id FOREIGN KEY (tenant_id) REFERENCES public.tenants(id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: files fk_files__uploaded_by; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.files
    ADD CONSTRAINT fk_files__uploaded_by FOREIGN KEY (tenant_id, uploaded_by) REFERENCES public.users(tenant_id, id) ON UPDATE RESTRICT ON DELETE SET NULL (uploaded_by);


-- Name: final_settlement_lines fk_final_settlement_lines__loan_installment_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.final_settlement_lines
    ADD CONSTRAINT fk_final_settlement_lines__loan_installment_id FOREIGN KEY (tenant_id, loan_installment_id) REFERENCES public.loan_installments(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: final_settlement_lines fk_final_settlement_lines__settlement_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.final_settlement_lines
    ADD CONSTRAINT fk_final_settlement_lines__settlement_id FOREIGN KEY (tenant_id, settlement_id) REFERENCES public.final_settlements(tenant_id, id) ON UPDATE RESTRICT ON DELETE CASCADE;


-- Name: final_settlements fk_final_settlements__approval_request_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.final_settlements
    ADD CONSTRAINT fk_final_settlements__approval_request_id FOREIGN KEY (tenant_id, approval_request_id) REFERENCES public.approval_requests(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: final_settlements fk_final_settlements__company_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.final_settlements
    ADD CONSTRAINT fk_final_settlements__company_id FOREIGN KEY (tenant_id, company_id) REFERENCES public.companies(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: final_settlements fk_final_settlements__employee_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.final_settlements
    ADD CONSTRAINT fk_final_settlements__employee_id FOREIGN KEY (tenant_id, employee_id) REFERENCES public.employees(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: final_settlements fk_final_settlements__paid_via_run_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.final_settlements
    ADD CONSTRAINT fk_final_settlements__paid_via_run_id FOREIGN KEY (tenant_id, paid_via_run_id) REFERENCES public.payroll_runs(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: gl_journal_lines fk_gl_journal_lines__cost_center_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.gl_journal_lines
    ADD CONSTRAINT fk_gl_journal_lines__cost_center_id FOREIGN KEY (tenant_id, cost_center_id) REFERENCES public.cost_centers(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: gl_journal_lines fk_gl_journal_lines__journal_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.gl_journal_lines
    ADD CONSTRAINT fk_gl_journal_lines__journal_id FOREIGN KEY (tenant_id, journal_id) REFERENCES public.gl_journals(tenant_id, id) ON UPDATE RESTRICT ON DELETE CASCADE;


-- Name: gl_journals fk_gl_journals__company_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.gl_journals
    ADD CONSTRAINT fk_gl_journals__company_id FOREIGN KEY (tenant_id, company_id) REFERENCES public.companies(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: gl_journals fk_gl_journals__file_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.gl_journals
    ADD CONSTRAINT fk_gl_journals__file_id FOREIGN KEY (tenant_id, file_id) REFERENCES public.files(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: gl_journals fk_gl_journals__reversal_of_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.gl_journals
    ADD CONSTRAINT fk_gl_journals__reversal_of_id FOREIGN KEY (tenant_id, reversal_of_id) REFERENCES public.gl_journals(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: gl_mappings fk_gl_mappings__company_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.gl_mappings
    ADD CONSTRAINT fk_gl_mappings__company_id FOREIGN KEY (tenant_id, company_id) REFERENCES public.companies(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: gl_mappings fk_gl_mappings__cost_center_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.gl_mappings
    ADD CONSTRAINT fk_gl_mappings__cost_center_id FOREIGN KEY (tenant_id, cost_center_id) REFERENCES public.cost_centers(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: gl_period_closes fk_gl_period_closes__closed_by; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.gl_period_closes
    ADD CONSTRAINT fk_gl_period_closes__closed_by FOREIGN KEY (tenant_id, closed_by) REFERENCES public.users(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: gl_period_closes fk_gl_period_closes__company_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.gl_period_closes
    ADD CONSTRAINT fk_gl_period_closes__company_id FOREIGN KEY (tenant_id, company_id) REFERENCES public.companies(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: gl_period_closes fk_gl_period_closes__reopened_by; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.gl_period_closes
    ADD CONSTRAINT fk_gl_period_closes__reopened_by FOREIGN KEY (tenant_id, reopened_by) REFERENCES public.users(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: gosi_filings fk_gosi_filings__company_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.gosi_filings
    ADD CONSTRAINT fk_gosi_filings__company_id FOREIGN KEY (tenant_id, company_id) REFERENCES public.companies(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: gosi_filings fk_gosi_filings__file_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.gosi_filings
    ADD CONSTRAINT fk_gosi_filings__file_id FOREIGN KEY (tenant_id, file_id) REFERENCES public.files(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: gosi_filings fk_gosi_filings__filed_by; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.gosi_filings
    ADD CONSTRAINT fk_gosi_filings__filed_by FOREIGN KEY (tenant_id, filed_by) REFERENCES public.users(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: gosi_filings fk_gosi_filings__gosi_registration_no; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.gosi_filings
    ADD CONSTRAINT fk_gosi_filings__gosi_registration_no FOREIGN KEY (tenant_id, gosi_registration_no) REFERENCES public.companies(tenant_id, gosi_registration_no) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: grades fk_grades__tenant_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.grades
    ADD CONSTRAINT fk_grades__tenant_id FOREIGN KEY (tenant_id) REFERENCES public.tenants(id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: leave_ledger fk_leave_ledger__employee_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.leave_ledger
    ADD CONSTRAINT fk_leave_ledger__employee_id FOREIGN KEY (tenant_id, employee_id) REFERENCES public.employees(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: leave_ledger fk_leave_ledger__leave_type_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.leave_ledger
    ADD CONSTRAINT fk_leave_ledger__leave_type_id FOREIGN KEY (tenant_id, leave_type_id) REFERENCES public.leave_types(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: leave_requests fk_leave_requests__approval_request_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.leave_requests
    ADD CONSTRAINT fk_leave_requests__approval_request_id FOREIGN KEY (tenant_id, approval_request_id) REFERENCES public.approval_requests(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: leave_requests fk_leave_requests__employee_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.leave_requests
    ADD CONSTRAINT fk_leave_requests__employee_id FOREIGN KEY (tenant_id, employee_id) REFERENCES public.employees(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: leave_requests fk_leave_requests__leave_type_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.leave_requests
    ADD CONSTRAINT fk_leave_requests__leave_type_id FOREIGN KEY (tenant_id, leave_type_id) REFERENCES public.leave_types(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: leave_types fk_leave_types__tenant_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.leave_types
    ADD CONSTRAINT fk_leave_types__tenant_id FOREIGN KEY (tenant_id) REFERENCES public.tenants(id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: loan_installments fk_loan_installments__loan_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.loan_installments
    ADD CONSTRAINT fk_loan_installments__loan_id FOREIGN KEY (tenant_id, loan_id) REFERENCES public.loans(tenant_id, id) ON UPDATE RESTRICT ON DELETE CASCADE;


-- Name: loans fk_loans__approval_request_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.loans
    ADD CONSTRAINT fk_loans__approval_request_id FOREIGN KEY (tenant_id, approval_request_id) REFERENCES public.approval_requests(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: loans fk_loans__employee_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.loans
    ADD CONSTRAINT fk_loans__employee_id FOREIGN KEY (tenant_id, employee_id) REFERENCES public.employees(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: nitaqat_snapshots fk_nitaqat_snapshots__company_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.nitaqat_snapshots
    ADD CONSTRAINT fk_nitaqat_snapshots__company_id FOREIGN KEY (tenant_id, company_id) REFERENCES public.companies(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: notification_deliveries fk_notification_deliveries__notification_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.notification_deliveries
    ADD CONSTRAINT fk_notification_deliveries__notification_id FOREIGN KEY (tenant_id, notification_id) REFERENCES public.notifications(tenant_id, id) ON UPDATE RESTRICT ON DELETE CASCADE;


-- Name: notifications fk_notifications__user_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.notifications
    ADD CONSTRAINT fk_notifications__user_id FOREIGN KEY (tenant_id, user_id) REFERENCES public.users(tenant_id, id) ON UPDATE RESTRICT ON DELETE CASCADE;


-- Name: number_sequences fk_number_sequences__company_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.number_sequences
    ADD CONSTRAINT fk_number_sequences__company_id FOREIGN KEY (tenant_id, company_id) REFERENCES public.companies(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: number_sequences fk_number_sequences__tenant_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.number_sequences
    ADD CONSTRAINT fk_number_sequences__tenant_id FOREIGN KEY (tenant_id) REFERENCES public.tenants(id) ON UPDATE RESTRICT ON DELETE CASCADE;


-- Name: overtime_requests fk_overtime_requests__approval_request_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.overtime_requests
    ADD CONSTRAINT fk_overtime_requests__approval_request_id FOREIGN KEY (tenant_id, approval_request_id) REFERENCES public.approval_requests(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: overtime_requests fk_overtime_requests__employee_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.overtime_requests
    ADD CONSTRAINT fk_overtime_requests__employee_id FOREIGN KEY (tenant_id, employee_id) REFERENCES public.employees(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: overtime_requests fk_overtime_requests__statutory_rule_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.overtime_requests
    ADD CONSTRAINT fk_overtime_requests__statutory_rule_id FOREIGN KEY (statutory_rule_id) REFERENCES public.statutory_rules(id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: pay_components fk_pay_components__tenant_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.pay_components
    ADD CONSTRAINT fk_pay_components__tenant_id FOREIGN KEY (tenant_id) REFERENCES public.tenants(id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: payroll_inputs fk_payroll_inputs__claimed_by_run_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.payroll_inputs
    ADD CONSTRAINT fk_payroll_inputs__claimed_by_run_id FOREIGN KEY (tenant_id, claimed_by_run_id) REFERENCES public.payroll_runs(tenant_id, id) ON UPDATE RESTRICT ON DELETE SET NULL (claimed_by_run_id);


-- Name: payroll_inputs fk_payroll_inputs__company_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.payroll_inputs
    ADD CONSTRAINT fk_payroll_inputs__company_id FOREIGN KEY (tenant_id, company_id) REFERENCES public.companies(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: payroll_inputs fk_payroll_inputs__consumed_run_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.payroll_inputs
    ADD CONSTRAINT fk_payroll_inputs__consumed_run_id FOREIGN KEY (tenant_id, consumed_run_id) REFERENCES public.payroll_runs(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: payroll_inputs fk_payroll_inputs__cost_center_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.payroll_inputs
    ADD CONSTRAINT fk_payroll_inputs__cost_center_id FOREIGN KEY (tenant_id, cost_center_id) REFERENCES public.cost_centers(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: payroll_inputs fk_payroll_inputs__employee_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.payroll_inputs
    ADD CONSTRAINT fk_payroll_inputs__employee_id FOREIGN KEY (tenant_id, employee_id) REFERENCES public.employees(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: payroll_inputs fk_payroll_inputs__pay_component_code; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.payroll_inputs
    ADD CONSTRAINT fk_payroll_inputs__pay_component_code FOREIGN KEY (tenant_id, pay_component_code) REFERENCES public.pay_components(tenant_id, code) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: payroll_issues fk_payroll_issues__employee_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.payroll_issues
    ADD CONSTRAINT fk_payroll_issues__employee_id FOREIGN KEY (tenant_id, employee_id) REFERENCES public.employees(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: payroll_issues fk_payroll_issues__override_by; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.payroll_issues
    ADD CONSTRAINT fk_payroll_issues__override_by FOREIGN KEY (tenant_id, override_by) REFERENCES public.users(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: payroll_issues fk_payroll_issues__run_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.payroll_issues
    ADD CONSTRAINT fk_payroll_issues__run_id FOREIGN KEY (tenant_id, run_id) REFERENCES public.payroll_runs(tenant_id, id) ON UPDATE RESTRICT ON DELETE CASCADE;


-- Name: payroll_runs fk_payroll_runs__approval_request_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.payroll_runs
    ADD CONSTRAINT fk_payroll_runs__approval_request_id FOREIGN KEY (tenant_id, approval_request_id) REFERENCES public.approval_requests(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: CONSTRAINT fk_payroll_runs__approval_request_id ON payroll_runs; Type: COMMENT; Schema: public; Owner: -

COMMENT ON CONSTRAINT fk_payroll_runs__approval_request_id ON public.payroll_runs IS '§8.2 row 58. RESTRICT: the decision that approved a paid run is audited evidence.';


-- Name: payroll_runs fk_payroll_runs__company_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.payroll_runs
    ADD CONSTRAINT fk_payroll_runs__company_id FOREIGN KEY (tenant_id, company_id) REFERENCES public.companies(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: payroll_runs fk_payroll_runs__parent_run_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.payroll_runs
    ADD CONSTRAINT fk_payroll_runs__parent_run_id FOREIGN KEY (tenant_id, parent_run_id) REFERENCES public.payroll_runs(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: payroll_runs fk_payroll_runs__source_import_job_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.payroll_runs
    ADD CONSTRAINT fk_payroll_runs__source_import_job_id FOREIGN KEY (tenant_id, source_import_job_id) REFERENCES public.background_jobs(tenant_id, id) ON UPDATE RESTRICT ON DELETE SET NULL (source_import_job_id);


-- Name: CONSTRAINT fk_payroll_runs__source_import_job_id ON payroll_runs; Type: COMMENT; Schema: public; Owner: -

COMMENT ON CONSTRAINT fk_payroll_runs__source_import_job_id ON public.payroll_runs IS '§8.2 row 57. COMPOSITE after all: background_jobs already carries UNIQUE (tenant_id, id), which PostgreSQL accepts as an FK target over a nullable tenant_id. NULLS NOT DISTINCT is unnecessary because id is the primary key. A platform-tier job (tenant_id NULL) is therefore unreachable from a tenant run, which is the intended isolation.';


-- Name: payroll_slip_lines fk_payroll_slip_lines__cost_center_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.payroll_slip_lines
    ADD CONSTRAINT fk_payroll_slip_lines__cost_center_id FOREIGN KEY (tenant_id, cost_center_id) REFERENCES public.cost_centers(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: payroll_slip_lines fk_payroll_slip_lines__loan_installment_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.payroll_slip_lines
    ADD CONSTRAINT fk_payroll_slip_lines__loan_installment_id FOREIGN KEY (tenant_id, loan_installment_id) REFERENCES public.loan_installments(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: CONSTRAINT fk_payroll_slip_lines__loan_installment_id ON payroll_slip_lines; Type: COMMENT; Schema: public; Owner: -

COMMENT ON CONSTRAINT fk_payroll_slip_lines__loan_installment_id ON public.payroll_slip_lines IS '§8.2 row 68. The surviving direction of the cycle §8.4 broke; read parent-side, so 040 indexes it.';


-- Name: payroll_slip_lines fk_payroll_slip_lines__pay_component_code; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.payroll_slip_lines
    ADD CONSTRAINT fk_payroll_slip_lines__pay_component_code FOREIGN KEY (tenant_id, pay_component_code) REFERENCES public.pay_components(tenant_id, code) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: payroll_slip_lines fk_payroll_slip_lines__payroll_input_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.payroll_slip_lines
    ADD CONSTRAINT fk_payroll_slip_lines__payroll_input_id FOREIGN KEY (tenant_id, payroll_input_id) REFERENCES public.payroll_inputs(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: payroll_slip_lines fk_payroll_slip_lines__slip_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.payroll_slip_lines
    ADD CONSTRAINT fk_payroll_slip_lines__slip_id FOREIGN KEY (tenant_id, slip_id) REFERENCES public.payroll_slips(tenant_id, id) ON UPDATE RESTRICT ON DELETE CASCADE;


-- Name: payroll_slip_lines fk_payroll_slip_lines__statutory_rule_band_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.payroll_slip_lines
    ADD CONSTRAINT fk_payroll_slip_lines__statutory_rule_band_id FOREIGN KEY (statutory_rule_band_id) REFERENCES public.statutory_rule_bands(id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: payroll_slip_lines fk_payroll_slip_lines__statutory_rule_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.payroll_slip_lines
    ADD CONSTRAINT fk_payroll_slip_lines__statutory_rule_id FOREIGN KEY (statutory_rule_id) REFERENCES public.statutory_rules(id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: payroll_slips fk_payroll_slips__employee_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.payroll_slips
    ADD CONSTRAINT fk_payroll_slips__employee_id FOREIGN KEY (tenant_id, employee_id) REFERENCES public.employees(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: payroll_slips fk_payroll_slips__payslip_file_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.payroll_slips
    ADD CONSTRAINT fk_payroll_slips__payslip_file_id FOREIGN KEY (tenant_id, payslip_file_id) REFERENCES public.files(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: payroll_slips fk_payroll_slips__run_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.payroll_slips
    ADD CONSTRAINT fk_payroll_slips__run_id FOREIGN KEY (tenant_id, run_id) REFERENCES public.payroll_runs(tenant_id, id) ON UPDATE RESTRICT ON DELETE CASCADE;


-- Name: payroll_slips fk_payroll_slips__template_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.payroll_slips
    ADD CONSTRAINT fk_payroll_slips__template_id FOREIGN KEY (tenant_id, template_id) REFERENCES public.document_templates(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: permission_grantor_records fk_permission_grantor_records__granted_by_user_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.permission_grantor_records
    ADD CONSTRAINT fk_permission_grantor_records__granted_by_user_id FOREIGN KEY (tenant_id, granted_by_user_id) REFERENCES public.users(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: permission_grantor_records fk_permission_grantor_records__grantor_user_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.permission_grantor_records
    ADD CONSTRAINT fk_permission_grantor_records__grantor_user_id FOREIGN KEY (tenant_id, grantor_user_id) REFERENCES public.users(tenant_id, id) ON UPDATE RESTRICT ON DELETE CASCADE;


-- Name: permission_grantor_records fk_permission_grantor_records__revoked_by; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.permission_grantor_records
    ADD CONSTRAINT fk_permission_grantor_records__revoked_by FOREIGN KEY (tenant_id, revoked_by) REFERENCES public.users(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: public_holidays fk_public_holidays__tenant_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.public_holidays
    ADD CONSTRAINT fk_public_holidays__tenant_id FOREIGN KEY (tenant_id) REFERENCES public.tenants(id) ON UPDATE RESTRICT ON DELETE CASCADE;


-- Name: retention_policies fk_retention_policies__tenant_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.retention_policies
    ADD CONSTRAINT fk_retention_policies__tenant_id FOREIGN KEY (tenant_id) REFERENCES public.tenants(id) ON UPDATE RESTRICT ON DELETE CASCADE;


-- Name: role_permissions fk_role_permissions__permission_code; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.role_permissions
    ADD CONSTRAINT fk_role_permissions__permission_code FOREIGN KEY (permission_code) REFERENCES public.permissions(code) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: role_permissions fk_role_permissions__role_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.role_permissions
    ADD CONSTRAINT fk_role_permissions__role_id FOREIGN KEY (tenant_id, role_id) REFERENCES public.roles(tenant_id, id) ON UPDATE RESTRICT ON DELETE CASCADE;


-- Name: roles fk_roles__tenant_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.roles
    ADD CONSTRAINT fk_roles__tenant_id FOREIGN KEY (tenant_id) REFERENCES public.tenants(id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: shift_assignments fk_shift_assignments__employee_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.shift_assignments
    ADD CONSTRAINT fk_shift_assignments__employee_id FOREIGN KEY (tenant_id, employee_id) REFERENCES public.employees(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: shift_assignments fk_shift_assignments__shift_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.shift_assignments
    ADD CONSTRAINT fk_shift_assignments__shift_id FOREIGN KEY (tenant_id, shift_id) REFERENCES public.shifts(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: shifts fk_shifts__tenant_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.shifts
    ADD CONSTRAINT fk_shifts__tenant_id FOREIGN KEY (tenant_id) REFERENCES public.tenants(id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: statutory_rule_bands fk_statutory_rule_bands__statutory_rule_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.statutory_rule_bands
    ADD CONSTRAINT fk_statutory_rule_bands__statutory_rule_id FOREIGN KEY (statutory_rule_id) REFERENCES public.statutory_rules(id) ON UPDATE RESTRICT ON DELETE CASCADE;


-- Name: tenant_settings fk_tenant_settings__tenant_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.tenant_settings
    ADD CONSTRAINT fk_tenant_settings__tenant_id FOREIGN KEY (tenant_id) REFERENCES public.tenants(id) ON UPDATE RESTRICT ON DELETE CASCADE;


-- Name: timesheet_day_reconciliations fk_timesheet_day_reconciliations__attendance_day_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_day_reconciliations
    ADD CONSTRAINT fk_timesheet_day_reconciliations__attendance_day_id FOREIGN KEY (tenant_id, attendance_day_id, work_date) REFERENCES public.attendance_days(tenant_id, id, work_date) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: timesheet_day_reconciliations fk_timesheet_day_reconciliations__timesheet_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheet_day_reconciliations
    ADD CONSTRAINT fk_timesheet_day_reconciliations__timesheet_id FOREIGN KEY (tenant_id, timesheet_id) REFERENCES public.timesheets(tenant_id, id) ON UPDATE RESTRICT ON DELETE CASCADE;


-- Name: timesheet_entries fk_timesheet_entries__cost_center_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE public.timesheet_entries
    ADD CONSTRAINT fk_timesheet_entries__cost_center_id FOREIGN KEY (tenant_id, cost_center_id) REFERENCES public.cost_centers(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: timesheet_entries fk_timesheet_entries__timesheet_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE public.timesheet_entries
    ADD CONSTRAINT fk_timesheet_entries__timesheet_id FOREIGN KEY (tenant_id, timesheet_id) REFERENCES public.timesheets(tenant_id, id) ON UPDATE RESTRICT ON DELETE CASCADE;


-- Name: timesheets fk_timesheets__approval_request_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheets
    ADD CONSTRAINT fk_timesheets__approval_request_id FOREIGN KEY (tenant_id, approval_request_id) REFERENCES public.approval_requests(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: timesheets fk_timesheets__company_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheets
    ADD CONSTRAINT fk_timesheets__company_id FOREIGN KEY (tenant_id, company_id) REFERENCES public.companies(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: timesheets fk_timesheets__employee_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheets
    ADD CONSTRAINT fk_timesheets__employee_id FOREIGN KEY (tenant_id, employee_id) REFERENCES public.employees(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: timesheets fk_timesheets__locked_run_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.timesheets
    ADD CONSTRAINT fk_timesheets__locked_run_id FOREIGN KEY (tenant_id, locked_run_id) REFERENCES public.payroll_runs(tenant_id, id) ON UPDATE RESTRICT ON DELETE SET NULL (locked_run_id);


-- Name: user_roles fk_user_roles__granted_by; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.user_roles
    ADD CONSTRAINT fk_user_roles__granted_by FOREIGN KEY (tenant_id, granted_by) REFERENCES public.users(tenant_id, id) ON UPDATE RESTRICT ON DELETE SET NULL (granted_by);


-- Name: user_roles fk_user_roles__role_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.user_roles
    ADD CONSTRAINT fk_user_roles__role_id FOREIGN KEY (tenant_id, role_id) REFERENCES public.roles(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: user_roles fk_user_roles__scope_branch_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.user_roles
    ADD CONSTRAINT fk_user_roles__scope_branch_id FOREIGN KEY (tenant_id, scope_branch_id) REFERENCES public.branches(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: user_roles fk_user_roles__scope_company_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.user_roles
    ADD CONSTRAINT fk_user_roles__scope_company_id FOREIGN KEY (tenant_id, scope_company_id) REFERENCES public.companies(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: user_roles fk_user_roles__scope_department_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.user_roles
    ADD CONSTRAINT fk_user_roles__scope_department_id FOREIGN KEY (tenant_id, scope_department_id) REFERENCES public.departments(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: user_roles fk_user_roles__user_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.user_roles
    ADD CONSTRAINT fk_user_roles__user_id FOREIGN KEY (tenant_id, user_id) REFERENCES public.users(tenant_id, id) ON UPDATE RESTRICT ON DELETE CASCADE;


-- Name: users fk_users__employee_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.users
    ADD CONSTRAINT fk_users__employee_id FOREIGN KEY (tenant_id, employee_id) REFERENCES public.employees(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: users fk_users__tenant_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.users
    ADD CONSTRAINT fk_users__tenant_id FOREIGN KEY (tenant_id) REFERENCES public.tenants(id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: wps_batches fk_wps_batches__company_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.wps_batches
    ADD CONSTRAINT fk_wps_batches__company_id FOREIGN KEY (tenant_id, company_id) REFERENCES public.companies(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: wps_batches fk_wps_batches__file_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.wps_batches
    ADD CONSTRAINT fk_wps_batches__file_id FOREIGN KEY (tenant_id, file_id) REFERENCES public.files(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: wps_batches fk_wps_batches__generated_by; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.wps_batches
    ADD CONSTRAINT fk_wps_batches__generated_by FOREIGN KEY (tenant_id, generated_by) REFERENCES public.users(tenant_id, id) ON UPDATE RESTRICT ON DELETE SET NULL (generated_by);


-- Name: wps_batches fk_wps_batches__resubmission_of_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.wps_batches
    ADD CONSTRAINT fk_wps_batches__resubmission_of_id FOREIGN KEY (tenant_id, resubmission_of_id) REFERENCES public.wps_batches(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: wps_batches fk_wps_batches__run_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.wps_batches
    ADD CONSTRAINT fk_wps_batches__run_id FOREIGN KEY (tenant_id, run_id) REFERENCES public.payroll_runs(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: wps_lines fk_wps_lines__batch_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.wps_lines
    ADD CONSTRAINT fk_wps_lines__batch_id FOREIGN KEY (tenant_id, batch_id) REFERENCES public.wps_batches(tenant_id, id) ON UPDATE RESTRICT ON DELETE CASCADE;


-- Name: wps_lines fk_wps_lines__confirmation_job_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.wps_lines
    ADD CONSTRAINT fk_wps_lines__confirmation_job_id FOREIGN KEY (confirmation_job_id) REFERENCES public.background_jobs(id) ON UPDATE RESTRICT ON DELETE SET NULL;


-- Name: wps_lines fk_wps_lines__employee_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.wps_lines
    ADD CONSTRAINT fk_wps_lines__employee_id FOREIGN KEY (tenant_id, employee_id) REFERENCES public.employees(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: wps_lines fk_wps_lines__slip_id; Type: FK CONSTRAINT; Schema: public; Owner: -

ALTER TABLE ONLY public.wps_lines
    ADD CONSTRAINT fk_wps_lines__slip_id FOREIGN KEY (tenant_id, slip_id) REFERENCES public.payroll_slips(tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- Name: rls_manifest p_manifest_read; Type: POLICY; Schema: app; Owner: -

CREATE POLICY p_manifest_read ON app.rls_manifest FOR SELECT USING (true);


-- Name: rls_manifest; Type: ROW SECURITY; Schema: app; Owner: -

ALTER TABLE app.rls_manifest ENABLE ROW LEVEL SECURITY;

-- Name: approval_actions; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.approval_actions ENABLE ROW LEVEL SECURITY;

-- Name: approval_delegations; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.approval_delegations ENABLE ROW LEVEL SECURITY;

-- Name: approval_requests; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.approval_requests ENABLE ROW LEVEL SECURITY;

-- Name: approval_workflows; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.approval_workflows ENABLE ROW LEVEL SECURITY;

-- Name: attendance_days; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.attendance_days ENABLE ROW LEVEL SECURITY;

-- Name: attendance_days_default; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.attendance_days_default ENABLE ROW LEVEL SECURITY;

-- Name: attendance_days_y2025m10; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.attendance_days_y2025m10 ENABLE ROW LEVEL SECURITY;

-- Name: attendance_days_y2025m11; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.attendance_days_y2025m11 ENABLE ROW LEVEL SECURITY;

-- Name: attendance_days_y2025m12; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.attendance_days_y2025m12 ENABLE ROW LEVEL SECURITY;

-- Name: attendance_days_y2026m01; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.attendance_days_y2026m01 ENABLE ROW LEVEL SECURITY;

-- Name: attendance_days_y2026m02; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.attendance_days_y2026m02 ENABLE ROW LEVEL SECURITY;

-- Name: attendance_days_y2026m03; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.attendance_days_y2026m03 ENABLE ROW LEVEL SECURITY;

-- Name: attendance_days_y2026m04; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.attendance_days_y2026m04 ENABLE ROW LEVEL SECURITY;

-- Name: attendance_days_y2026m05; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.attendance_days_y2026m05 ENABLE ROW LEVEL SECURITY;

-- Name: attendance_days_y2026m06; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.attendance_days_y2026m06 ENABLE ROW LEVEL SECURITY;

-- Name: attendance_days_y2026m07; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.attendance_days_y2026m07 ENABLE ROW LEVEL SECURITY;

-- Name: attendance_days_y2026m08; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.attendance_days_y2026m08 ENABLE ROW LEVEL SECURITY;

-- Name: attendance_days_y2026m09; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.attendance_days_y2026m09 ENABLE ROW LEVEL SECURITY;

-- Name: attendance_days_y2026m10; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.attendance_days_y2026m10 ENABLE ROW LEVEL SECURITY;

-- Name: attendance_days_y2026m11; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.attendance_days_y2026m11 ENABLE ROW LEVEL SECURITY;

-- Name: attendance_days_y2026m12; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.attendance_days_y2026m12 ENABLE ROW LEVEL SECURITY;

-- Name: attendance_devices; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.attendance_devices ENABLE ROW LEVEL SECURITY;

-- Name: attendance_punches; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.attendance_punches ENABLE ROW LEVEL SECURITY;

-- Name: attendance_punches_default; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.attendance_punches_default ENABLE ROW LEVEL SECURITY;

-- Name: attendance_punches_y2025m10; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.attendance_punches_y2025m10 ENABLE ROW LEVEL SECURITY;

-- Name: attendance_punches_y2025m11; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.attendance_punches_y2025m11 ENABLE ROW LEVEL SECURITY;

-- Name: attendance_punches_y2025m12; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.attendance_punches_y2025m12 ENABLE ROW LEVEL SECURITY;

-- Name: attendance_punches_y2026m01; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.attendance_punches_y2026m01 ENABLE ROW LEVEL SECURITY;

-- Name: attendance_punches_y2026m02; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.attendance_punches_y2026m02 ENABLE ROW LEVEL SECURITY;

-- Name: attendance_punches_y2026m03; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.attendance_punches_y2026m03 ENABLE ROW LEVEL SECURITY;

-- Name: attendance_punches_y2026m04; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.attendance_punches_y2026m04 ENABLE ROW LEVEL SECURITY;

-- Name: attendance_punches_y2026m05; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.attendance_punches_y2026m05 ENABLE ROW LEVEL SECURITY;

-- Name: attendance_punches_y2026m06; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.attendance_punches_y2026m06 ENABLE ROW LEVEL SECURITY;

-- Name: attendance_punches_y2026m07; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.attendance_punches_y2026m07 ENABLE ROW LEVEL SECURITY;

-- Name: attendance_punches_y2026m08; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.attendance_punches_y2026m08 ENABLE ROW LEVEL SECURITY;

-- Name: attendance_punches_y2026m09; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.attendance_punches_y2026m09 ENABLE ROW LEVEL SECURITY;

-- Name: attendance_punches_y2026m10; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.attendance_punches_y2026m10 ENABLE ROW LEVEL SECURITY;

-- Name: attendance_punches_y2026m11; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.attendance_punches_y2026m11 ENABLE ROW LEVEL SECURITY;

-- Name: attendance_punches_y2026m12; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.attendance_punches_y2026m12 ENABLE ROW LEVEL SECURITY;

-- Name: audit_logs; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.audit_logs ENABLE ROW LEVEL SECURITY;

-- Name: audit_logs_default; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.audit_logs_default ENABLE ROW LEVEL SECURITY;

-- Name: audit_logs_y2025m10; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.audit_logs_y2025m10 ENABLE ROW LEVEL SECURITY;

-- Name: audit_logs_y2025m11; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.audit_logs_y2025m11 ENABLE ROW LEVEL SECURITY;

-- Name: audit_logs_y2025m12; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.audit_logs_y2025m12 ENABLE ROW LEVEL SECURITY;

-- Name: audit_logs_y2026m01; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.audit_logs_y2026m01 ENABLE ROW LEVEL SECURITY;

-- Name: audit_logs_y2026m02; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.audit_logs_y2026m02 ENABLE ROW LEVEL SECURITY;

-- Name: audit_logs_y2026m03; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.audit_logs_y2026m03 ENABLE ROW LEVEL SECURITY;

-- Name: audit_logs_y2026m04; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.audit_logs_y2026m04 ENABLE ROW LEVEL SECURITY;

-- Name: audit_logs_y2026m05; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.audit_logs_y2026m05 ENABLE ROW LEVEL SECURITY;

-- Name: audit_logs_y2026m06; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.audit_logs_y2026m06 ENABLE ROW LEVEL SECURITY;

-- Name: audit_logs_y2026m07; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.audit_logs_y2026m07 ENABLE ROW LEVEL SECURITY;

-- Name: audit_logs_y2026m08; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.audit_logs_y2026m08 ENABLE ROW LEVEL SECURITY;

-- Name: audit_logs_y2026m09; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.audit_logs_y2026m09 ENABLE ROW LEVEL SECURITY;

-- Name: audit_logs_y2026m10; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.audit_logs_y2026m10 ENABLE ROW LEVEL SECURITY;

-- Name: audit_logs_y2026m11; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.audit_logs_y2026m11 ENABLE ROW LEVEL SECURITY;

-- Name: audit_logs_y2026m12; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.audit_logs_y2026m12 ENABLE ROW LEVEL SECURITY;

-- Name: auth_sessions; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.auth_sessions ENABLE ROW LEVEL SECURITY;

-- Name: auth_tokens; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.auth_tokens ENABLE ROW LEVEL SECURITY;

-- Name: background_job_items; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.background_job_items ENABLE ROW LEVEL SECURITY;

-- Name: background_job_items_default; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.background_job_items_default ENABLE ROW LEVEL SECURITY;

-- Name: background_job_items_y2025m10; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.background_job_items_y2025m10 ENABLE ROW LEVEL SECURITY;

-- Name: background_job_items_y2025m11; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.background_job_items_y2025m11 ENABLE ROW LEVEL SECURITY;

-- Name: background_job_items_y2025m12; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.background_job_items_y2025m12 ENABLE ROW LEVEL SECURITY;

-- Name: background_job_items_y2026m01; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.background_job_items_y2026m01 ENABLE ROW LEVEL SECURITY;

-- Name: background_job_items_y2026m02; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.background_job_items_y2026m02 ENABLE ROW LEVEL SECURITY;

-- Name: background_job_items_y2026m03; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.background_job_items_y2026m03 ENABLE ROW LEVEL SECURITY;

-- Name: background_job_items_y2026m04; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.background_job_items_y2026m04 ENABLE ROW LEVEL SECURITY;

-- Name: background_job_items_y2026m05; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.background_job_items_y2026m05 ENABLE ROW LEVEL SECURITY;

-- Name: background_job_items_y2026m06; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.background_job_items_y2026m06 ENABLE ROW LEVEL SECURITY;

-- Name: background_job_items_y2026m07; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.background_job_items_y2026m07 ENABLE ROW LEVEL SECURITY;

-- Name: background_job_items_y2026m08; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.background_job_items_y2026m08 ENABLE ROW LEVEL SECURITY;

-- Name: background_job_items_y2026m09; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.background_job_items_y2026m09 ENABLE ROW LEVEL SECURITY;

-- Name: background_job_items_y2026m10; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.background_job_items_y2026m10 ENABLE ROW LEVEL SECURITY;

-- Name: background_job_items_y2026m11; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.background_job_items_y2026m11 ENABLE ROW LEVEL SECURITY;

-- Name: background_job_items_y2026m12; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.background_job_items_y2026m12 ENABLE ROW LEVEL SECURITY;

-- Name: background_jobs; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.background_jobs ENABLE ROW LEVEL SECURITY;

-- Name: branches; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.branches ENABLE ROW LEVEL SECURITY;

-- Name: companies; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.companies ENABLE ROW LEVEL SECURITY;

-- Name: company_pay_policies; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.company_pay_policies ENABLE ROW LEVEL SECURITY;

-- Name: cost_centers; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.cost_centers ENABLE ROW LEVEL SECURITY;

-- Name: data_protection_keys; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.data_protection_keys ENABLE ROW LEVEL SECURITY;

-- Name: departments; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.departments ENABLE ROW LEVEL SECURITY;

-- Name: designations; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.designations ENABLE ROW LEVEL SECURITY;

-- Name: document_templates; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.document_templates ENABLE ROW LEVEL SECURITY;

-- Name: employee_assignments; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.employee_assignments ENABLE ROW LEVEL SECURITY;

-- Name: employee_bank_accounts; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.employee_bank_accounts ENABLE ROW LEVEL SECURITY;

-- Name: employee_contracts; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.employee_contracts ENABLE ROW LEVEL SECURITY;

-- Name: employee_documents; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.employee_documents ENABLE ROW LEVEL SECURITY;

-- Name: employee_gosi_registrations; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.employee_gosi_registrations ENABLE ROW LEVEL SECURITY;

-- Name: employee_salaries; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.employee_salaries ENABLE ROW LEVEL SECURITY;

-- Name: employees; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.employees ENABLE ROW LEVEL SECURITY;

-- Name: eos_calculations; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.eos_calculations ENABLE ROW LEVEL SECURITY;

-- Name: files; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.files ENABLE ROW LEVEL SECURITY;

-- Name: final_settlement_lines; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.final_settlement_lines ENABLE ROW LEVEL SECURITY;

-- Name: final_settlements; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.final_settlements ENABLE ROW LEVEL SECURITY;

-- Name: gl_journal_lines; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.gl_journal_lines ENABLE ROW LEVEL SECURITY;

-- Name: gl_journals; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.gl_journals ENABLE ROW LEVEL SECURITY;

-- Name: gl_mappings; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.gl_mappings ENABLE ROW LEVEL SECURITY;

-- Name: gl_period_closes; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.gl_period_closes ENABLE ROW LEVEL SECURITY;

-- Name: gosi_filings; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.gosi_filings ENABLE ROW LEVEL SECURITY;

-- Name: grades; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.grades ENABLE ROW LEVEL SECURITY;

-- Name: leave_ledger; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.leave_ledger ENABLE ROW LEVEL SECURITY;

-- Name: leave_requests; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.leave_requests ENABLE ROW LEVEL SECURITY;

-- Name: leave_types; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.leave_types ENABLE ROW LEVEL SECURITY;

-- Name: loan_installments; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.loan_installments ENABLE ROW LEVEL SECURITY;

-- Name: loans; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.loans ENABLE ROW LEVEL SECURITY;

-- Name: nitaqat_grid; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.nitaqat_grid ENABLE ROW LEVEL SECURITY;

-- Name: nitaqat_snapshots; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.nitaqat_snapshots ENABLE ROW LEVEL SECURITY;

-- Name: notification_deliveries; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.notification_deliveries ENABLE ROW LEVEL SECURITY;

-- Name: notifications; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.notifications ENABLE ROW LEVEL SECURITY;

-- Name: number_sequences; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.number_sequences ENABLE ROW LEVEL SECURITY;

-- Name: overtime_requests; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.overtime_requests ENABLE ROW LEVEL SECURITY;

-- Name: auth_sessions p_auth; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_auth ON public.auth_sessions USING (((((subject_kind)::text = 'Tenant'::text) AND (tenant_id = app.current_tenant())) OR (((subject_kind)::text = 'Platform'::text) AND (tenant_id IS NULL) AND app.is_platform()))) WITH CHECK (((((subject_kind)::text = 'Tenant'::text) AND (tenant_id = app.current_tenant())) OR (((subject_kind)::text = 'Platform'::text) AND (tenant_id IS NULL) AND app.is_platform())));


-- Name: auth_tokens p_auth; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_auth ON public.auth_tokens USING (((((subject_kind)::text = 'Tenant'::text) AND (tenant_id = app.current_tenant())) OR (((subject_kind)::text = 'Platform'::text) AND (tenant_id IS NULL) AND app.is_platform()))) WITH CHECK (((((subject_kind)::text = 'Tenant'::text) AND (tenant_id = app.current_tenant())) OR (((subject_kind)::text = 'Platform'::text) AND (tenant_id IS NULL) AND app.is_platform())));


-- Name: background_job_items p_job_platform_queue; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_job_platform_queue ON public.background_job_items TO kynex_job USING ((tenant_id IS NULL)) WITH CHECK ((tenant_id IS NULL));


-- Name: background_jobs p_job_platform_queue; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_job_platform_queue ON public.background_jobs TO kynex_job USING ((tenant_id IS NULL)) WITH CHECK ((tenant_id IS NULL));


-- Name: data_protection_keys p_keyring; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_keyring ON public.data_protection_keys USING (true) WITH CHECK (true);


-- Name: platform_users p_platform; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_platform ON public.platform_users USING (app.is_platform()) WITH CHECK (app.is_platform());


-- Name: nitaqat_grid p_reference_read; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_reference_read ON public.nitaqat_grid FOR SELECT USING (true);


-- Name: permissions p_reference_read; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_reference_read ON public.permissions FOR SELECT USING (true);


-- Name: statutory_rule_bands p_reference_read; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_reference_read ON public.statutory_rule_bands FOR SELECT USING (true);


-- Name: statutory_rules p_reference_read; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_reference_read ON public.statutory_rules FOR SELECT USING (true);


-- Name: tenants p_self_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_self_tenant ON public.tenants USING (((id = app.current_tenant()) OR app.is_platform())) WITH CHECK (app.is_platform());


-- Name: approval_actions p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.approval_actions USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: approval_delegations p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.approval_delegations USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: approval_requests p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.approval_requests USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: approval_workflows p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.approval_workflows USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: attendance_days p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.attendance_days USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: attendance_devices p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.attendance_devices USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: attendance_punches p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.attendance_punches USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: branches p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.branches USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: companies p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.companies USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: company_pay_policies p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.company_pay_policies USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: cost_centers p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.cost_centers USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: departments p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.departments USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: designations p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.designations USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: document_templates p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.document_templates USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: employee_assignments p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.employee_assignments USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: employee_bank_accounts p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.employee_bank_accounts USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: employee_contracts p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.employee_contracts USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: employee_documents p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.employee_documents USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: employee_gosi_registrations p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.employee_gosi_registrations USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: employee_salaries p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.employee_salaries USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: employees p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.employees USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: eos_calculations p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.eos_calculations USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: files p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.files USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: final_settlement_lines p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.final_settlement_lines USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: final_settlements p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.final_settlements USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: gl_journal_lines p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.gl_journal_lines USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: gl_journals p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.gl_journals USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: gl_mappings p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.gl_mappings USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: gl_period_closes p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.gl_period_closes USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: gosi_filings p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.gosi_filings USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: grades p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.grades USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: leave_ledger p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.leave_ledger USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: leave_requests p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.leave_requests USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: leave_types p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.leave_types USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: loan_installments p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.loan_installments USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: loans p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.loans USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: nitaqat_snapshots p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.nitaqat_snapshots USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: notification_deliveries p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.notification_deliveries USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: notifications p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.notifications USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: number_sequences p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.number_sequences USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: overtime_requests p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.overtime_requests USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: pay_components p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.pay_components USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: payroll_audit_logs p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.payroll_audit_logs USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: payroll_inputs p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.payroll_inputs USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: payroll_issues p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.payroll_issues USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: payroll_runs p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.payroll_runs USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: payroll_slip_lines p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.payroll_slip_lines USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: payroll_slips p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.payroll_slips USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: permission_grantor_records p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.permission_grantor_records USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: retention_purge_audits p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.retention_purge_audits USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: role_permissions p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.role_permissions USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: roles p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.roles USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: shift_assignments p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.shift_assignments USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: shifts p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.shifts USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: tenant_settings p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.tenant_settings USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: timesheet_day_reconciliations p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.timesheet_day_reconciliations USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: timesheet_entries p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.timesheet_entries USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: timesheets p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.timesheets USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: user_roles p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.user_roles USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: users p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.users USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: wps_batches p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.wps_batches USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: wps_lines p_tenant; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant ON public.wps_lines USING ((tenant_id = app.current_tenant())) WITH CHECK ((tenant_id = app.current_tenant()));


-- Name: audit_logs p_tenant_or_platform; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant_or_platform ON public.audit_logs USING (((tenant_id = app.current_tenant()) OR ((tenant_id IS NULL) AND app.is_platform()))) WITH CHECK (((tenant_id = app.current_tenant()) OR ((tenant_id IS NULL) AND app.is_platform())));


-- Name: background_job_items p_tenant_or_platform; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant_or_platform ON public.background_job_items USING (((tenant_id = app.current_tenant()) OR ((tenant_id IS NULL) AND app.is_platform()))) WITH CHECK (((tenant_id = app.current_tenant()) OR ((tenant_id IS NULL) AND app.is_platform())));


-- Name: background_jobs p_tenant_or_platform; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant_or_platform ON public.background_jobs USING (((tenant_id = app.current_tenant()) OR ((tenant_id IS NULL) AND app.is_platform()))) WITH CHECK (((tenant_id = app.current_tenant()) OR ((tenant_id IS NULL) AND app.is_platform())));


-- Name: public_holidays p_tenant_or_platform; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant_or_platform ON public.public_holidays USING (((tenant_id = app.current_tenant()) OR ((tenant_id IS NULL) AND app.is_platform()))) WITH CHECK (((tenant_id = app.current_tenant()) OR ((tenant_id IS NULL) AND app.is_platform())));


-- Name: retention_policies p_tenant_or_platform; Type: POLICY; Schema: public; Owner: -

CREATE POLICY p_tenant_or_platform ON public.retention_policies USING (((tenant_id = app.current_tenant()) OR ((tenant_id IS NULL) AND app.is_platform()))) WITH CHECK (((tenant_id = app.current_tenant()) OR ((tenant_id IS NULL) AND app.is_platform())));


-- Name: pay_components; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.pay_components ENABLE ROW LEVEL SECURITY;

-- Name: payroll_audit_logs; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.payroll_audit_logs ENABLE ROW LEVEL SECURITY;

-- Name: payroll_inputs; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.payroll_inputs ENABLE ROW LEVEL SECURITY;

-- Name: payroll_issues; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.payroll_issues ENABLE ROW LEVEL SECURITY;

-- Name: payroll_runs; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.payroll_runs ENABLE ROW LEVEL SECURITY;

-- Name: payroll_slip_lines; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.payroll_slip_lines ENABLE ROW LEVEL SECURITY;

-- Name: payroll_slips; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.payroll_slips ENABLE ROW LEVEL SECURITY;

-- Name: permission_grantor_records; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.permission_grantor_records ENABLE ROW LEVEL SECURITY;

-- Name: permissions; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.permissions ENABLE ROW LEVEL SECURITY;

-- Name: platform_users; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.platform_users ENABLE ROW LEVEL SECURITY;

-- Name: public_holidays; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.public_holidays ENABLE ROW LEVEL SECURITY;

-- Name: retention_policies; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.retention_policies ENABLE ROW LEVEL SECURITY;

-- Name: retention_purge_audits; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.retention_purge_audits ENABLE ROW LEVEL SECURITY;

-- Name: role_permissions; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.role_permissions ENABLE ROW LEVEL SECURITY;

-- Name: roles; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.roles ENABLE ROW LEVEL SECURITY;

-- Name: shift_assignments; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.shift_assignments ENABLE ROW LEVEL SECURITY;

-- Name: shifts; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.shifts ENABLE ROW LEVEL SECURITY;

-- Name: statutory_rule_bands; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.statutory_rule_bands ENABLE ROW LEVEL SECURITY;

-- Name: statutory_rules; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.statutory_rules ENABLE ROW LEVEL SECURITY;

-- Name: tenant_settings; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.tenant_settings ENABLE ROW LEVEL SECURITY;

-- Name: tenants; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.tenants ENABLE ROW LEVEL SECURITY;

-- Name: timesheet_day_reconciliations; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.timesheet_day_reconciliations ENABLE ROW LEVEL SECURITY;

-- Name: timesheet_entries; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.timesheet_entries ENABLE ROW LEVEL SECURITY;

-- Name: timesheet_entries_default; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.timesheet_entries_default ENABLE ROW LEVEL SECURITY;

-- Name: timesheet_entries_y2025m10; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.timesheet_entries_y2025m10 ENABLE ROW LEVEL SECURITY;

-- Name: timesheet_entries_y2025m11; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.timesheet_entries_y2025m11 ENABLE ROW LEVEL SECURITY;

-- Name: timesheet_entries_y2025m12; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.timesheet_entries_y2025m12 ENABLE ROW LEVEL SECURITY;

-- Name: timesheet_entries_y2026m01; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.timesheet_entries_y2026m01 ENABLE ROW LEVEL SECURITY;

-- Name: timesheet_entries_y2026m02; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.timesheet_entries_y2026m02 ENABLE ROW LEVEL SECURITY;

-- Name: timesheet_entries_y2026m03; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.timesheet_entries_y2026m03 ENABLE ROW LEVEL SECURITY;

-- Name: timesheet_entries_y2026m04; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.timesheet_entries_y2026m04 ENABLE ROW LEVEL SECURITY;

-- Name: timesheet_entries_y2026m05; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.timesheet_entries_y2026m05 ENABLE ROW LEVEL SECURITY;

-- Name: timesheet_entries_y2026m06; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.timesheet_entries_y2026m06 ENABLE ROW LEVEL SECURITY;

-- Name: timesheet_entries_y2026m07; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.timesheet_entries_y2026m07 ENABLE ROW LEVEL SECURITY;

-- Name: timesheet_entries_y2026m08; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.timesheet_entries_y2026m08 ENABLE ROW LEVEL SECURITY;

-- Name: timesheet_entries_y2026m09; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.timesheet_entries_y2026m09 ENABLE ROW LEVEL SECURITY;

-- Name: timesheet_entries_y2026m10; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.timesheet_entries_y2026m10 ENABLE ROW LEVEL SECURITY;

-- Name: timesheet_entries_y2026m11; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.timesheet_entries_y2026m11 ENABLE ROW LEVEL SECURITY;

-- Name: timesheet_entries_y2026m12; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.timesheet_entries_y2026m12 ENABLE ROW LEVEL SECURITY;

-- Name: timesheets; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.timesheets ENABLE ROW LEVEL SECURITY;

-- Name: user_roles; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.user_roles ENABLE ROW LEVEL SECURITY;

-- Name: users; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.users ENABLE ROW LEVEL SECURITY;

-- Name: wps_batches; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.wps_batches ENABLE ROW LEVEL SECURITY;

-- Name: wps_lines; Type: ROW SECURITY; Schema: public; Owner: -

ALTER TABLE public.wps_lines ENABLE ROW LEVEL SECURITY;

-- Name: SCHEMA app; Type: ACL; Schema: -; Owner: -

GRANT USAGE ON SCHEMA app TO kynex_app;
GRANT USAGE ON SCHEMA app TO kynex_job;
GRANT USAGE ON SCHEMA app TO kynex_platform;
GRANT USAGE ON SCHEMA app TO kynex_ro;
GRANT USAGE ON SCHEMA app TO kynex_migrator;


-- Name: SCHEMA public; Type: ACL; Schema: -; Owner: -

REVOKE USAGE ON SCHEMA public FROM PUBLIC;
GRANT ALL ON SCHEMA public TO kynex_owner;
GRANT USAGE ON SCHEMA public TO kynex_app;
GRANT USAGE ON SCHEMA public TO kynex_job;
GRANT USAGE ON SCHEMA public TO kynex_platform;
GRANT USAGE ON SCHEMA public TO kynex_ro;
GRANT USAGE ON SCHEMA public TO kynex_migrator;


-- Name: FUNCTION ensure_partition_headroom(p_months_ahead integer); Type: ACL; Schema: app; Owner: -

REVOKE ALL ON FUNCTION app.ensure_partition_headroom(p_months_ahead integer) FROM PUBLIC;
GRANT ALL ON FUNCTION app.ensure_partition_headroom(p_months_ahead integer) TO kynex_job;


-- Name: FUNCTION ensure_partition_month(p_month date); Type: ACL; Schema: app; Owner: -

REVOKE ALL ON FUNCTION app.ensure_partition_month(p_month date) FROM PUBLIC;
GRANT ALL ON FUNCTION app.ensure_partition_month(p_month date) TO kynex_job;


-- Name: FUNCTION partition_headroom(); Type: ACL; Schema: app; Owner: -

REVOKE ALL ON FUNCTION app.partition_headroom() FROM PUBLIC;
GRANT ALL ON FUNCTION app.partition_headroom() TO kynex_job;
GRANT ALL ON FUNCTION app.partition_headroom() TO kynex_platform;
GRANT ALL ON FUNCTION app.partition_headroom() TO kynex_ro;


-- Name: FUNCTION resolve_login(p_tenant_slug text, p_email text); Type: ACL; Schema: app; Owner: -

REVOKE ALL ON FUNCTION app.resolve_login(p_tenant_slug text, p_email text) FROM PUBLIC;
GRANT ALL ON FUNCTION app.resolve_login(p_tenant_slug text, p_email text) TO kynex_app;


-- Name: FUNCTION resolve_platform_login(p_email text); Type: ACL; Schema: app; Owner: -

REVOKE ALL ON FUNCTION app.resolve_platform_login(p_email text) FROM PUBLIC;
GRANT ALL ON FUNCTION app.resolve_platform_login(p_email text) TO kynex_app;


-- Name: TABLE rls_manifest; Type: ACL; Schema: app; Owner: -

GRANT SELECT ON TABLE app.rls_manifest TO kynex_app;
GRANT SELECT ON TABLE app.rls_manifest TO kynex_job;
GRANT SELECT ON TABLE app.rls_manifest TO kynex_platform;
GRANT SELECT ON TABLE app.rls_manifest TO kynex_ro;


-- Name: TABLE approval_actions; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.approval_actions TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.approval_actions TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.approval_actions TO kynex_platform;
GRANT SELECT ON TABLE public.approval_actions TO kynex_ro;


-- Name: TABLE approval_delegations; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.approval_delegations TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.approval_delegations TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.approval_delegations TO kynex_platform;
GRANT SELECT ON TABLE public.approval_delegations TO kynex_ro;


-- Name: TABLE approval_requests; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.approval_requests TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.approval_requests TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.approval_requests TO kynex_platform;
GRANT SELECT ON TABLE public.approval_requests TO kynex_ro;


-- Name: TABLE approval_workflows; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.approval_workflows TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.approval_workflows TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.approval_workflows TO kynex_platform;
GRANT SELECT ON TABLE public.approval_workflows TO kynex_ro;


-- Name: TABLE attendance_days; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.attendance_days TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.attendance_days TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.attendance_days TO kynex_platform;
GRANT SELECT ON TABLE public.attendance_days TO kynex_ro;


-- Name: TABLE attendance_devices; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.attendance_devices TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.attendance_devices TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.attendance_devices TO kynex_platform;


-- Name: COLUMN attendance_devices.id; Type: ACL; Schema: public; Owner: -

GRANT SELECT(id) ON TABLE public.attendance_devices TO kynex_ro;


-- Name: COLUMN attendance_devices.tenant_id; Type: ACL; Schema: public; Owner: -

GRANT SELECT(tenant_id) ON TABLE public.attendance_devices TO kynex_ro;


-- Name: COLUMN attendance_devices.branch_id; Type: ACL; Schema: public; Owner: -

GRANT SELECT(branch_id) ON TABLE public.attendance_devices TO kynex_ro;


-- Name: COLUMN attendance_devices.serial; Type: ACL; Schema: public; Owner: -

GRANT SELECT(serial) ON TABLE public.attendance_devices TO kynex_ro;


-- Name: COLUMN attendance_devices.name; Type: ACL; Schema: public; Owner: -

GRANT SELECT(name) ON TABLE public.attendance_devices TO kynex_ro;


-- Name: COLUMN attendance_devices.model; Type: ACL; Schema: public; Owner: -

GRANT SELECT(model) ON TABLE public.attendance_devices TO kynex_ro;


-- Name: COLUMN attendance_devices.is_active; Type: ACL; Schema: public; Owner: -

GRANT SELECT(is_active) ON TABLE public.attendance_devices TO kynex_ro;


-- Name: COLUMN attendance_devices.last_seen_at; Type: ACL; Schema: public; Owner: -

GRANT SELECT(last_seen_at) ON TABLE public.attendance_devices TO kynex_ro;


-- Name: COLUMN attendance_devices.sync_watermark; Type: ACL; Schema: public; Owner: -

GRANT SELECT(sync_watermark) ON TABLE public.attendance_devices TO kynex_ro;


-- Name: COLUMN attendance_devices.recent_nonces; Type: ACL; Schema: public; Owner: -

GRANT SELECT(recent_nonces) ON TABLE public.attendance_devices TO kynex_ro;


-- Name: COLUMN attendance_devices.created_at; Type: ACL; Schema: public; Owner: -

GRANT SELECT(created_at) ON TABLE public.attendance_devices TO kynex_ro;


-- Name: COLUMN attendance_devices.created_by; Type: ACL; Schema: public; Owner: -

GRANT SELECT(created_by) ON TABLE public.attendance_devices TO kynex_ro;


-- Name: COLUMN attendance_devices.updated_at; Type: ACL; Schema: public; Owner: -

GRANT SELECT(updated_at) ON TABLE public.attendance_devices TO kynex_ro;


-- Name: COLUMN attendance_devices.updated_by; Type: ACL; Schema: public; Owner: -

GRANT SELECT(updated_by) ON TABLE public.attendance_devices TO kynex_ro;


-- Name: TABLE attendance_punches; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.attendance_punches TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.attendance_punches TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.attendance_punches TO kynex_platform;
GRANT SELECT ON TABLE public.attendance_punches TO kynex_ro;


-- Name: TABLE audit_logs; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.audit_logs TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.audit_logs TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.audit_logs TO kynex_platform;
GRANT SELECT ON TABLE public.audit_logs TO kynex_ro;


-- Name: TABLE auth_sessions; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.auth_sessions TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.auth_sessions TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.auth_sessions TO kynex_platform;


-- Name: COLUMN auth_sessions.id; Type: ACL; Schema: public; Owner: -

GRANT SELECT(id) ON TABLE public.auth_sessions TO kynex_ro;


-- Name: COLUMN auth_sessions.tenant_id; Type: ACL; Schema: public; Owner: -

GRANT SELECT(tenant_id) ON TABLE public.auth_sessions TO kynex_ro;


-- Name: COLUMN auth_sessions.user_id; Type: ACL; Schema: public; Owner: -

GRANT SELECT(user_id) ON TABLE public.auth_sessions TO kynex_ro;


-- Name: COLUMN auth_sessions.platform_user_id; Type: ACL; Schema: public; Owner: -

GRANT SELECT(platform_user_id) ON TABLE public.auth_sessions TO kynex_ro;


-- Name: COLUMN auth_sessions.device_id; Type: ACL; Schema: public; Owner: -

GRANT SELECT(device_id) ON TABLE public.auth_sessions TO kynex_ro;


-- Name: COLUMN auth_sessions.subject_kind; Type: ACL; Schema: public; Owner: -

GRANT SELECT(subject_kind) ON TABLE public.auth_sessions TO kynex_ro;


-- Name: COLUMN auth_sessions.push_platform; Type: ACL; Schema: public; Owner: -

GRANT SELECT(push_platform) ON TABLE public.auth_sessions TO kynex_ro;


-- Name: COLUMN auth_sessions.ip; Type: ACL; Schema: public; Owner: -

GRANT SELECT(ip) ON TABLE public.auth_sessions TO kynex_ro;


-- Name: COLUMN auth_sessions.user_agent; Type: ACL; Schema: public; Owner: -

GRANT SELECT(user_agent) ON TABLE public.auth_sessions TO kynex_ro;


-- Name: COLUMN auth_sessions.last_seen_at; Type: ACL; Schema: public; Owner: -

GRANT SELECT(last_seen_at) ON TABLE public.auth_sessions TO kynex_ro;


-- Name: COLUMN auth_sessions.expires_at; Type: ACL; Schema: public; Owner: -

GRANT SELECT(expires_at) ON TABLE public.auth_sessions TO kynex_ro;


-- Name: COLUMN auth_sessions.revoked_at; Type: ACL; Schema: public; Owner: -

GRANT SELECT(revoked_at) ON TABLE public.auth_sessions TO kynex_ro;


-- Name: COLUMN auth_sessions.created_at; Type: ACL; Schema: public; Owner: -

GRANT SELECT(created_at) ON TABLE public.auth_sessions TO kynex_ro;


-- Name: COLUMN auth_sessions.created_by; Type: ACL; Schema: public; Owner: -

GRANT SELECT(created_by) ON TABLE public.auth_sessions TO kynex_ro;


-- Name: COLUMN auth_sessions.updated_at; Type: ACL; Schema: public; Owner: -

GRANT SELECT(updated_at) ON TABLE public.auth_sessions TO kynex_ro;


-- Name: COLUMN auth_sessions.updated_by; Type: ACL; Schema: public; Owner: -

GRANT SELECT(updated_by) ON TABLE public.auth_sessions TO kynex_ro;


-- Name: TABLE auth_tokens; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.auth_tokens TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.auth_tokens TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.auth_tokens TO kynex_platform;


-- Name: COLUMN auth_tokens.id; Type: ACL; Schema: public; Owner: -

GRANT SELECT(id) ON TABLE public.auth_tokens TO kynex_ro;


-- Name: COLUMN auth_tokens.tenant_id; Type: ACL; Schema: public; Owner: -

GRANT SELECT(tenant_id) ON TABLE public.auth_tokens TO kynex_ro;


-- Name: COLUMN auth_tokens.user_id; Type: ACL; Schema: public; Owner: -

GRANT SELECT(user_id) ON TABLE public.auth_tokens TO kynex_ro;


-- Name: COLUMN auth_tokens.platform_user_id; Type: ACL; Schema: public; Owner: -

GRANT SELECT(platform_user_id) ON TABLE public.auth_tokens TO kynex_ro;


-- Name: COLUMN auth_tokens.subject_kind; Type: ACL; Schema: public; Owner: -

GRANT SELECT(subject_kind) ON TABLE public.auth_tokens TO kynex_ro;


-- Name: COLUMN auth_tokens.purpose; Type: ACL; Schema: public; Owner: -

GRANT SELECT(purpose) ON TABLE public.auth_tokens TO kynex_ro;


-- Name: COLUMN auth_tokens.attempts; Type: ACL; Schema: public; Owner: -

GRANT SELECT(attempts) ON TABLE public.auth_tokens TO kynex_ro;


-- Name: COLUMN auth_tokens.expires_at; Type: ACL; Schema: public; Owner: -

GRANT SELECT(expires_at) ON TABLE public.auth_tokens TO kynex_ro;


-- Name: COLUMN auth_tokens.consumed_at; Type: ACL; Schema: public; Owner: -

GRANT SELECT(consumed_at) ON TABLE public.auth_tokens TO kynex_ro;


-- Name: COLUMN auth_tokens.created_at; Type: ACL; Schema: public; Owner: -

GRANT SELECT(created_at) ON TABLE public.auth_tokens TO kynex_ro;


-- Name: COLUMN auth_tokens.created_by; Type: ACL; Schema: public; Owner: -

GRANT SELECT(created_by) ON TABLE public.auth_tokens TO kynex_ro;


-- Name: COLUMN auth_tokens.updated_at; Type: ACL; Schema: public; Owner: -

GRANT SELECT(updated_at) ON TABLE public.auth_tokens TO kynex_ro;


-- Name: COLUMN auth_tokens.updated_by; Type: ACL; Schema: public; Owner: -

GRANT SELECT(updated_by) ON TABLE public.auth_tokens TO kynex_ro;


-- Name: TABLE background_job_items; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.background_job_items TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.background_job_items TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.background_job_items TO kynex_platform;
GRANT SELECT ON TABLE public.background_job_items TO kynex_ro;


-- Name: TABLE background_jobs; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.background_jobs TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.background_jobs TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.background_jobs TO kynex_platform;
GRANT SELECT ON TABLE public.background_jobs TO kynex_ro;


-- Name: TABLE branches; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.branches TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.branches TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.branches TO kynex_platform;
GRANT SELECT ON TABLE public.branches TO kynex_ro;


-- Name: TABLE companies; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.companies TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.companies TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.companies TO kynex_platform;
GRANT SELECT ON TABLE public.companies TO kynex_ro;


-- Name: TABLE company_pay_policies; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.company_pay_policies TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.company_pay_policies TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.company_pay_policies TO kynex_platform;
GRANT SELECT ON TABLE public.company_pay_policies TO kynex_ro;


-- Name: TABLE cost_centers; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.cost_centers TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.cost_centers TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.cost_centers TO kynex_platform;
GRANT SELECT ON TABLE public.cost_centers TO kynex_ro;


-- Name: TABLE data_protection_keys; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,UPDATE ON TABLE public.data_protection_keys TO kynex_app;
GRANT SELECT,INSERT,UPDATE ON TABLE public.data_protection_keys TO kynex_job;
GRANT SELECT ON TABLE public.data_protection_keys TO kynex_platform;


-- Name: COLUMN data_protection_keys.id; Type: ACL; Schema: public; Owner: -

GRANT SELECT(id) ON TABLE public.data_protection_keys TO kynex_ro;


-- Name: COLUMN data_protection_keys.friendly_name; Type: ACL; Schema: public; Owner: -

GRANT SELECT(friendly_name) ON TABLE public.data_protection_keys TO kynex_ro;


-- Name: COLUMN data_protection_keys.created_at; Type: ACL; Schema: public; Owner: -

GRANT SELECT(created_at) ON TABLE public.data_protection_keys TO kynex_ro;


-- Name: COLUMN data_protection_keys.created_by; Type: ACL; Schema: public; Owner: -

GRANT SELECT(created_by) ON TABLE public.data_protection_keys TO kynex_ro;


-- Name: COLUMN data_protection_keys.updated_at; Type: ACL; Schema: public; Owner: -

GRANT SELECT(updated_at) ON TABLE public.data_protection_keys TO kynex_ro;


-- Name: COLUMN data_protection_keys.updated_by; Type: ACL; Schema: public; Owner: -

GRANT SELECT(updated_by) ON TABLE public.data_protection_keys TO kynex_ro;


-- Name: TABLE departments; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.departments TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.departments TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.departments TO kynex_platform;
GRANT SELECT ON TABLE public.departments TO kynex_ro;


-- Name: TABLE designations; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.designations TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.designations TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.designations TO kynex_platform;
GRANT SELECT ON TABLE public.designations TO kynex_ro;


-- Name: TABLE document_templates; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.document_templates TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.document_templates TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.document_templates TO kynex_platform;
GRANT SELECT ON TABLE public.document_templates TO kynex_ro;


-- Name: TABLE employee_assignments; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.employee_assignments TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.employee_assignments TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.employee_assignments TO kynex_platform;
GRANT SELECT ON TABLE public.employee_assignments TO kynex_ro;


-- Name: TABLE employee_bank_accounts; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.employee_bank_accounts TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.employee_bank_accounts TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.employee_bank_accounts TO kynex_platform;
GRANT SELECT ON TABLE public.employee_bank_accounts TO kynex_ro;


-- Name: TABLE employee_contracts; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.employee_contracts TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.employee_contracts TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.employee_contracts TO kynex_platform;
GRANT SELECT ON TABLE public.employee_contracts TO kynex_ro;


-- Name: TABLE employee_documents; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.employee_documents TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.employee_documents TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.employee_documents TO kynex_platform;
GRANT SELECT ON TABLE public.employee_documents TO kynex_ro;


-- Name: TABLE employee_gosi_registrations; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.employee_gosi_registrations TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.employee_gosi_registrations TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.employee_gosi_registrations TO kynex_platform;
GRANT SELECT ON TABLE public.employee_gosi_registrations TO kynex_ro;


-- Name: TABLE employee_salaries; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.employee_salaries TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.employee_salaries TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.employee_salaries TO kynex_platform;
GRANT SELECT ON TABLE public.employee_salaries TO kynex_ro;


-- Name: TABLE employees; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.employees TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.employees TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.employees TO kynex_platform;
GRANT SELECT ON TABLE public.employees TO kynex_ro;


-- Name: TABLE eos_calculations; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.eos_calculations TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.eos_calculations TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.eos_calculations TO kynex_platform;
GRANT SELECT ON TABLE public.eos_calculations TO kynex_ro;


-- Name: TABLE files; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.files TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.files TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.files TO kynex_platform;
GRANT SELECT ON TABLE public.files TO kynex_ro;


-- Name: TABLE final_settlement_lines; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.final_settlement_lines TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.final_settlement_lines TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.final_settlement_lines TO kynex_platform;
GRANT SELECT ON TABLE public.final_settlement_lines TO kynex_ro;


-- Name: TABLE final_settlements; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.final_settlements TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.final_settlements TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.final_settlements TO kynex_platform;
GRANT SELECT ON TABLE public.final_settlements TO kynex_ro;


-- Name: TABLE gl_journal_lines; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.gl_journal_lines TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.gl_journal_lines TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.gl_journal_lines TO kynex_platform;
GRANT SELECT ON TABLE public.gl_journal_lines TO kynex_ro;


-- Name: TABLE gl_journals; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.gl_journals TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.gl_journals TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.gl_journals TO kynex_platform;
GRANT SELECT ON TABLE public.gl_journals TO kynex_ro;


-- Name: TABLE gl_mappings; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.gl_mappings TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.gl_mappings TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.gl_mappings TO kynex_platform;
GRANT SELECT ON TABLE public.gl_mappings TO kynex_ro;


-- Name: TABLE gl_period_closes; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.gl_period_closes TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.gl_period_closes TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.gl_period_closes TO kynex_platform;
GRANT SELECT ON TABLE public.gl_period_closes TO kynex_ro;


-- Name: TABLE gosi_filings; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.gosi_filings TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.gosi_filings TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.gosi_filings TO kynex_platform;
GRANT SELECT ON TABLE public.gosi_filings TO kynex_ro;


-- Name: TABLE grades; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.grades TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.grades TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.grades TO kynex_platform;
GRANT SELECT ON TABLE public.grades TO kynex_ro;


-- Name: TABLE leave_ledger; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.leave_ledger TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.leave_ledger TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.leave_ledger TO kynex_platform;
GRANT SELECT ON TABLE public.leave_ledger TO kynex_ro;


-- Name: TABLE leave_requests; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.leave_requests TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.leave_requests TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.leave_requests TO kynex_platform;
GRANT SELECT ON TABLE public.leave_requests TO kynex_ro;


-- Name: TABLE leave_types; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.leave_types TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.leave_types TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.leave_types TO kynex_platform;
GRANT SELECT ON TABLE public.leave_types TO kynex_ro;


-- Name: TABLE loan_installments; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.loan_installments TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.loan_installments TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.loan_installments TO kynex_platform;
GRANT SELECT ON TABLE public.loan_installments TO kynex_ro;


-- Name: TABLE loans; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.loans TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.loans TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.loans TO kynex_platform;
GRANT SELECT ON TABLE public.loans TO kynex_ro;


-- Name: TABLE nitaqat_grid; Type: ACL; Schema: public; Owner: -

GRANT SELECT ON TABLE public.nitaqat_grid TO kynex_app;
GRANT SELECT ON TABLE public.nitaqat_grid TO kynex_job;
GRANT SELECT ON TABLE public.nitaqat_grid TO kynex_platform;
GRANT SELECT ON TABLE public.nitaqat_grid TO kynex_ro;


-- Name: TABLE nitaqat_snapshots; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.nitaqat_snapshots TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.nitaqat_snapshots TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.nitaqat_snapshots TO kynex_platform;
GRANT SELECT ON TABLE public.nitaqat_snapshots TO kynex_ro;


-- Name: TABLE notification_deliveries; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.notification_deliveries TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.notification_deliveries TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.notification_deliveries TO kynex_platform;
GRANT SELECT ON TABLE public.notification_deliveries TO kynex_ro;


-- Name: TABLE notifications; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.notifications TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.notifications TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.notifications TO kynex_platform;
GRANT SELECT ON TABLE public.notifications TO kynex_ro;


-- Name: TABLE number_sequences; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.number_sequences TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.number_sequences TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.number_sequences TO kynex_platform;
GRANT SELECT ON TABLE public.number_sequences TO kynex_ro;


-- Name: TABLE overtime_requests; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.overtime_requests TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.overtime_requests TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.overtime_requests TO kynex_platform;
GRANT SELECT ON TABLE public.overtime_requests TO kynex_ro;


-- Name: TABLE pay_components; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.pay_components TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.pay_components TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.pay_components TO kynex_platform;
GRANT SELECT ON TABLE public.pay_components TO kynex_ro;


-- Name: TABLE payroll_audit_logs; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.payroll_audit_logs TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.payroll_audit_logs TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.payroll_audit_logs TO kynex_platform;
GRANT SELECT ON TABLE public.payroll_audit_logs TO kynex_ro;


-- Name: TABLE payroll_inputs; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.payroll_inputs TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.payroll_inputs TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.payroll_inputs TO kynex_platform;
GRANT SELECT ON TABLE public.payroll_inputs TO kynex_ro;


-- Name: TABLE payroll_issues; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.payroll_issues TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.payroll_issues TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.payroll_issues TO kynex_platform;
GRANT SELECT ON TABLE public.payroll_issues TO kynex_ro;


-- Name: TABLE payroll_runs; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.payroll_runs TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.payroll_runs TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.payroll_runs TO kynex_platform;
GRANT SELECT ON TABLE public.payroll_runs TO kynex_ro;


-- Name: TABLE payroll_slip_lines; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.payroll_slip_lines TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.payroll_slip_lines TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.payroll_slip_lines TO kynex_platform;
GRANT SELECT ON TABLE public.payroll_slip_lines TO kynex_ro;


-- Name: TABLE payroll_slips; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.payroll_slips TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.payroll_slips TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.payroll_slips TO kynex_platform;
GRANT SELECT ON TABLE public.payroll_slips TO kynex_ro;


-- Name: TABLE permission_grantor_records; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.permission_grantor_records TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.permission_grantor_records TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.permission_grantor_records TO kynex_platform;
GRANT SELECT ON TABLE public.permission_grantor_records TO kynex_ro;


-- Name: TABLE permissions; Type: ACL; Schema: public; Owner: -

GRANT SELECT ON TABLE public.permissions TO kynex_app;
GRANT SELECT ON TABLE public.permissions TO kynex_job;
GRANT SELECT ON TABLE public.permissions TO kynex_platform;
GRANT SELECT ON TABLE public.permissions TO kynex_ro;


-- Name: TABLE platform_users; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.platform_users TO kynex_platform;


-- Name: TABLE public_holidays; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.public_holidays TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.public_holidays TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.public_holidays TO kynex_platform;
GRANT SELECT ON TABLE public.public_holidays TO kynex_ro;


-- Name: TABLE retention_policies; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.retention_policies TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.retention_policies TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.retention_policies TO kynex_platform;
GRANT SELECT ON TABLE public.retention_policies TO kynex_ro;


-- Name: TABLE retention_purge_audits; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.retention_purge_audits TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.retention_purge_audits TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.retention_purge_audits TO kynex_platform;
GRANT SELECT ON TABLE public.retention_purge_audits TO kynex_ro;


-- Name: TABLE role_permissions; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.role_permissions TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.role_permissions TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.role_permissions TO kynex_platform;
GRANT SELECT ON TABLE public.role_permissions TO kynex_ro;


-- Name: TABLE roles; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.roles TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.roles TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.roles TO kynex_platform;
GRANT SELECT ON TABLE public.roles TO kynex_ro;


-- Name: TABLE shift_assignments; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.shift_assignments TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.shift_assignments TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.shift_assignments TO kynex_platform;
GRANT SELECT ON TABLE public.shift_assignments TO kynex_ro;


-- Name: TABLE shifts; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.shifts TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.shifts TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.shifts TO kynex_platform;
GRANT SELECT ON TABLE public.shifts TO kynex_ro;


-- Name: TABLE statutory_rule_bands; Type: ACL; Schema: public; Owner: -

GRANT SELECT ON TABLE public.statutory_rule_bands TO kynex_app;
GRANT SELECT ON TABLE public.statutory_rule_bands TO kynex_job;
GRANT SELECT ON TABLE public.statutory_rule_bands TO kynex_platform;
GRANT SELECT ON TABLE public.statutory_rule_bands TO kynex_ro;


-- Name: TABLE statutory_rules; Type: ACL; Schema: public; Owner: -

GRANT SELECT ON TABLE public.statutory_rules TO kynex_app;
GRANT SELECT ON TABLE public.statutory_rules TO kynex_job;
GRANT SELECT ON TABLE public.statutory_rules TO kynex_platform;
GRANT SELECT ON TABLE public.statutory_rules TO kynex_ro;


-- Name: TABLE tenant_settings; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.tenant_settings TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.tenant_settings TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.tenant_settings TO kynex_platform;
GRANT SELECT ON TABLE public.tenant_settings TO kynex_ro;


-- Name: TABLE tenants; Type: ACL; Schema: public; Owner: -

GRANT SELECT ON TABLE public.tenants TO kynex_app;
GRANT SELECT ON TABLE public.tenants TO kynex_job;
GRANT SELECT ON TABLE public.tenants TO kynex_ro;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.tenants TO kynex_platform;


-- Name: TABLE timesheet_day_reconciliations; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.timesheet_day_reconciliations TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.timesheet_day_reconciliations TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.timesheet_day_reconciliations TO kynex_platform;
GRANT SELECT ON TABLE public.timesheet_day_reconciliations TO kynex_ro;


-- Name: TABLE timesheet_entries; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.timesheet_entries TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.timesheet_entries TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.timesheet_entries TO kynex_platform;
GRANT SELECT ON TABLE public.timesheet_entries TO kynex_ro;


-- Name: TABLE timesheets; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.timesheets TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.timesheets TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.timesheets TO kynex_platform;
GRANT SELECT ON TABLE public.timesheets TO kynex_ro;


-- Name: TABLE user_roles; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.user_roles TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.user_roles TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.user_roles TO kynex_platform;
GRANT SELECT ON TABLE public.user_roles TO kynex_ro;


-- Name: TABLE users; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.users TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.users TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.users TO kynex_platform;


-- Name: COLUMN users.id; Type: ACL; Schema: public; Owner: -

GRANT SELECT(id) ON TABLE public.users TO kynex_ro;


-- Name: COLUMN users.tenant_id; Type: ACL; Schema: public; Owner: -

GRANT SELECT(tenant_id) ON TABLE public.users TO kynex_ro;


-- Name: COLUMN users.employee_id; Type: ACL; Schema: public; Owner: -

GRANT SELECT(employee_id) ON TABLE public.users TO kynex_ro;


-- Name: COLUMN users.normalized_email; Type: ACL; Schema: public; Owner: -

GRANT SELECT(normalized_email) ON TABLE public.users TO kynex_ro;


-- Name: COLUMN users.status; Type: ACL; Schema: public; Owner: -

GRANT SELECT(status) ON TABLE public.users TO kynex_ro;


-- Name: COLUMN users.mfa_enabled; Type: ACL; Schema: public; Owner: -

GRANT SELECT(mfa_enabled) ON TABLE public.users TO kynex_ro;


-- Name: COLUMN users.failed_login_count; Type: ACL; Schema: public; Owner: -

GRANT SELECT(failed_login_count) ON TABLE public.users TO kynex_ro;


-- Name: COLUMN users.lockout_end; Type: ACL; Schema: public; Owner: -

GRANT SELECT(lockout_end) ON TABLE public.users TO kynex_ro;


-- Name: COLUMN users.notification_prefs; Type: ACL; Schema: public; Owner: -

GRANT SELECT(notification_prefs) ON TABLE public.users TO kynex_ro;


-- Name: COLUMN users.last_login_at; Type: ACL; Schema: public; Owner: -

GRANT SELECT(last_login_at) ON TABLE public.users TO kynex_ro;


-- Name: COLUMN users.deleted_at; Type: ACL; Schema: public; Owner: -

GRANT SELECT(deleted_at) ON TABLE public.users TO kynex_ro;


-- Name: COLUMN users.created_at; Type: ACL; Schema: public; Owner: -

GRANT SELECT(created_at) ON TABLE public.users TO kynex_ro;


-- Name: COLUMN users.created_by; Type: ACL; Schema: public; Owner: -

GRANT SELECT(created_by) ON TABLE public.users TO kynex_ro;


-- Name: COLUMN users.updated_at; Type: ACL; Schema: public; Owner: -

GRANT SELECT(updated_at) ON TABLE public.users TO kynex_ro;


-- Name: COLUMN users.updated_by; Type: ACL; Schema: public; Owner: -

GRANT SELECT(updated_by) ON TABLE public.users TO kynex_ro;


-- Name: TABLE v_employee_current; Type: ACL; Schema: public; Owner: -

GRANT SELECT ON TABLE public.v_employee_current TO kynex_app;
GRANT SELECT ON TABLE public.v_employee_current TO kynex_job;
GRANT SELECT ON TABLE public.v_employee_current TO kynex_platform;
GRANT SELECT ON TABLE public.v_employee_current TO kynex_ro;


-- Name: TABLE v_leave_balances; Type: ACL; Schema: public; Owner: -

GRANT SELECT ON TABLE public.v_leave_balances TO kynex_app;
GRANT SELECT ON TABLE public.v_leave_balances TO kynex_job;
GRANT SELECT ON TABLE public.v_leave_balances TO kynex_platform;
GRANT SELECT ON TABLE public.v_leave_balances TO kynex_ro;


-- Name: TABLE wps_batches; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.wps_batches TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.wps_batches TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.wps_batches TO kynex_platform;
GRANT SELECT ON TABLE public.wps_batches TO kynex_ro;


-- Name: TABLE wps_lines; Type: ACL; Schema: public; Owner: -

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.wps_lines TO kynex_app;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.wps_lines TO kynex_job;
GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public.wps_lines TO kynex_platform;
GRANT SELECT ON TABLE public.wps_lines TO kynex_ro;


-- Name: DEFAULT PRIVILEGES FOR SEQUENCES; Type: DEFAULT ACL; Schema: public; Owner: -

ALTER DEFAULT PRIVILEGES FOR ROLE kynex_owner IN SCHEMA public GRANT SELECT,USAGE ON SEQUENCES TO kynex_app;
ALTER DEFAULT PRIVILEGES FOR ROLE kynex_owner IN SCHEMA public GRANT SELECT,USAGE ON SEQUENCES TO kynex_job;
ALTER DEFAULT PRIVILEGES FOR ROLE kynex_owner IN SCHEMA public GRANT SELECT,USAGE ON SEQUENCES TO kynex_platform;


-- PostgreSQL database dump complete


