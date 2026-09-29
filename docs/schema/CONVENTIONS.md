# Schema conventions

Every table in the KynexOne baseline obeys these rules. They are stated once here and not repeated
per table. If a table cannot obey one, that is a design decision and it is recorded in
`DATA_DICTIONARY.md` against the table, not left implicit.

Source of truth for the table set: `../../TARGET_SCHEMA.md` — **pinned to revision 7, 76 tables**.
Nothing here may contradict it.

> **Revision 7 is the approved design of record, and the baseline DDL slice is written.** The owner
> has ruled on every open question, the final adversarial review is applied with nothing disputed,
> and revision 7 settles the twelve contradictions the first implementation of the baseline found.
> Four of those change rules in *this* file and are marked **[R7]** below: the domain-K **unit rule**
> (minutes, §4), **object naming** (§1 — CONVENTIONS §16 decision 4 is now closed), the
> **nullable-subject `EXCLUDE`** rule (§5), the **composite `ON DELETE SET NULL`** rule and the
> **PostgreSQL 15 floor** it implies (§3), and the **external-id convention**, which is additive
> rather than a rename (§14). §8 is a per-FK
> relationship register, §9 the enumerated domain of every status column, §10 a state machine per
> lifecycle entity, §12 soft delete and the seeded retention matrix, §13 types/currency/time, §14
> audit columns, §16 polymorphic pointers, and **§19 the platform design** — SQL DDL baseline, RLS,
> partitioning, indexes, transactions, operations. **Where those sections state something, they win
> over anything marked [NEW] here.**
>
> **Decision 9: the 75-table hard cap is dropped**, replaced by a principle — *no duplicate tables,
> nothing kept that nothing uses, every table traceable to a capability*. 76 stands and no cut is
> taken; `TARGET_SCHEMA.md` §7 is kept as the record of what the cuts would have cost, so the
> question is not re-opened from memory. **A principle is a weaker brake than a number** (§6 Risk 1
> says so itself), so every addition must name its capability and pass the no-duplicate test in
> review.
>
> **§19 is a specification, not a claim of completion: none of it is built yet.** The rules below
> describe the database the baseline will create.

**Status marker on every rule:**

| Marker | Meaning |
|---|---|
| **[TS]** | Stated in `TARGET_SCHEMA.md`. Settled. Section reference given. |
| **[NEW]** | Proposed by this document. Consistent with TARGET_SCHEMA but not stated there. Needs one architect sign-off before the baseline migration is written. |
| **[OPEN]** | Genuinely undecided. **Do not guess.** Listed in §12 with who must decide. |

---

## 1. Naming

| Object | Rule | Example | Status |
|---|---|---|---|
| Table | `snake_case`, plural noun | `payroll_slip_lines` | **[TS]** §2 |
| Column | `snake_case`, singular | `contributory_wage` | **[TS]** §2 |
| Foreign key column | `<referenced_table_singular>_id` | `payroll_run_id`, `cost_center_id` | **[TS]** §2 |
| Self-reference FK | `parent_id`, or the role it plays | `parent_id`, `supersedes_id`, `reversal_of_id` | **[TS]** §2 |
| Boolean | `is_` / `has_` / a plain adjective, never negated | `is_active`, `wps_eligible` — never `is_not_locked` | **[NEW]** |
| Timestamp | `<verb_past>_at` | `locked_at`, `approved_at`, `purged_at` | **[TS]** §2 |
| Date (not instant) | `<noun>_date` or `<verb_past>_on` | `joining_date`, `gosi_first_registered_on` | **[TS]** §2 |
| Monetary amount | `<thing>_amount` or the bare domain word | `variance_amount`, `net`, `basic` | **[TS]** §2 |
| Closed-set discriminator | `kind`, `status`, `<x>_type` | `run_type`, `entry_type`, `purge_state` | **[TS]** §2 |
| JSON column | plural noun or `<thing>_json` | `sections`, `exceptions`, `rules_snapshot` | **[TS]** §2 |
| Primary key constraint | `pk_<table>` | `pk_payroll_slips` | **[TS]** §9.0 |
| Foreign key constraint | `fk_<table>__<column>` | `fk_payroll_slips__run_id` | **[TS]** §9.0 |
| Unique constraint | `uq_<table>__<cols joined by _>` | `uq_payroll_slips__run_id_employee_id` | **[TS]** §9.0 |
| Check constraint | `ck_<table>__<what it asserts>`, and **every enum CHECK is named and mirrored by a C# constants class of the same name** | `ck_gl_journal_lines__debit_xor_credit` | **[TS]** §9.0, §1 |
| Check constraint — **the 36 `chk_` legacy names** | The 36 CHECKs `TARGET_SCHEMA.md` §9 names keep `chk_<table>_<column>`. **Closed set: no thirty-seventh is ever added.** | `chk_payroll_runs_status` | **[TS]** §9.0 |
| Exclusion constraint | `ex_<table>__<subject>_no_overlap` | `ex_employee_salaries__employee_no_overlap` | **[TS]** §9.0 |
| Index | `ix_<table>__<cols>` , partial adds `__<predicate>` | `ix_payroll_inputs__pending` | **[TS]** §9.0 |
| Trigger | `trg_<table>__<what it enforces>` | `trg_leave_ledger__append_only` | **[TS]** §9.0 (`trg_payroll_audit_logs_append_only` exists and keeps its name, like the 36) |
| View | `v_<what it answers>` | `v_leave_balances`, `v_employee_current` | **[TS]** §2 |

*Rationale:* a name that encodes its object type and its table makes a failing constraint in a
production log self-explanatory without opening the schema.

**Naming is decided — this was `§16` decision 4 and it is closed [R7] [TS] §9.0.** It could not stay
open past the first `CREATE TABLE`, and the baseline is now written. Two things follow from it:

1. **`ck_<table>__<assertion>` is the rule for every CHECK written from revision 7 onward**, and for
   every CHECK in the baseline that `TARGET_SCHEMA.md` §9 does not itself name. The assertion is in
   words, not a column list: `ck_audit_logs__erasure_is_complete`, not `ck_audit_logs__pd_erased_at`.
2. **The 36 names §9 gives are load-bearing and frozen.** Each is mirrored by a **C# constants class
   of the same name**, and that string is what CI compares the constants class against
   (`KEEPING_DOCS_HONEST.md` assertion 5). Renaming them to `ck_` would silently break the assertion
   that is the whole reason the design named constraints at all. They are exempt, the exemption is
   closed, and everything added later takes a `ck_` name.

**A constraint name belongs to exactly one table [R7].** Postgres stores a constraint against one
relation, so a design row that gives one name to a constraint on two tables describes something that
can only exist as two objects — and a failure message naming it would not say which table raised it,
which defeats the point of encoding the table in the name. §9's row 3b did this
(`chk_auth_subject_kind` over both `auth_sessions` and `auth_tokens`) and is split in revision 7.

**Reserved word rule [NEW]:** never name a column with a Postgres reserved word (`end`, `order`,
`user`, `references`). Use `end_date`, `step_order`, `actor_user_id`. Quoting works and is a trap
for the next person.

---

## 2. Keys

| Rule | Status |
|---|---|
| Every primary key is `uuid`, generated **app-side as UUIDv7**. No `bigserial`, no `int`, no composite natural PK. | **[TS]** §Conventions |
| This includes `employees.id`. The three current employee key forms (`int Id`, `Guid PublicId`, and a `Guid EmployeeId` in 12 places pointing at `PublicId`) collapse to one `employee_id uuid`. | **[TS]** §Conventions, Q1 |
| Human-facing identifiers are **separate columns**, never the key: `employee_number`, `payslip_number`, `batch_number`, `settlement_number`, `letter_number`. | **[TS]** §Conventions |
| All of those are allocated from `number_sequences` with a single `UPDATE … RETURNING`. **No counter ever lives in a JSON settings blob.** | **[TS]** §Conventions |
| One `bigint` sequence column exists and is not a key: `audit_logs.seq`, from a **per-tenant Postgres sequence** (never reuses a value), for Merkle checkpoint ranges. | **[TS]** §Q, §19.3 |
| **On the five partitioned tables the primary key is composite: `(id, <partition key>)`**, and the tenant key becomes `UNIQUE (tenant_id, id, <partition key>)`. Postgres requires the partition key in every unique constraint. | **[TS]** §19.3 |

*Rationale for UUIDv7 over `bigint`:* the key is exposed in mobile and API URLs, so it must not be
guessable or enumerable; v7's time prefix keeps inserts index-local, which is the only real cost of
random UUIDs. *Rationale for a separate display number:* a customer-visible number has formatting,
prefixes and yearly resets that a key must never acquire.

---

## 3. Tenancy

| Tier | Marker | Columns | Status |
|---|---|---|---|
| Platform | **P** | no `tenant_id` | **[TS]** §2 |
| Reference | **R** | seeded by the baseline, read-only to tenants | **[TS]** §2 |
| Tenant | **T** | `tenant_id uuid NOT NULL` | **[TS]** §2 |
| Company | **C** | `tenant_id` + `company_id` (legal entity / MOL establishment) | **[TS]** §2 |

**The composite-key rule [TS] §Conventions — this is the single most important rule in the schema:**

1. Every tenant-tier table carries `tenant_id uuid NOT NULL`.
2. Every tenant-tier table declares `UNIQUE (tenant_id, id)` **in addition to** its primary key on `id`.
3. Every FK between two tenant tables is composite:
   `FOREIGN KEY (tenant_id, x_id) REFERENCES x (tenant_id, id)`.
4. Company-tier FKs are `(tenant_id, company_id) REFERENCES companies (tenant_id, id)`.

*Rationale:* a row can then never point across tenants, even through an application bug, a bad
import or a hand-written `UPDATE`. The redundant unique index is the price; tenant isolation
enforced by the database rather than by every developer is what it buys.

**A composite `ON DELETE SET NULL` must name its column [R7] [TS] §8.1.** Write
`ON DELETE SET NULL (uploaded_by)`, never a bare `ON DELETE SET NULL`. Rule 3 above makes every
optional FK composite on `(tenant_id, x_id)`, and a bare `SET NULL` nulls **both** referencing
columns — including `tenant_id`, which is `NOT NULL` on every tenant table. **The DDL is accepted
without complaint**; the failure is at runtime, `23502`, on the first parent delete that fires the
rule, which in practice is a retention purge. This is one of the two defects the baseline
implementation found that the design had never addressed, and the reason it is worth a rule of its
own is that nothing catches it until production: no migration test that never deletes a parent will
ever see it. Every `SET NULL` row in `TARGET_SCHEMA.md` §8.2 is to be read as naming its own column.

