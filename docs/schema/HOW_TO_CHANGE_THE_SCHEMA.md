# How to change the schema

The practical runbook. Read `CONVENTIONS.md` first; it says what a table must look like. This says
how to get a change into production without breaking the running app.

**The one sentence version:** every schema change is deployed in the order *expand → migrate →
contract*, across **at least two releases**, because the old code and the new schema are live at
the same time.

> **Read this before your first change.** As of `TARGET_SCHEMA.md` §19.1 the baseline is
> **hand-written SQL DDL**, not an EF `InitialCreate`. **The schema is no longer changed by editing
> C# entities alone.** An EF model edit that does not reach the SQL will pass the six EF gates and
> fail the `pg_dump` byte-diff, because EF's snapshot cannot see triggers, policies, partitions,
> `EXCLUDE` constraints or grants. §1 below says which artefact to edit for which change.

---

## 0. Before you touch anything

| Check | Where |
|---|---|
| Does this need a new table? **The hard cap is gone** (decision 9) — the rule is the principle: no duplicate tables, nothing kept that nothing uses, every table traceable to a capability. | `TARGET_SCHEMA.md` §7. A principle is a weaker brake than a number, so **name the capability in the PR** and pass the no-duplicate test in review |
| Will the new table have an **RLS policy**, and **which shape**? | The ratchet asserts the *shape*, not just that RLS is on. Declare a / b / c / grant-only in the generator manifest (`CONVENTIONS.md` §12) |
| Are you adding a **view**? | It must be `WITH (security_invoker = true)` or it reads as `kynex_owner`, which holds `BYPASSRLS`. See `ANTI_PATTERNS.md` §12 |
| Is the table **integration-facing**? | It carries `external_system` / `external_id` / `external_synced_at` — one convention, not a new spelling |
| Is the table **partitioned**? | Five are (`CONVENTIONS.md` §13). Every unique must contain the partition key, and the PK is composite |
| Who approves a change in this domain? | `OWNERSHIP_AND_RETENTION.md` §3 |
| Does the column hold personal data? | If yes, the **DPO** adds or amends a `retention_policies` row before you write the migration |
| Is the table frozen, append-only or effective-dated? | `CONVENTIONS.md` §5–6 — those have extra rules |
| Does the column have a status/kind value set? | `TARGET_SCHEMA.md` §9 is the register and §10 the legal transitions — both must be updated |
| Are you about to add a Postgres `enum`, a soft-delete column on a fifth table, or a settings table? | Read `ANTI_PATTERNS.md` first. The answer is no. Soft delete exists on exactly four tables (`CONVENTIONS.md` §6). |

### 1. Which artefact do I edit?

| Change | Edit | Why |
|---|---|---|
| A new table, column or type | **The SQL in `Db/baseline/` if pre-baseline; an ordinary EF migration after** | After the baseline ships, EF migrations handle columns and tables normally |
| A trigger, RLS policy, partition, `EXCLUDE`, grant, role, index comment | **SQL** — `migrationBuilder.Sql` in a migration, and the matching `Db/baseline/*.sql` | EF cannot model them, and its snapshot does not know they exist |
| Anything at all | **regenerate `schema.sql`** | The byte-diff gate compares a freshly migrated database against it |

**The baseline layout** (`backend-dotnet/Zayra.Api/Db/baseline/`), applied in filename order:

| File | Contents |
|---|---|
| `001_extensions.sql` | `btree_gist`, `pg_trgm`, `pgcrypto`, `pg_stat_statements` — and `REVOKE ALL ON pg_stat_statements FROM PUBLIC`, because it is world-readable by default and its query texts carry every tenant's literals |
| `002_roles.sql` | the six roles, `REVOKE ALL ON SCHEMA public FROM PUBLIC`, schema `app`, `app.current_tenant()`, `app.is_platform()`. **The role that runs this file must itself hold `CREATEROLE` *and* `BYPASSRLS`** — Postgres will not let a role grant an attribute it lacks, and without it the file fails partway, after four roles exist |
| `010_platform.sql` · `011_identity.sql` · `012_org.sql` · `013_employees.sql` · `014_statutory.sql` · `015_payroll.sql` · `016_wps_gl.sql` · `017_leave_attendance.sql` · `018_workflow_audit.sql` | `CREATE TABLE` only — columns, PK and UNIQUE — with explicit types, `NOT NULL`, defaults, and a `COMMENT ON TABLE` carrying `@tier:`, `@owner:` and `@retention:` tags. **Written.** |
| `020_constraints_a_f.sql` · `021_constraints_g_r.sql` · `022_constraints_cross.sql` | composite `UNIQUE (tenant_id, id)`, **all 165 FKs** with the `ON DELETE`/`ON UPDATE` of §8 (77 + 79 + 6, plus the 3 revision 7 added — §8.2 reconciles the count), the `CHECK` sets of §9, `EXCLUDE USING gist` for every dated table, `numrange` exclusions for bands. `022` exists because six register rows have a domain A–F child and a G–R parent, so neither of the first two files can declare them. |
| `030_partitions.sql` | the five partitioned parents — **rebuilt** (`RENAME` → `CREATE … LIKE … PARTITION BY RANGE` → `DROP`), because there is no `ALTER TABLE … PARTITION BY` in PostgreSQL — 15 initial monthly partitions with **`+00`-qualified `timestamptz` bounds**, the `DEFAULT` catch-alls, and the **9 FKs restated verbatim from `021`** that `LIKE` will not copy |
| `040_indexes.sql` | the §19.4 inventory, each with `COMMENT ON INDEX` **naming the query it serves** |
| `050_triggers.sql` | frozen-row guards, append-only guards, the `payroll_runs` state-transition guard, deferred total-reconciliation triggers (§11.2), GL balance, attendance-lock guard, `trg_row_stamp` |
| `060_policies.sql` | `ENABLE`/`FORCE ROW LEVEL SECURITY`, grants, and the **six policy populations** for all 76 tables (62/5/2/4/2/1 — `CONVENTIONS.md` §12) — **generated from the model, never hand-edited** |
| `070_seed_reference.sql` | `permissions`, `statutory_rules` + bands, `nitaqat_grid`, KSA `public_holidays`, `retention_policies` |
| `tests/070_rls_proof.sql` | the RLS and partition proofs — fail-closed, cross-tenant read/update/delete, self-promotion, the credential surface, transitive `BYPASSRLS` reachability, forced RLS everywhere, ungranted partition children, `security_invoker` views **and the counter-proof that the flag is what holds the door**. **Written.** |
| `tests/080_schema_ratchets.sql` | only what `070` does not already assert: declared policy **shape** vs live policy text, the manifest closing in both directions and arithmetically, table `COMMENT` + `@owner:` + retention declaration, index justification, and the heap check that closes the normaliser's blind spot. **Written.** |
| `schema.sql` | the canonical `pg_dump --schema-only --no-owner` output, **normalised and committed** (16,922 lines). Generated on PostgreSQL 16; verified byte-identical from `pg_dump` 16.15 and 18.4, and from both `postgres:16` and `postgres:16-alpine`. Never hand-edited — regenerate it with `./scripts/schema-drift-gate.sh --write`. |

