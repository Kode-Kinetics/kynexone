-- =============================================================================
-- 050_triggers.sql
-- KynexOne baseline schema — TARGET_SCHEMA.md revision 6.
--
-- Everything 020 and 021 deferred because it could not be a CHECK:
--
--   PART 1  trg_row_stamp — the one row-stamping trigger of the §1 conventions,
--           with that section's exempt list.
--   PART 2  trg_payroll_run_transition — the §10.1 state machine, including the
--           Opening-run restrictions, the attendance lock, and the claim release
--           that unsticks a crashed run.
--   PART 3  the §1 frozen-row guards: slips, slip lines, WPS lines, GOSI filings,
--           EOS calculations and settlement lines are snapshots, not live joins.
--   PART 4  the §11.2 deferred total-reconciliation triggers.
--   PART 5  the §8.1 BEFORE DELETE guards on every CASCADE parent that has an
--           editable state, so CASCADE can only ever fire on a draft.
--   PART 6  the append-only guards, including the single permitted UPDATE shape
--           on audit_logs that §12.3's erasure needs.
--
-- ACTOR CONTEXT. §19.2 names two GUCs, app.tenant_id and app.platform, and no
-- actor GUC, because created_by/updated_by are plain uuids and not part of RLS.
-- This file reads 'app.user_id' with current_setting(..., true) so an unset GUC is
-- NULL rather than an error, and falls back to whatever the statement supplied.
-- That keeps the baseline, the seeders and kynex_migrator able to insert without a
-- session context, while the API's interceptor (§19.2) sets it per request.
--
-- FIRING ORDER. PostgreSQL fires BEFORE triggers in name order. Every guard in
-- PARTS 2, 3, 5 and 6 sorts before 'trg_row_stamp', so a rejected write is
-- rejected before it is stamped.
-- =============================================================================


-- =============================================================================
-- PART 1 — ROW STAMPING (§1 "Row stamping, stated once")
--
-- "maintained by one trg_row_stamp BEFORE INSERT/UPDATE trigger, not by
-- application code, so a raw SQL fix or a background job cannot skip it."
--
-- Taken at its word: created_at and created_by are forced on INSERT and are
-- immutable thereafter, and updated_at/updated_by are forced on UPDATE. An
-- application that supplies its own values does not win the argument.
--
-- The exempt list is not hard-coded. §1 exempts "the append-only and immutable
-- tables, which carry created_at and their own actor column only" and "the
-- reference tables, which carry effective_from instead" — and 010-018 expressed
-- exactly that by giving those tables no updated_by column. So the attachment
-- loop below keys on the presence of updated_by, which makes the trigger set and
-- the exempt list the same statement of fact rather than two lists to drift apart.
-- It reproduces §1's list exactly: leave_ledger, audit_logs, payroll_audit_logs,
-- retention_purge_audits, attendance_punches, payroll_slip_lines, wps_lines,
-- final_settlement_lines, gl_journal_lines, nitaqat_snapshots, background_job_items,
-- plus the reference tables.
-- =============================================================================

CREATE FUNCTION fn_row_stamp() RETURNS trigger
LANGUAGE plpgsql AS $$
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

COMMENT ON FUNCTION fn_row_stamp() IS
    '§1 row stamping. Reads the actor from the app.user_id GUC; an unset GUC leaves whatever the '
    'statement supplied, so the baseline and the seeders can run without a session context.';

DO $$
DECLARE t text;
BEGIN
    FOR t IN
        SELECT c.relname
          FROM pg_class c
          JOIN pg_namespace n ON n.oid = c.relnamespace
         WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p')
           -- Only the partitioned PARENT: a trigger created on it is cloned onto
           -- every partition, and onto every partition 030 adds later. Attaching
           -- to a child as well would fail on the duplicate name, and a monthly
           -- partition created after this file ran would otherwise go unstamped.
           AND NOT c.relispartition
           AND EXISTS (SELECT 1 FROM information_schema.columns
                        WHERE table_schema = 'public' AND table_name = c.relname
                          AND column_name = 'updated_by')
         ORDER BY 1
    LOOP
        EXECUTE format(
            'CREATE TRIGGER trg_row_stamp BEFORE INSERT OR UPDATE ON %I '
            'FOR EACH ROW EXECUTE FUNCTION fn_row_stamp()', t);
    END LOOP;