**PostgreSQL 15 is the floor, and production is Neon 17.11 [R7] [TS] §19.1.** The column-list
`SET NULL` above arrived in **PG 15**, and there is no workaround that keeps the composite tenant
guard — so the floor is a consequence of the single most important rule in this schema, not a
preference. Two lesser floors sit under it and are covered by the same number: `UNIQUE NULLS NOT
DISTINCT` (PG 15), which every nullable-tenant and nullable-company unique uses, and
`DETACH PARTITION … CONCURRENTLY` (PG 14), which §13's default-partition recovery needs. CI, the
verification containers and local development must all be **PG 15 or later**; a boot assertion
records `server_version_num`.

**Three tables reach tenancy only through a nullable `company_id`, and all three now carry a direct
tenant FK [R7] [TS] §8.2 rows 159–161**: `gl_mappings`, `approval_workflows` and
`document_templates`. In each, `company_id IS NULL` is not an edge case — it is the *default* row,
the tenant-wide workflow, the tenant-wide template — so the rows with no tenant FK at all were
exactly the rows every company falls back to. The test to apply when adding a table: **if every path
from this table to `tenants` passes through a nullable column, it has no tenant FK.**

**Platform operators are a separate table [TS] §5 decision 2.** `platform_users`, not a
`user_kind` column on `users`. **`users.tenant_id` is therefore `NOT NULL`** — no nullable-tenant
row sits in a client-data table at all.

**Nullable-`tenant_id` tables, enumerated:**

| Table | Why NULL is legal |
|---|---|
| `auth_sessions`, `auth_tokens` | **The only two**, per §5 decision 2: they serve both subject kinds through an XOR CHECK, because duplicating rotation, reuse-detection and lockout logic would breach the no-duplicate-tables principle. Authentication infrastructure, not client data. |
| `public_holidays` | NULL = the platform KSA calendar. |
| `retention_policies` | NULL = the platform default period; a tenant row may only **lengthen** it, enforced by a CHECK against the platform row. |
| `background_jobs`, **`background_job_items`** | Platform-scope work, **and its per-row outcomes** — added in revision 7. An item inherits its job's NULL tenant, so under shape (a) the worker draining a platform queue cannot see its own items: zero processed, zero errors, nothing raised. |
| `audit_logs` | Platform-scope events. |

**That is seven tables, and the number is asserted** (`KEEPING_DOCS_HONEST.md` assertion 6): an
eighth fails the build. Each carries a CHECK tying the NULL to the platform case, and each gets
Postgres RLS plus a tenant-isolation test in the baseline PR. **No new nullable-tenant table without
owner sign-off** — and note that in every case found so far the wrong policy shape made the table
look *empty* rather than look *wrong*, which is why this list is enumerated rather than derived.

> *Closed:* `TARGET_SCHEMA.md` §6 Risk 3 once listed `users` among the nullable-tenant tables. It no
> longer does — it names the correct five and cites decision 2.

**`tenant_id` column position [NEW]:** always the second column, immediately after `id`. See §10.

---

## 4. Types

| Concept | Type | Status |
|---|---|---|
| Money (currency of record, SAR) | `numeric(18,2)` | **[TS]** §Conventions |
| Rate / percentage / multiplier | `numeric(9,6)` — store `0.090000`, not `9` | **[TS]** §Conventions |
| Instant | `timestamptz`, always stored and compared in **UTC** | **[TS]** §Conventions |
| Calendar date (payroll, effective dating, leave, attendance day) | `date` | **[TS]** §Conventions |
| Date range | `daterange` (`payroll_runs.attendance_locked_range`) | **[TS]** §F |
| **Worked / scheduled / late / absent / overtime / booked duration** | **`integer` whole minutes**, column named `<what>_minutes`. **No `hours` column exists in the baseline.** | **[R7] [TS]** §2.K |
| **Leave duration** | `numeric(9,2)` **days** — the one duration that is not minutes, because leave is granted and consumed in days including half days | **[R7] [TS]** §2.K |
| **Accounting or payroll period** | **two `smallint` columns, `year` + `month`**, both `NOT NULL`, with `CHECK (month BETWEEN 1 AND 12)`. Never a bare `period`, never a `date`. A role prefix only where one row carries more than one period: `run_year`/`covered_year` on `payroll_inputs`, `start_year` on `loans`, `due_year` on `loan_installments` | **[R7] [TS]** §2.F |
| Numeric band bound | `numeric` + `numrange` in the exclusion constraint | **[TS]** §E |
| Currency code | `char(3)`, on `companies` only | **[TS]** §1 |
| Time zone | IANA id, validated, on `tenants` and optionally `companies` | **[TS]** §1 |
| Identifier / format-bearing text | **length-bounded** — the full list is §13.3 | **[TS]** §13.3 |
| Free-text note (names, addresses, reasons, comments, messages) | unbounded `text` | **[TS]** §13.3 |
| Hash | `text` holding lowercase hex (`sha256`, `entry_hash`, `envelope_hash`, `refresh_token_hash`) | **[NEW]** |
| Set of codes | `text[]` (`enabled_modules`, `request_types`) | **[TS]** §A, §O |
| Structured document | `jsonb`, never `json` | **[TS]** §2 |

*Rationale for `numeric` money:* payroll differences of one halala are audit findings; float is
disqualified. *Rationale for `timestamptz` + UTC:* KSA is UTC+3 with no DST today, which is exactly
the condition under which a `timestamp` column silently works until it does not.
*Rationale for `rate numeric(9,6)`:* the GOSI Entrant2024 ladder steps by 0.5 percentage points,
so two decimal places on a percentage is not enough headroom.

**Bounded text [TS] §13.3.** Identifier and format-bearing columns are length-bounded so a paste
accident cannot become a filing rejection. The register is in §13.3; the ones you will meet most:
`slug` 63 · `employee_number` 32 · `iban` 34 (+ a SA IBAN pattern CHECK, mod-97 in the service) ·
`bank_code` 16 · `gosi_registration_no` / `gosi_employee_no` / `mol_establishment_no` 20 ·
`cr_number` 15 · `national_id` / `iqama_no` 10 with `CHECK (col ~ '^[12][0-9]{9}$')` ·
`border_no` 12 · `occupation_code` 16 · `nationality_code` 2 · `currency_code` 3 ·
`timezone_id` 64 · **every status and kind column 40** · `pay_components.code` 32 ·
`letter_number` / `payslip_number` / `batch_number` 40.

**Currency rule [TS] §13.2.** SAR is the currency of record. Each legal entity carries
`companies.currency_code char(3) NOT NULL DEFAULT 'SAR'`, and **every money column in the design is
in that company's currency**. There is **no per-row currency column anywhere** — revision 2 had **26
`Currency` properties in `Models/` and they disagreed with each other**; all are removed. Multi-currency payroll is explicitly out of this baseline.
*Rationale:* a per-row currency column that is always `'SAR'` is a column nobody validates and every
`SUM` ignores, right up to the day one row says otherwise.

**Duration unit rule [R7] [TS] §2.K.** Every attendance, overtime and timesheet duration is **whole
minutes** as an `integer`; leave is **days** as `numeric(9,2)`. Hours are a *presentation* concern —
the UI divides by 60 at the edge — and never a stored type. Revision 6 mixed the two: it wrote
`timesheets.total_hours` and `timesheet_entries.hours` beside `attendance_days.worked_minutes`, which
made the timesheet reconciliation the expression `timesheet_hours × 60 − attendance_minutes` — a unit
conversion wearing the costume of a subtraction, and unenforceable as an exact `CHECK` the moment an
entry held a third of an hour. In minutes, both sides of the reconciliation are the same integer unit
and no rounding rule has to be negotiated between the attendance engine and the timesheet engine. The
two `numeric` exceptions are deliberate and both are contractual rather than measured:
`employee_contracts.weekly_hours` and `approval_workflows.steps[].sla_hours`.

**Time and calendar rule [TS] §13.4.** The database stores instants in
UTC. The **business day is a local date**, anchored by `tenants.timezone_id` (IANA, validated,
default `Asia/Riyadh`) with an optional `companies.timezone_id` override.
`attendance_days.work_date`, `overtime_requests.work_date`, `timesheet_entries.work_date` and the
`payroll_runs` year/month period are local dates in that zone — **never derived from UTC at read
time**. Hijri dates are **derived, never stored**: `HijriDateService` feeds the Ramadan and
statutory working-hours rules from the same local date.

---

## 5. Effective dating

**One convention, everywhere, without exception [TS] §Conventions:**

```sql
effective_from date NOT NULL,
effective_to   date NULL,        -- NULL = open-ended
-- both bounds INCLUSIVE
```

Every range expression in constraints, queries and rule resolution is exactly:

```sql
daterange(effective_from, COALESCE(effective_to, 'infinity'::date), '[]')
```

No-overlap is enforced by the database, not the service:

```sql
EXCLUDE USING gist (
  tenant_id  WITH =,
  <subject>  WITH =,
  daterange(effective_from, COALESCE(effective_to,'infinity'::date), '[]') WITH &&
)   -- requires the btree_gist extension
```

Resolution for a date `D` is the single row whose range contains `D`.

**A nullable subject column must be wrapped in `COALESCE` [R7] [TS] §1.** An `EXCLUDE` constraint
**skips any row whose operand is `NULL`**, so a nullable column in the subject list does not weaken
the guarantee — it **exempts the NULL rows entirely**, and nothing is raised. The live example is
`retention_policies`, where `tenant_id IS NULL` marks the **platform default row**: under the plain
convention the only rows free to overlap were the ones the whole retention engine falls back to when
a tenant has no override. Two overlapping platform defaults for one entity would have been accepted
by the database, resolved non-deterministically, and silently changed how long personal data is kept.
Write:

```sql
EXCLUDE USING gist (
  COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid) WITH =,   -- nullable uuid subject
  COALESCE(gosi_branch, '') WITH =,                                           -- nullable text dimension
  entity_name WITH =,
  daterange(effective_from, COALESCE(effective_to,'infinity'::date), '[]') WITH &&
)
```