The gate scripts live in `scripts/`, not in the baseline directory, because they are tooling
rather than schema and must not be swept into the apply set:

| Script | What it is |
|---|---|
| `scripts/schema-normalise.sh` | the normaliser the drift gate runs over **both** sides of its diff. It drops `\restrict`/`\unrestrict` guard lines (`pg_dump` 16.10+ regenerates their nonce on every run, so an un-normalised diff is red on a byte-identical database), the version banner, and `SET`/empty-comment lines. **It must not sort, reformat or touch anything inside a statement** — a normaliser that tidies the payload hides the drift the gate exists to catch. `--self-test` proves both directions, including that an indented `SET search_path` inside a `SECURITY DEFINER` function survives. |
| `scripts/schema-build.sh` | the single apply path every gate shares: DROP, CREATE, `001`/`002` as the superuser, everything after as `kynex_migrator` with `SET ROLE kynex_owner`. The file list is **discovered**, so a new numbered file is picked up by every gate the moment it lands. |
| `scripts/schema-drift-gate.sh` | gate 2, and `--write` regenerates `schema.sql` |
| `scripts/schema-apply-order-gate.sh` | gate 1 |
| `scripts/schema-ratchet-gate.sh` | gate 3 — runs `070` then `080` |
| `scripts/schema-gate-selftest.sh` | gate 4 — breaks the schema ten ways and fails if a gate misses |
| `scripts/schema-gates.sh` | **all of them, one command** (see below) |

**How EF stays in charge.** The baseline ships as *one* EF migration whose `Up` executes those files
as embedded resources. So `__EFMigrationsHistory` stays authoritative, `dotnet ef database update`
remains the only apply path, and the readiness manifest keeps working. **The EF model classes are
reverse-checked against the SQL, not the other way round.**

Two related fixes land with it: squash to the single baseline, and **stop `rm -rf Migrations` in the
Dockerfile** (audit P1-16), which restores the readiness gate's primary mechanism.

### Tooling

`dotnet-ef` is pinned as a **local** tool. The manifest is `dotnet-tools.json` at the repo root
(`integration/`), not in the project folder. A global install is shadowed by it.

```bash
cd integration
dotnet tool restore                       # manifest is at the repo root
dotnet build backend-dotnet/Zayra.Api/Zayra.Api.csproj -c Debug
dotnet-ef migrations add <Name> --project backend-dotnet/Zayra.Api/Zayra.Api.csproj
```

**Never pass `--no-build` to a `migrations add`.** It reads the stale compiled snapshot, reports
drift that does not exist, and regenerates the same delta forever. Build once, then run EF
commands against the fresh build.

### Running the gates locally — one command

```bash
cd integration
./scripts/schema-gates.sh              # everything, including the self-test  (~45s)
./scripts/schema-gates.sh --fast       # skip the self-test                   (~15s)
./scripts/schema-gates.sh --write      # regenerate schema.sql, then run everything
```

It needs **Docker and nothing else** — no psql, no local cluster, no .NET. One throwaway
`postgres:16` container is started, shared by every gate, and destroyed on exit whether the run
passes, fails or is interrupted. No real database is reachable from it.

These are the same scripts CI runs, not a local approximation of them, so "it passed locally" and
"it passed in CI" cannot diverge. CI drives them with `SCHEMA_GATE_MODE=direct` against its
`services: postgres` container instead of starting one.

**After any DDL change, regenerate the canonical dump and commit it in the same commit:**

```bash
./scripts/schema-drift-gate.sh --write     # then review the diff — it IS your change
```

The diff to `schema.sql` is the most reviewable artefact a schema PR produces. A reviewer sees the
trigger, the policy and the partition bound next to the DDL that created them, which is precisely
what no EF-based check can show them.

**Never hand-write a migration file.** EF discovers migrations by the `[Migration]` /
`[DbContext]` attribute pair. A hand-written file without them is **invisible**: `dotnet ef
database update` exits 0 having silently skipped it, and so does every other tool in the pipeline.
That happened here — three files added on 2026-07-13 stayed invisible for **70 days** (72 files on
disk, 69 visible to EF). `scripts/check-migration-visibility.sh` now catches it, and it is the
first gate to run because a discrepancy there invalidates every gate after it.

