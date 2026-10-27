# M2 — the ledger is append-only in the database

Migration `*_FinanceGlEntriesAppendOnly` (cost-accounting v1, WP-E). SQL only, no new table or column, so there is no
data-dictionary column line, retention row or RLS shape to add (`TableBudgetRatchetTests` is unaffected). It follows
`CONVENTIONS.md` §5 (append-only) and rule 3 of the schema skill: the database enforces, not the service. Every function and
trigger is re-runnable (`CREATE OR REPLACE`, `DROP TRIGGER IF EXISTS` then `CREATE`), the migration sets
`lock_timeout = 5s` so it fails fast instead of queueing behind a long transaction, and `Down` drops all of it.

Names follow `trg_<table>__<what it enforces>`. The contract's `trg_finance_gl_entries_append_only` is spelled with the double
underscore for that reason.

## What is frozen, and what may still change

`finance_gl_entries`, trigger `trg_finance_gl_entries__append_only` (BEFORE UPDATE OR DELETE):

| | Rule |
|---|---|
| Frozen after insert | `id`, `tenant_id`, `company_id` (no NULL to value exception), `source_*`, `event_type`, `debit_account`, `credit_account`, `amount`, `currency`, `entry_date`, `period`, `description`, `posted_by`, `posted_by_name`, `created_at_utc`, `reversal_of_entry_id`, `posting_key`, `cost_center_id`, `cost_center_code`, `cost_center_name`, `cost_center_gl_segment`. An UPDATE that rewrites a value with itself is not a change. |
| DELETE | Refused, except while the tenant itself is being purged (soft-deleted: inactive and `__deleted_` in the slug; `PlatformController.PurgeTenant`, `SoftDeletedTenantRule`). |
| TRUNCATE | Refused (`trg_finance_gl_entries__no_truncate`). A plain TRUNCATE already fails because the loan tables reference the ledger; the trigger covers `CASCADE`. |
| `is_reversed` | false to true only, never on a reversal row, and only with its contra (below). |
| `erp_*` | The state machine below. |

`session_replication_role = replica` does not skip any of this: every ledger trigger is `ENABLE ALWAYS`.

### Checks evaluated at COMMIT

EF sends the UPDATE of an original and the INSERT of its contra, or a stamp and its export, in an order the writer does not
control. These rules are therefore constraint triggers, `DEFERRABLE INITIALLY DEFERRED`, judged on the final state.

* `trg_finance_gl_entries__reversal_valid` (AFTER INSERT, rows with `reversal_of_entry_id`; AM-11 database half as ruled by AM-25). The
  original exists in the same tenant, is in the same company, has the same currency, is not itself a reversal and is not already
  reversed; the reversal is side-swapped (one leg may be empty: the void writes one-sided rows; period, date and reference are free, so a
  period-shifted void passes); its cost centre id and all three snapshots are copied verbatim; and its amount, and all reversals of one
  original together, never exceed the original (a partial contra is the bonus cancel; a second full contra is a double void).
* `trg_finance_gl_entries__reversed_needs_contra` (AFTER UPDATE OF `is_reversed`). A contra of this original was written **in the same
  transaction** and the contras together cover the original (every writer flags an original only when its reversal closes it out). The
  one other shape is a void with funds DISBURSED: the retained cash leg of a `PayrollNetSettlement` or `PayrollRemit:<group>` journal is
  flagged with no linked contra, and the unlinked `...Reclass` row of the same run, written in the same transaction, stands in for it.
* "Same transaction" and "already reversed" are decided with transaction-local settings (`kynex.gl_contra_for`, `kynex.gl_reclass`,
  `kynex.gl_flagged`) written by `trg_finance_gl_entries__reversal_witness` and the append-only trigger, not with `xmin`: under the
  savepoints EF opens inside an explicit transaction a row's `xmin` is a sub-transaction id. The settings vanish at COMMIT or ROLLBACK.