The same trap applies to `UNIQUE`, where the remedy is `UNIQUE NULLS NOT DISTINCT` (PG 15) — which is
why every nullable-tenant and nullable-company unique in the baseline is written that way. **CI
asserts both**: every `EXCLUDE` or `UNIQUE` whose column list contains a nullable column must use
`COALESCE` or `NULLS NOT DISTINCT`, which is checkable from `pg_constraint` and
`information_schema.columns` without reading the design.

**Effective-dated tables:** `employee_assignments`, `employee_salaries`, `employee_contracts`,
`employee_bank_accounts`, `shift_assignments`, `employee_gosi_registrations`, `statutory_rules`,
`nitaqat_grid`, `retention_policies` and **`company_pay_policies`** — which revision 3 moved out of
`companies.settings` precisely so it gets the same `EXCLUDE … gist` discipline as every other dated
table. `permission_grantor_records` is bounded by `expires_at` rather than a date range.

**Mandatory boundary tests in the baseline suite [TS] §Conventions:** a row ending on the 30th and
the next starting on the 31st; a payroll run on the exact `effective_from`; a rule whose
`effective_to` equals the period end; an overlapping insert that **the database** must reject.

*Rationale for inclusive-inclusive:* HR and payroll users write "in post until 31 March". A
half-open convention forces every screen, report and import to do an off-by-one translation, and
one of them eventually gets it wrong in a way that costs a month's salary.

**Bands use the other convention, deliberately [TS] §E:** `statutory_rule_bands` and `nitaqat_grid`
percentage bands are **inclusive lower, exclusive upper** (`numrange(lower, upper, '[)')`), because
a continuous quantity has no "last value". Dates are `'[]'`; numeric bands are `'[)'`. That is the
whole rule.

---

## 6. Immutability, deletion and append-only

| Class | Rule | Tables | Status |
|---|---|---|---|
| **Frozen** | A trigger rejects `UPDATE` and `DELETE` once the parent is `Locked`, `Filed` or `Approved`. The row holds a snapshot, not a live join. | `payroll_slips`, `payroll_slip_lines`, `wps_lines`, `gosi_filings`, `eos_calculations`, `final_settlement_lines` | **[TS]** §Conventions |
| **Append-only** | `BEFORE UPDATE`/`BEFORE DELETE` triggers raise. Corrections are **reversing rows**, never edits. | `leave_ledger`, `audit_logs`, `payroll_audit_logs`, `retention_purge_audits` | **[TS]** §Conventions |
| **Immutable raw** | Written once by ingestion; a correction is a new row with `source='Correction'`. | `attendance_punches` | **[TS]** §K |
| **Versioned** | Never edited once referenced; a change is a new `version`. | `document_templates.version`, `employee_documents` via `supersedes_id` | **[TS]** §D |
| **Mutable** | Ordinary rows. | everything else | — |

**Soft delete exists on exactly four tenant tables, plus one platform table. [TS] §12.1**

| Table | Lifecycle columns | Read by |
|---|---|---|
| `tenants` | `soft_deleted_at`, `purged_at` (+ `status='SoftDeleted'`, replacing `IsActive=false`) | `Infrastructure/Retention/Rules/SoftDeletedTenantRule.cs:94` |
| `employees` | `deleted_at`, `retention_until`, `privacy_status` (`Normal`/`PendingErasure`/`Anonymised`/`MergedDuplicate`), `redacted_at` | `Infrastructure/Retention/Rules/ExpiredEmployeeRecordRule.cs:74` |
| `users` | `deleted_at` | — |
| `companies` | `soft_deleted_at` | — |
| `platform_users` | `deleted_at` | platform-tier, outside every tenant purge |

**Everywhere else, delete means delete.** A ported controller that adds an `IsDeleted` filter is
wrong. The live codebase carries `IsDeleted`/`DeletedAt` on **74 model properties** and `IsActive`
on **70**; the baseline keeps soft delete only where a legal trace outlives the record.

**`is_active` survives only where it means "usable now" on a catalogue [TS] §12.1:**
`pay_components` · `leave_types` · `cost_centers` · `designations` · `grades` ·
`approval_workflows` · `attendance_devices`. Nowhere else.

*Rationale:* a soft-delete flag has to be remembered in every query, is forgotten in exactly one of
them, and that bug is a data leak. Confining it to five named tables makes "is there a filter
missing here?" a question with a five-item answer.

**Everywhere else, lifecycle is domain state a human can explain:** `status`, `revoked_at`,
`consumed_at`, `effective_to`, `purge_state`, `void_reason`.

**Hard delete is allowed only for:** transient rows past their TTL (`auth_tokens`, expired
`auth_sessions`, delivered `notification_deliveries`, completed `background_job_items`) and PDPL
purges executed by the retention job, which writes `retention_purge_audits`. See
`OWNERSHIP_AND_RETENTION.md`.

**Referential behaviour [TS] §8** — every FK declares cardinality, optionality and `ON DELETE`
explicitly. **None is left to the ORM default. `TARGET_SCHEMA.md` §8.2 is the per-FK register and
is authoritative**; the rules it follows are:

| Relationship shape | Action | Status |
|---|---|---|
| Anything financial, filed, audited or historical — **the default** | `ON DELETE RESTRICT` | **[TS]** §8.1 |
| Child that **is a part of its parent** and has no independent meaning — a slip's lines, a batch's lines, a journal's lines, a job's items, a notification's deliveries, a rule's bands, a timesheet's entries, a loan's schedule | `ON DELETE CASCADE`. **Every CASCADE parent also has a `BEFORE DELETE` guard that raises once the parent leaves its editable state, so CASCADE can only ever fire on a draft.** | **[TS]** §8.1 |
| A **genuinely optional** column, where the fact survives the loss (a device, a shift, an uploader, a lock) | `ON DELETE SET NULL` | **[TS]** §8.1 |
| Every FK, without exception | `ON UPDATE RESTRICT` — keys are `uuid` and never updated | **[TS]** §8.1 |
| Anything reached from an audit table | **no FK at all** — `audit_logs`, `payroll_audit_logs` and `retention_purge_audits` are FK-free by design, so evidence outlives the rows and the jobs it describes | **[TS]** §Q |

**There are no foreign-key cycles [TS] §8.4** — two were broken outright. If one is ever
introduced it must be `DEFERRABLE INITIALLY DEFERRED` with a **declared insertion order**. A cycle
without one is a table pair nobody can seed, import or truncate.

**Nullability is declared per FK [TS] §8.2**, in the register's *Opt.* column: R = required,
O = optional. Not inferred, not left to the entity's `?`.

### Writing the guards: one rule that is not obvious [R7]

The frozen-row and append-only guards above are **one trigger function attached to many tables**,
which is right — a guard per table is the copy-paste this schema exists to end. But a shared
PL/pgSQL trigger function **must read the row as `to_jsonb(NEW)`** and pull fields out of the jsonb:

```sql
-- right
DECLARE r jsonb := to_jsonb(NEW);
BEGIN
  IF (r ->> 'status') = 'Locked' THEN RAISE EXCEPTION ...

-- wrong: fails at runtime on a table that has no `status`, even if that table
-- can never reach this branch
IF NEW.status = 'Locked' THEN ...
```

PL/pgSQL resolves `NEW.<column>` against **every table the function is attached to**, on first call
per table, **including in branches that table can never reach**. So the moment one shared guard
names a column that one of its tables lacks, that table's writes fail — on a code path the author
had correctly reasoned was unreachable. This cost real debugging time while `050_triggers.sql` was
written; it is recorded here so it is paid for once.

---

## 7. Enum governance

**One mechanism, no exceptions [TS] §1.** Every status, kind, purpose, category and type column is:

```
text  +  a named CHECK (col IN (…))  +  a C# constants class of the same name  +  a row in §9
```

This follows what the live database already does
(`Migrations/20260624000003_AddStatusCheckConstraints.cs:37,40`).

| Banned | Why |
|---|---|
| **PostgreSQL enums** (`CREATE TYPE … AS ENUM`) | Adding a value becomes a migration with a lock. The values are also invisible to every tool that reads the schema as text. |
| **Lookup tables for closed sets** | A closed set the code switches on does not need a join, a tenant-scoped row or an FK; it needs a constraint. |

**Eight closed sets were ungoverned until revision 7 [R7] [TS] §9 rows 37–44.** The rule above has no
exceptions, but revision 6 shipped eight columns that met its definition and had no domain anywhere:
`statutory_rules.gosi_branch` **and `payroll_slip_lines.gosi_branch`** (`Annuities`, `SANED`,
`OccupationalHazards` — `SANED` keeps its capitals: it is the acronym the scheme is published
under); `statutory_rules.payer` **and `payroll_slip_lines.gosi_payer`** (`Employee`, `Employer`); `overtime_requests.ot_type`; `attendance_punches.direction`;
`final_settlements.separation_type`; `notification_deliveries.channel`;
`retention_purge_audits.outcome`; and `eos_calculations.separation_reason`. All eight now have §9
rows and `ck_` names. The first two matter most, and **both instances of each are in scope** — the reference row *and* the
frozen slip line. Branch and payer are exactly the dimensions `gosi_filings` reports its seven totals
across, `trg_gosi_filing_totals` pivots on them, and §11.2 makes that trigger a **`WARNING`, not a
block** (deliberately: a filed return may legitimately differ, and the variance is the point). So an
unconstrained branch string is not a loud failure — it is a **silent wrong number**: the value
matches no pivot arm, the amount contributes to *no* total, the return is short by that line, and the
only signal is a warning that a variance exists without saying a value was unroutable. **The test when adding a column: if only a
release can add a value, it needs a CHECK, a C# constants class and a §9 row — in the same PR.**

**The only lookup tables are the open, tenant-editable catalogues [TS] §1:**
`permissions` · `pay_components` · `leave_types` · `cost_centers` · `designations` · `grades`.
If a tenant can add a value at runtime, it is a table. If only a release can, it is a CHECK.

*Why not a lookup table [TS] §13.1:* a closed set with **behaviour attached to each value** is
code, not data. The open catalogues customers really do edit are already tables.

