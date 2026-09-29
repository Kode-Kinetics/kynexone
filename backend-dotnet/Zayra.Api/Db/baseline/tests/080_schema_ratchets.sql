-- =============================================================================
-- tests/080_schema_ratchets.sql
--
-- The ratchets of KEEPING_DOCS_HONEST.md §3 that 070_rls_proof.sql does NOT
-- already make. Run it AFTER 070 — scripts/schema-ratchet-gate.sh runs both, in
-- that order, against a throwaway container. It never runs against a real
-- database.
--
-- ── WHAT IS REUSED, AND NOT REPEATED HERE ────────────────────────────────────
-- 070_rls_proof.sql already proves, and this file deliberately does not restate:
--
--   assertion  9 (part)  PROOF 6a   RLS ENABLE + FORCE on every relkind r/p
--   assertion  9 (part)  PROOF 6e   every relation has a manifest entry
--   assertion 10 (part)  PROOF 6b   every partition child forced, zero direct
--                                   grants to any login role
--   assertion 10 (part)  PROOF 6c   no partition child carries its own policy
--   assertion 13         PROOF 6d   every view carries security_invoker = true
--   assertion 13         PROOF 6f   the COUNTER-proof: the same view WITHOUT
--                                   security_invoker demonstrably leaks both
--                                   tenants, so the flag is what holds the door
--   assertion 14         PROOF 2h   no SECURITY DEFINER function executable by
--                                   PUBLIC or kynex_ro; every one pins
--                                   search_path; resolve_login is tenant-bound
--   assertion 15         PROOF 5    no LOGIN role reaches a BYPASSRLS role
--                                   except kynex_migrator, walked transitively
--                                   through pg_auth_members
--
-- Duplicating any of those would give two places to update and one of them
-- would rot. What follows is only what was missing.
--
-- ── WHAT IS NEW HERE ─────────────────────────────────────────────────────────
--   R1  policy SHAPE, not merely presence — the manifest's declared shape must
--       match the live policy text (assertion 9's sharp half)
--   R2  the manifest closes arithmetically, and in BOTH directions
--   R3  every table has a COMMENT, an @owner: from the four §12.4 roles, and a
--       retention declaration (assertions 8 and 12, as far as they can be made
--       today — see the PENDING note on R3d)
--   R4  every index that is not a constraint's index carries a COMMENT ON INDEX
--       naming the query it serves (§19.1, 040_indexes.sql)
--   R5  every table is heap with no tablespace — this one closes a hole the
--       drift gate's own normaliser opens (see the note on R5)
--
-- Every assertion raises on failure. Reaching the end is the pass.
-- =============================================================================

\set ON_ERROR_STOP on

\echo ''
\echo '=============================================================='
\echo ' 080 — schema ratchets (the assertions 070 does not make)'
\echo '=============================================================='


-- =============================================================================
-- R1 — THE MANIFEST'S DECLARED SHAPE MATCHES THE LIVE POLICY TEXT
--
-- KEEPING_DOCS_HONEST.md assertion 9, and §19.2's own words: "A table with RLS
-- enabled and the wrong policy is as wrong as a table with none — shape (a) on
-- retention_policies was exactly that defect." It has now happened twice
-- (retention_policies, then background_job_items) and BOTH TIMES the table
-- looked empty rather than wrong, because every check in front of it asked
-- whether a policy existed.
--
-- So this asks a different question: for each relation, the FULL SET of its
-- policies — name, command, grantee roles, USING text and WITH CHECK text — is
-- rendered as one signature and compared against the signatures its declared
-- shape permits. Declaring 'a' and carrying (b)'s policy fails. So does an
-- extra policy nobody declared, a policy narrowed from ALL to SELECT, a policy
-- re-granted from {public} to a named role, and any edit to the predicate.
--
-- The permitted signatures below are the six policy shapes of §19.2 written
-- out. They are long on purpose: a reviewer can read the tenancy predicate here
-- and compare it with the document without opening pg_policies.
-- =============================================================================

\echo ''
\echo '== R1. every relation carries exactly the policy its declared shape permits =='

DO $ratchet$
DECLARE
    v_bad text;
    n     int;
BEGIN
    CREATE TEMP TABLE permitted_shape_signature(shape text, signature text) ON COMMIT DROP;

    INSERT INTO permitted_shape_signature VALUES
      -- (a) tenant and company tier. Company-tier tables need nothing extra:
      -- company_id is already bound to the tenant by its composite FK.
      ('a', 'p_tenant :: ALL :: {public} :: USING (tenant_id = app.current_tenant()) :: CHECK (tenant_id = app.current_tenant())'),

      -- (b) nullable-tenant tier.
      ('b', 'p_tenant_or_platform :: ALL :: {public} :: USING ((tenant_id = app.current_tenant()) OR ((tenant_id IS NULL) AND app.is_platform())) :: CHECK ((tenant_id = app.current_tenant()) OR ((tenant_id IS NULL) AND app.is_platform()))'),

      -- (b) plus the kynex_job queue policy, so the leased-queue claim
      -- (FOR UPDATE SKIP LOCKED) can see queued PLATFORM work without needing
      -- is_platform(). Only the job role gets it, and only for tenant_id IS NULL.
      ('b', E'p_job_platform_queue :: ALL :: {kynex_job} :: USING (tenant_id IS NULL) :: CHECK (tenant_id IS NULL)\np_tenant_or_platform :: ALL :: {public} :: USING ((tenant_id = app.current_tenant()) OR ((tenant_id IS NULL) AND app.is_platform())) :: CHECK ((tenant_id = app.current_tenant()) OR ((tenant_id IS NULL) AND app.is_platform()))'),

      -- (c) reference tier: readable by everyone, protected by WITHHOLDING the
      -- write grant. No WITH CHECK, because there is no sanctioned write.
      ('c', 'p_reference_read :: SELECT :: {public} :: USING true :: CHECK -'),

      -- auth_sessions / auth_tokens: the only tables carrying both subject kinds.
      ('p_auth', 'p_auth :: ALL :: {public} :: USING ((((subject_kind)::text = ''Tenant''::text) AND (tenant_id = app.current_tenant())) OR (((subject_kind)::text = ''Platform''::text) AND (tenant_id IS NULL) AND app.is_platform())) :: CHECK ((((subject_kind)::text = ''Tenant''::text) AND (tenant_id = app.current_tenant())) OR (((subject_kind)::text = ''Platform''::text) AND (tenant_id IS NULL) AND app.is_platform()))'),

      -- platform_users: no tenant_id at all, so there is nothing to filter on.
      -- Policed by grant; the policy exists so a stolen kynex_app connection
      -- sees an EMPTY table rather than an operator list.
      ('platform', 'p_platform :: ALL :: {public} :: USING app.is_platform() :: CHECK app.is_platform()'),

      -- tenants itself: the row IS the tenant, so the predicate is on id.
      ('self_tenant', 'p_self_tenant :: ALL :: {public} :: USING ((id = app.current_tenant()) OR app.is_platform()) :: CHECK app.is_platform()'),

      -- data_protection_keys: the ASP.NET key ring. Every session must read
      -- every key or no cookie issued by another instance decrypts; the table
      -- holds no tenant data. Grant-policed, RLS on and forced so the intent is
      -- recorded rather than assumed.
      ('keyring', 'p_keyring :: ALL :: {public} :: USING true :: CHECK true');

    WITH live AS (
        SELECT m.relname, m.shape,
               coalesce(string_agg(
                   p.policyname || ' :: ' || p.cmd || ' :: ' || p.roles::text ||
                   ' :: USING ' || coalesce(p.qual, '-') ||
                   ' :: CHECK ' || coalesce(p.with_check, '-'),
                   E'\n' ORDER BY p.policyname), '<none>') AS signature
          FROM app.rls_manifest m
          JOIN pg_class c      ON c.relname = m.relname
          JOIN pg_namespace ns ON ns.oid = c.relnamespace AND ns.nspname = 'public'
          LEFT JOIN pg_policies p ON p.tablename = m.relname AND p.schemaname = 'public'
         WHERE NOT c.relispartition        -- children: PROOF 6c already requires zero policies
           AND c.relkind <> 'v'            -- views:    PROOF 6d already requires security_invoker
         GROUP BY m.relname, m.shape)
    SELECT string_agg(format(E'\n     %s declares shape "%s" but carries:\n       %s',
                             l.relname, l.shape, replace(l.signature, E'\n', E'\n       ')),
                      '' ORDER BY l.relname)
      INTO v_bad
      FROM live l
     WHERE NOT EXISTS (SELECT 1 FROM permitted_shape_signature s
                        WHERE s.shape = l.shape AND s.signature = l.signature);

    IF v_bad IS NOT NULL THEN
        RAISE EXCEPTION E'R1 FAILED — a relation''s live policy does not match its declared shape.%\n\n   A table with RLS on and the WRONG policy looks empty, not wrong. Either fix\n   060_policies.sql or, if the new shape is intended, add it to the permitted\n   list above WITH the reason — never just to make this pass.', v_bad;
    END IF;

    SELECT count(*) INTO n
      FROM app.rls_manifest m
      JOIN pg_class c ON c.relname = m.relname
      JOIN pg_namespace ns ON ns.oid = c.relnamespace AND ns.nspname = 'public'
     WHERE NOT c.relispartition AND c.relkind <> 'v';
    IF n < 76 THEN
        RAISE EXCEPTION 'R1 INCONCLUSIVE — only % non-child relations compared; the baseline has 76 tables', n;
    END IF;
    RAISE NOTICE '   % non-child relations: live policy == declared shape        ok', n;
END
$ratchet$;


-- =============================================================================
-- R2 — THE MANIFEST CLOSES, IN BOTH DIRECTIONS AND ARITHMETICALLY
--
-- PROOF 6e walks relation → manifest. That catches a new table with no entry.
-- It does NOT catch the other direction: an entry for a table that was dropped
-- or renamed, which leaves the ratchet quietly asserting something about
-- nothing. Assertion 9 asks for both, plus the sum — "a count that does not
-- close is how revision 6 shipped 66+6+4=76 while platform_users and
-- data_protection_keys sat in no shape at all".
--
-- A partition child is declared with its PARENT's shape. That is checked too: a
-- child inheriting the wrong shape would make the population arithmetic add up
-- while describing the wrong thing.
-- =============================================================================

\echo ''
\echo '== R2. the manifest closes: no orphan entry, and the populations sum =='

DO $ratchet$
DECLARE
    v_bad  text;
    n_rel  int;
    n_man  int;
    n_sum  int;
BEGIN
    -- 2a. no entry without a relation (the direction PROOF 6e does not walk)
    SELECT string_agg(m.relname || ' (shape ' || m.shape || ')', ', ' ORDER BY m.relname)
      INTO v_bad
      FROM app.rls_manifest m
     WHERE NOT EXISTS (
        SELECT 1 FROM pg_class c JOIN pg_namespace ns ON ns.oid = c.relnamespace
         WHERE ns.nspname = 'public' AND c.relname = m.relname AND c.relkind IN ('r','p','v'));
    IF v_bad IS NOT NULL THEN
        RAISE EXCEPTION E'R2a FAILED — the manifest declares a shape for a relation that does not exist: %\n   The entry was left behind by a DROP or a rename. Remove it from 060_policies.sql:\n   a ratchet asserting something about nothing reports green by definition.', v_bad;
    END IF;

    -- 2b. the arithmetic closes over relkind r, p AND v
    SELECT count(*) INTO n_rel
      FROM pg_class c JOIN pg_namespace ns ON ns.oid = c.relnamespace
     WHERE ns.nspname = 'public' AND c.relkind IN ('r','p','v')
       AND NOT EXISTS (SELECT 1 FROM pg_depend d
                        WHERE d.classid = 'pg_class'::regclass AND d.objid = c.oid AND d.deptype = 'e');
    SELECT count(*) INTO n_man FROM app.rls_manifest;
    SELECT sum(cnt) INTO n_sum FROM (SELECT count(*) AS cnt FROM app.rls_manifest GROUP BY shape) q;

    IF n_rel <> n_man OR n_man <> n_sum THEN
        RAISE EXCEPTION 'R2b FAILED — the count does not close: % relations (r/p/v, extension objects excluded), % manifest entries, % summed across shapes.', n_rel, n_man, n_sum;
    END IF;

    -- 2c. a partition child declares its PARENT's shape
    SELECT string_agg(format('%s declares "%s" but its parent %s declares "%s"',
                             cm.relname, cm.shape, pm.relname, pm.shape), ', ')
      INTO v_bad
      FROM pg_inherits i
      JOIN pg_class ch ON ch.oid = i.inhrelid
      JOIN pg_class pa ON pa.oid = i.inhparent
      JOIN app.rls_manifest cm ON cm.relname = ch.relname
      JOIN app.rls_manifest pm ON pm.relname = pa.relname
     WHERE cm.shape <> pm.shape;
    IF v_bad IS NOT NULL THEN
        RAISE EXCEPTION 'R2c FAILED — a partition child declares a different shape from its parent: %', v_bad;
    END IF;

    RAISE NOTICE '   % relations == % manifest entries, summed across % shapes    ok',
                 n_rel, n_man, (SELECT count(DISTINCT shape) FROM app.rls_manifest);
END
$ratchet$;


-- =============================================================================
-- R3 — A COMMENT, AN OWNER AND A RETENTION DECLARATION ON EVERY TABLE
--
-- KEEPING_DOCS_HONEST.md assertion 8 ("every table names the capability it
-- serves") and assertion 12 ("a retention row and an owner per table — the one
-- with legal consequences").
--
-- Decision 9 dropped the hard table cap, so counting tables stopped being the
-- brake. This is what replaced it: a table that nobody will name an owner for,
-- or state a retention period for, does not ship.
-- =============================================================================

\echo ''
\echo '== R3. every table: a COMMENT, an @owner from the four roles, a retention declaration =='

DO $ratchet$
DECLARE
    v_bad text;
    n     int;
    -- The four owner roles of §12.4. retention_policies.owner_role is CHECKed
    -- against the same set, which is what makes the comment and the data
    -- comparable at all.
    k_owners text[] := ARRAY['HR','Finance','Platform','Compliance'];
BEGIN
    -- 3a. every table has a non-empty COMMENT ON TABLE
    SELECT string_agg(c.relname, ', ' ORDER BY c.relname), count(*) INTO v_bad, n
      FROM pg_class c JOIN pg_namespace ns ON ns.oid = c.relnamespace
     WHERE ns.nspname = 'public' AND c.relkind IN ('r','p') AND NOT c.relispartition
       AND NOT EXISTS (SELECT 1 FROM pg_depend d
                        WHERE d.classid = 'pg_class'::regclass AND d.objid = c.oid AND d.deptype = 'e')
       AND coalesce(btrim(obj_description(c.oid, 'pg_class')), '') = '';
    IF v_bad IS NOT NULL THEN
        RAISE EXCEPTION E'R3a FAILED — table with no COMMENT ON TABLE: %\n   Add one sentence saying what capability the table serves, plus @tier:, @owner:\n   and @retention: tags. DATA_DICTIONARY.md is generated from these.', v_bad;
    END IF;

    -- 3b. every comment carries @owner: with one of the four §12.4 roles
    SELECT string_agg(c.relname || ' → ' ||
             coalesce(nullif(split_part(split_part(obj_description(c.oid,'pg_class'), '@owner:', 2), ' ', 1), ''), '<absent>'),
             ', ' ORDER BY c.relname)
      INTO v_bad
      FROM pg_class c JOIN pg_namespace ns ON ns.oid = c.relnamespace
     WHERE ns.nspname = 'public' AND c.relkind IN ('r','p') AND NOT c.relispartition
       AND NOT EXISTS (SELECT 1 FROM pg_depend d
                        WHERE d.classid = 'pg_class'::regclass AND d.objid = c.oid AND d.deptype = 'e')
       AND NOT (btrim(split_part(split_part(obj_description(c.oid,'pg_class'), '@owner:', 2), ' ', 1)) = ANY (k_owners));
    IF v_bad IS NOT NULL THEN
        RAISE EXCEPTION E'R3b FAILED — table with no @owner: tag, or an owner outside {HR, Finance, Platform, Compliance}: %\n   The four values are §12.4''s, and retention_policies.owner_role is CHECKed against\n   the same set. A fifth value makes the comment and the row incomparable.', v_bad;
    END IF;

    -- 3c. every table carries @retention:, EXCEPT the fourteen below.
    --
    -- The exemption list is a RATCHET, not a licence. It names the fourteen
    -- tables whose retention class has not been settled with the DPO. It may
    -- only SHRINK: the count is pinned, so removing a tag or adding an
    -- unclassified table fails here, and classifying one also fails until the
    -- pin is lowered in the same commit that adds the tag. Inventing a period
    -- to make this green would be worse than the gap — a wrong retention
    -- period is a PDPL finding, a missing one is a task.
    CREATE TEMP TABLE retention_unclassified(relname text) ON COMMIT DROP;
    INSERT INTO retention_unclassified VALUES
      ('branches'), ('cost_centers'), ('departments'), ('designations'),
      ('document_templates'), ('grades'), ('number_sequences'), ('pay_components'),
      ('permission_grantor_records'), ('public_holidays'), ('role_permissions'),
      ('roles'), ('tenant_settings'), ('user_roles');

    SELECT string_agg(c.relname, ', ' ORDER BY c.relname) INTO v_bad
      FROM pg_class c JOIN pg_namespace ns ON ns.oid = c.relnamespace
     WHERE ns.nspname = 'public' AND c.relkind IN ('r','p') AND NOT c.relispartition
       AND NOT EXISTS (SELECT 1 FROM pg_depend d
                        WHERE d.classid = 'pg_class'::regclass AND d.objid = c.oid AND d.deptype = 'e')
       AND obj_description(c.oid, 'pg_class') NOT LIKE '%@retention:%'
       AND NOT EXISTS (SELECT 1 FROM retention_unclassified u WHERE u.relname = c.relname);
    IF v_bad IS NOT NULL THEN
        RAISE EXCEPTION E'R3c FAILED — table with no @retention: tag and no entry on the unclassified list: %\n   Add @retention:<period>-from-<trigger>-then-<disposition> to its COMMENT ON TABLE,\n   with a trigger from {SoftDelete, Separation, RecordDate, Expiry} and a disposition\n   from {Anonymise, Purge, Keep}. If the class genuinely is not settled, add the table\n   to the unclassified list in 080_schema_ratchets.sql AND raise it with the DPO —\n   the list is reviewed, and it may only shrink.', v_bad;
    END IF;

    -- the list may only shrink, and may not describe tables that no longer exist
    SELECT string_agg(u.relname, ', ' ORDER BY u.relname) INTO v_bad
      FROM retention_unclassified u
     WHERE NOT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace ns ON ns.oid = c.relnamespace
                        WHERE ns.nspname = 'public' AND c.relname = u.relname AND c.relkind IN ('r','p'));
    IF v_bad IS NOT NULL THEN
        RAISE EXCEPTION 'R3c FAILED — the unclassified list names a table that does not exist: %. Remove it.', v_bad;
    END IF;

    SELECT count(*) INTO n
      FROM retention_unclassified u
      JOIN pg_class c ON c.relname = u.relname
      JOIN pg_namespace ns ON ns.oid = c.relnamespace AND ns.nspname = 'public'
     WHERE obj_description(c.oid, 'pg_class') NOT LIKE '%@retention:%';
    IF n <> 14 THEN
        RAISE EXCEPTION E'R3c FAILED — the unclassified ratchet moved: % tables still lack @retention:, pinned at 14.\n   If you classified one, lower the pin in the SAME commit. If one lost its tag, put it back.', n;
    END IF;

    SELECT count(*) INTO n
      FROM pg_class c JOIN pg_namespace ns ON ns.oid = c.relnamespace
     WHERE ns.nspname = 'public' AND c.relkind IN ('r','p') AND NOT c.relispartition
       AND NOT EXISTS (SELECT 1 FROM pg_depend d
                        WHERE d.classid = 'pg_class'::regclass AND d.objid = c.oid AND d.deptype = 'e');
    IF n <> 76 THEN
        RAISE EXCEPTION 'R3 INCONCLUSIVE — % tables found, the baseline is 76. Every count above is against the wrong population.', n;
    END IF;
    RAISE NOTICE '   76 tables: comment + owner ok; 62 classified, 14 pinned unclassified  ok';
END
$ratchet$;

-- -----------------------------------------------------------------------------
-- R3d — COLUMN COMMENT COVERAGE, as a shrink-only ratchet.
--
-- §19.1 gate 3 asks for "every table AND COLUMN". That is not today's schema:
-- 66 of 1268 columns carry a comment. Writing the gate as specified would make
-- it red on the day it lands, and a gate that is red on day one is a gate
-- somebody disables — the exact failure KEEPING_DOCS_HONEST.md §18 describes
-- for the normaliser.
--
-- So it lands as a ceiling instead. A new column with no comment pushes the
-- number past the pin and fails. The pin can only be LOWERED, and lowering it
-- is the work of writing the dictionary. This is weaker than §19.1 asks for and
-- it is recorded as weaker rather than dressed up.
-- -----------------------------------------------------------------------------

\echo ''
\echo '== R3d. column comment coverage may only improve =='

DO $ratchet$
DECLARE
    k_pin  int := 1202;   -- uncommented columns on the day this ratchet landed
    n      int;
    v_new  text;
BEGIN
    SELECT count(*) INTO n
      FROM pg_attribute a
      JOIN pg_class c      ON c.oid = a.attrelid
      JOIN pg_namespace ns ON ns.oid = c.relnamespace AND ns.nspname = 'public'
     WHERE c.relkind IN ('r','p') AND NOT c.relispartition
       AND a.attnum > 0 AND NOT a.attisdropped
       AND NOT EXISTS (SELECT 1 FROM pg_depend d
                        WHERE d.classid = 'pg_class'::regclass AND d.objid = c.oid AND d.deptype = 'e')
       AND col_description(c.oid, a.attnum) IS NULL;

    IF n > k_pin THEN
        SELECT string_agg(c.relname || '.' || a.attname, ', ' ORDER BY c.relname, a.attname)
          INTO v_new
          FROM pg_attribute a
          JOIN pg_class c      ON c.oid = a.attrelid
          JOIN pg_namespace ns ON ns.oid = c.relnamespace AND ns.nspname = 'public'
         WHERE c.relkind IN ('r','p') AND NOT c.relispartition
           AND a.attnum > 0 AND NOT a.attisdropped
           AND col_description(c.oid, a.attnum) IS NULL
           AND c.relname IN (SELECT relname FROM app.rls_manifest);
        RAISE EXCEPTION E'R3d FAILED — % columns have no COMMENT, up from the pinned %.\n   A column added to this schema carries a COMMENT ON COLUMN. The dictionary is\n   generated from them, so an uncommented column is a column the docs cannot describe.\n   (Full uncommented set, for locating the new one: %)', n, k_pin, left(coalesce(v_new,''), 2000);
    END IF;

    IF n < k_pin THEN
        RAISE EXCEPTION E'R3d FAILED — % columns have no COMMENT, BELOW the pin of %.\n   Good news, and the ratchet has to move with it: lower k_pin to % in\n   080_schema_ratchets.sql, in this same commit. A pin left above reality is slack\n   that the next uncommented column slides into unnoticed.', n, k_pin, n;
    END IF;
    RAISE NOTICE '   % columns without a COMMENT, exactly the pin (§19.1 gate 3 is not yet met)  ok', n;
END
$ratchet$;


-- =============================================================================
-- R4 — NO UNJUSTIFIED INDEX
--
-- §19.1: 040_indexes.sql is "the inventory of §19.4, each with a COMMENT ON
-- INDEX naming the query it serves". An index nobody can name a query for is an
-- index nobody can retire: it is write amplification and bloat that outlives the
-- person who added it, and on the five partitioned tables it is that cost times
-- eighty children.
--
-- Constraint-backed indexes are excluded — their justification is the
-- constraint, which assertion 17 covers — and so are partition children, whose
-- indexes are created by ATTACH from the parent's and cannot carry their own
-- comment.
-- =============================================================================

\echo ''
\echo '== R4. every non-constraint index names the query it serves =='

DO $ratchet$
DECLARE
    v_bad text;
    n     int;
BEGIN
    SELECT string_agg(c.relname, ', ' ORDER BY c.relname), count(*) INTO v_bad, n
      FROM pg_class c
      JOIN pg_namespace ns ON ns.oid = c.relnamespace AND ns.nspname = 'public'
     WHERE c.relkind = 'i'
       AND NOT c.relispartition
       AND NOT EXISTS (SELECT 1 FROM pg_constraint k WHERE k.conindid = c.oid)
       AND NOT EXISTS (SELECT 1 FROM pg_depend d
                        WHERE d.classid = 'pg_class'::regclass AND d.objid = c.oid AND d.deptype = 'e')
       AND coalesce(btrim(obj_description(c.oid, 'pg_class')), '') = '';
    IF v_bad IS NOT NULL THEN
        RAISE EXCEPTION E'R4 FAILED — index with no COMMENT ON INDEX: %\n   Add one naming the query it serves, e.g.\n     COMMENT ON INDEX ix_foo__bar IS ''Serves: the payroll run list filtered by status (§19.4).'';\n   An index whose query nobody can name is an index nobody can ever retire.', v_bad;
    END IF;

    SELECT count(*) INTO n
      FROM pg_class c JOIN pg_namespace ns ON ns.oid = c.relnamespace AND ns.nspname = 'public'
     WHERE c.relkind = 'i' AND NOT c.relispartition
       AND NOT EXISTS (SELECT 1 FROM pg_constraint k WHERE k.conindid = c.oid);
    IF n < 100 THEN
        RAISE EXCEPTION 'R4 INCONCLUSIVE — only % non-constraint indexes found; §19.4''s inventory is larger than that', n;
    END IF;
    RAISE NOTICE '   % non-constraint indexes, every one justified                ok', n;
END
$ratchet$;


-- =============================================================================
-- R5 — HEAP, AND NO TABLESPACE
--
-- This one exists because of the DRIFT GATE'S OWN NORMALISER. §19.1 tells
-- schema-normalise.sh to strip `SET` lines, and pg_dump emits
-- `SET default_table_access_method = heap;` and `SET default_tablespace = '';`
-- in the BODY of the dump, not only in the preamble. So a table created with a
-- different access method or pinned to a tablespace would change only lines the
-- normaliser deletes, and the byte-diff gate would not see it.
--
-- Rather than widen the normaliser — which is what assertion 18 exists to
-- prevent — the hole is closed here, where it can be stated plainly.
-- =============================================================================

\echo ''
\echo '== R5. every table is heap with no tablespace (closes the normaliser''s blind spot) =='

DO $ratchet$
DECLARE
    v_bad text;
BEGIN
    SELECT string_agg(c.relname || ' (' || coalesce(am.amname, '?') ||
                      CASE WHEN c.reltablespace <> 0 THEN ', tablespace ' || c.reltablespace::text ELSE '' END || ')',
                      ', ' ORDER BY c.relname)
      INTO v_bad
      FROM pg_class c
      JOIN pg_namespace ns ON ns.oid = c.relnamespace AND ns.nspname = 'public'
      LEFT JOIN pg_am am ON am.oid = c.relam
     WHERE c.relkind IN ('r','p','i')
       AND (c.reltablespace <> 0
            OR (c.relkind IN ('r') AND coalesce(am.amname, 'heap') <> 'heap'));
    IF v_bad IS NOT NULL THEN
        RAISE EXCEPTION E'R5 FAILED — a relation is not plain heap-in-the-default-tablespace: %\n   The drift gate CANNOT see this: §19.1 has the normaliser strip SET lines, and that\n   is the only place pg_dump records it. Either revert the change or widen R5 to declare\n   it — do NOT widen schema-normalise.sh (assertion 18).', v_bad;
    END IF;
    RAISE NOTICE '   every table and index is heap in the default tablespace      ok';
END
$ratchet$;


-- =============================================================================
-- R6 — PENDING, AND SAID SO OUT LOUD
--
-- Assertion 12 has two halves. The half this file enforces is the comment half:
-- every table names an owner and a retention class (R3). The other half is
-- "every table has a retention_policies ROW, matched on entity_name, with a
-- non-null owner_role that AGREES with the @owner: comment".
--
-- That half cannot be enforced yet, and the reason is not an oversight: §19.1's
-- 070_seed_reference.sql — permissions, statutory_rules and bands, nitaqat_grid,
-- KSA public_holidays, retention_policies — HAS NOT BEEN WRITTEN. The table
-- exists and is empty.
--
-- The options were to write the assertion against an empty table, where it
-- passes vacuously and reports green forever, or to seed 76 retention rows by
-- guessing legal periods for the fourteen tables nobody has classified. Both are
-- worse than saying this. So the gate PRINTS the gap on every run, and the
-- assertion below fails the moment the seed lands with a table missing a row —
-- it is armed, not absent.
-- =============================================================================

\echo ''
\echo '== R6. retention_policies rows =='

DO $ratchet$
DECLARE
    n_rows int;
    v_bad  text;
BEGIN
    SELECT count(*) INTO n_rows FROM retention_policies;

    IF n_rows = 0 THEN
        RAISE WARNING E'R6 PENDING — retention_policies is EMPTY, so assertion 12''s row half is not yet enforceable.\n   070_seed_reference.sql (§19.1) has not been written. This gate arms itself the moment\n   the first row lands: from then on every table needs one, and its owner_role must agree\n   with the @owner: tag R3b already checks. Until then the comment half (R3) is the whole\n   of the control, and that is a REAL gap, not a formality: a table can ship today with an\n   owner in a comment that the retention job will never read.';
        RETURN;
    END IF;

    SELECT string_agg(c.relname, ', ' ORDER BY c.relname) INTO v_bad
      FROM pg_class c JOIN pg_namespace ns ON ns.oid = c.relnamespace
     WHERE ns.nspname = 'public' AND c.relkind IN ('r','p') AND NOT c.relispartition
       AND NOT EXISTS (SELECT 1 FROM pg_depend d
                        WHERE d.classid = 'pg_class'::regclass AND d.objid = c.oid AND d.deptype = 'e')
       AND NOT EXISTS (SELECT 1 FROM retention_policies rp
                        WHERE rp.tenant_id IS NULL AND rp.entity_name = c.relname);
    IF v_bad IS NOT NULL THEN
        RAISE EXCEPTION E'R6 FAILED — table with no platform retention_policies row: %\n   A table with personal data and no retention class is a PDPL gap, not a docs gap.\n   Add a row to 070_seed_reference.sql with a legal_basis, a minimum_retention_months,\n   a trigger_event and a disposition.', v_bad;
    END IF;

    -- and the row must agree with the comment R3b checked
    SELECT string_agg(format('%s: comment says %s, row says %s', c.relname,
                             btrim(split_part(split_part(obj_description(c.oid,'pg_class'), '@owner:', 2), ' ', 1)),
                             rp.owner_role), ', ')
      INTO v_bad
      FROM pg_class c
      JOIN pg_namespace ns ON ns.oid = c.relnamespace AND ns.nspname = 'public'
      JOIN retention_policies rp ON rp.entity_name = c.relname AND rp.tenant_id IS NULL
     WHERE c.relkind IN ('r','p') AND NOT c.relispartition
       AND rp.owner_role IS DISTINCT FROM btrim(split_part(split_part(obj_description(c.oid,'pg_class'), '@owner:', 2), ' ', 1));
    IF v_bad IS NOT NULL THEN
        RAISE EXCEPTION 'R6 FAILED — the @owner: comment and retention_policies.owner_role disagree: %', v_bad;
    END IF;

    RAISE NOTICE '   % retention rows, every table covered, owners agree          ok', n_rows;
END
$ratchet$;


\echo ''
\echo 'ALL SCHEMA RATCHETS PASSED'