END $$;


-- =============================================================================
-- PART 2 — THE PAYROLL RUN STATE MACHINE (§10.1)
--
-- "the money path gets a database guarantee, not a service promise".
--
--   Draft                -> Processing, Voided
--   Processing           -> Processed (when slips + excluded = selected_employee_count),
--                           Draft (failure, cancel, or the stale-heartbeat watchdog)
--   Processed            -> PendingFinanceReview, Approved, Draft
--   PendingFinanceReview -> Approved, Draft
--   Approved             -> Locked, Draft (only while no WPS batch exists)
--   Locked               -> Paid, Voided (only while no accepted WPS batch and no
--                           posted GL journal exist)
--   Paid                 -> Completed (when every wps_lines.bank_status is terminal)
--   Completed, Voided    -> terminal
--
-- Plus the three invariants §10.1 attaches: an Opening run may only go
-- Draft -> Locked and never Paid; slips and lines are immutable from Approved
-- onward (PART 3); attendance_locked_range is set on Approved and cleared on Void.
-- =============================================================================

CREATE FUNCTION fn_payroll_run_transition() RETURNS trigger
LANGUAGE plpgsql AS $$
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

COMMENT ON FUNCTION fn_payroll_run_transition() IS
    '§10.1 payroll_runs state machine, enforced in the database because the money path gets a '
    'guarantee rather than a service promise.';

CREATE TRIGGER trg_payroll_run_transition
    BEFORE UPDATE ON payroll_runs
    FOR EACH ROW EXECUTE FUNCTION fn_payroll_run_transition();

-- -----------------------------------------------------------------------------
-- Processing -> Draft releases the run's claimed inputs, in the same transaction.
--
-- §19.5 (ii): a crashed run stays Processing, not Voided, so the claim-release
-- rule of §F — which keys on a voided run — would never fire and the run's
-- Claimed payroll_inputs would be stranded, invisible to the next run. The
-- watchdog moves a stale run to Draft, and this is what makes the release happen.
-- AFTER, not BEFORE, so the release only runs once the transition above has been
-- accepted.
-- -----------------------------------------------------------------------------

CREATE FUNCTION fn_payroll_run_release_claims() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    UPDATE payroll_inputs
       SET status = 'Pending', claimed_by_run_id = NULL, claimed_at = NULL
     WHERE tenant_id = NEW.tenant_id
       AND claimed_by_run_id = NEW.id
       AND status = 'Claimed';
    RETURN NULL;
END $$;

COMMENT ON FUNCTION fn_payroll_run_release_claims() IS
    '§10.1 / §19.5 (ii): the Processing -> Draft transition releases every payroll_inputs row this '
    'run holds as Claimed back to Pending, which is the only path that unsticks a crashed run.';

CREATE TRIGGER trg_payroll_run_release_claims
    AFTER UPDATE ON payroll_runs
    FOR EACH ROW
    WHEN (OLD.status = 'Processing' AND NEW.status = 'Draft')
    EXECUTE FUNCTION fn_payroll_run_release_claims();


-- =============================================================================
-- PART 3 — FROZEN ROWS (§1 "Frozen rows")
--
-- "Slips, slip lines, WPS lines, GOSI filings, EOS calculations and settlement
-- lines hold snapshots, not live joins. A trigger rejects UPDATE and DELETE once
-- the parent is Locked, Filed or Approved."
--
-- Each guard names the state that freezes it and, where the design names columns
-- that must still move afterwards, allows exactly those.
-- =============================================================================

-- --- payroll_slips and payroll_slip_lines: frozen from Approved onward -------