**Adding a value** widens the CHECK in an ordinary migration, and it must be deployed **before** any
code writes the new value (`HOW_TO_CHANGE_THE_SCHEMA.md`). The same PR updates the C# constants
class, `TARGET_SCHEMA.md` §9 and `DATA_DICTIONARY.md`; CI compares them
(`KEEPING_DOCS_HONEST.md` assertion 5).

**State changes [TS] §10.** No status column is ever written by a free string assignment. Every
lifecycle entity has an enumerated transition set, a named actor and a stated enforcement point —
a database trigger, a `CHECK`, or one service method with a test. **Illegal transitions fail
loudly.** `TARGET_SCHEMA.md` §10 holds the per-entity machines; §9 holds the value set of every
enumerated column.

*Rationale:* a `CHECK` says which values are legal. Only a transition set says which **changes**
are legal, and "Locked → Draft" being rejected is the difference between an audit trail and a
suggestion.

---

## 8. JSON policy

`jsonb` is allowed in exactly four situations, and banned everywhere else.

**Allowed:**

| Situation | Requirement | Examples |
|---|---|---|
| **Versioned configuration** written and read as one unit by one owner | A versioned C# record validates on write; a `section_versions` sibling guards concurrent writes | `tenant_settings.sections`, `companies.settings` (non-money only), `shifts.rules`, `leave_types.policy`, `approval_workflows.steps` |
| **Frozen snapshot** — evidence of what was true, never queried for joins | Written once with the parent; never updated | `eos_calculations.rules_snapshot`, `approval_requests.workflow_snapshot`, `nitaqat_snapshots.employee_breakdown`, `audit_logs.personal_data` |
| **Open-ended payload** whose shape is owned by the caller | Never the target of a constraint | `approval_requests.payload`, `background_jobs.payload` / `result`, `payroll_issues.evidence` |
| **Small owned detail list** with no independent lifecycle and no FK | Bounded (tens, not thousands); nothing else references an element | `attendance_days.exceptions`, `leave_requests.day_breakdown`, `grades.pay_scale`, `plan_limits`, `final_settlements.clearance` |

**Banned:**

| Never put this in JSON | Why | Rule |
|---|---|---|
| A counter or sequence | Concurrent `jsonb_set` loses increments | It goes in `number_sequences`. **[TS]** §Conventions |
| Anything another row must reference | JSON cannot be an FK target | Give it a table. **[TS]** §C (cost centre), §E (bands) |
| Anything the tenant filter must see | A composite FK cannot guard inside JSON | **[TS]** §6 Risk 3 |
| Money that appears in a total a user reconciles | It must be a column that `SUM()` and the GL can reach | **[TS]** §H |
| A set you need to enforce no-overlap on | `EXCLUDE … gist` needs columns | **No exception.** Revision 3 moved `companies.settings.pay_policy[]` into the `company_pay_policies` table for exactly this reason **[TS]** §C |

**Writing rule [TS] §A:** `tenant_settings` is written **per section**, with `jsonb_set` on a single
key guarded by that key's version. A whole-row `PUT` is not an API the service exposes. Two admins
editing different sections never clobber each other.

**Two JSON columns were removed by revision 3, and two were kept with a stated reason [TS] §13.5.**
Learn all four — they are the worked examples of this policy:

| Column | Outcome | Reason |
|---|---|---|
| `companies.settings.pay_policy` | **Removed** → `company_pay_policies` table | The riskiest blob: the one place contractual money lived, and the one dated structure that could not use the `daterange` EXCLUDE. |
| `employees.dependents` | **Removed** outright | No consumer. Not relocated — deleted. |
| `leave_types.policy` | **Kept** | Entitlement-by-service-year is band-shaped, but `statutory_rule_bands` is platform *reference* data and a leave entitlement is *tenant contractual* data. Putting tenant rows in a reference table would break "no tenant overrides of statutory rules". Floor in the bands table, tenant ladder in the JSON, engine takes `max(statutory, contractual)`. |
| `final_settlements.clearance` | **Kept** | A clearance checklist is not a multi-step approval. Routing it through the approval engine would create one `approval_requests` row per item and turn a checklist into an inbox. It is a schema-validated array of `{key, required, done_by, done_at}`, and an undone required item blocks the settlement leaving Draft (§10.10). |

**Indexing rule [NEW]:** a `jsonb` column that needs an index is a column that should have been a
column. If you are reaching for `GIN`, re-read the tables above.

---

## 9. Audit columns

**Both are now settled. [TS] §Conventions (row stamping) and §14 (the audit log).**

### Row stamping — every mutable table

```sql
created_at timestamptz NOT NULL DEFAULT now(),
created_by uuid,          -- plain uuid, NOT a foreign key
updated_at timestamptz,
updated_by uuid           -- plain uuid, NOT a foreign key
```

Maintained by **one `trg_row_stamp` BEFORE INSERT/UPDATE trigger**, never by application code — so a
raw SQL fix or a background job cannot skip it.

| Rule | Why |
|---|---|
| `created_by`/`updated_by` are **plain uuids, not FKs** | A purged actor must never block a business row. |
| They are **never the audit trail** | §14's `audit_logs` is. These answer "who touched this row last", nothing more. |
| **`updated_at` is never a concurrency token — `xmin` is** (§19.5) | `UseXminAsConcurrencyToken()` globally: no column, no migration, and it cannot be forgotten by a writer. |

**Exempt — append-only and immutable tables**, which carry `created_at` and their own actor column
only: `leave_ledger` · `audit_logs` · `payroll_audit_logs` · `retention_purge_audits` ·
`attendance_punches` · `payroll_slip_lines` · `wps_lines` · `final_settlement_lines` ·
`gl_journal_lines` · `nitaqat_snapshots` · `background_job_items`.

**Also exempt — reference tables**, which carry `effective_from` instead.

*This exempt list is CI-enforced* (`KEEPING_DOCS_HONEST.md` assertion 9): a mutable table without
the four columns fails, and an exempt table that grows `updated_by` fails.

### What §14 adds to the audit log

| Addition | Where | Why |
|---|---|---|
| `before jsonb`, `after jsonb` | `audit_logs`, `payroll_audit_logs` | Six per-module logs being merged already carry old/new values (`Models/LoansAdvancesBonuses.cs:127-128,219-220,335-336`, `Models/SetupAdmin.cs:197-198`, `Models/Leave.cs:413`, `Models/EmployeeHistory.cs:11-12`). Merging them into a log without these would have been a regression. Both sit **inside the hashed envelope** and share the purge path with `personal_data`. |
| `correlation_id uuid` | `audit_logs`, `payroll_audit_logs`, `retention_purge_audits`, `background_jobs`, `background_job_items` | Set once per HTTP request or job run and propagated. One approval decision that writes an action, a status, a ledger row and three notifications is reassembled with **one query**. |
| `on_behalf_of_user_id`, `user_agent`, `hash_algorithm` | `audit_logs` | Live columns revision 2 dropped silently. |

**What audit does not do [TS] §14.4:** it is not a second copy of the database. Category, entity,
entity id, actor and **the changed fields only**. Full-row snapshots stay on the frozen artefacts
(slips, WPS lines, filings, EOS snapshots).

**Audit columns are not the audit log.** They answer "who touched this row last"; `audit_logs` and
`payroll_audit_logs` answer "what happened and can it be proven". Do not use one for the other.

---

## 10. Standard column order

**[NEW].** Every `CREATE TABLE` lists columns in this order. Reviewers read hundreds of these; a
fixed order makes a missing `tenant_id` visible at a glance.

| # | Group | Columns |
|---|---|---|
| 1 | Identity | `id` |
| 2 | Tenancy | `tenant_id`, then `company_id` |
| 3 | Parent FKs | the owning parent first (`run_id`, `slip_id`, `job_id`), then the rest |
| 4 | Natural / human key | `employee_number`, `code`, `payslip_number`, `scope_key` |
| 5 | Discriminators | `kind`, `status`, `<x>_type`, `severity` |
| 6 | Effective dating | `effective_from`, `effective_to` — or `year`, `month`, `work_date` (**never a bare `period`**, §4) |
| 7 | Domain payload | the columns the table exists for |
| 8 | Money and totals | amounts, then totals, then YTD |
| 9 | Snapshot / witness | frozen copies (`rules_version`, `contributory_wage`, `full_basic`, identity snapshots) |
| 10 | Provenance | `source_type`, `source_id`, `source_system`, `idempotency_key` |
| 11 | JSON | `jsonb` columns |
| 12 | Lifecycle timestamps | `detected_at`, `approved_at`, `locked_at`, `consumed_at`, `revoked_at` |
| 13 | Soft delete and retention — **the four tables only** (§6) | `deleted_at` / `soft_deleted_at`, `retention_until`, `privacy_status`, `redacted_at`, `purged_at` |
| 14 | Audit | `created_at`, `created_by`, `updated_at`, `updated_by` |

Constraints follow the columns in this order: `PRIMARY KEY`, `UNIQUE (tenant_id, id)`, other
`UNIQUE`, `FOREIGN KEY`, `CHECK`, `EXCLUDE`. Indexes are declared after the table.

---

## 11. Idempotency and concurrency

| Rule | Status |
|---|---|
| Every async or import writer carries `idempotency_key` with a **unique index**. | **[TS]** §Conventions |
| Tables that have one: `attendance_punches`, `leave_ledger`, `notifications`, `background_jobs`, `payroll_runs`, `gl_journals`. | **[TS]** §2 |
| **The blanket `idempotency_key UNIQUE` does not survive partitioning.** On `attendance_punches` it is `UNIQUE (tenant_id, idempotency_key, occurred_at)`, which is also the `ON CONFLICT` target for batch ingest. Safe in practice because the key is derived from `(device serial, employee, occurred_at)`, so a duplicate always carries the same `occurred_at`. | **[TS]** §19.3 |
| **`audit_logs` is `UNIQUE (chain_key, seq, created_at)`.** Cross-partition uniqueness of `seq` is guaranteed at *allocation* instead, by the per-tenant sequence; the checkpointer walks contiguous `seq` ranges and raises on a gap or duplicate. `TARGET_SCHEMA.md` states plainly that this is a real, if small, weakening. | **[TS]** §19.3 |
| **Every financial POST requires an `Idempotency-Key` header** — approve run, generate WPS file, post GL journal, approve settlement, file GOSI return — stored with the resulting entity id, so a double-click or a retry-on-504 returns the first result rather than filing twice. | **[TS]** §19.5 |
| A claim/consume transition is **one statement** (`UPDATE … WHERE status='Pending' … RETURNING`), never read-then-write. | **[TS]** §F |
| A crashed producer releases its claim by predicate (`claimed_by_run_id` points at a voided run), so a retry is idempotent. | **[TS]** §F |
| A cancelled record **bumps `revision`** and inserts a replacement; it never reuses the key its replacement needs. | **[TS]** §F |
| A worker lease is `lease_owner` + `heartbeat_at` on the job row. No separate heartbeat table. | **[TS]** §R |