---

## 1. The expand → migrate → contract pattern

Three releases. You may compress 1 and 2 into one release only when the table is empty.

| Phase | Schema | Code | Reversible? |
|---|---|---|---|
| **Expand** | Add the new thing. Nullable, defaulted, no constraint that existing rows violate. | Writes **both** old and new. Reads old. | Yes — drop the addition |
| **Migrate** | Backfill in batches. Then, and only then, add the `NOT NULL` / `CHECK` / `UNIQUE`. | Reads new, still writes both. | Yes — flip reads back |
| **Contract** | Drop the old thing. | Stopped touching old **in a previously deployed release**. | No — this is the irreversible one |

**Why three.** During any deploy, some instances run the old code and some the new, against one
database. A change that is safe only if all instances are new is a change that takes the app down.

### Worked example: splitting `employees.name_en` into `first_name_en` + `last_name_en`

*(Illustrative. The real schema keeps `name_en`.)*

**Release 1 — expand**

```sql
ALTER TABLE employees ADD COLUMN first_name_en text NULL;
ALTER TABLE employees ADD COLUMN last_name_en  text NULL;
```

Code: on every write, populate `name_en` **and** the two new columns. Reads still use `name_en`.
Deploy. Old instances keep working — they ignore columns they do not know about.

**Release 2 — migrate**

Backfill in batches (see §5), then, in a *separate* migration once the backfill is verified at zero
remaining:

```sql
ALTER TABLE employees ALTER COLUMN first_name_en SET NOT NULL;
ALTER TABLE employees ALTER COLUMN last_name_en  SET NOT NULL;
```

Code: reads use the new columns; writes still populate all three. Deploy. **Verify in production**
that nothing reads `name_en` — a log counter on the old read path, or a week of grep-able telemetry.

**Release 3 — contract**

```sql
ALTER TABLE employees DROP COLUMN name_en;
```

Code: no longer mentions `name_en` — and has not, **in a release that is already deployed**. Deploy.

**The rule this example exists to teach:** the `DROP` ships in the release *after* the one that
stopped using the column. Never in the same one. If the release that stops using it has to be
rolled back, the column must still be there.

---

## 2. Recipes

### Add a table

1. Confirm the budget (§0). Get the domain owner and the Data Architect.
2. Write the entity with: `id uuid` PK, `tenant_id uuid NOT NULL`, `UNIQUE (tenant_id, id)`,
   composite FKs, audit columns, columns in the standard order (`CONVENTIONS.md` §10).
3. **Write the consumer in the same PR.** A `DbSet` with no reader outside `Models/`, `Data/` and
   `Migrations/` fails `OrphanEntityRatchetTests` and the build. That test exists because 29
   entities already reached that state.
4. Add: a tenant-isolation test, a `retention_policies` row, the entity's row in
   `DATA_DICTIONARY.md`, its edge in `ERD.md`, its FK rows in `TARGET_SCHEMA.md` §8.2 and any
   status values in §9. **CI checks these.**
5. If it is effective-dated, add the `EXCLUDE … gist` constraint **and** the four boundary tests
   (`CONVENTIONS.md` §5). A no-overlap rule enforced only in the service is not enforced.

### Add a column

Nullable, or `NOT NULL DEFAULT` — Postgres 11+ does not rewrite the table for a constant default.

```sql
ALTER TABLE loans ADD COLUMN write_off_reason text NULL;
```

Adding it `NOT NULL` with no default against a non-empty table fails, and against a large empty-ish
table it takes an `ACCESS EXCLUSIVE` lock for the rewrite. Expand → backfill → `SET NOT NULL`.

### Add a value to a CHECK-enum

```sql
-- payroll_runs.run_type is §9 row 10, so it keeps its frozen chk_ name (TARGET_SCHEMA §9.0)
ALTER TABLE payroll_runs DROP CONSTRAINT chk_payroll_runs_type;
ALTER TABLE payroll_runs ADD  CONSTRAINT chk_payroll_runs_type
  CHECK (run_type IN ('Regular','OffCycle','FinalSettlement','Correction','Opening','Parallel'));
```

**Which name do I use?** `ck_<table>__<assertion>` for everything — *except* the 36 enumerations
`TARGET_SCHEMA.md` §9's numbered table names, which keep their `chk_<table>_<column>` spelling
because a C# constants class of the same name is what CI asserts against. That set is **closed**: no
thirty-seventh `chk_` name is ever added, and §9 rows 37 onward already use `ck_`.

**Deploy this before any code writes the new value.** Both statements go in one migration and one
transaction. **Three things move together** (`TARGET_SCHEMA.md` §1): the named CHECK, the C#
constants class of the same name, and the value list in `DATA_DICTIONARY.md`. CI compares all three.

**Adding a value is not the same as allowing a transition.** If the column has a state machine
(pending `TARGET_SCHEMA.md` §10), the new value also needs its legal transitions and an enforcement
point. A value nothing can legally reach is dead the day it ships.

### Change a type

Never in place on a live column. It is `ALTER COLUMN … TYPE`, which takes an `ACCESS EXCLUSIVE`
lock and rewrites the table, and it breaks every old instance mid-deploy.

Do the three-phase dance with a new column:

1. Add `amount_v2 numeric(18,2) NULL`; dual-write.
2. Backfill; `SET NOT NULL`; switch reads.
3. Drop `amount`; rename `amount_v2 → amount` — see *rename safely*.

**Widening is the one exception.** `numeric(18,2) → numeric(20,2)`, `varchar(50) → text`, `int →
bigint` on a non-key column: Postgres does these without a rewrite, and no reader breaks. Still its
own migration, still its own release.

