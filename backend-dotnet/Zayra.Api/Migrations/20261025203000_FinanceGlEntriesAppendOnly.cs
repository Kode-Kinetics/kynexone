using System;
using System.Linq;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Zayra.Api.Data;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// M2 of cost-centre accounting (v1-spec 1.10, 1.11 as rewritten by AM-11 and AM-12): database protection for the ledger.
    /// SQL only, no model change, expand-only in effect (no existing row is touched; the rules bind future changes). Re-runnable:
    /// <c>CREATE OR REPLACE FUNCTION</c>, <c>DROP TRIGGER IF EXISTS</c> then <c>CREATE TRIGGER</c>.
    ///
    /// <para><b>finance_gl_entries</b> becomes append-only. Frozen after insert: every economic field, the source references, the
    /// cost-centre id and its snapshots, the posting key, <c>description</c>, <c>posted_by*</c> and <c>created_at_utc</c>; there is NO
    /// company NULL-to-value exception (AM-12). Mutable, and only along rules: <c>is_reversed</c> (false to true, with a matching
    /// contra at COMMIT) and the <c>erp_*</c> columns (a state machine read from <c>ErpPostingTransitions</c> and the export code). A
    /// reversal row is validated at insert. DELETE and TRUNCATE are refused. Every ledger trigger is <c>ENABLE ALWAYS</c>, so
    /// <c>session_replication_role = replica</c> does not skip them.</para>
    ///
    /// <para><b>Around the ledger</b>: the cost-centre in-use guard and parent-cycle guard (1.11 b, c), the frozen export artifact (1.11 d),
    /// and the payroll cost-centre stamps, frozen once a run is Locked.</para>
    ///
    /// <para><b>Who can undo it.</b> A trigger only holds against a role that cannot disable it. The migration cannot know which role
    /// the application connects as, and must not move the application to another role. It reports, in a NOTICE, every login role that
    /// COULD switch these triggers off (owner, members of the owner, superusers); if the application's role is among them the ledger is
    /// append-only by convention only, and that is a release blocker for the CTO (AM-12, S-049).</para>
    ///
    /// <para>Deferred checks are CONSTRAINT TRIGGERs evaluated at COMMIT, because EF sends the UPDATE of the original and the INSERT of its
    /// contra (and the stamp and its export) in an order the writers do not control.</para>
    /// </summary>
    [DbContext(typeof(ZayraDbContext))]
    [Migration("20261025203000_FinanceGlEntriesAppendOnly")]
    public partial class FinanceGlEntriesAppendOnly : Migration
    {
        /// <summary>Transaction-scoped, as in M1: CREATE TRIGGER takes SHARE ROW EXCLUSIVE and must not queue every writer behind it.</summary>
        public const string LockTimeoutSql = "SET LOCAL lock_timeout = '5s';";

        /// <summary>Columns of finance_gl_entries that never change after insert. The trigger compares each one; the sweep test pins this list
        /// against the entity, so a new column is frozen by default.</summary>
        public static readonly string[] FrozenColumns =
        [
            "id", "tenant_id", "company_id", "source_module", "source_entity_id", "source_entity_ref", "event_type",
            "debit_account", "credit_account", "amount", "currency", "entry_date", "period", "description",
            "posted_by_name", "posted_by", "reversal_of_entry_id", "created_at_utc", "posting_key",
            "cost_center_id", "cost_center_code", "cost_center_name", "cost_center_gl_segment",
        ];

        /// <summary>The ledger triggers, in creation order: (table, trigger name).</summary>
        private static readonly (string Table, string Trigger)[] LedgerTriggers =
        [
            ("finance_gl_entries", "trg_finance_gl_entries__append_only"),
            ("finance_gl_entries", "trg_finance_gl_entries__no_truncate"),
            ("finance_gl_entries", "trg_finance_gl_entries__reversal_witness"),
            ("finance_gl_entries", "trg_finance_gl_entries__reversal_valid"),
            ("finance_gl_entries", "trg_finance_gl_entries__reversed_needs_contra"),
            ("finance_gl_entries", "trg_finance_gl_entries__erp_evidence"),
            ("cost_centers", "trg_cost_centers__guard"),
            ("cost_centers", "trg_cost_centers__parent_cycle"),
            ("gl_journal_export_lines", "trg_gl_journal_export_lines__immutable"),
            ("payroll_runs", "trg_payroll_runs__stamp_freeze"),
            ("payroll_earnings", "trg_payroll_earnings__stamp_freeze"),
            ("payroll_deductions", "trg_payroll_deductions__stamp_freeze"),
            ("payroll_slips", "trg_payroll_slips__stamp_freeze"),
        ];

        private static readonly string[] Functions =
        [
            "finance_ledger_tenant_is_purging(uuid)", "finance_ledger_trigger_bypass_roles()",
            "finance_gl_entries_append_only()", "finance_gl_entries_no_truncate()", "finance_gl_entries_reversal_witness()", "finance_gl_entries_reversal_valid()",
            "finance_gl_entries_reversed_needs_contra()", "finance_gl_entries_erp_evidence()",
            "cost_centers_guard()", "cost_centers_parent_cycle()", "gl_journal_export_lines_immutable()",
            "payroll_runs_stamp_freeze()", "payroll_lines_stamp_freeze()",
        ];

        /// <summary>
        /// Every statement of M2, each runnable on its own and any number of times. Published so the Postgres integration fixture (which
        /// builds its schema from the EF model, not the migration chain) runs production's own text, as Release A and ENG do.
        /// </summary>
        public static readonly string[] PostgresOnlyDdl = BuildDdl();

        /// <summary>Reports, as one NOTICE, every login role that can switch the ledger triggers off. Never fails the migration.</summary>
        public const string RoleReportSql = """
            DO $do$
            DECLARE
              v_roles text;
              v_owner name;
            BEGIN
              SELECT pg_get_userbyid(c.relowner) INTO v_owner FROM pg_class c WHERE c.oid = to_regclass('finance_gl_entries');
              SELECT string_agg(role_name || ' (' || why || ')', ', ' ORDER BY role_name) INTO v_roles FROM finance_ledger_trigger_bypass_roles();
              RAISE NOTICE 'finance_gl_entries ledger triggers: the login roles that can switch off these triggers (owner %, its members, superusers) are: %. Migrating as %. RELEASE BLOCKER if the application connects as any of them: the ledger is then append-only by convention only. Connect the application as a role that does not own the tables and holds SELECT, INSERT, UPDATE (no TRUNCATE, no ownership).',
                v_owner, coalesce(v_roles, '(none)'), current_user;
            END
            $do$;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(LockTimeoutSql);
            foreach (var statement in PostgresOnlyDdl) migrationBuilder.Sql(statement);
            migrationBuilder.Sql(RoleReportSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(LockTimeoutSql);
            foreach (var (table, trigger) in LedgerTriggers.Reverse())
                migrationBuilder.Sql($"DROP TRIGGER IF EXISTS {trigger} ON {table};");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_finance_gl_entries_reversal_of;");
            foreach (var function in Functions)
                migrationBuilder.Sql($"DROP FUNCTION IF EXISTS {function};");
        }

        private static string[] BuildDdl()
        {
            var frozen = string.Join("\n", FrozenColumns.Select(c =>
                $"  IF NEW.{c} IS DISTINCT FROM OLD.{c} THEN v_changed := array_append(v_changed, '{c}'); END IF;"));

            var appendOnly = $$"""
                CREATE OR REPLACE FUNCTION finance_gl_entries_append_only() RETURNS trigger LANGUAGE plpgsql AS $fn$
                DECLARE
                  v_changed text[] := ARRAY[]::text[];
                BEGIN
                  IF TG_OP = 'DELETE' THEN
                    -- The one legitimate removal: erasing every row of a SOFT-DELETED tenant (PlatformController.PurgeTenant and
                    -- SoftDeletedTenantRule delete by tenant id). A live or merely suspended tenant's ledger is undeletable.
                    IF finance_ledger_tenant_is_purging(OLD.tenant_id) THEN RETURN OLD; END IF;
                    RAISE EXCEPTION 'finance_gl_entries is append-only: DELETE is not permitted (row id=%)', OLD.id
                      USING ERRCODE = 'restrict_violation';
                  END IF;

                {{frozen}}
                  IF cardinality(v_changed) > 0 THEN
                    RAISE EXCEPTION 'finance_gl_entries is append-only: % cannot change after posting (row id=%)', array_to_string(v_changed, ', '), OLD.id
                      USING ERRCODE = 'restrict_violation';
                  END IF;

                  -- is_reversed: false to true only, and never on a reversal row. The matching contra is checked at COMMIT.
                  IF OLD.is_reversed AND NOT NEW.is_reversed THEN
                    RAISE EXCEPTION 'finance_gl_entries is append-only: is_reversed cannot go back to false (row id=%)', OLD.id
                      USING ERRCODE = 'restrict_violation';
                  END IF;
                  IF NOT OLD.is_reversed AND NEW.is_reversed AND OLD.reversal_of_entry_id IS NOT NULL THEN
                    RAISE EXCEPTION 'finance_gl_entries is append-only: a reversal row cannot itself be marked is_reversed (row id=%)', OLD.id
                      USING ERRCODE = 'restrict_violation';
                  END IF;

                  -- Remember, for this transaction only, which originals are being flagged now: a reversal of an original that is ALREADY
                  -- flagged (by an earlier transaction) is a double reversal, but the writers flag the original and insert its contra together.
                  IF NOT OLD.is_reversed AND NEW.is_reversed THEN
                    PERFORM set_config('kynex.gl_flagged', coalesce(current_setting('kynex.gl_flagged', true), '') || NEW.id::text || ',', true);
                  END IF;

                  -- erp_*: the state machine the export code implies (ErpPostingTransitions at run level; ErpPostingEvidence per line).
                  --   NotReady -> ReadyForErp | Exported | Rejected | Posted     ReadyForErp -> Exported | Rejected | Posted
                  --   Exported -> Exported | Rejected | Posted                   Rejected -> Rejected | Exported | Posted
                  --   Posted   -> Posted only (a re-export never walks it back).
                  IF NEW.erp_posting_status IS DISTINCT FROM OLD.erp_posting_status THEN
                    IF NOT (
                         (OLD.erp_posting_status = 'NotReady'    AND NEW.erp_posting_status IN ('ReadyForErp', 'Exported', 'Rejected', 'Posted'))
                      OR (OLD.erp_posting_status = 'ReadyForErp' AND NEW.erp_posting_status IN ('Exported', 'Rejected', 'Posted'))
                      OR (OLD.erp_posting_status = 'Exported'    AND NEW.erp_posting_status IN ('Rejected', 'Posted'))
                      OR (OLD.erp_posting_status = 'Rejected'    AND NEW.erp_posting_status IN ('Exported', 'Posted'))
                    ) THEN
                      RAISE EXCEPTION 'finance_gl_entries is append-only: erp_posting_status cannot move from % to % (row id=%)',
                        OLD.erp_posting_status, NEW.erp_posting_status, OLD.id USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF NEW.erp_posting_status = 'Posted' AND coalesce(NEW.erp_document_number, '') = '' THEN
                      RAISE EXCEPTION 'finance_gl_entries is append-only: erp_posting_status Posted needs the ERP document number (row id=%)', OLD.id
                        USING ERRCODE = 'restrict_violation';
                    END IF;
                  END IF;
                  IF OLD.erp_status_changed_at_utc IS NOT NULL AND NEW.erp_status_changed_at_utc IS NULL THEN
                    RAISE EXCEPTION 'finance_gl_entries is append-only: erp_status_changed_at_utc cannot be cleared (row id=%)', OLD.id
                      USING ERRCODE = 'restrict_violation';
                  END IF;
                  IF OLD.erp_posting_status = 'Posted' THEN
                    IF OLD.erp_document_number IS NOT NULL AND coalesce(NEW.erp_document_number, '') = '' THEN
                      RAISE EXCEPTION 'finance_gl_entries is append-only: erp_document_number cannot be cleared once Posted (row id=%)', OLD.id
                        USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF NEW.erp_rejection_reason IS DISTINCT FROM OLD.erp_rejection_reason THEN
                      RAISE EXCEPTION 'finance_gl_entries is append-only: erp_rejection_reason cannot change once Posted (row id=%)', OLD.id
                        USING ERRCODE = 'restrict_violation';
                    END IF;
                  END IF;
                  RETURN NEW;
                END
                $fn$;
                """;

            return
            [
                """
                CREATE OR REPLACE FUNCTION finance_ledger_tenant_is_purging(p_tenant uuid) RETURNS boolean LANGUAGE sql STABLE AS $fn$
                  SELECT EXISTS (SELECT 1 FROM tenants t WHERE t.id = p_tenant AND NOT t.is_active AND position('__deleted_' in t.slug) > 0)
                $fn$;
                """,

                // Which login roles could undo the triggers: the table owner, members of the owning role (they inherit ownership), superusers.
                """
                CREATE OR REPLACE FUNCTION finance_ledger_trigger_bypass_roles() RETURNS TABLE (role_name name, why text) LANGUAGE sql STABLE AS $fn$
                  SELECT r.rolname,
                         (CASE WHEN r.rolsuper THEN 'superuser'
                               WHEN r.oid = c.relowner THEN 'owns finance_gl_entries'
                               ELSE 'member of the role that owns finance_gl_entries' END)::text
                  FROM pg_roles r CROSS JOIN pg_class c
                  WHERE c.oid = to_regclass('finance_gl_entries')
                    AND r.rolcanlogin
                    AND (r.rolsuper OR pg_has_role(r.oid, c.relowner, 'USAGE'))
                  ORDER BY r.rolname
                $fn$;
                """,

                appendOnly,
                """
                DROP TRIGGER IF EXISTS trg_finance_gl_entries__append_only ON finance_gl_entries;
                CREATE TRIGGER trg_finance_gl_entries__append_only BEFORE UPDATE OR DELETE ON finance_gl_entries
                  FOR EACH ROW EXECUTE FUNCTION finance_gl_entries_append_only();
                ALTER TABLE finance_gl_entries ENABLE ALWAYS TRIGGER trg_finance_gl_entries__append_only;
                """,

                // Row triggers do not fire on TRUNCATE; a statement trigger does.
                """
                CREATE OR REPLACE FUNCTION finance_gl_entries_no_truncate() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  RAISE EXCEPTION 'finance_gl_entries is append-only: TRUNCATE is not permitted' USING ERRCODE = 'restrict_violation';
                END
                $fn$;
                """,
                """
                DROP TRIGGER IF EXISTS trg_finance_gl_entries__no_truncate ON finance_gl_entries;
                CREATE TRIGGER trg_finance_gl_entries__no_truncate BEFORE TRUNCATE ON finance_gl_entries
                  FOR EACH STATEMENT EXECUTE FUNCTION finance_gl_entries_no_truncate();
                ALTER TABLE finance_gl_entries ENABLE ALWAYS TRIGGER trg_finance_gl_entries__no_truncate;
                """,

                // Transaction-local witnesses (set_config(..., is_local => true): gone at COMMIT or ROLLBACK, undone with a rolled-back savepoint).
                // They let the deferred checks ask "was this written in THIS transaction" without relying on xmin, which is a sub-transaction id
                // under the savepoints EF opens inside an explicit transaction. Only reversal rows and the void's reclass rows pay for the trigger.
                """
                CREATE OR REPLACE FUNCTION finance_gl_entries_reversal_witness() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF NEW.reversal_of_entry_id IS NOT NULL THEN
                    PERFORM set_config('kynex.gl_contra_for', coalesce(current_setting('kynex.gl_contra_for', true), '') || NEW.reversal_of_entry_id::text || ',', true);
                  ELSE
                    PERFORM set_config('kynex.gl_reclass', coalesce(current_setting('kynex.gl_reclass', true), '') || NEW.source_entity_id::text || ':' || NEW.event_type || ',', true);
                  END IF;
                  RETURN NULL;
                END
                $fn$;
                """,
                """
                DROP TRIGGER IF EXISTS trg_finance_gl_entries__reversal_witness ON finance_gl_entries;
                CREATE TRIGGER trg_finance_gl_entries__reversal_witness AFTER INSERT ON finance_gl_entries
                  FOR EACH ROW
                  WHEN (NEW.reversal_of_entry_id IS NOT NULL OR NEW.event_type = 'PayrollNetSettlementReclass' OR NEW.event_type LIKE 'PayrollRemitReclass:%')
                  EXECUTE FUNCTION finance_gl_entries_reversal_witness();
                ALTER TABLE finance_gl_entries ENABLE ALWAYS TRIGGER trg_finance_gl_entries__reversal_witness;
                """,

                // The reversal triggers look contras up by their original on every insert and every flag.
                "CREATE INDEX IF NOT EXISTS ix_finance_gl_entries_reversal_of ON finance_gl_entries (tenant_id, reversal_of_entry_id) WHERE reversal_of_entry_id IS NOT NULL;",
                "COMMENT ON INDEX ix_finance_gl_entries_reversal_of IS 'Serves: the M2 reversal triggers (contras of one original), LoansController.HasContraAsync and the bonus and void ledgers (reversals of a source).';",

                // AM-11 database half, as ruled by AM-25: a reversal row points at an existing entry of the same tenant, company and currency that is
                // not itself a reversal and is not already reversed; it is side-swapped (one leg may be empty: the void writes one-sided rows, and a
                // period-shifted void changes only period, date and reference); it copies the cost centre verbatim; and its amount, and the sum of all
                // reversals of the original, never exceed the original (a partial contra is a bonus cancel; a second full one is a double void).
                """
                CREATE OR REPLACE FUNCTION finance_gl_entries_reversal_valid() RETURNS trigger LANGUAGE plpgsql AS $fn$
                DECLARE
                  o finance_gl_entries%ROWTYPE;
                  v_total numeric;
                BEGIN
                  SELECT * INTO o FROM finance_gl_entries WHERE id = NEW.reversal_of_entry_id AND tenant_id = NEW.tenant_id;
                  IF NOT FOUND OR o.id = NEW.id THEN
                    RAISE EXCEPTION 'finance_gl_entries reversal % points at no other entry of this tenant (reversal_of_entry_id=%)', NEW.id, NEW.reversal_of_entry_id
                      USING ERRCODE = 'check_violation';
                  END IF;
                  IF o.company_id IS DISTINCT FROM NEW.company_id THEN
                    RAISE EXCEPTION 'finance_gl_entries reversal % is in company % but its original % is in company %', NEW.id, NEW.company_id, o.id, o.company_id
                      USING ERRCODE = 'check_violation';
                  END IF;
                  IF o.reversal_of_entry_id IS NOT NULL THEN
                    RAISE EXCEPTION 'finance_gl_entries reversal % reverses a reversal (%): reversing a reversal re-applies the original', NEW.id, o.id
                      USING ERRCODE = 'check_violation';
                  END IF;
                  IF o.is_reversed AND position(o.id::text || ',' in coalesce(current_setting('kynex.gl_flagged', true), '')) = 0 THEN
                    RAISE EXCEPTION 'finance_gl_entries reversal % targets % which was already reversed', NEW.id, o.id
                      USING ERRCODE = 'check_violation';
                  END IF;
                  IF NEW.currency IS DISTINCT FROM o.currency THEN
                    RAISE EXCEPTION 'finance_gl_entries reversal % is in % but its original % is in %', NEW.id, NEW.currency, o.id, o.currency
                      USING ERRCODE = 'check_violation';
                  END IF;
                  IF NOT ((NEW.debit_account = '' OR NEW.debit_account = o.credit_account)
                      AND (NEW.credit_account = '' OR NEW.credit_account = o.debit_account)
                      AND (NEW.debit_account <> '' OR NEW.credit_account <> '')) THEN
                    RAISE EXCEPTION 'finance_gl_entries reversal % does not mirror the accounts of its original %', NEW.id, o.id
                      USING ERRCODE = 'check_violation';
                  END IF;
                  IF NEW.cost_center_id IS DISTINCT FROM o.cost_center_id
                     OR NEW.cost_center_code IS DISTINCT FROM o.cost_center_code
                     OR NEW.cost_center_name IS DISTINCT FROM o.cost_center_name
                     OR NEW.cost_center_gl_segment IS DISTINCT FROM o.cost_center_gl_segment THEN
                    RAISE EXCEPTION 'finance_gl_entries reversal % must carry the cost centre of its original % verbatim', NEW.id, o.id
                      USING ERRCODE = 'check_violation';
                  END IF;
                  SELECT coalesce(sum(amount), 0) INTO v_total FROM finance_gl_entries WHERE tenant_id = NEW.tenant_id AND reversal_of_entry_id = o.id;
                  IF NEW.amount > o.amount OR v_total > o.amount THEN
                    RAISE EXCEPTION 'finance_gl_entries reversals of % total % and exceed the original amount %', o.id, v_total, o.amount
                      USING ERRCODE = 'check_violation';
                  END IF;
                  RETURN NULL;
                END
                $fn$;
                """,
                """
                DROP TRIGGER IF EXISTS trg_finance_gl_entries__reversal_valid ON finance_gl_entries;
                CREATE CONSTRAINT TRIGGER trg_finance_gl_entries__reversal_valid AFTER INSERT ON finance_gl_entries
                  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (NEW.reversal_of_entry_id IS NOT NULL)
                  EXECUTE FUNCTION finance_gl_entries_reversal_valid();
                ALTER TABLE finance_gl_entries ENABLE ALWAYS TRIGGER trg_finance_gl_entries__reversal_valid;
                """,

                // AM-12: is_reversed may become true only with its contra WRITTEN IN THE SAME TRANSACTION (the witness), and the contras together cover
                // the original: the writers flag an original only when its reversal closes it out (BonusesController: "only when the contra closes it
                // out completely"; a void, a loan reversal and a settlement cancel always reverse the whole line). The one other shape is
                // PayrollVoidService with funds DISBURSED: the retained cash leg (and only that) is flagged without a linked contra, and the unlinked
                // reclassification row of the same run, written in the same transaction, stands in for it.
                """
                CREATE OR REPLACE FUNCTION finance_gl_entries_reversed_needs_contra() RETURNS trigger LANGUAGE plpgsql AS $fn$
                DECLARE
                  v_total numeric;
                  v_reclass text;
                BEGIN
                  IF position(NEW.id::text || ',' in coalesce(current_setting('kynex.gl_contra_for', true), '')) > 0 THEN
                    SELECT coalesce(sum(c.amount), 0) INTO v_total
                    FROM finance_gl_entries c
                    WHERE c.tenant_id = NEW.tenant_id AND c.reversal_of_entry_id = NEW.id AND c.company_id IS NOT DISTINCT FROM NEW.company_id;
                    IF v_total >= NEW.amount THEN RETURN NULL; END IF;
                    RAISE EXCEPTION 'finance_gl_entries is_reversed on % needs contras covering the original: they total % of %', NEW.id, v_total, NEW.amount
                      USING ERRCODE = 'check_violation';
                  END IF;

                  v_reclass := CASE
                    WHEN NEW.event_type = 'PayrollNetSettlement' THEN 'PayrollNetSettlementReclass'
                    WHEN NEW.event_type LIKE 'PayrollRemit:%' THEN 'PayrollRemitReclass:' || substr(NEW.event_type, length('PayrollRemit:') + 1)
                  END;
                  IF v_reclass IS NOT NULL AND NEW.debit_account = ''
                     AND position(NEW.source_entity_id::text || ':' || v_reclass || ',' in coalesce(current_setting('kynex.gl_reclass', true), '')) > 0 THEN
                    RETURN NULL;
                  END IF;

                  RAISE EXCEPTION 'finance_gl_entries is_reversed on % needs a matching contra written in the same transaction', NEW.id
                    USING ERRCODE = 'check_violation';
                END
                $fn$;
                """,
                """
                DROP TRIGGER IF EXISTS trg_finance_gl_entries__reversed_needs_contra ON finance_gl_entries;
                CREATE CONSTRAINT TRIGGER trg_finance_gl_entries__reversed_needs_contra AFTER UPDATE OF is_reversed ON finance_gl_entries
                  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (NOT OLD.is_reversed AND NEW.is_reversed)
                  EXECUTE FUNCTION finance_gl_entries_reversed_needs_contra();
                ALTER TABLE finance_gl_entries ENABLE ALWAYS TRIGGER trg_finance_gl_entries__reversed_needs_contra;
                """,

                // AM-12: Exported and Posted assert something that happened outside the database, so they are backed by the artifact that says so,
                // judged at COMMIT (the stamp and its export are one SaveChanges). Posted needs a CONFIRMED export carrying the entry with the same
                // ERP document number (whole export confirmed, or a confirmed journal in journal_confirmations_json). Changing the number of a line
                // that is already Posted is the Replacement path only.
                """
                CREATE OR REPLACE FUNCTION finance_gl_entries_erp_evidence() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF NEW.erp_posting_status = 'Exported' THEN
                    IF NOT EXISTS (SELECT 1 FROM gl_journal_export_lines l WHERE l.tenant_id = NEW.tenant_id AND l.finance_gl_entry_id = NEW.id) THEN
                      RAISE EXCEPTION 'finance_gl_entries erp_posting_status Exported on % has no export line behind it', NEW.id
                        USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NULL;
                  END IF;

                  IF NOT EXISTS (
                    SELECT 1
                    FROM gl_journal_export_lines l
                    JOIN gl_journal_exports e ON e.id = l.gl_journal_export_id AND e.tenant_id = l.tenant_id
                    WHERE l.tenant_id = NEW.tenant_id AND l.finance_gl_entry_id = NEW.id
                      AND (OLD.erp_posting_status <> 'Posted' OR OLD.erp_document_number IS NULL OR e.kind = 'Replacement')
                      AND ((e.status = 'Confirmed' AND e.erp_document_number = NEW.erp_document_number)
                           OR EXISTS (
                                SELECT 1
                                FROM json_array_elements(CASE WHEN json_typeof(e.journal_confirmations_json) = 'array'
                                                              THEN e.journal_confirmations_json ELSE '[]'::json END) c
                                WHERE c->>'Status' = 'Confirmed' AND c->>'ErpDocumentNumber' = NEW.erp_document_number))
                  ) THEN
                    RAISE EXCEPTION 'finance_gl_entries erp_posting_status Posted on % with ERP document % has no confirmed export evidence (a change of the number of a Posted line needs a confirmed Replacement export)',
                      NEW.id, NEW.erp_document_number USING ERRCODE = 'check_violation';
                  END IF;
                  RETURN NULL;
                END
                $fn$;
                """,
                """
                DROP TRIGGER IF EXISTS trg_finance_gl_entries__erp_evidence ON finance_gl_entries;
                CREATE CONSTRAINT TRIGGER trg_finance_gl_entries__erp_evidence AFTER UPDATE OF erp_posting_status, erp_document_number ON finance_gl_entries
                  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW
                  WHEN (NEW.erp_posting_status IN ('Exported', 'Posted')
                        AND (OLD.erp_posting_status IS DISTINCT FROM NEW.erp_posting_status OR OLD.erp_document_number IS DISTINCT FROM NEW.erp_document_number))
                  EXECUTE FUNCTION finance_gl_entries_erp_evidence();
                ALTER TABLE finance_gl_entries ENABLE ALWAYS TRIGGER trg_finance_gl_entries__erp_evidence;
                """,

                // 1.11 (b): code and company are frozen once a posted line references the centre; deactivating, soft-deleting or moving it is refused
                // while a live row points at it. A centre referenced only by history may be deactivated: the snapshots protect the history.
                """
                CREATE OR REPLACE FUNCTION cost_centers_guard() RETURNS trigger LANGUAGE plpgsql AS $fn$
                DECLARE
                  v_n bigint;
                  v_counts text := '';
                BEGIN
                  IF NEW.code IS DISTINCT FROM OLD.code OR NEW.company_id IS DISTINCT FROM OLD.company_id THEN
                    SELECT count(*) INTO v_n FROM finance_gl_entries WHERE tenant_id = OLD.tenant_id AND cost_center_id = OLD.id;
                    IF v_n > 0 THEN
                      RAISE EXCEPTION 'cost_centre_in_use:finance_gl_entries=%', v_n USING ERRCODE = 'restrict_violation';
                    END IF;
                  END IF;

                  IF (NEW.is_deleted AND NOT OLD.is_deleted) OR (OLD.is_active AND NOT NEW.is_active) OR NEW.company_id IS DISTINCT FROM OLD.company_id THEN
                    SELECT count(*) INTO v_n FROM departments WHERE tenant_id = OLD.tenant_id AND cost_center_id = OLD.id AND NOT is_deleted;
                    IF v_n > 0 THEN v_counts := v_counts || 'departments=' || v_n || ','; END IF;
                    SELECT count(*) INTO v_n FROM positions WHERE tenant_id = OLD.tenant_id AND cost_center_id = OLD.id AND NOT is_deleted;
                    IF v_n > 0 THEN v_counts := v_counts || 'positions=' || v_n || ','; END IF;
                    SELECT count(*) INTO v_n FROM employees WHERE tenant_id = OLD.tenant_id AND cost_center_id = OLD.id AND NOT is_deleted;
                    IF v_n > 0 THEN v_counts := v_counts || 'employees=' || v_n || ','; END IF;
                    SELECT count(*) INTO v_n FROM gl_account_mappings WHERE tenant_id = OLD.tenant_id AND segment_cost_center_id = OLD.id AND is_active;
                    IF v_n > 0 THEN v_counts := v_counts || 'gl_account_mappings=' || v_n || ','; END IF;
                    SELECT count(*) INTO v_n FROM companies WHERE tenant_id = OLD.tenant_id AND default_cost_center_id = OLD.id AND NOT is_deleted;
                    IF v_n > 0 THEN v_counts := v_counts || 'companies=' || v_n || ','; END IF;
                    SELECT count(*) INTO v_n FROM cost_centers WHERE tenant_id = OLD.tenant_id AND parent_id = OLD.id AND is_active AND NOT is_deleted;
                    IF v_n > 0 THEN v_counts := v_counts || 'cost_centers=' || v_n || ','; END IF;
                    IF v_counts <> '' THEN
                      RAISE EXCEPTION 'cost_centre_in_use:%', rtrim(v_counts, ',') USING ERRCODE = 'restrict_violation';
                    END IF;
                  END IF;
                  RETURN NEW;
                END
                $fn$;
                """,
                """
                DROP TRIGGER IF EXISTS trg_cost_centers__guard ON cost_centers;
                CREATE TRIGGER trg_cost_centers__guard BEFORE UPDATE OF code, company_id, is_active, is_deleted ON cost_centers
                  FOR EACH ROW EXECUTE FUNCTION cost_centers_guard();
                ALTER TABLE cost_centers ENABLE ALWAYS TRIGGER trg_cost_centers__guard;
                """,

                // 1.11 (c): no cycle, and no chain deeper than ten levels (counting the subtree under the row being re-parented).
                """
                CREATE OR REPLACE FUNCTION cost_centers_parent_cycle() RETURNS trigger LANGUAGE plpgsql AS $fn$
                DECLARE
                  v_up int;
                  v_cycle boolean;
                  v_down int := 0;
                BEGIN
                  IF NEW.parent_id IS NULL THEN RETURN NEW; END IF;
                  IF TG_OP = 'UPDATE' AND NEW.parent_id IS NOT DISTINCT FROM OLD.parent_id THEN RETURN NEW; END IF;

                  WITH RECURSIVE up(id, parent_id, depth) AS (
                    SELECT c.id, c.parent_id, 1 FROM cost_centers c WHERE c.tenant_id = NEW.tenant_id AND c.id = NEW.parent_id
                    UNION ALL
                    SELECT c.id, c.parent_id, up.depth + 1 FROM cost_centers c JOIN up ON c.id = up.parent_id AND c.tenant_id = NEW.tenant_id WHERE up.depth < 12)
                  SELECT coalesce(bool_or(id = NEW.id), false), coalesce(max(depth), 0) INTO v_cycle, v_up FROM up;
                  IF v_cycle THEN
                    RAISE EXCEPTION 'cost_centre_cycle: cost centre % cannot sit under % (it is one of its own ancestors)', NEW.id, NEW.parent_id
                      USING ERRCODE = 'check_violation';
                  END IF;

                  IF TG_OP = 'UPDATE' THEN
                    WITH RECURSIVE down(id, depth) AS (
                      SELECT c.id, 1 FROM cost_centers c WHERE c.tenant_id = NEW.tenant_id AND c.parent_id = NEW.id
                      UNION ALL
                      SELECT c.id, d.depth + 1 FROM cost_centers c JOIN down d ON c.parent_id = d.id AND c.tenant_id = NEW.tenant_id WHERE d.depth < 12)
                    SELECT coalesce(max(depth), 0) INTO v_down FROM down;
                  END IF;

                  IF v_up + 1 + v_down > 10 THEN
                    RAISE EXCEPTION 'cost_centre_depth: cost centre % would sit % levels deep (the limit is 10)', NEW.id, v_up + 1 + v_down
                      USING ERRCODE = 'check_violation';
                  END IF;
                  RETURN NEW;
                END
                $fn$;
                """,
                """
                DROP TRIGGER IF EXISTS trg_cost_centers__parent_cycle ON cost_centers;
                CREATE TRIGGER trg_cost_centers__parent_cycle BEFORE INSERT OR UPDATE OF parent_id ON cost_centers
                  FOR EACH ROW EXECUTE FUNCTION cost_centers_parent_cycle();
                ALTER TABLE cost_centers ENABLE ALWAYS TRIGGER trg_cost_centers__parent_cycle;
                """,

                // 1.11 (d): the export artifact is frozen; the hash verification depends on it.
                """
                CREATE OR REPLACE FUNCTION gl_journal_export_lines_immutable() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF TG_OP = 'DELETE' AND finance_ledger_tenant_is_purging(OLD.tenant_id) THEN RETURN OLD; END IF;
                  RAISE EXCEPTION 'gl_journal_export_lines is frozen: % is not permitted (row id=%)', TG_OP, OLD.id USING ERRCODE = 'restrict_violation';
                END
                $fn$;
                """,
                """
                DROP TRIGGER IF EXISTS trg_gl_journal_export_lines__immutable ON gl_journal_export_lines;
                CREATE TRIGGER trg_gl_journal_export_lines__immutable BEFORE UPDATE OR DELETE ON gl_journal_export_lines
                  FOR EACH ROW EXECUTE FUNCTION gl_journal_export_lines_immutable();
                ALTER TABLE gl_journal_export_lines ENABLE ALWAYS TRIGGER trg_gl_journal_export_lines__immutable;
                """,

                // AM-12 / S-021: the payroll stamps are frozen once the run is Locked (or has moved on: Paid, Voided). The run that BECOMES Locked in
                // a transaction is registered in a transaction-local setting, because its stamps are written in that same transaction in an order the
                // writer does not control; children are therefore judged at COMMIT. A later transaction gets no such excuse.
                """
                CREATE OR REPLACE FUNCTION payroll_runs_stamp_freeze() RETURNS trigger LANGUAGE plpgsql AS $fn$
                DECLARE
                  v_was boolean := OLD.status IN ('Locked', 'Paid', 'Voided');
                  v_is boolean := NEW.status IN ('Locked', 'Paid', 'Voided');
                BEGIN
                  IF v_is AND NOT v_was THEN
                    PERFORM set_config('kynex.payroll_run_locked', coalesce(current_setting('kynex.payroll_run_locked', true), '') || NEW.id::text || ',', true);
                  ELSIF v_is AND v_was THEN
                    IF NEW.cost_center_resolution_hash IS DISTINCT FROM OLD.cost_center_resolution_hash THEN
                      RAISE EXCEPTION 'payroll_runs.cost_center_resolution_hash is frozen: run % is %', OLD.id, OLD.status USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF NEW.cost_accounting_applied IS DISTINCT FROM OLD.cost_accounting_applied THEN
                      RAISE EXCEPTION 'payroll_runs.cost_accounting_applied is frozen: run % is %', OLD.id, OLD.status USING ERRCODE = 'restrict_violation';
                    END IF;
                  END IF;
                  RETURN NEW;
                END
                $fn$;
                """,
                """
                DROP TRIGGER IF EXISTS trg_payroll_runs__stamp_freeze ON payroll_runs;
                CREATE TRIGGER trg_payroll_runs__stamp_freeze BEFORE UPDATE OF status, cost_center_resolution_hash, cost_accounting_applied ON payroll_runs
                  FOR EACH ROW EXECUTE FUNCTION payroll_runs_stamp_freeze();
                ALTER TABLE payroll_runs ENABLE ALWAYS TRIGGER trg_payroll_runs__stamp_freeze;
                """,
                // One function for the three line tables: it reads the run id out of to_jsonb(NEW), as the schema conventions require of a shared guard.
                """
                CREATE OR REPLACE FUNCTION payroll_lines_stamp_freeze() RETURNS trigger LANGUAGE plpgsql AS $fn$
                DECLARE
                  v_run uuid := CASE TG_TABLE_NAME WHEN 'payroll_slips' THEN (to_jsonb(NEW)->>'run_id')::uuid ELSE (to_jsonb(NEW)->>'payroll_run_id')::uuid END;
                  v_status text;
                BEGIN
                  SELECT r.status INTO v_status FROM payroll_runs r WHERE r.id = v_run;
                  IF v_status IN ('Locked', 'Paid', 'Voided')
                     AND position(v_run::text || ',' in coalesce(current_setting('kynex.payroll_run_locked', true), '')) = 0 THEN
                    RAISE EXCEPTION '% cost-centre stamp is frozen: payroll run % is %', TG_TABLE_NAME, v_run, v_status USING ERRCODE = 'check_violation';
                  END IF;
                  RETURN NULL;
                END
                $fn$;
                """,
                """
                DROP TRIGGER IF EXISTS trg_payroll_earnings__stamp_freeze ON payroll_earnings;
                CREATE CONSTRAINT TRIGGER trg_payroll_earnings__stamp_freeze AFTER UPDATE OF cost_center_id, cost_center_source ON payroll_earnings
                  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW
                  WHEN (OLD.cost_center_id IS DISTINCT FROM NEW.cost_center_id OR OLD.cost_center_source IS DISTINCT FROM NEW.cost_center_source)
                  EXECUTE FUNCTION payroll_lines_stamp_freeze();
                ALTER TABLE payroll_earnings ENABLE ALWAYS TRIGGER trg_payroll_earnings__stamp_freeze;
                """,
                """
                DROP TRIGGER IF EXISTS trg_payroll_deductions__stamp_freeze ON payroll_deductions;
                CREATE CONSTRAINT TRIGGER trg_payroll_deductions__stamp_freeze AFTER UPDATE OF cost_center_id, cost_center_source ON payroll_deductions
                  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW
                  WHEN (OLD.cost_center_id IS DISTINCT FROM NEW.cost_center_id OR OLD.cost_center_source IS DISTINCT FROM NEW.cost_center_source)
                  EXECUTE FUNCTION payroll_lines_stamp_freeze();
                ALTER TABLE payroll_deductions ENABLE ALWAYS TRIGGER trg_payroll_deductions__stamp_freeze;
                """,
                """
                DROP TRIGGER IF EXISTS trg_payroll_slips__stamp_freeze ON payroll_slips;
                CREATE CONSTRAINT TRIGGER trg_payroll_slips__stamp_freeze AFTER UPDATE OF cost_center_id ON payroll_slips
                  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW
                  WHEN (OLD.cost_center_id IS DISTINCT FROM NEW.cost_center_id)
                  EXECUTE FUNCTION payroll_lines_stamp_freeze();
                ALTER TABLE payroll_slips ENABLE ALWAYS TRIGGER trg_payroll_slips__stamp_freeze;
                """,
            ];
        }
    }
}