---

## 12. Row-level security

**[TS] §19.2.** RLS is the **second** layer, not a replacement: the EF query filters, boot
assertions, write guards and source ratchets all stay. What changes is that isolation stops
depending on 654 correct call sites.

### The five roles

The application stops connecting as `neondb_owner`, which is superuser-class and holds `BYPASSRLS`.

| Role | Login | Purpose | Notable grants |
|---|---|---|---|
| `kynex_owner` | **no** | Owns every object | `BYPASSRLS`; the only role with DDL |
| `kynex_migrator` | yes | **Applies the baseline and every migration** | No privilege of its own — `GRANT kynex_owner TO kynex_migrator` and nothing else. Credentials live only in the CI/deploy secret store, never in an application environment |
| `kynex_app` | yes | The API | `NOBYPASSRLS`; DML on tenant tables, `SELECT` only on reference tables |
| `kynex_job` | yes | The seven background services | as `kynex_app`, plus its own `background_jobs` policy and longer timeouts |
| `kynex_platform` | yes | Platform / tenant-admin surface | may set `app.platform='on'`; full DML on `platform_users`, `tenants` |
| `kynex_ro` | yes | Support and analytics | `SELECT` only; **no** access to the secret columns below |

`kynex_migrator` exists because nothing else could apply the baseline: `kynex_owner` holds the only
DDL privilege and cannot log in. It is deliberately **a membership, not a privilege**, so revoking
one `GRANT` disarms it.

Three tests assert this from the catalog, not from review:

1. **No login role may *reach* a `BYPASSRLS` role except `kynex_migrator`.** A direct
   `pg_roles.rolbypassrls` check is **not enough** — `GRANT kynex_owner TO kynex_app` would defeat
   it while every row still reads `rolbypassrls = false`. The assertion walks `pg_auth_members`
   **recursively** from every `rolcanlogin` role and fails if any closure but `kynex_migrator`'s
   contains a `BYPASSRLS` role.
2. `kynex_app` holds no write grant on a reference table and no DDL
   (`information_schema.role_table_grants`).
3. **No `SECURITY DEFINER` function is executable by `PUBLIC` or by `kynex_ro`**, and every one
   declares `SET search_path = pg_catalog, app`.

### GUC accessors

`app.current_tenant()` and `app.is_platform()` live in schema `app`. They are **`STABLE PARALLEL
SAFE`**, use `current_setting(…, true)` so an unset GUC is NULL rather than an error, and are
**never `SECURITY DEFINER`**.

*Why `STABLE` matters:* the planner folds the call to a constant and still chooses the
tenant-leading index. That is what makes RLS nearly free here rather than a sequential scan.

**`app.is_platform()` checks role membership, not just the GUC.** Postgres does not restrict `SET`
on a *custom* GUC by role, so any session — including `kynex_app` — could run
`SET app.platform='on'` and satisfy shape (b). The function ANDs the GUC with actual membership, so
**the GUC can only ever narrow an authority the role already holds**:

```sql
CREATE FUNCTION app.is_platform() RETURNS boolean LANGUAGE sql STABLE PARALLEL SAFE AS
$$ SELECT coalesce(current_setting('app.platform', true), 'off') = 'on'
        AND pg_has_role(current_user, 'kynex_platform', 'MEMBER') $$;
```

`app.current_tenant()` is left self-assertable **by design** — a session may choose *which* tenant
it acts for, and the middleware and interceptor are what bind it to the authenticated JWT.

> **Threat model, stated plainly.** GUC-based RLS defends against a **missing predicate** — the
> forgotten `WHERE tenant_id`, the ambient worker path, the `IgnoreQueryFilters()` that should not
> have been there. It does **not** defend against an attacker who can execute arbitrary SQL on an
> authenticated connection, because that attacker can set the tenant GUC to any value. Defence
> against that is the layer above: authentication, the JWT-bound middleware, parameterised queries,
> the raw-SQL lint, least privilege. **Anyone reading this as "RLS makes SQL injection harmless" has
> read it wrong.**

**Two details of `app.is_platform()` are load-bearing, and both fail silently [R7].**

1. **It must not be `SECURITY DEFINER`.** Inside a definer function `current_user` is the *function's
   owner*, so `pg_has_role(current_user, 'kynex_platform', …)` would answer about `kynex_owner` and
   the membership test would pass for **everyone**. The function is `STABLE PARALLEL SAFE` and
   nothing more — which is also what lets the planner fold it to a constant and keep the
   tenant-leading index.
2. **The privilege tested is `'MEMBER'`, not `'USAGE'`.** `USAGE` is false for a `NOINHERIT`
   membership, so a platform operator whose grant happens to be `NOINHERIT` would be silently
   demoted out of the platform tier and would see an empty database instead of an error.

Neither mistake produces a failure that looks like a failure, so both are asserted in CI.

### Six policy populations, summing to 76 [R7]

Generated from the model, **never hand-edited** (`060_policies.sql`). Revision 6 called this "three
shapes, and only three" and then counted 66 + 6 + 4 = 76 while separately describing `platform_users`
as outside all three — which is 77, and which hid two tables that fit no shape at all. **The
arithmetic has to close, because the coverage ratchet counts it.** As built:

| Shape | Tables | Policy |
|---|---|---|
| **(a) tenant and company tier** | **62** | `USING (tenant_id = app.current_tenant())`, same `WITH CHECK`. Company tier needs nothing extra — `company_id` is already bound to the tenant by its composite FK. |
| **(b) nullable-tenant tier** | **5** — `audit_logs`, `background_jobs`, **`background_job_items`**, `public_holidays`, **`retention_policies`** | `USING (tenant_id = app.current_tenant() OR (tenant_id IS NULL AND app.is_platform()))`. `background_jobs` also carries a `kynex_job` policy so the leased-queue claim (`FOR UPDATE SKIP LOCKED`) sees queued platform work without `is_platform()`. |
| **`p_auth`** | **2** — `auth_sessions`, `auth_tokens` | The one hand-written policy, below. |
| **(c) reference tier** | **4** — `permissions`, `statutory_rules`, `statutory_rule_bands`, `nitaqat_grid` | Protected by **withholding the write grant**, with `FOR SELECT USING (true)`. This removes the largest bypass category outright: reading statutory data stops needing a bypass because it stops being filtered. |
| **grant-policed** | **2** — `platform_users`, `data_protection_keys` | No tenant column to filter on; policed by grant, plus a deny-by-default policy so a stray grant still sees nothing. |
| **self-tenant** | **1** — `tenants` | `USING (id = app.current_tenant() OR app.is_platform())` with a **platform-only `WITH CHECK`**. |
| **Total** | **76** | |

**`tenants` cannot express shape (a) at all.** It has no `tenant_id` — it *is* the tenant — so its
predicate is on its own key, and its `WITH CHECK` is platform-only: a tenant session may read its own
row and must never create or re-key one. Revision 6 counted it inside (a), where the policy would
have referenced a column that does not exist.

**`platform_users` is policed by grant** — `kynex_platform` only, with `kynex_app` and `kynex_job`
holding no grant whatsoever. RLS is still enabled and forced with `USING (app.is_platform())`, so a
stolen `kynex_app` connection sees an **empty table** rather than an operator list.

**`data_protection_keys` is in no shape, deliberately.** It is the **deployment-wide** ASP.NET Data
Protection key ring — not tenant data and not operator data. Every tenant's payloads are protected by
the same ring, so there is nothing to filter on and filtering it would break decryption. Grant plus
the `xml` column revoke is the control.

**Two nullable-tenant tables are shape (b) because the alternative is a *silent failure*, not a
leak.** This is the pattern to recognise, because it has now appeared twice:

- **`retention_policies`** — its platform DEFAULT rows carry `tenant_id IS NULL`. Under shape (a) a
  tenant session sees only its own *override* rows, so **the retention engine runs with overrides and
  no defaults at all**, under-retaining or skipping every entity whose only policy is the platform
  row. The §12.4 CHECK still prevents a tenant row shortening a platform period.
- **`background_job_items`** — added in revision 7. A platform job (`background_jobs.tenant_id IS
  NULL`) writes items that inherit its NULL tenant. Under shape (a) **the worker draining that queue
  cannot see its own items**: the import reports zero rows processed and zero errors while the
  per-row outcomes sit invisible in the table. Nothing raises.

In both, the wrong shape makes a table look *empty* rather than look *wrong*. **The nullable-tenant
set is therefore seven** — the five in shape (b) plus `auth_sessions` and `auth_tokens` under
`p_auth` — and §3's table lists all seven.

**The ratchet asserts the *shape*, not merely that RLS is on.** A table with RLS enabled and the
wrong policy is as wrong as a table with none — shape (a) on `retention_policies` was exactly that
defect. The generator emits a manifest of `table → shape` (a / b / p_auth / c / grant-only /
self-tenant) and CI compares it against `pg_policies` + `role_table_grants`, failing when a live
policy's text does not match its declared shape, when a table has no entry, when an entry has no
table, **or when the six populations do not sum to the table count**.

`ENABLE` **and** `FORCE ROW LEVEL SECURITY` on every table, every partitioned parent **and every
partition child**.

### Views are `security_invoker` — a P0, not a detail

A view **without** `WITH (security_invoker = true)` evaluates its base tables with the privileges and
policies of the **view owner**. Every object here is owned by `kynex_owner`, which holds
`BYPASSRLS` — so an ordinary view would read **every tenant's rows** and hand them to the caller.
`FORCE ROW LEVEL SECURITY` does **not** help, because **`BYPASSRLS` outranks `FORCE`**.

Both baseline views are therefore created `WITH (security_invoker = true)`:

