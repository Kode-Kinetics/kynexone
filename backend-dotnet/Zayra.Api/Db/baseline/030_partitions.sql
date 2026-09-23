-- =============================================================================
-- 030_partitions.sql
-- KynexOne baseline — the five monthly RANGE-partitioned parents, their initial
-- children, the DEFAULT catch-alls, and the maintenance template the
-- `PartitionMaintenance` job runs.
--
-- Source of truth: TARGET_SCHEMA.md revision 6 §19.3; CONVENTIONS.md §13.
-- Applied by kynex_migrator with SET ROLE kynex_owner, after 021_constraints.
--
-- -----------------------------------------------------------------------------
-- WHY THIS FILE LOOKS LIKE A REBUILD AND NOT AN ALTER
--
-- The `-- PARTITIONED:` comments in 017/018 say "030_partitions.sql converts the
-- parent". PostgreSQL has no such statement. There is no
-- `ALTER TABLE … PARTITION BY …` in any release through 17:
--
--     ERROR:  syntax error at or near "PARTITION"
--     LINE 1: ALTER TABLE q PARTITION BY RANGE (k);
--
-- A table is partitioned at CREATE or not at all. The documented migration for
-- an existing table is rename → create the partitioned parent → move the rows →
-- drop the old table, and there is no cheaper route.
--
-- Two ways to honour §19.1, which puts "the five partitioned parents" in this
-- file rather than in 010–018:
--   (a) add `PARTITION BY RANGE (…)` to the CREATE TABLE in 017/018 and leave
--       only children here — minimal, but it moves the parents out of the file
--       §19.1 assigns them to, and edits two other engineers' files;
--   (b) rebuild the five parents here from the shape 017/018 already declared.
-- (b) is taken. The rebuild copies the column list, types, NOT NULLs, defaults
-- and CHECK constraints from the live table with `LIKE … INCLUDING …`, so this
-- file never restates a column and cannot drift from 017/018. What it DOES
-- restate, deliberately and visibly, is exactly what §19.3 says the partition
-- key forces: the composite primary keys and unique constraints, and the
-- foreign keys, because `LIKE` never copies a foreign key.
--
-- The rebuild is free at baseline time — the tables are empty — and this file is
-- baseline-only. It is NOT a template for partitioning a populated table later;
-- that is the scheduled, batched operation of §19.3 consequence 4.
-- =============================================================================

SET search_path = public, pg_catalog;


-- -----------------------------------------------------------------------------
-- 1. RELEASE THE ONE INBOUND FOREIGN KEY
--
-- `attendance_days` is the only partitioned table that is an FK target (§19.3
-- consequence 2, §8 row 126). The referencing constraint must be dropped before
-- the parent can be replaced, and is re-created against the partitioned parent
-- in section 5 with the identical name, columns and actions.
-- -----------------------------------------------------------------------------

ALTER TABLE timesheet_day_reconciliations
    DROP CONSTRAINT fk_timesheet_day_reconciliations__attendance_day_id;


-- -----------------------------------------------------------------------------
-- 2. REBUILD THE FIVE PARENTS AS PARTITIONED TABLES
--
-- `LIKE … INCLUDING CONSTRAINTS` copies CHECK constraints WITH their names
-- (verified: ck_* names survive), and INCLUDING COMMENTS copies the column
-- comments. It does not copy the TABLE comment, so each is captured and
-- re-applied. INCLUDING INDEXES is deliberately omitted: it copies PK/UNIQUE
-- constraints but renames the underlying indexes by PostgreSQL's default rules,
-- which would silently replace `pk_attendance_punches` with
-- `attendance_punches_pkey` and break the naming convention of CONVENTIONS.md §1
-- and the byte-diff schema-drift gate of §19.1. The keys are therefore declared
-- explicitly in section 3.
--
-- The old table is dropped BEFORE the keys are added, because an index name is
-- unique per schema and the pre-conversion table still holds those names.
-- -----------------------------------------------------------------------------

DO $$
DECLARE
    r        record;
    v_stage  text;
    v_note   text;