CREATE FUNCTION fn_payroll_slip_frozen() RETURNS trigger
LANGUAGE plpgsql AS $$
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

COMMENT ON FUNCTION fn_payroll_slip_frozen() IS
    '§10.1: "slips and lines are immutable from Approved onward". Voided is deliberately not in the '
    'frozen set — a voided run''s slips are already unreachable, and §12 purges them.';

CREATE TRIGGER trg_payroll_slips_frozen
    BEFORE UPDATE OR DELETE ON payroll_slips
    FOR EACH ROW EXECUTE FUNCTION fn_payroll_slip_frozen();

CREATE TRIGGER trg_payroll_slip_lines_frozen
    BEFORE UPDATE OR DELETE ON payroll_slip_lines
    FOR EACH ROW EXECUTE FUNCTION fn_payroll_slip_frozen();

-- --- wps_lines: frozen at Submitted, except the confirmation columns ---------

CREATE FUNCTION fn_wps_line_frozen() RETURNS trigger
LANGUAGE plpgsql AS $$
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

CREATE TRIGGER trg_wps_lines_frozen
    BEFORE UPDATE OR DELETE ON wps_lines
    FOR EACH ROW EXECUTE FUNCTION fn_wps_line_frozen();

-- --- final_settlement_lines: frozen at Approved ------------------------------

CREATE FUNCTION fn_settlement_line_frozen() RETURNS trigger
LANGUAGE plpgsql AS $$
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

COMMENT ON FUNCTION fn_settlement_line_frozen() IS '§10.10: "Lines freeze at Approved".';

CREATE TRIGGER trg_final_settlement_lines_frozen
    BEFORE UPDATE OR DELETE ON final_settlement_lines
    FOR EACH ROW EXECUTE FUNCTION fn_settlement_line_frozen();

-- --- gosi_filings: frozen at Filed, except the reconciliation columns --------

CREATE FUNCTION fn_gosi_filing_frozen() RETURNS trigger
LANGUAGE plpgsql AS $$
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

CREATE TRIGGER trg_gosi_filings_frozen
    BEFORE UPDATE OR DELETE ON gosi_filings
    FOR EACH ROW EXECUTE FUNCTION fn_gosi_filing_frozen();

-- --- eos_calculations: frozen at Final --------------------------------------

CREATE FUNCTION fn_eos_calculation_frozen() RETURNS trigger
LANGUAGE plpgsql AS $$
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

CREATE TRIGGER trg_eos_calculations_frozen
    BEFORE UPDATE OR DELETE ON eos_calculations
    FOR EACH ROW EXECUTE FUNCTION fn_eos_calculation_frozen();


-- =============================================================================
-- PART 4 — THE §11.2 DEFERRED TOTAL-RECONCILIATION TRIGGERS
--
-- "Lines are always authoritative. Every stored total is a cache written in the
-- same transaction as the lines, and every one has a named invariant enforced by
-- a deferred constraint trigger on the parent (checked at COMMIT, so partial
-- writes inside a transaction are legal)."
--
-- All of these are CONSTRAINT TRIGGERs, DEFERRABLE INITIALLY DEFERRED. Two
-- properties matter and are easy to lose:
--   * the body runs at COMMIT, so a writer may insert the lines and the total in
--     any order inside one transaction;
--   * a WHEN clause on a constraint trigger is evaluated at the time of the row
--     operation, NOT at commit — which is exactly what trg_run_totals needs, since
--     it is conditioned on a transition that has already happened by then.
-- Every function tolerates the parent having been deleted in the same transaction.
-- =============================================================================

-- --- payroll_slips: gross / deductions / net / statutory / loans / arrears ---

CREATE FUNCTION fn_slip_totals() RETURNS trigger
LANGUAGE plpgsql AS $$
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