* `ix_finance_gl_entries_reversal_of (tenant_id, reversal_of_entry_id) WHERE reversal_of_entry_id IS NOT NULL` serves the lookups above
  (and `LoansController.HasContraAsync`); without it a void of N lines would scan the ledger N times.
* `trg_finance_gl_entries__erp_evidence` (AFTER UPDATE OF `erp_posting_status`, `erp_document_number`). Exported needs an export line
  carrying the entry. Posted needs a Confirmed export carrying the entry with the same ERP document number, either the whole export or a
  Confirmed journal in `journal_confirmations_json`. Changing the number of a line that is already Posted needs a Confirmed
  **Replacement** export.

### The `erp_posting_status` state machine

Read from `ErpPostingTransitions` (run level), `ErpPostingEvidence` (line level) and the export kinds in `JournalExportService`.

```text
NotReady    -> ReadyForErp | Exported | Rejected | Posted
ReadyForErp ->               Exported | Rejected | Posted
Exported    ->               (Exported) | Rejected | Posted
Rejected    ->               (Rejected) | Exported | Posted      (a Supplementary export re-sends a refused journal)
Posted      -> Posted only                                       (a re-export never walks it back; Replacement may change the number)
```

Posted needs a non-empty document number. A Posted line's document number cannot be cleared and its rejection reason cannot change.
`erp_status_changed_at_utc` cannot be cleared once set.

## Around the ledger

* `trg_cost_centers__guard`: `code` and `company_id` are frozen while a posted line references the centre; deactivating, soft-deleting or
  moving it is refused while a live department, position, employee, active mapping, company default or active child points at it. The
  message is `cost_centre_in_use:<table>=<count>[,...]`. A centre referenced only by history may be deactivated, renamed or soft-deleted.
  The lookups on `departments`, `positions` and `employees` are deliberately unindexed (v1-spec 1.6): this runs on a rare edit.
* `trg_cost_centers__parent_cycle`: no cycle, no chain deeper than ten levels, counting the subtree under a re-parented centre.
* `trg_gl_journal_export_lines__immutable`: the export artifact is frozen (the hash verification depends on it); erased only with its tenant.
* `trg_payroll_runs__stamp_freeze`, `trg_payroll_{earnings,deductions,slips}__stamp_freeze`: once a run is Locked (or Paid, Voided) its
  `cost_center_resolution_hash`, `cost_accounting_applied` and the line cost-centre stamps cannot change. The run that BECOMES Locked in a
  transaction is registered in a transaction-local setting (`kynex.payroll_run_locked`), so the Lock can write its stamps in the same
  transaction in either order; a later transaction gets no such excuse.

## Who can switch this off

Triggers hold only against a role that does not own the tables. The migration cannot know which role the application connects as and does
not change roles. It raises one NOTICE listing every login role that could disable the triggers (the owner, members of the owning role,
superusers), `finance_ledger_trigger_bypass_roles()` returns the same list at any time, and the NOTICE says
**RELEASE BLOCKER** if the application's role is among them. In this repository the migration job (`dotnet ef database update` on
`PROD_DATABASE_URL`) and the application (`ConnectionStrings__Default`) both point at the same Neon database, and
`scripts/check_migration_target.py` compares host and database but deliberately never the user, so nothing in the repository shows two
different roles.

## Correcting a posted line

Post a contra (the void, a reversal, an adjustment). Nothing edits a posted line. A data repair that genuinely must touch one runs as the
table owner inside a migration, with `ALTER TABLE ... DISABLE TRIGGER <name>` and `ENABLE ALWAYS TRIGGER <name>` in the same transaction.

## Proof

`FinanceLedgerAppendOnlyPostgresTests`, `LedgerSupportTriggersPostgresTests`, `LedgerRuntimeRolePostgresTests`,
`FinanceLedgerMigrationPostgresTests` (database rules, raw SQL, real PostgreSQL), `FinanceLedgerWritersPostgresTests` (the real writers
against the triggers) and `Security/FinanceLedgerWriterSweepTests` (a ratchet over every writer of the ledger, T-052 and S-020).