### Drop a column

Three releases, and the `DROP` is the third. Before you write it, prove nothing reads it:

```bash
grep -rn "ColumnName\|column_name" integration/backend-dotnet integration/frontend/src integration/mobile
```

Zero hits **and** that zero has been deployed for at least one release. Then:

```sql
ALTER TABLE x DROP COLUMN y;
```

### Rename safely

There is no safe in-place rename. `ALTER TABLE … RENAME COLUMN` breaks every running old instance
at the instant it commits. A rename is add + dual-write + backfill + switch + drop — four releases
if you count the final rename of the new column into the old name, which is why **it is usually not
worth doing**. Pick the right name the first time; a slightly wrong name that is documented is
cheaper than a rename.

### Add or change an RLS policy

Never hand-edit `060_policies.sql` — it is generated from the model. Change the model's tier, or the
generator. Then regenerate `schema.sql` and let the byte-diff gate confirm.

A new table needs a policy **in the same PR**, and the manifest entry that declares **which shape**:
(a) tenant/company, (b) nullable-tenant, (c) reference, or grant-only. There is no fifth shape — if
your table seems to need one, the tier is wrong. **The ratchet compares live policy text against the
declared shape**, so "RLS is on" is not enough: shape (a) on `retention_policies` was exactly that
defect, and it would have run the retention engine with no platform defaults.

### Add a view

```sql
CREATE VIEW v_something WITH (security_invoker = true) AS …;
```

**The `WITH` clause is not optional.** Without it the view evaluates its base tables as
`kynex_owner`, which holds `BYPASSRLS`, and returns every tenant's rows — and `FORCE ROW LEVEL
SECURITY` does not save you, because `BYPASSRLS` outranks `FORCE`. Two assertions enforce it. Read
`ANTI_PATTERNS.md` §12 before adding your first one; it is the sharpest failure in this codebase's
history and it was caught in review, not in production.

### Add a SECURITY DEFINER function

Three requirements, all CI-asserted, each of which was a hole in the original sketch:

```sql
CREATE FUNCTION app.thing(…) … SECURITY DEFINER SET search_path = pg_catalog, app AS $$ … $$;
REVOKE EXECUTE ON FUNCTION app.thing FROM PUBLIC;
GRANT  EXECUTE ON FUNCTION app.thing TO kynex_app;
```

`EXECUTE` is granted to `PUBLIC` by default, so without the `REVOKE` a `kynex_ro` support session
could call `app.resolve_login` and receive `password_hash` — defeating the column-level revoke
entirely. The pinned `search_path` stops a shadowing object hijacking the body.

### Add a partition, or a table that needs partitioning

**Partitioning an existing large table requires a full rewrite**, which is why all five are
partitioned from the baseline. If a sixth table needs it, that is an owner-level decision taken
*before* it has data, not after.

Routine partition creation is **not a migration**: `background_jobs kind='PartitionMaintenance'`
pre-creates three months ahead and alerts under two months of headroom. If you find yourself writing
`CREATE TABLE … PARTITION OF` in a migration, the maintenance job is broken — fix that instead.

On a partitioned table: **every unique constraint must contain the partition key**, the PK is
`(id, <partition key>)`, and the tenant key is `UNIQUE (tenant_id, id, <partition key>)`.

### Add an index to a live table

```sql
CREATE INDEX CONCURRENTLY ix_payroll_inputs__pending
  ON payroll_inputs (tenant_id, company_id, covered_year, covered_month)
  WHERE status = 'Pending';