COMMENT ON FUNCTION fn_slip_totals() IS
    '§11.2 trg_slip_totals. Four of the seven totals map onto payroll_slip_lines.kind directly. '
    'THREE DO NOT, because §11.2 says "the matching kinds" and no line column names them: '
    'employee_statutory_total is read as the Deduction lines carrying a gosi_branch with '
    'gosi_payer=''Employee''; loan_deductions as the Deduction lines carrying a loan_installment_id; '
    'arrears_amount as the signed sum of the lines whose payroll_input is kind=''Arrears''. Those '
    'three readings are inferences, and they are the only ones the schema supports.';

CREATE CONSTRAINT TRIGGER trg_slip_totals
    AFTER INSERT OR UPDATE ON payroll_slips
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION fn_slip_totals();

CREATE CONSTRAINT TRIGGER trg_slip_totals_lines
    AFTER INSERT OR UPDATE OR DELETE ON payroll_slip_lines
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION fn_slip_totals();

-- --- payroll_runs: totals, conditioned on Processing -> Processed ------------
--
-- §11.2, verbatim: "enforced only in the transaction that moves the run
-- Processing -> Processed, and not at all while Processing (§19.5 commits the run
-- in ~25 chunks; a trigger that fired on every chunk would either fail all of them
-- or pass on a partial total). While a run is Processing its stored totals are
-- undefined and must not be displayed as authoritative."
--
-- Hence: one trigger, on the parent only, with no trigger on payroll_slips at all.

CREATE FUNCTION fn_run_totals() RETURNS trigger
LANGUAGE plpgsql AS $$
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

CREATE CONSTRAINT TRIGGER trg_run_totals
    AFTER UPDATE ON payroll_runs
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW
    WHEN (OLD.status = 'Processing' AND NEW.status = 'Processed')
    EXECUTE FUNCTION fn_run_totals();

-- --- wps_batches.total_amount, employee_count -------------------------------

CREATE FUNCTION fn_wps_totals() RETURNS trigger
LANGUAGE plpgsql AS $$
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

CREATE CONSTRAINT TRIGGER trg_wps_totals
    AFTER INSERT OR UPDATE ON wps_batches
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fn_wps_totals();

CREATE CONSTRAINT TRIGGER trg_wps_totals_lines
    AFTER INSERT OR UPDATE OR DELETE ON wps_lines
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fn_wps_totals();

-- --- loans.outstanding ------------------------------------------------------
--
-- §11.2 calls this "the one the audit called sharpest": three writers (payroll
-- recovery, early settlement, final settlement), one invariant, checked at every
-- commit. §19.5 explains why READ COMMITTED is enough — recovery UPDATEs the
-- single loans row, so the row lock serialises concurrent recoveries.

CREATE FUNCTION fn_loan_outstanding() RETURNS trigger
LANGUAGE plpgsql AS $$
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

CREATE CONSTRAINT TRIGGER trg_loan_outstanding
    AFTER INSERT OR UPDATE ON loans
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fn_loan_outstanding();

CREATE CONSTRAINT TRIGGER trg_loan_outstanding_installments
    AFTER INSERT OR UPDATE OR DELETE ON loan_installments
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fn_loan_outstanding();

-- --- timesheets.total_minutes -----------------------------------------------
--
-- §11.2 writes this invariant as "timesheets.total_hours = SUM(timesheet_entries.hours)".
-- Neither column exists: 017_leave_attendance.sql built timesheets.total_minutes and
-- timesheet_entries.minutes, which is the same fact at the grain the rest of the
-- design uses (timesheet_day_reconciliations is minutes on both sides, and its CHECK
-- already depends on that). Implemented in minutes; §11.2's wording is the stale side.

CREATE FUNCTION fn_timesheet_minutes() RETURNS trigger
LANGUAGE plpgsql AS $$
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

CREATE CONSTRAINT TRIGGER trg_timesheet_minutes
    AFTER INSERT OR UPDATE ON timesheets
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fn_timesheet_minutes();