```sql
CREATE VIEW v_leave_balances  WITH (security_invoker = true) AS …;
CREATE VIEW v_employee_current WITH (security_invoker = true) AS …;
```

Two CI assertions keep it that way: `pg_class.reloptions` must contain `security_invoker=true` for
every view in the baseline, and **the coverage ratchet spans `relkind IN ('r','p','v')`**, so a
future view fails by absence rather than by review.

This matters most on `v_leave_balances`, which §11.6 makes **the only sanctioned way to read a
balance** — i.e. the mandated read path was a cross-tenant leak by default. See
`ANTI_PATTERNS.md` §12.

### `p_auth` — the one hand-written policy

`auth_sessions` and `auth_tokens` are the only tables carrying both subject kinds, so they get a
stated policy rather than the generic shape:

```sql
CREATE POLICY p_auth ON auth_sessions
  USING (
        (subject_kind = 'Tenant'   AND tenant_id = app.current_tenant())
     OR (subject_kind = 'Platform' AND tenant_id IS NULL AND app.is_platform())
  ) WITH CHECK (same);
```

plus a table `CHECK` tying the three columns together: `subject_kind='Tenant'` requires
`user_id IS NOT NULL AND platform_user_id IS NULL AND tenant_id IS NOT NULL`, and the mirror for
`'Platform'`. A tenant session can never be created without a tenant, and a platform session can
never be read by `kynex_app`.

### Fail-closed

With `app.tenant_id` unset, `app.current_tenant()` is NULL, `tenant_id = NULL` is NULL, **no row is
visible, and every INSERT raises `42501`**. A misconfigured request returns empty or errors; it
never returns another tenant's data.

That is the property the EF filter cannot offer: today `IgnoreQueryFilters()` returns *all* tenants,
and the anonymous path drops the filter entirely (`ZayraDbContext.cs:62-75`). See
`ANTI_PATTERNS.md` §11.

### Connection and GUC

**Connection-scoped on the direct endpoint**, not transaction-scoped. The deciding evidence is
local: `Program.cs:276-281` registers `EnableRetryOnFailure`, and `NpgsqlRetryingExecutionStrategy`
throws on any user-initiated `BeginTransaction` outside `strategy.ExecuteAsync` — a trap this repo
has hit and documented three times (`Infrastructure/Timesheets/TimesheetService.cs:15`,
`IEstablishmentGuard.cs:60`, `TenantDefaultsBackfill.cs:77`). A transaction-scoped GUC would put all
~100 ported controllers on that landmine.

1. **The guarantee is the interceptor, not the middleware.** EF opens and closes connections per
   operation and Npgsql's `DISCARD ALL` wipes the GUC on every return to the pool, so **a GUC set
   once in middleware is gone by the second query of the same request**. The tenant therefore lives
   in an **`AsyncLocal<TenantContext>` ambient** set by the middleware — *not* in `HttpContext`,
   which is absent in hosted services, in retry continuations after a connection reset, and inside
   `Task.Run` — and a `DbConnectionInterceptor.ConnectionOpenedAsync` re-applies it on **every**
   open. Background workers set the same ambient per tenant iteration, so one mechanism serves both.
   The extra round trip is then avoidable: a `DbCommandInterceptor` folds
   `set_config('app.tenant_id', @t, false)` into the first command on a freshly opened connection as
   one `NpgsqlBatch`, so the steady-state cost is **zero** extra round trips. The standalone
   `SELECT set_config(…)` survives only as the fallback for paths that cannot batch.
   **Raw `NpgsqlConnection`, Dapper and `IDbConnection` bypass both interceptors entirely, so they
   are banned**, enforced by a source-scanning test that extends today's lint (which misses
   `SqlQueryRaw`).
2. Npgsql's `DISCARD ALL` on return-to-pool **must stay active** (`No Reset On Close` must *not* be
   set), so a GUC never survives into another request. A test asserts it.
3. A `DbConnectionInterceptor.ConnectionOpenedAsync` backstop sets the GUC for any path opening
   outside the middleware, and **throws** when no tenant is resolvable and the path is not on the
   allow-list (login, health, platform).
4. Pool pinned: `Maximum Pool Size=<modelled>;Timeout=10;Command Timeout=30;Keepalive=30;Multiplexing=false`.
   **Multiplexing must be off** — it interleaves commands across connections and would break
   connection-scoped session state.
5. Background workers connect as `kynex_job` and set the GUC per tenant iteration. Cross-tenant
   sweeps iterate tenant by tenant, which they should anyway for fairness and bounded transactions.

### The four named bypass surfaces

These replace the ambient bypass. **Each is one auditable place with its own test.**

| # | Surface | Mechanism |
|---|---|---|
| 1 | **Login and credential recovery** | `app.resolve_login(tenant_slug citext, email citext)` — `SECURITY DEFINER`, returning only `(user_id, tenant_id, password_hash, status, lockout_end)`, plus a `platform_users` twin keyed on email alone. **Three corrections, each of which was otherwise a hole:** (i) email is unique **per tenant**, not globally, so a function keyed on email alone is ambiguous the moment two tenants share an address — the tenant is resolved first, from the sign-in host, the workspace slug or an explicit field, and is **part of the key**; (ii) a `SECURITY DEFINER` function owned by a `BYPASSRLS` role is `EXECUTE`-to-`PUBLIC` by default, so `kynex_ro` could call it and receive `password_hash`, defeating the column revoke — hence `REVOKE EXECUTE … FROM PUBLIC; GRANT EXECUTE TO kynex_app;`; (iii) `SET search_path = pg_catalog, app` so it cannot be hijacked by a shadowing object. CI asserts (ii) and (iii) for **every** such function. **Nothing else may read `users` or `auth_tokens` before a tenant is known.** |
| 2 | **Platform administration** | `kynex_platform` with `app.platform='on'`, set only by the platform controller pipeline, and an `audit_logs` row per request. |
| 3 | **Background jobs and seeders** | `kynex_job` with an explicit per-tenant GUC loop. **Declared, never ambient** — this closes today's fail-open worker path. |
| 4 | **Migrations and backfills** | `kynex_owner`, `BYPASSRLS`, no LOGIN. |

**The bypass-count ratchet is retired** in favour of the policy-coverage ratchet: counting call
sites stops being the measurement once there are four surfaces.

**The denominator, stated once**, because three numbers circulate: **346** raw
`IgnoreQueryFilters()` sites in `Zayra.Api` **production code** (7 of them inside the `ScopedBypass`
helper itself), of which `QueryFilterBypassRatchetTests` approves **330** across the 60 production
files it pins. **Tests are excluded from both.** A repository-wide grep including test projects
returns a larger number again — that is where this document's earlier figure of 654 came from.
Wherever a bypass count appears, it means *production sites in `Zayra.Api`, tests excluded*.

### Secret columns

Revoked from `kynex_ro` by **column privilege**, so support and analytics access cannot read
credentials: `users.password_hash`, `users.mfa_secret_encrypted`, `users.mfa_recovery_hashes`,
the `platform_users` equivalents, `data_protection_keys.xml`, `attendance_devices.api_key_hash`,
`auth_tokens.token_hash`, and **[R7]** `auth_sessions.refresh_token_hash`,
`auth_sessions.previous_token_hash` and `auth_sessions.push_token`.

The three added in revision 7 are the same class as the ones already listed and were simply missed:
the two token hashes are bearer secrets, and they are the **pair that refresh-token reuse detection
turns on** — a reader of both can impersonate a device *and* know whether the theft has been noticed.
`push_token` is a third-party credential: it addresses a real handset through APNs or FCM, and anyone
holding it can send notifications that appear to come from the product.

**`pg_stat_statements` is revoked from `PUBLIC`, and that is a tenant-isolation control [R7].** The
view is **world-readable by default**, and its `query` texts carry the literals of every statement the
server has run — employee numbers, IBANs, national IDs, emails, from every tenant. **No RLS policy
covers it**, because it is not a table in this schema, so `kynex_ro` reading it is a cross-tenant read
that every other control here would have prevented. `REVOKE ALL ON pg_stat_statements FROM PUBLIC`,
grant it to the operator role only, and apply the same reasoning to any future extension view that
records query text.

---

## 13. Partitioning

**[TS] §19.3.** Five tables are declaratively RANGE-partitioned **by month from the baseline** —
each reaches 6–9 M rows per 5,000-employee tenant per year, and **a large table cannot be
partitioned later without a full rewrite**.

| Table | Partition key | Online window | Then |
|---|---|---|---|
| `attendance_punches` | `occurred_at` | 24 months | detach, export to `files`, drop |
| `audit_logs` | `created_at` | indefinite, per `retention_policies` | detach to cold storage; **PDPL erasure nulls `personal_data`/`before`/`after` in place, never drops a partition** |
| `background_job_items` | `created_at` | 90 days | detach and drop |
| `attendance_days` | `work_date` | **24 months** | detach, export, drop |
| `timesheet_entries` | `work_date` | **24 months** | detach, export, drop |

**Six consequences you must know before touching these tables:**

0. **A table cannot be converted to partitioned [R7].** There is no `ALTER TABLE … PARTITION BY` in
   PostgreSQL, through PG 17. The baseline **rebuilds** each parent — `RENAME`,
   `CREATE TABLE … (LIKE … INCLUDING ALL) PARTITION BY RANGE (<key>)`, `DROP` — which is free while
   the tables are empty. **`LIKE` does not copy foreign keys, in either direction**, so
   `030_partitions.sql` restates **9 constraints verbatim from `021`**: the 8 outbound FKs of the
   five partitioned tables and the 1 inbound FK that targets one of them. That duplication is a
   maintenance hazard and the rule is explicit — **changing the `ON DELETE`, the columns or the
   target of any of those nine means changing `021` and `030` in the same commit**, with a comment
   in each naming the other. The §19.1 drift gate is what catches a divergence.
1. **Every unique constraint on a partitioned table must contain the partition key.** See §11 for
   the two constraints this changed and the one it did not.
2. **The one composite FK into a partitioned table changes shape.**
   `timesheet_day_reconciliations.attendance_day_id` becomes
   `(tenant_id, attendance_day_id, work_date) → attendance_days (tenant_id, id, work_date)`; the
   reconciliation row already carries `work_date`, so no new column is needed (§8 row 126).
   **No other partitioned table is an FK target**, which is why the other four can be partitioned freely.