BEGIN
    FOR r IN
        SELECT * FROM (VALUES
            ('attendance_punches',   'occurred_at'),
            ('attendance_days',      'work_date'),
            ('timesheet_entries',    'work_date'),
            ('audit_logs',           'created_at'),
            ('background_job_items', 'created_at')
        ) AS v(tbl, key)
    LOOP
        v_stage := r.tbl || '__preconvert';

        -- Once-only, like 010-021 and unlike 001/002. Re-running would leave
        -- the file half-applied (parents skipped, keys re-added), so it refuses
        -- instead. The baseline ships as one EF migration and applies once; a
        -- re-apply means the database is not the one this file expects.
        IF EXISTS (SELECT 1 FROM pg_class c
                    JOIN pg_namespace n ON n.oid = c.relnamespace
                   WHERE n.nspname = 'public' AND c.relname = r.tbl
                     AND c.relkind = 'p') THEN
            RAISE EXCEPTION
              '030_partitions.sql has already been applied (% is partitioned). This file is once-only.',
              r.tbl;
        END IF;

        v_note := obj_description(format('public.%I', r.tbl)::regclass, 'pg_class');

        EXECUTE format('ALTER TABLE public.%I RENAME TO %I', r.tbl, v_stage);

        EXECUTE format(
            'CREATE TABLE public.%I (LIKE public.%I
                 INCLUDING DEFAULTS
                 INCLUDING CONSTRAINTS
                 INCLUDING COMMENTS
                 INCLUDING STORAGE
                 INCLUDING COMPRESSION
                 INCLUDING GENERATED
                 INCLUDING STATISTICS)
             PARTITION BY RANGE (%I)', r.tbl, v_stage, r.key);

        EXECUTE format('DROP TABLE public.%I', v_stage);

        IF v_note IS NOT NULL THEN
            EXECUTE format('COMMENT ON TABLE public.%I IS %L', r.tbl, v_note);
        END IF;
    END LOOP;
END
$$;


-- -----------------------------------------------------------------------------
-- 3. THE KEYS THE PARTITION KEY FORCES (§19.3 consequence 1)
--
-- Every unique constraint on a partitioned table must contain the partition key,
-- because PostgreSQL can only enforce uniqueness within one child. The names and
-- column lists below are identical to those 017/018 already declared — 017/018
-- anticipated the partitioning correctly — so this section adds no new key; it
-- re-creates the same ones on the partitioned parent.
--
-- Two of them are a REAL, if small, weakening, and §19.3 says so out loud rather
-- than hiding it:
--   * attendance_punches.idempotency_key is unique per (tenant, key, occurred_at)
--     instead of per (tenant, key). Safe in practice because the key is derived
--     from (device serial, employee, occurred_at), so a duplicate always arrives
--     carrying the same occurred_at and still collides.
--   * audit_logs (chain_key, seq) is unique per (chain_key, seq, created_at).
--     Cross-partition uniqueness of seq is guaranteed at ALLOCATION instead, by a
--     per-tenant sequence that never reuses a value, and the checkpointer walks
--     contiguous seq ranges and raises on a gap or a duplicate.
-- attendance_days (tenant_id, employee_id, work_date) already contained the key
-- and is unchanged.
-- -----------------------------------------------------------------------------

ALTER TABLE attendance_punches
    ADD CONSTRAINT pk_attendance_punches PRIMARY KEY (id, occurred_at),
    ADD CONSTRAINT uq_attendance_punches__tenant_id
        UNIQUE (tenant_id, id, occurred_at),
    ADD CONSTRAINT uq_attendance_punches__idempotency_key
        UNIQUE (tenant_id, idempotency_key, occurred_at),
    ADD CONSTRAINT uq_attendance_punches__device_external_id
        UNIQUE (tenant_id, device_id, external_id, occurred_at);

ALTER TABLE attendance_days
    ADD CONSTRAINT pk_attendance_days PRIMARY KEY (id, work_date),
    ADD CONSTRAINT uq_attendance_days__tenant_id
        UNIQUE (tenant_id, id, work_date),
    ADD CONSTRAINT uq_attendance_days__employee_work_date
        UNIQUE (tenant_id, employee_id, work_date);

ALTER TABLE timesheet_entries
    ADD CONSTRAINT pk_timesheet_entries PRIMARY KEY (id, work_date),
    ADD CONSTRAINT uq_timesheet_entries__tenant_id
        UNIQUE (tenant_id, id, work_date);

ALTER TABLE audit_logs
    ADD CONSTRAINT pk_audit_logs PRIMARY KEY (id, created_at),
    ADD CONSTRAINT uq_audit_logs__tenant_id
        UNIQUE (tenant_id, id, created_at),
    ADD CONSTRAINT uq_audit_logs__chain_seq
        UNIQUE (chain_key, seq, created_at);

ALTER TABLE background_job_items
    ADD CONSTRAINT pk_background_job_items PRIMARY KEY (id, created_at),
    ADD CONSTRAINT uq_background_job_items__tenant_id
        UNIQUE (tenant_id, id, created_at);


-- -----------------------------------------------------------------------------
-- 4. OUTBOUND FOREIGN KEYS, RE-CREATED
--
-- `LIKE` never copies a foreign key, so the constraints 021_constraints_g_r.sql
-- put on these tables were dropped with the pre-conversion table. They are
-- restored here verbatim — same names, same columns, same ON UPDATE / ON DELETE
-- actions, same §8 row references. `audit_logs` is FK-free by design (§Q), so it
-- has nothing to restore.
--
-- If 021 ever changes one of these, this section must change with it. The
-- schema-drift gate of §19.1 catches the divergence: a constraint changed in 021
-- and not here produces a different pg_dump.
-- -----------------------------------------------------------------------------