CREATE CONSTRAINT TRIGGER trg_timesheet_minutes_entries
    AFTER INSERT OR UPDATE OR DELETE ON timesheet_entries
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fn_timesheet_minutes();

-- --- final_settlements.gross / deductions / net ------------------------------
--
-- "the signed SUM of final_settlement_lines". The sign comes from the kind, whose
-- closed set ck_final_settlement_lines__kind already fixes: the five entitlements
-- are gross, LoanRecovery and OtherDeduction are deductions, net = gross - deductions.

CREATE FUNCTION fn_settlement_totals() RETURNS trigger
LANGUAGE plpgsql AS $$
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

CREATE CONSTRAINT TRIGGER trg_settlement_totals
    AFTER INSERT OR UPDATE ON final_settlements
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fn_settlement_totals();

CREATE CONSTRAINT TRIGGER trg_settlement_totals_lines
    AFTER INSERT OR UPDATE OR DELETE ON final_settlement_lines
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fn_settlement_totals();

-- --- gosi_filings: branch x payer totals — WARN, not block -------------------
--
-- §11.2: "= SUM over the payroll_slip_lines of the runs in that period and
-- establishment, or a recorded variance_reason ... (WARN, not block: a filed
-- return may legitimately differ, and the variance is the point)". So this one
-- raises WARNING and never aborts.
--
-- CAVEAT, and it is a real gap: payroll_slip_lines.gosi_branch and .gosi_payer are
-- unconstrained varchar(40). §9's closing paragraph lists the closed sets that get
-- a named CHECK and does not include either of them, so the baseline has no
-- vocabulary for the branch and payer this aggregation pivots on. The literals
-- below come from §13's statutory description — Annuities, SANED, Occupational
-- Hazards, paid by Employee or Employer — and a typo in a writer silently
-- contributes to no total at all. Until §9 gains those two CHECKs this comparison
-- is advisory in a second sense as well as the one §11.2 intended.

CREATE FUNCTION fn_gosi_filing_totals() RETURNS trigger
LANGUAGE plpgsql AS $$
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

CREATE CONSTRAINT TRIGGER trg_gosi_filing_totals
    AFTER INSERT OR UPDATE ON gosi_filings
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fn_gosi_filing_totals();

-- --- gl_journals: SUM(debit) = SUM(credit) per journal -----------------------

CREATE FUNCTION fn_gl_balance() RETURNS trigger
LANGUAGE plpgsql AS $$
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

CREATE CONSTRAINT TRIGGER trg_gl_balance
    AFTER INSERT OR UPDATE ON gl_journals
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fn_gl_balance();

CREATE CONSTRAINT TRIGGER trg_gl_balance_lines
    AFTER INSERT OR UPDATE OR DELETE ON gl_journal_lines
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fn_gl_balance();


-- =============================================================================
-- PART 5 — BEFORE DELETE GUARDS ON EVERY CASCADE PARENT (§8.1)
--
-- "CASCADE only where the child is a part of its parent ... Every CASCADE parent
-- also has a BEFORE DELETE guard that raises once the parent leaves its editable
-- state, so CASCADE can only ever fire on a draft."
--
-- Eleven parents in the schema own a part-of CASCADE and have a lifecycle state to
-- leave. Each gets a guard naming the states in which it is still a draft:
--
--   payroll_runs        (slips, issues)            Draft, Voided
--   payroll_slips       (slip lines)               run is Draft or Processing
--   wps_batches         (wps lines)                Generated
--   gl_journals         (journal lines)            Draft
--   final_settlements   (settlement lines)         Draft, Cancelled
--   loans               (installment schedule)     PendingApproval, Rejected
--   timesheets          (entries, reconciliations) Draft, Rejected
--   notifications       (deliveries)               nothing has left the queue
--   background_jobs     (job items)                not Running or Leased
--   employees           (employee_documents)       Draft
--   statutory_rules     (rule bands)               not cited by any slip line
--
-- FOUR CASCADE parents deliberately get no state guard: tenants, users, roles and
-- platform_users. Their CASCADE is ownership, not part-of, and their delete is the
-- §8.3 tenant purge itself — an explicit ordered job that has already resolved
-- every RESTRICT business row before it reaches them. A state guard there would
-- block the purge it is meant to protect, and §12.1 already restricts who may
-- soft-delete them. Stated here so the omission reads as a decision.
-- =============================================================================