```

`CONCURRENTLY` **cannot run inside a transaction**, and EF wraps migrations in one. Mark the
migration `[Migration]` with `migrationBuilder.Sql(..., suppressTransaction: true)`. A failed
concurrent build leaves an `INVALID` index — check `pg_index.indisvalid`, drop and retry; it does
not self-heal.

### Add a constraint to a live table

`NOT VALID` first, then validate without blocking writes:

```sql
ALTER TABLE x ADD CONSTRAINT ck_x__y CHECK (y > 0) NOT VALID;   -- release 1, instant
ALTER TABLE x VALIDATE CONSTRAINT ck_x__y;                       -- release 2, after the backfill
```

Adding a constraint `VALID` scans the whole table under a lock. On `payroll_slip_lines` that is a
production outage.

---

## 3. What CI will reject

These gates exist and run on every PR (`integration/.github/workflows/ci.yml`). Each one is here
because it already failed to exist once.

| Gate | Job | Rejects |
|---|---|---|
| **Every migration on disk is visible to EF** | `schema-gates` | A hand-written migration with no `[Migration]` attribute. Runs **first** — a discrepancy here makes every later gate meaningless. |
| **No EF model/migration drift** | `schema-gates` | An entity changed with no migration. Note it also fails if the *check itself* errors — a gate that cannot fail is not a gate. |
| **Migration schema contains every modeled column** | `schema-gates` | A column in the model that no migration creates. |
| **Upgrade from previous release, with data** | `schema-gates` | A migration that works on an empty database and fails on a populated one. |
| **Fresh and upgrade converge on the same schema** | `schema-gates` | A migration whose result differs from a from-scratch build — the classic cause of "works in dev". |
| **Application startup and DI validation** | `schema-gates` | An entity registered with nothing to resolve it. |
| **Orphan-entity ratchet** | `backend-tests` | A `DbSet` referenced nowhere outside `Models/`, `Data/`, `Migrations/`. The pinned count **may only go down**. |
| **Query-filter bypass / raw-SQL ratchets** | `backend-tests` | A query that escapes the tenant filter, or new raw SQL. |
| **Doc-sync check** *(proposed)* | `backend-tests` | A table, column, FK or CHECK-enum value that the model has and these documents do not; a table with no RLS policy, no partition declaration, no retention row or no owner. See `KEEPING_DOCS_HONEST.md`. |

**Five gates are added**, in a second job called `baseline-schema-gates`, because the six above
**cannot see raw-SQL objects** — a trigger, a policy, a partition, an `EXCLUDE`, a grant, a
`COMMENT` or a `security_invoker` reloption is invisible to every EF-based check, so they would all
pass while comparing two equally-blind schemas. `schema-gates` itself is untouched and still runs
its six in the same order; §19.1 keeps it verbatim on purpose.

The job runs on a push to `main`, and on a PR that touches `Db/baseline/**`, `Data/V2/**`,
`docs/schema/**`, `TARGET_SCHEMA.md` or the gate scripts. **The path filter fails open**: if the PR
base cannot be resolved, the gates run rather than being skipped.

| # | Gate | Script | Rejects |
|---|---|---|---|
| 0 | **Normaliser self-test** | `schema-normalise.sh --self-test` | A normaliser widened until the byte-diff passes on anything. It runs **first**, because the normaliser is gate 2's blind spot by construction: whatever it deletes, the diff cannot see. Its fixture carries one line per object class EF cannot see — `EXCLUDE`, `security_invoker`, a partition bound, `FORCE ROW LEVEL SECURITY`, a policy, a trigger, the comment tags, a grant, a revoke — so a new delete rule that swallows any of them fails here. (`KEEPING_DOCS_HONEST.md` assertion 18.) |
| 1 | **Apply-order gate** | `schema-apply-order-gate.sh` | A baseline that is not repeatable — built twice from empty, the two normalised dumps must be byte-identical, which catches a bound or default computed from `now()`, a name derived from the current date, or an `IF NOT EXISTS` that skipped on the second pass. **And** it builds once with `020_constraints_a_f.sql` moved ahead of `010_platform.sql` and requires that build to FAIL with a legible Postgres error. A baseline that survives its own files being reordered never had an order, and the gate would be theatre. |
| 2 | **Schema-drift gate** | `schema-drift-gate.sh` | `pg_dump --schema-only --no-owner` of a database built from the baseline, normalised, **byte-diffed against committed `schema.sql`**. Any difference fails. This is the gate that catches the trigger, policy, partition, `EXCLUDE`, grant and `COMMENT` that EF cannot see. Fix: `./scripts/schema-drift-gate.sh --write`. |
| 3 | **Policy / partition / comment ratchets** | `schema-ratchet-gate.sh` | Runs `tests/070_rls_proof.sql` **in full** and then `tests/080_schema_ratchets.sql`. See the two tables below for what each one owns. |
| 4 | **Gate self-test** | `schema-gate-selftest.sh` | Breaks a throwaway build ten ways — a dropped `COMMENT`, a dropped policy, a policy silently widened, a view that loses `security_invoker`, a granted partition child, an unjustified index, an edited baseline file, an out-of-order apply, a widened normaliser — and fails if the gate that owns each one exits zero. Case 0 is the control: the same suites must pass on a clean build. **A gate you have not seen fail is not a gate**, and this repository has already shipped a lint that passed vacuously and a health check that always returned zero. |

**What `070_rls_proof.sql` already owned, and is reused rather than reimplemented:**

| Assertion | Proof | What it asserts |
|---|---|---|
| 9 (part) | 6a | RLS `ENABLE` **and** `FORCE` on every `relkind` `r`/`p` |
| 9 (part) | 6e | every relation has a manifest entry |
| 10 (part) | 6b | every partition child forced, **zero direct grants** to any login role |
| 10 (part) | 6c | no partition child carries its own policy |
| 13 | 6d, 6f | every view carries `security_invoker = true` — **and the counter-proof**: the same view without it demonstrably returns both tenants |
| 14 | 2h | no `SECURITY DEFINER` function executable by `PUBLIC` or `kynex_ro`; every one pins `search_path`; `resolve_login` is tenant-bound |
| 15 | 5 | no LOGIN role reaches a `BYPASSRLS` role except `kynex_migrator`, walked **transitively** through `pg_auth_members` |

**What `080_schema_ratchets.sql` adds, because `070` does not assert it:**

| Ratchet | Assertion | What it asserts |
|---|---|---|
| **R1** | 9 (the sharp half) | The manifest's declared **shape** matches the **live policy text**. For each relation the full policy set — name, command, grantee roles, `USING` and `WITH CHECK` — is rendered as one signature and compared against the signatures its shape permits. A table with RLS on and the **wrong** policy looks *empty*, not *wrong*; that defect has shipped twice (`retention_policies`, then `background_job_items`) and every check in front of it only asked whether a policy existed. |
| **R2** | 9 (arithmetic) | No manifest entry without a relation — the direction `6e` does not walk — the counts closing over `relkind IN ('r','p','v')`, and every partition child declaring its **parent's** shape. |
| **R3a–c** | 8, 12 | Every table has a `COMMENT`, an `@owner:` from the four §12.4 roles, and an `@retention:` tag — **or an entry on the unclassified list, which may only shrink** (14 today, pinned). |
| **R3d** | §19.1 gate 3 | Column-comment coverage, as a **ceiling**: 1,202 of 1,268 columns have no comment today, so a new uncommented column fails and the pin can only be lowered. Weaker than §19.1 asks for — see below. |
| **R4** | §19.1, `040` | Every non-constraint index carries a `COMMENT ON INDEX` naming the query it serves. An index whose query nobody can name is an index nobody can ever retire. |
| **R5** | — | Every table is plain heap in the default tablespace. This one exists **because of the normaliser**: `pg_dump` records the access method and tablespace only in `SET` lines, which §19.1 has the normaliser strip, so gate 2 cannot see them. Closing the hole here is correct; widening the normaliser would not be. |
| **R6** | 12 (row half) | **PENDING, and it says so on every run.** `070_seed_reference.sql` has not been written, so `retention_policies` is empty and "every table has a retention row whose `owner_role` agrees with its `@owner:` comment" cannot be enforced. The assertion is written and **arms itself the moment the first row lands**. It is not seeded with guessed periods: a wrong retention period is a PDPL finding, a missing one is a task. |

**Two things §19.1 asks for that cannot be built as written, recorded rather than quietly dropped:**

1. **"Every table *and column* has a `COMMENT ON`"** (gate 3). 66 of 1,268 columns carry one. A gate
   written as specified would be red the day it lands, and a gate that is red on day one is a gate
   somebody disables — the exact failure assertion 18 describes for the normaliser. It ships as the
   shrink-only ceiling R3d instead, and the generated data dictionary stays blocked until the pin
   reaches zero.
2. **The policy-coverage ratchet's stated SQL** — `relkind IN ('r','p') AND NOT relrowsecurity` —
   is the version `KEEPING_DOCS_HONEST.md` §3 itself warns about: it excludes views, and a view was
   therefore never *unprotected*, it was **not in the population**. What is built covers
   `relkind IN ('r','p','v')` and asserts the **shape**, not merely that RLS is on.

**Migrations are applied by CI, not by the app and not by Render.** The `migrate-backend` job runs
`dotnet ef database update` against production behind a `production` environment approval, and
`build-image` / `deploy-backend` wait on it.

**Two things that must never be reintroduced**, both of which caused real outages:

1. **No `preDeployCommand` running `--migrate` on Render.** The shipped image strips migrations, so
   `--migrate` correctly refuses ("this build has NO migrations compiled into it") — and the gate
   silently became a no-op. The one migration path is the CI job.
2. **`cancel-in-progress` must stay off for pushes to `main`.** It once cancelled a `migrate-backend`
   job that was waiting on the production approval, while Render's autoDeploy shipped the code
   anyway. Production ran new code against an old schema until `/health/ready` returned `42P01`.
   A cancelled job **reads like a skipped one**. Six consecutive merges died that way.

---

## 4. Rules that are never broken

| Never | Because |
|---|---|
| **Edit a migration that has shipped.** | It has already run somewhere. Editing it makes the recorded history a lie and the fresh-vs-upgrade gate diverge. Write a new migration. |
| **Do an in-place destructive change in one release.** | Old instances are live against the new schema for the length of the deploy. |
| **Ship a `DROP` in the same release that stops using the column.** | If that release is rolled back, the data is gone. The `DROP` goes one release later. |
| **Add `NOT NULL` without a default to a populated table.** | It fails, or rewrites the table under an exclusive lock. |
| **`CREATE INDEX` without `CONCURRENTLY` on a populated table.** | It blocks every write for the duration. |
| **Add a `UNIQUE` constraint without checking for duplicates first.** | It fails at the worst moment. `CREATE UNIQUE INDEX CONCURRENTLY` first, then attach it. |
| **`UPDATE` or `DELETE` an append-only or frozen table.** | A trigger raises. If you are reaching for it, the design is wrong — write a reversing row or a correction run. |
| **Write a data migration that cannot be run twice.** | See §5. |
| **Add a `tenant_id`-less tenant table, or a non-composite FK between tenant tables.** | It is the isolation guarantee. `CONVENTIONS.md` §3. |
| **Leave an `ON DELETE` to the ORM default.** | Every FK declares it explicitly (`TARGET_SCHEMA.md` §1). EF's default is not the same as the design's. |
| **Introduce a foreign-key cycle.** | There are none. If one is unavoidable it must be `DEFERRABLE INITIALLY DEFERRED` with a declared insertion order, or nobody can seed, import or truncate the pair. |
| **Write a status column by free string assignment.** | Transitions are enumerated and enforced; illegal ones must fail loudly. |
| **Add a per-row currency column.** | SAR is the currency of record; the currency is `companies.currency_code`. Multi-currency payroll is out of this baseline. |
| **Derive a business date by casting a `timestamptz` in SQL.** | Work dates and payroll periods are local dates in the company's `timezone_id`. |
| **Use `dotnet ef --no-build` for `migrations add`.** | It reads a stale snapshot and regenerates the same delta forever. |
| **Ship a schema change without regenerating `schema.sql`.** | The byte-diff gate fails, and if it somehow did not, EF's snapshot would be the only record of a trigger it cannot see. |
| **Hand-edit `060_policies.sql`.** | It is generated. Edit the model or the generator. |
| **Add a table without an RLS policy.** | The coverage ratchet fails by absence — which is the point. **And it must land in one of the six populations**, whose counts must still sum to the table count. |
| **Add a nullable `tenant_id` without moving the table to shape (b).** | Under shape (a) the NULL rows become invisible to every session, so the table looks **empty** rather than wrong. It has happened twice — `retention_policies`, then `background_job_items`. The nullable-tenant set is seven and an eighth fails the build. |
| **Convert a table to partitioned with `ALTER TABLE`.** | There is no such statement in PostgreSQL. Rebuild the parent — and remember `LIKE` does not copy foreign keys, in either direction. |
| **Write a `timestamptz` partition bound without an explicit `+00`.** | It resolves against the session's `TimeZone` at DDL time, so bounds written under different settings leave a few hours each month that route into `DEFAULT` silently. |
| **Let `PartitionMaintenance` create a `DEFAULT` partition.** | It could then silently repair a missing month and suppress the first-row alert that makes a missing month scheduled work instead of an outage. |
| **Make `app.is_platform()` `SECURITY DEFINER`, or test `'USAGE'` instead of `'MEMBER'`.** | Inside a definer function `current_user` is the owner, so it would return true for everyone; `'USAGE'` is false for a `NOINHERIT` membership and silently demotes a real operator. |
| **Write a `SECURITY DEFINER` function that reads credentials without confining it to the session's tenant.** | That is what made `app.resolve_login` a cross-tenant credential read: an authenticated tenant-A session passes tenant B's slug and receives B's `password_hash`. Permitted only when no tenant is bound, or the requested tenant is the session's own. |
| **Connect the application as the cluster superuser (`neondb_owner`).** | It is superuser-class, so it holds `BYPASSRLS` and every policy in the design does nothing — while everything appears to work. The application gets `kynex_app`; the workers get `kynex_job`. |
| **Leave `pg_stat_statements` — or any extension view that records query text — readable by `PUBLIC`.** | Its query texts carry literals from every tenant, and **no RLS policy covers it**, because it is not a table in this schema. It is world-readable by default. |
| **Write a shared trigger function that reads `NEW.<column>` by field.** | PL/pgSQL type-checks it against *every* table the function is attached to, including in branches that table can never reach. Read the row as `to_jsonb(NEW)`. `CONVENTIONS.md` §6. |
| **Add a unique constraint on a partitioned table without the partition key.** | Postgres rejects it. |
| **Put a nullable column in an `EXCLUDE` subject list, or in a `UNIQUE`, without `COALESCE` or `NULLS NOT DISTINCT`.** | A NULL operand makes the row **exempt from the constraint entirely**, silently. On `retention_policies` that left the platform default rows — the only rows the retention engine falls back to — as the only rows free to overlap. `CONVENTIONS.md` §5. |
| **Write a bare `ON DELETE SET NULL` on a composite FK.** | It nulls `tenant_id` too, which is `NOT NULL`, so the delete raises `23502` **at runtime** on a purge path while the DDL was accepted without complaint. Name the column: `ON DELETE SET NULL (uploaded_by)`. Requires PG 15. |
| **Run against PostgreSQL older than 15, anywhere — CI, a container, a laptop.** | The column-list `SET NULL` above and `UNIQUE NULLS NOT DISTINCT` both need it, and there is no workaround that keeps the composite tenant guard. Production is Neon 17.11. |
| **Store a duration as hours, or a period as a bare `period` column.** | Attendance, overtime and timesheet durations are **whole minutes**; leave is days. Periods are two `smallint` columns, `year` + `month`. `CONVENTIONS.md` §4. |
| **Connect the application as `neondb_owner`, or let any login role *reach* `BYPASSRLS`.** | `kynex_migrator` is the only exception. A direct `rolbypassrls` check is **not enough** — `GRANT kynex_owner TO kynex_app` defeats it while every row still reads false — so the test walks `pg_auth_members` **recursively** from every `rolcanlogin` role. See `ANTI_PATTERNS.md` §11. |
| **Create a view without `security_invoker`.** | It reads as its owner, which holds `BYPASSRLS`. `ANTI_PATTERNS.md` §12. |
| **`GRANT` anything on a partition child.** | Access is through the parent. A child with a grant and no policy is an unfiltered copy of millions of rows; the ratchet asserts zero direct grants on every child. |
| **Use raw `NpgsqlConnection`, Dapper or `IDbConnection`.** | They bypass both the connection and command interceptors, so the tenant GUC is never set. A source-scanning test enforces it. |
| **Rely on middleware alone to set the tenant GUC.** | EF opens and closes connections per operation and `DISCARD ALL` wipes the GUC, so it is gone by the second query. The guarantee is the `AsyncLocal` ambient plus the interceptor. |
| **Use `REPEATABLE READ` for a read-compute-INSERT.** | It does not detect write skew. Leave debit, encashment and comp-off take `pg_advisory_xact_lock`. |
| **Use session-scoped `pg_advisory_lock` on a pooled connection.** | A fault before unlock leaks the lock into the next request's connection (`AccessManagementService.cs:1917`). Always `pg_advisory_xact_lock`. |
| **Set `No Reset On Close`, or enable Npgsql multiplexing.** | Either one breaks the connection-scoped tenant GUC: the first lets it survive into another request, the second interleaves commands across connections. |
| **Hold one transaction across a whole payroll run.** | Batches of 200, each its own transaction, `ON CONFLICT DO NOTHING`, resume from the first employee without a slip. The state machine is the unit of atomicity, not the transaction. |
| **Change a `statutory_rules` row.** | A change is a **new effective-dated row**. Editing one silently rewrites what a past payslip was calculated from. |

---

## 5. Writing a data migration that is safe to re-run

A data migration will be run twice. Assume it: a retry, a partial failure, a restored replica, a
second environment. Four properties, all required.

**1. Idempotent** — re-running changes nothing.

```sql
-- good: the predicate excludes rows already done
UPDATE employees SET status = 'Active'
WHERE status IS NULL AND joining_date <= CURRENT_DATE;

-- bad: runs again, does it again
UPDATE loans SET outstanding = outstanding - 100;
```

For inserts, use the table's natural or idempotency key:

```sql
INSERT INTO leave_ledger (id, tenant_id, employee_id, leave_type_id, entry_type, days, idempotency_key)
SELECT … FROM …
ON CONFLICT (tenant_id, idempotency_key) DO NOTHING;
```

**2. Batched** — one statement over 2 million rows holds a lock and a transaction open for minutes.

```sql
-- loop until 0 rows affected
WITH batch AS (
  SELECT id FROM employees
  WHERE nationality_class IS NULL
  ORDER BY id LIMIT 5000 FOR UPDATE SKIP LOCKED
)
UPDATE employees e SET nationality_class = derive(e.nationality_code)
FROM batch WHERE e.id = batch.id;
```

`SKIP LOCKED` lets a retry make progress past a row another transaction holds.

**3. Resumable** — progress is a property of the data, not of a variable in a script. The
`WHERE … IS NULL` predicate *is* the cursor. If the derivation has no such predicate, add a
temporary `backfilled_at timestamptz` column and drop it in the contract phase.

**4. Observable** — log rows remaining per batch. A backfill you cannot watch is a backfill you
cannot decide to stop.

**Long backfills do not belong in an EF migration.** A migration that runs for 20 minutes blocks
the deploy and is not resumable. Put it in a `background_jobs` row: it already has
`idempotency_key`, `progress`, `attempts`, `lease_owner`, `heartbeat_at` and `background_job_items`
for per-row failures. That is what the table is for.

**Never mix DDL and a large DML backfill in one migration.** Ship the DDL, then run the backfill,
then ship the constraint. Three steps, and each one can be verified before the next.

---

## 6. Rolling back and rolling forward

> **Neon branches change the economics of this.** §19.6: every deploy that migrates first creates a
> restore point (`neon branches create --name premigrate-<sha>`), so **rollback of a bad migration is
> a connection-string change, not a restore** — RPO 0, RTO ≤ 15 min. The branch is deleted after a
> successful soak. Every migration PR rehearses on a branch cut from production and reports apply
> time and lock duration. Targets for *logical* loss remain RPO ≤ 5 min, RTO ≤ 2 h.
>
> **A database restore alone is not a recovery.** A restore that recovers rows pointing at deleted
> payslip blobs has recovered nothing, which is why B2 versioning and blob recovery are proven in
> the same drill (§19.6 gate 6).

**Prefer rolling forward.** A roll-back of a schema change is a schema change, and it is one you
wrote under pressure.

| Situation | Action |
|---|---|
| Expand phase is broken | Roll back the code. The schema addition is harmless — it is nullable and nothing depends on it. Fix and re-ship. |
| Migrate phase is broken | Roll the **code** back to reading the old column. The dual-write means the old column is still correct. Then fix the backfill. This is the reason dual-write exists. |
| Contract phase is broken | **You cannot roll this back** — the column is gone. Roll *forward*: re-add the column and re-derive its value. This is why `DROP` is the last thing and never shares a release. |
| A backfill produced wrong values | Do **not** `UPDATE` back. Write a new, idempotent correction migration with its own predicate, so the correction is itself re-runnable. |
| The migration ran, the deploy failed | The database is ahead of the code. That is the safe direction, and the whole point of expand-first. Redeploy or roll the code back; do not "un-migrate". |

**`dotnet ef database update <PreviousMigration>` is not a rollback plan.** EF generates a `Down()`
from the model's shape, not from what your data now looks like. For anything beyond a bare `ADD
COLUMN`, `Down()` loses data. Treat `Down()` as documentation.

**Rollback checklist, written into the PR before it is merged:**

1. Which release does this depend on already being deployed?
2. If this is reverted, what breaks — code, data, or nothing?
3. Is there a `DROP` in this PR? If yes, in which earlier release did the code stop using it?
4. How do you verify success in production, in one query?

A PR that changes the schema and cannot answer these four is not ready.

---

## 7. The change checklist

```
[ ] Domain owner and approver identified          (OWNERSHIP_AND_RETENTION.md §3)
[ ] New table? Budget is OVER (76/75) — owner approved + something removed (TARGET_SCHEMA.md §7)
[ ] Personal data? retention_policies row added or amended by the DPO
[ ] Obeys CONVENTIONS.md: keys, tenancy, types, column order, naming
[ ] Effective-dated? EXCLUDE constraint + 4 boundary tests
[ ] Migration generated by `dotnet-ef migrations add`, never hand-written
[ ] Every new FK declares ON DELETE explicitly; no new FK cycle
[ ] New enum value? CHECK + C# constants class + DATA_DICTIONARY.md all updated
[ ] Expand/migrate/contract phase stated in the PR description
[ ] No DROP in the same release that stops using the thing
[ ] Data migration is idempotent, batched, resumable, observable
[ ] Long backfill moved to background_jobs, not the migration
[ ] Consumer code in the same PR (orphan ratchet)
[ ] Tenant-isolation test added
[ ] DATA_DICTIONARY.md, ERD.md, OWNERSHIP_AND_RETENTION.md updated
[ ] Rollback checklist (§6) answered in the PR
[ ] ./scripts/schema-gates.sh green locally (all five, including the self-test)
[ ] schema.sql regenerated and committed in the SAME commit (byte-diff gate)
[ ] New table? RLS policy in the same PR; shape declared in the manifest
[ ] New view? WITH (security_invoker = true)
[ ] New SECURITY DEFINER function? REVOKE EXECUTE FROM PUBLIC + SET search_path
[ ] Integration-facing? external_system / external_id / external_synced_at
[ ] New index? EXPLAIN (ANALYZE, BUFFERS) on a seeded 5,000-employee tenant recorded in the PR
[ ] Partitioned table? Unique contains the partition key; PK composite
[ ] Migration rehearsed on a Neon branch cut from production; apply time and lock duration reported
[ ] New table? @owner: and @retention: tags in its COMMENT ON TABLE (R3)
[ ] New index? COMMENT ON INDEX naming the query it serves (R4)
[ ] All schema jobs green: `schema-gates` (six) AND `baseline-schema-gates` (five) — eleven, not six
```