ALTER TABLE attendance_punches
    ADD CONSTRAINT fk_attendance_punches__employee_id
        FOREIGN KEY (tenant_id, employee_id) REFERENCES employees (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    ADD CONSTRAINT fk_attendance_punches__device_id
        FOREIGN KEY (tenant_id, device_id) REFERENCES attendance_devices (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE SET NULL (device_id),
    ADD CONSTRAINT fk_attendance_punches__approval_request_id
        FOREIGN KEY (tenant_id, approval_request_id) REFERENCES approval_requests (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT;

ALTER TABLE attendance_days
    ADD CONSTRAINT fk_attendance_days__employee_id
        FOREIGN KEY (tenant_id, employee_id) REFERENCES employees (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    ADD CONSTRAINT fk_attendance_days__shift_id
        FOREIGN KEY (tenant_id, shift_id) REFERENCES shifts (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE SET NULL (shift_id),
    -- voiding or deleting a draft run unlocks the days (§8 row 115)
    ADD CONSTRAINT fk_attendance_days__locked_run_id
        FOREIGN KEY (tenant_id, locked_run_id) REFERENCES payroll_runs (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE SET NULL (locked_run_id);

ALTER TABLE timesheet_entries
    -- CASCADE guarded: the guard raises unless the timesheet is Draft or
    -- Rejected, which are the only states in which entries are editable.
    ADD CONSTRAINT fk_timesheet_entries__timesheet_id
        FOREIGN KEY (tenant_id, timesheet_id) REFERENCES timesheets (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE CASCADE,
    ADD CONSTRAINT fk_timesheet_entries__cost_center_id
        FOREIGN KEY (tenant_id, cost_center_id) REFERENCES cost_centers (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT;

ALTER TABLE background_job_items
    -- single-column: background_jobs.tenant_id is nullable for platform jobs, so
    -- a composite reference would not be enforced for them (§8 row 155)
    ADD CONSTRAINT fk_background_job_items__job_id
        FOREIGN KEY (job_id) REFERENCES background_jobs (id)
        ON UPDATE RESTRICT ON DELETE CASCADE;


-- -----------------------------------------------------------------------------
-- 5. THE ONE INBOUND FOREIGN KEY, RESTORED (§19.3 consequence 2, §8 row 126)
-- -----------------------------------------------------------------------------

ALTER TABLE timesheet_day_reconciliations
    ADD CONSTRAINT fk_timesheet_day_reconciliations__attendance_day_id
        FOREIGN KEY (tenant_id, attendance_day_id, work_date)
        REFERENCES attendance_days (tenant_id, id, work_date)
        ON UPDATE RESTRICT ON DELETE RESTRICT;


-- =============================================================================
-- 6. THE MAINTENANCE TEMPLATE (§19.3 consequence 3, CONVENTIONS.md §13 rule 1)
--
-- ONE fixed template, taking no parameter but the month, covering all five
-- parents. It creates the child, enables and FORCES row level security on it,
-- and issues NO GRANT OF ANY KIND. There is no code path in this schema that
-- grants on a partition child, which is the property CONVENTIONS.md §13 rule 2
-- makes the ratchet assert.
--
-- Why RLS on the child is not optional, demonstrated rather than asserted: a
-- policy on the parent governs access THROUGH the parent only. Naming the child
-- directly evaluates the CHILD's relrowsecurity and the CHILD's own policies —
-- of which there are none. Measured in postgres:16:
--      child with RLS off + a grant  →  every tenant's rows
--      child with RLS on  + a grant  →  0 rows (no policy ⇒ deny all)
--      child with RLS on  + no grant →  ERROR 42501
-- The template gives the last two and never the first.
--
-- SECURITY DEFINER because kynex_job holds no DDL privilege and must not: the
-- job's authority is to run this template, not to create tables. The parent name
-- is not a parameter — it is matched against a hard-coded list — so there is no
-- identifier the caller controls and no injection surface.
--
-- The range bound is formatted from the key column's own type. This matters:
-- a `timestamptz` partition bound is resolved against the SERVER's TimeZone at
-- DDL time, so `FOR VALUES FROM ('2026-01-01')` would silently mean a different
-- instant on a server set to Asia/Riyadh than on one set to UTC, and the two
-- would leave a three-hour hole that lands rows in the DEFAULT partition. The
-- bounds are therefore written with an explicit +00 offset for timestamptz keys
-- and as plain dates for date keys.
-- =============================================================================

CREATE OR REPLACE FUNCTION app.ensure_partition_month(p_month date)
    RETURNS integer
    LANGUAGE plpgsql
    SECURITY DEFINER
    SET search_path = pg_catalog, app
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

REVOKE ALL ON FUNCTION app.ensure_partition_month(date) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app.ensure_partition_month(date) TO kynex_job;

COMMENT ON FUNCTION app.ensure_partition_month(date) IS
  'The fixed partition template of §19.3: creates the month''s child on each of the five partitioned parents, enables and FORCES row level security on it, and issues no GRANT of any kind. The parent list is hard-coded, so the month is the only input. Returns how many children it created.';


-- The job entry point. §19.3: pre-create three months ahead, alert below two.
CREATE OR REPLACE FUNCTION app.ensure_partition_headroom(p_months_ahead integer DEFAULT 3)
    RETURNS integer
    LANGUAGE plpgsql
    SECURITY DEFINER
    SET search_path = pg_catalog, app
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

REVOKE ALL ON FUNCTION app.ensure_partition_headroom(integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app.ensure_partition_headroom(integer) TO kynex_job;

COMMENT ON FUNCTION app.ensure_partition_headroom(integer) IS
  'What background_jobs kind=''PartitionMaintenance'' calls: ensures the current month and the next p_months_ahead exist on all five parents (§19.3). Idempotent.';


-- What the alert reads. Two signals, both from §19.3 rule 3: months of headroom
-- (alert below two) and DEFAULT partition occupancy (alert on the FIRST row, not
-- on a threshold, because the recovery is an ACCESS EXCLUSIVE validation scan).
-- Not SECURITY DEFINER: it reads only catalog and row counts the caller could
-- reach anyway, and it must not become a way to count another tenant's rows —
-- the count it returns is of the DEFAULT partition, under the caller's own RLS.
CREATE OR REPLACE FUNCTION app.partition_headroom()
    RETURNS TABLE (parent text, months_headroom integer, default_rows bigint)
    LANGUAGE plpgsql
    STABLE
AS $$
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
$$;

REVOKE ALL ON FUNCTION app.partition_headroom() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app.partition_headroom() TO kynex_job, kynex_platform, kynex_ro;

COMMENT ON FUNCTION app.partition_headroom() IS
  'The two §19.3 alert signals: months of pre-created headroom per parent (alert below 2) and rows sitting in the DEFAULT partition (alert on the first one). Not SECURITY DEFINER, so the DEFAULT row count it returns is the caller''s own tenant''s.';


-- =============================================================================
-- 7. THE INITIAL CHILDREN AND THE DEFAULT CATCH-ALLS
--
-- Fifteen months (§19.1): the eleven months before the current one, the current
-- one, and three ahead. The backward months exist because a go-live import of
-- historical attendance or an audit backfill lands in the past, and a row with
-- nowhere to go lands in DEFAULT — which §19.3 treats as an incident, not a
-- fallback. The three forward months are the headroom the job then maintains.
--
-- The children are created by CALLING THE TEMPLATE, not by repeating it, so the
-- baseline itself is the template's first test: if the template ever granted
-- anything, these 75 children would carry it and the proof script would fail.
-- =============================================================================

DO $$
DECLARE
    i integer;
BEGIN
    FOR i IN -11 .. 3 LOOP
        PERFORM app.ensure_partition_month(
            (date_trunc('month', now()) + make_interval(months => i))::date);
    END LOOP;
END
$$;

-- DEFAULT partitions. Created outside the template on purpose: a DEFAULT is
-- created once, at baseline, and never by the job — a job that could create a
-- DEFAULT could also silently repair the symptom of a missing month and hide the
-- alert. Same two RLS statements, same absence of any GRANT.
DO $$
DECLARE
    t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['attendance_punches','attendance_days','timesheet_entries',
                             'audit_logs','background_job_items']
    LOOP
        IF NOT EXISTS (
            SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE n.nspname = 'public' AND c.relname = t || '_default')
        THEN
            EXECUTE format('CREATE TABLE public.%I PARTITION OF public.%I DEFAULT',
                           t || '_default', t);
            EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY', t || '_default');
            EXECUTE format('ALTER TABLE public.%I FORCE ROW LEVEL SECURITY',  t || '_default');
            EXECUTE format('COMMENT ON TABLE public.%I IS %L', t || '_default',
                'DEFAULT catch-all (§19.3 rule 3). A row landing here is an incident: '
                'the month partition was missing. Alert on the FIRST row — recovery is '
                'DETACH CONCURRENTLY, create the month, batched INSERT…SELECT, then an '
                'ATTACH that takes ACCESS EXCLUSIVE and a full validation scan.');
        END IF;
    END LOOP;
END
$$;