CREATE FUNCTION fn_cascade_parent_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
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

COMMENT ON FUNCTION fn_cascade_parent_guard() IS
    '§8.1: a CASCADE may only ever fire on a draft. Generic over any parent with a status column; '
    'the editable states are the trigger arguments.';

CREATE TRIGGER trg_payroll_runs_cascade_guard      BEFORE DELETE ON payroll_runs
    FOR EACH ROW EXECUTE FUNCTION fn_cascade_parent_guard('Draft', 'Voided');
CREATE TRIGGER trg_wps_batches_cascade_guard       BEFORE DELETE ON wps_batches
    FOR EACH ROW EXECUTE FUNCTION fn_cascade_parent_guard('Generated');
CREATE TRIGGER trg_gl_journals_cascade_guard       BEFORE DELETE ON gl_journals
    FOR EACH ROW EXECUTE FUNCTION fn_cascade_parent_guard('Draft');
CREATE TRIGGER trg_final_settlements_cascade_guard BEFORE DELETE ON final_settlements
    FOR EACH ROW EXECUTE FUNCTION fn_cascade_parent_guard('Draft', 'Cancelled');
CREATE TRIGGER trg_loans_cascade_guard             BEFORE DELETE ON loans
    FOR EACH ROW EXECUTE FUNCTION fn_cascade_parent_guard('PendingApproval', 'Rejected');
CREATE TRIGGER trg_timesheets_cascade_guard        BEFORE DELETE ON timesheets
    FOR EACH ROW EXECUTE FUNCTION fn_cascade_parent_guard('Draft', 'Rejected');
CREATE TRIGGER trg_background_jobs_cascade_guard   BEFORE DELETE ON background_jobs
    FOR EACH ROW EXECUTE FUNCTION fn_cascade_parent_guard('Queued', 'Succeeded', 'Failed', 'Cancelled');
CREATE TRIGGER trg_employees_cascade_guard         BEFORE DELETE ON employees
    FOR EACH ROW EXECUTE FUNCTION fn_cascade_parent_guard('Draft');

-- payroll_slips has no status of its own: its editable state is its run's.
CREATE FUNCTION fn_payroll_slips_cascade_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE v_run text;
BEGIN
    SELECT status INTO v_run FROM payroll_runs WHERE tenant_id = OLD.tenant_id AND id = OLD.run_id;
    IF v_run IS NOT NULL AND v_run NOT IN ('Draft', 'Processing', 'Voided') THEN
        RAISE EXCEPTION 'cannot delete payroll_slips.%: its run is %', OLD.id, v_run
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN OLD;
END $$;

CREATE TRIGGER trg_payroll_slips_cascade_guard BEFORE DELETE ON payroll_slips
    FOR EACH ROW EXECUTE FUNCTION fn_payroll_slips_cascade_guard();

-- notifications: a delivery that has left the queue is an operational fact.
CREATE FUNCTION fn_notifications_cascade_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF EXISTS (SELECT 1 FROM notification_deliveries
                WHERE tenant_id = OLD.tenant_id AND notification_id = OLD.id
                  AND status NOT IN ('Queued', 'Suppressed')) THEN
        RAISE EXCEPTION 'cannot delete notifications.%: a delivery has already left the queue', OLD.id
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN OLD;
END $$;

CREATE TRIGGER trg_notifications_cascade_guard BEFORE DELETE ON notifications
    FOR EACH ROW EXECUTE FUNCTION fn_notifications_cascade_guard();