3. **Partition creation is an operational job, not a migration.**
   `background_jobs kind='PartitionMaintenance'` pre-creates three months ahead and alerts below two
   months of headroom.
4. **Partition children are a security surface, not a detail.** Policies on the parent govern access
   *through* the parent, but **privileges are not inherited for direct access**: each child has its
   own ACL, and **a child with a grant and no policy is an unfiltered copy of millions of rows that
   any `kynex_app` session can read by naming it**. `PartitionMaintenance` is also the only process
   that creates tables in production, so one careless `GRANT` in that job is the whole leak.

**The four child rules, all enforced:**

| # | Rule |
|---|---|
| 1 | The job uses **one fixed template** taking no parameter but the month: `CREATE TABLE … PARTITION OF … FOR VALUES FROM … TO …`, then `ENABLE` + `FORCE ROW LEVEL SECURITY`, and **no `GRANT` of any kind**. Access is through the parent, which already carries the grants and policies. |
| 2 | **The coverage ratchet stops excluding `relispartition = true`** — revision 5's exclusion *was* the hole. It now asserts, for every child: RLS enabled and forced, and **zero** direct grants to any login role. |
| 3 | A `DEFAULT` partition exists on every parent so a missing month degrades rather than errors, and **the alert fires on the first row landing in it** — not on a threshold. A populated default is a countdown, because the recovery is expensive. |
| 4 | **Recovery from a populated `DEFAULT`**, written down because it is the classic partitioned outage: `DETACH PARTITION … CONCURRENTLY`, create the missing month, `INSERT … SELECT` the rows out in batches, then attach. **Attaching over a populated default takes `ACCESS EXCLUSIVE` and a full validation scan** — on `attendance_punches` that is minutes of blocked writes, so the work is **scheduled, not improvised**. |

**Two traps in the bounds themselves, both of which route rows into `DEFAULT` silently [R7]:**

5. **A `timestamptz` partition bound is resolved against the server's `TimeZone` at DDL time**, not
   at insert time. Two partitions written under different settings leave a **gap or an overlap of a
   few hours** at the month boundary. An overlap is rejected and is harmless; a gap is not — rows in
   it land in `DEFAULT`, at the hour of every month change, unannounced. **Every `timestamptz` bound
   carries an explicit offset: `'2026-03-01 00:00:00+00'`.** This affects `attendance_punches`,
   `audit_logs` and `background_job_items`; the two `work_date` parents are `date`-keyed and immune,
   which is one more dividend from the local-date rule of §4.
6. **`DEFAULT` partitions are created once, with the parent, and never by the maintenance job.** The
   reason is the alert, not tidiness: a job that *can* create a `DEFAULT` can silently repair a
   missing month by making one, and rule 3's first-row alert — the thing that turns a missing month
   from an outage into scheduled work — would then never fire. The maintenance job creates months
   and nothing else.

> Revision 5 said a missing month means "a slow insert rather than an outage". That was true of the
> insert and false of the recovery. Both are now stated.

---

## 14. Index rules

**[TS] §19.4.** Five rules, stated once, as was done for effective dating. The per-query inventory
is §19.4's table; these are the rules a new index must obey.

1. **Every index on a tenant table leads with `tenant_id`** (company tier: `tenant_id, company_id`).
   Under RLS the policy predicate is `tenant_id = app.current_tenant()`, so a tenant-leading index
   is what makes RLS nearly free rather than a sequential scan.
2. **Every FK gets a covering index on its referencing columns**, unless it is in the exclusions
   below, **with the reason stated in `COMMENT ON`**.
3. **Every filter + sort + page endpoint declares its index in the same PR as the endpoint.**
4. **Partial indexes for small hot subsets**: `WHERE status='Pending'`, `read_at IS NULL`,
   `effective_to IS NULL`, `purge_state='PendingPurge'`, `status IN ('Queued','Leased','Running')`.
5. **No index ships without an `EXPLAIN (ANALYZE, BUFFERS)`** against a seeded 5,000-employee
   tenant, recorded in the PR.

**FKs deliberately left unindexed**, with the reason. An unindexed FK costs only on a parent
`DELETE` or key `UPDATE`, and these parents are never hard-deleted:

| Class | Columns |
|---|---|
| FKs to immutable reference data | `statutory_rule_id`, `statutory_rule_band_id`, `permission_code`, `nitaqat_grid`, `document_templates.template_id` |
| **`RESTRICT`** actor columns, each named with its table | `payroll_issues.override_by`, `gosi_filings.filed_by`, `gl_period_closes.closed_by` and `.reopened_by`, `permission_grantor_records.granted_by_user_id` and `.revoked_by`. **The `RESTRICT` itself is the reason** — a purge that must remove such a user has to resolve the business row first, by definition. The old justification, "`users` rows are soft-deleted, never removed", was wrong: `TARGET_SCHEMA.md` §8.3 hard-deletes `users` during a tenant purge |
| Self-FKs on small hierarchies | `departments.parent_id`, `cost_centers.parent_id` (hundreds of rows at most) |

**Two classes moved *into* the indexed set in revision 6:**

- **Every `SET NULL` child of `users`** — `files.uploaded_by`, `approval_actions.on_behalf_of_user_id`,
  `wps_batches.generated_by`, **`user_roles.granted_by`**. A `SET NULL` FK is *not* exempt on the
  "parents are never deleted" argument, because the nulling `UPDATE` still scans the child — and
  §8.3 does delete users.
- **Never write `granted_by` unqualified [R7].** Revisions 5 and 6 had it on *both* lists at once.
  They are two columns: **`user_roles.granted_by` is `SET NULL` and is indexed**;
  **`permission_grantor_records.granted_by_user_id` is `RESTRICT` and is not**. Every entry on either
  list now names its table.
- **The two chain pointers** `wps_batches.resubmission_of_id` and `gl_journals.reversal_of_id`,
  which are read from the parent side more often than assumed.

**152 of the 165 FKs carry a covering index; 13 deliberately do not** — the exclusion classes above
contain exactly 13, each with its reason in a `COMMENT ON`. Both numbers are countable from
`pg_constraint` and `pg_index`, which is why they are stated rather than estimated. (`TARGET_SCHEMA.md`
§8.2 reconciles the denominator: 165 register rows, one FK each — 156 declared in `020`/`021`, 6
cross-domain in `022`, 3 added by revision 7. The old figure, "roughly 125 of 160", matched neither
the register nor its own exclusion list.)

**H4 (the approvals inbox) is now indexable**, because `approval_requests` gained the two
denormalised approver columns the partial index needs — see `DATA_DICTIONARY.md`.

### The external-id convention — additive, not a rename

**[R7] [TS] §18.** When a table gains an external system that owns the identity of one of its rows,
it takes the same three columns:

```sql
external_system    text,
external_id        text,
external_synced_at timestamptz
```

**This replaces nothing.** Revision 6 said the trio replaced `qiwa_contract_no`,
`submission_reference` and `gosi_employee_no`, while `TARGET_SCHEMA.md` §2 went on naming all three
and never added the trio to any table; revision 7 resolves it in favour of §2. The three are
**typed statutory identifiers**, not opaque foreign keys: each has its own format, its own length
bound in §13.3, and its own meaning in a filing or an inspection. Collapsing them into one
`external_id` would lose the type, lose the bound, and make it impossible for one row to hold both a
GOSI employee number and a future integration's key.

**The rule, therefore:** a typed statutory identifier is a named column; the trio is for **opaque
keys owned by an external system** — a Qiwa or Mudad record id, a provider message id, a bank's own
batch handle. **No table in the 76 carries the trio today**, because no table in the KSA core has an
external system that owns a row's identity. The first one that does adds it then, per table, beside
whatever named identifiers it already holds.

---

## 15. Transactions and concurrency

**[TS] §19.5.**

| Workflow | Isolation | Why |
|---|---|---|
| Ordinary CRUD, list, ESS reads | `READ COMMITTED` | Nothing read-compute-writes |
| Payroll run calculation | `READ COMMITTED` + chunking + idempotent re-entry | Volume, not anomaly, is the risk |
| Leave debit, encashment, comp-off — **every read-compute-INSERT** | **`pg_advisory_xact_lock(tenant, employee, leave_type)` is the RULE, not an alternative** | **`REPEATABLE READ` does *not* prevent this anomaly.** Two sessions each SUM the ledger, each see enough balance, each INSERT a *different* row, and both commit — **write skew**, which only `SERIALIZABLE` detects. Take the advisory lock. |
| Loan recovery and `outstanding` | `READ COMMITTED` | Different shape, and safe for a stated reason: it is a **single-row `UPDATE`**, which takes a row lock, not a read-compute-insert |
| `payroll_inputs` claim | `READ COMMITTED`, single statement | The `UPDATE … RETURNING` needs nothing stronger |
| WPS generation, GL posting, settlement approval | `REPEATABLE READ` + idempotency key | Must not double-file or double-post |
| Audit checkpointing, retention purge | `READ COMMITTED`, per-tenant, bounded batches | Long transactions are the hazard |

**The chunked-batch rule.** A 5,000-employee run writes ~5,000 slips and ~75,000 lines. One
transaction would hold locks and WAL for minutes on a 512 MB instance; per-employee transactions
would make the run non-atomic. Therefore **the run's state machine (§10.1) is the unit of
atomicity, not the SQL transaction**:

- employees processed in **batches of 200**, each in its own transaction;
- the writer is `INSERT … ON CONFLICT (tenant_id, run_id, employee_id) DO NOTHING`;
- a crashed run **resumes from the first employee without a slip**;
- the run leaves `Processing` only when the slip count matches the selection.

The same rule applies to the attendance sweep, the WPS line build and the GL line build.

**Retry.** `EnableRetryOnFailure` covers transient *connection* faults — **not** serialization
failures or deadlocks. Financial paths wrap their unit in `strategy.ExecuteAsync` and retry `40001`
(serialization) and `40P01` (deadlock) up to 3 times with jittered backoff. The retry is provably
safe because every financial writer carries an idempotency key or an `ON CONFLICT` target.

**Optimistic concurrency: `UseXminAsConcurrencyToken()` globally** — no column, no migration — so
two HR users editing one employee cannot silently last-write-wins. Exempt: append-only tables,
frozen artefacts after lock, and `tenant_settings`, which has its own per-section version.