-- statutory_rules is reference tier and has no status; its "editable state" is
-- "nothing has been calculated from it yet". Its bands CASCADE, and a slip line
-- that cites the rule holds a RESTRICT on the rule but not on its bands, so the
-- bands could otherwise vanish from under a filed payslip.
CREATE FUNCTION fn_statutory_rules_cascade_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
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

CREATE TRIGGER trg_statutory_rules_cascade_guard BEFORE DELETE ON statutory_rules
    FOR EACH ROW EXECUTE FUNCTION fn_statutory_rules_cascade_guard();


-- =============================================================================
-- PART 6 — APPEND-ONLY TABLES (§1, §12.3)
--
-- "leave_ledger, audit_logs, payroll_audit_logs and retention_purge_audits have
-- BEFORE UPDATE/DELETE triggers that raise. Corrections are reversing rows."
--
-- audit_logs is the one exception, and it is exactly one UPDATE shape wide.
-- =============================================================================

CREATE FUNCTION fn_append_only() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION '% on % is refused: the table is append-only; write a reversing row instead',
        TG_OP, TG_TABLE_NAME USING ERRCODE = 'check_violation';
END $$;

CREATE TRIGGER trg_leave_ledger_append_only
    BEFORE UPDATE OR DELETE ON leave_ledger
    FOR EACH ROW EXECUTE FUNCTION fn_append_only();

CREATE TRIGGER trg_payroll_audit_logs_append_only
    BEFORE UPDATE OR DELETE ON payroll_audit_logs
    FOR EACH ROW EXECUTE FUNCTION fn_append_only();

CREATE TRIGGER trg_retention_purge_audits_append_only
    BEFORE UPDATE OR DELETE ON retention_purge_audits
    FOR EACH ROW EXECUTE FUNCTION fn_append_only();

-- -----------------------------------------------------------------------------
-- audit_logs: append-only with EXACTLY ONE permitted UPDATE shape.
--
-- §12.3: "Audit rows are redacted in place too — personal_data (including the ip
-- and user_agent it carries), before and after are nulled and
-- personal_data_erased_at stamped, through the single permitted UPDATE shape of
-- the conventions. envelope_hash is untouched, and because the row keeps
-- personal_data_hash, before_hash and after_hash, a verifier can RECOMPUTE the
-- envelope hash from the surviving columns plus those digests rather than trusting
-- the stored value."
--
-- That last sentence is the whole reason this guard is column-exact rather than a
-- hash comparison: "comparing a stored hash with itself proves nothing, because
-- anyone able to UPDATE the row could rewrite row, hash and root together". So the
-- guard is written the only way that binds: every column but the four is compared
-- to its old value, and any difference is refused.
--
-- The permitted shape, in full:
--   * personal_data_erased_at goes from NULL to a non-NULL timestamp — once, and
--     never back;
--   * personal_data, before and after all become NULL;
--   * every other column, including envelope_hash and the three digests, is
--     byte-identical to what it was.
-- Anything else — a second erasure, a hash edit, a seq edit, a row that erases the
-- payload without stamping, a row that stamps without erasing — is refused.
-- ck_audit_logs__erasure_is_complete already refuses the stamped-but-not-nulled
-- half at CHECK level; this refuses the other five.
-- -----------------------------------------------------------------------------

CREATE FUNCTION fn_audit_logs_append_only() RETURNS trigger
LANGUAGE plpgsql AS $$
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

COMMENT ON FUNCTION fn_audit_logs_append_only() IS
    '§12.3 PDPL erasure: the single permitted UPDATE shape on audit_logs. Column-exact by design — a '
    'hash comparison would prove nothing, because whoever can UPDATE the row could rewrite the hash '
    'with it. envelope_hash and the three digests survive so a verifier can recompute the envelope '
    'hash against a Merkle root published before the erasure.';

CREATE TRIGGER trg_audit_logs_append_only
    BEFORE UPDATE OR DELETE ON audit_logs
    FOR EACH ROW EXECUTE FUNCTION fn_audit_logs_append_only();