**Per-role timeouts** (none are set anywhere today):

```sql
ALTER ROLE kynex_app      SET statement_timeout='30s',   lock_timeout='5s',  idle_in_transaction_session_timeout='15s';
ALTER ROLE kynex_job      SET statement_timeout='10min', lock_timeout='30s', idle_in_transaction_session_timeout='2min';
ALTER ROLE kynex_platform SET statement_timeout='60s',   lock_timeout='10s', idle_in_transaction_session_timeout='30s';
ALTER ROLE kynex_ro       SET statement_timeout='120s',  lock_timeout='5s',  idle_in_transaction_session_timeout='60s';
```

Plus `log_lock_waits=on` and `deadlock_timeout=1s` at database level.

**Payroll run totals are undefined while `Processing`.** The chunked batches mean a partial sum is
not a number anyone may read, so `trg_run_totals` is **conditioned on the status transition**, not
on every line insert. A crashed run **releases its claimed inputs** and, on resume, **re-reads its
own `Claimed` rows** rather than claiming afresh. The run's exit condition is
`slips + explicitly excluded = selected_employee_count` — which is why `payroll_runs` carries both
counts as columns.

**Session-scoped advisory locks are banned.** `AccessManagementService.cs:1917` uses
`pg_advisory_lock` on a pooled connection, so a fault before unlock **leaks the lock into the next
request's connection**. Always `pg_advisory_xact_lock`, matching the other three call sites.

---

## 16. What is [OPEN] — and what is merely not built yet

Revision 5 closes the design. **Two different things remain, and conflating them would be a
mistake:** what nobody has decided, and what has been decided and not yet built.

### Decided, and partly built — §19

**Revision 7 moves the first slice from "not built" to "built".** The extensions file, the nine
domain table files and the constraint files exist under `backend-dotnet/Zayra.Api/Db/baseline/` and
verify against a throwaway Postgres container. **RLS, the five partitions, the index inventory, the
triggers, the reference seeds and the ten operational signals remain owner-approved (decision 10) and
unbuilt.** The audit's
own estimate for the RLS half is ≈3–4 engineer-weeks, of which ~1.5 are genuinely additive.
**Nothing in §12–§15 of this file describes the database as it exists today.**

### Genuinely undecided

| # | Undecided | Who decides | Blocks |
|---|---|---|---|
| 1 | **`[COUNSEL]`: the GOSI rate ladder and wage bounds** (§E). Until confirmed, an unknown cohort **blocks** payroll for that employee — decision 3, and it must stay a block. | **Counsel** | `statutory_rules` seed data |
| 2 | **`[COUNSEL]`: the retention periods** (§12.4), including the newly-stated basis for 24 months — "the KSA labour-claim limitation window (one year from the end of the relationship) with a margin". The reasoning is now on the page and reviewable, which is the improvement; it is still not legal advice. | **Counsel** | `retention_policies` seed data |
| 3 | **The region decision** (platform audit P2-19): app in Oregon, database in `us-east-1`, **~70 ms per round trip**. Moving Render to Virginia or Neon to `us-west-2` is the cheapest performance work available. Carried explicitly as an **owner call**, with the new request-latency SLO to price it. | **Owner** | The latency SLO, and the B2 residency question |
| ~~4~~ | ~~Constraint, index and trigger **naming** and standard **column order**.~~ **Closed in revision 7** (`TARGET_SCHEMA.md` §9.0, decision 11): `ck_<table>__<assertion>` is the rule, §9's 36 `chk_` names are a closed legacy set because C# and CI assert against them, and §10's column order stands. It could not survive the first `CREATE TABLE`, and the baseline is written. | — | — |

### Every defect this document reported is closed

| Reported | Outcome in revision 6 |
|---|---|
| Retention vs partition window (24 vs 36 months) | **Resolved at 24 months in both places**, the direction that does not over-retain, with the legal basis now stated on the page and flagged `[COUNSEL]`. |
| §5 "Still open" contradicted §19 | Fixed. |
| Bypass figure 330 vs 654 | Fixed, and better than fixed: the **denominator is now stated** — 346 production sites, 330 ratchet-approved across 60 files, tests excluded. The 654 was a repo-wide grep including tests. |
| `payroll_runs.totals`, `gosi_filings` totals, `companies.go_live_period` named as groups | All three **expanded into named, typed columns** (4 + 7 + 2). |
| `gosi_filings` uniqueness cited an undeclared `revision` | `revision` is now a column and the unique is restated. |
| `company_pay_policies.value_json` "bounded" with no bound | **`CHECK (pg_column_size(value_json) <= 8192)`** plus a schema validated on write. |

### The twelve the implementation reported, and where each landed [R7]

| # | Reported | Outcome in revision 7 |
|---|---|---|
| 1 | §11.2/§15 said `hours`, the SQL is minutes everywhere | **Minutes**, leave in days. §4 above; `TARGET_SCHEMA.md` §2.K, §11.2, §15 |
| 2 | `chk_<table>_<col>` (§9) vs `ck_<table>__<assertion>` (§1), naming still listed as undecided | `ck_` is the rule; the 36 `chk_` names are a **closed legacy set** because C# asserts them. §1 above; §9.0 |
| 3 | Eight closed sets with no enumerated domain | Eight new §9 rows, 37–44. §7 above |
| 4 | §18 said the external-id trio replaces three named columns; §2 still named them | §2 wins; the trio is **additive**. §14 above; §18 |
| 5 | `year`/`month` vs `period_year`/`period_month` vs a bare `period` | One form: `year` + `month`, role-prefixed only where a row has two periods. §4 above; §2.F |
| 6 | Three payroll tables marked tier C with no `company_id` and no §8 FK | Tier corrected to **T**; the company is reached through the run. §2.F |
| 7 | `gl_mappings` and `approval_workflows` reach tenancy only through a nullable `company_id` | Direct tenant FKs added — **and `document_templates` found by the same test**. §3 above; §8.2 rows 159–161 |
| 8 | `tenant_settings` PK, `employees.work_email`, `citext`, six implied uniques, nine introduced columns | Each adopted or rejected by name in the new `TARGET_SCHEMA.md` §20 |
| 9 | A NULL subject silently disables a gist `EXCLUDE`; a composite `ON DELETE SET NULL` fails at runtime | Both are now rules, §5 and §3 above, with a **PostgreSQL 15 floor** (production is Neon 17.11) |
| 10 | The §19.1 byte-diff drift gate would fail every run on `pg_dump` 16+'s `\restrict` nonce | Normalisation specified, and deliberately narrow: drop the guard lines, drop the version banner, touch nothing inside a statement. §19.1 |
| 11 | §2's per-domain header counts (5/7/7) contradicted §1's table | Corrected to 7/8/8 |
| 12 | §8 read as 160 FKs; the SQL applies 156 | **165 constraints, one per register row**: 156 declared, 6 cross-domain in a new `022`, 3 new. §8.2 |
| 13 | `payroll_slip_lines.gosi_branch`/`gosi_payer` ungoverned; `trg_gosi_filing_totals` pivots on them and only **warns** | Both instances of both columns enumerated. An unmatched value contributes to **no** GOSI total and the warning does not say so — a silent wrong number in a filed return. §9 rows 37–38, §11.2 |
| 14 | An `Opening` run had no abandon path: §10.1 allowed only Draft → Locked, so a wrong go-live import could only be deleted | **Draft → Voided** and **Locked → Voided** added for `Opening`, `void_reason` mandatory. A go-live import is the thing that gets redone, and deleting the row destroys the provenance the run exists to carry. §10.1 |
| — | §19.4's "125 of 160 indexed" matched neither the register nor its own exclusion list, and `granted_by` was on both the indexed and the unindexed list | **152 of 165 covered, 13 excluded.** `user_roles.granted_by` is `SET NULL` and indexed; `permission_grantor_records.granted_by_user_id` is `RESTRICT` and is not. §14 above |
| — | A trigger function shared by several tables type-checks `NEW.<col>` against every one of them, even in unreachable branches | Shared guards read `to_jsonb(NEW)`. §6 above |
| 15 | §19.2's shape arithmetic did not close: 66+6+4=76 omitted `platform_users` | **Six populations, 62/5/2/4/2/1 = 76.** `tenants` cannot express shape (a) — no `tenant_id` — and `data_protection_keys` is in no shape at all. §12 above |
| 16 | `background_job_items.tenant_id` is nullable and was missing from shape (b) | Added. **The nullable-tenant set is seven.** A platform job's worker could not see its own items: zero processed, zero errors. §3, §12 above |
| 17 | `app.resolve_login` as specified is a **cross-tenant credential read** — an authenticated tenant-A session passes tenant B's slug and receives B's `password_hash` | A confinement guard is now part of the specification: permitted only when no tenant is bound (the real login path) or the requested tenant is the session's own; otherwise `42501`. §19.2 |
| 18 | §19.3 said "convert the parent to partitioned"; PostgreSQL has no such statement | The baseline **rebuilds** each parent, and `LIKE` not copying FKs forces 9 constraints to be restated in both `021` and `030`. §13 above |
| 19 | `timestamptz` partition bounds resolve against the server `TimeZone`; a `DEFAULT`-creating maintenance job hides its own alert | Bounds carry an explicit `+00`; `DEFAULT` partitions stay outside the maintenance template. §13 above |
| 20 | Three secret columns missing from the revoke list; `pg_stat_statements` world-readable | `auth_sessions.refresh_token_hash`, `.previous_token_hash`, `.push_token` added; `pg_stat_statements` revoked from `PUBLIC` — a cross-tenant read **no policy covers**. §12 above |
| 21 | `app.is_platform()` would answer about the wrong principal if `SECURITY DEFINER`, and `'USAGE'` silently drops a `NOINHERIT` operator | Not `SECURITY DEFINER`; tests `'MEMBER'`. §12 above |
| 22 | Nothing said what is needed to *create* the role graph, or that the app must stop using the cluster superuser | Deploy preconditions stated: the bootstrap role holds `CREATEROLE` **and** `BYPASSRLS`; the application is handed only `kynex_app`. §19.6 |

The one remaining defect class is external: two `[COUNSEL]` items and one owner decision (§16 above).
`TARGET_SCHEMA.md` §5 carries the owner-facing record of every settled decision, including decision 11
(naming) which revision 7 adds.
