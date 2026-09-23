# Keeping these documents honest

Documentation that drifts is worse than none: it is believed for a while, then trusted by nobody,
and then the next person re-derives everything from the code anyway.

This repository already has the failure mode recorded — the stale-audit-docs problem, where the
repo's own specs understated the product by roughly six weeks and had to be verified against code
before being quoted. **A document nothing checks is a document that is wrong.**

So: split the artefacts into what a machine can derive and what only a human can state, generate
the first, and fail CI when the second stops matching the model.

> **Revision 5 changes the source of truth for generation.** §19.1 makes the baseline hand-written
> SQL with a `COMMENT ON` on every table and column, and states that **the data dictionary is
> generated from `information_schema`**. That is strictly better than generating from the EF model:
> `information_schema` and `pg_catalog` can see triggers, policies, partitions and `EXCLUDE`
> constraints, and **the EF model cannot**. The proposal below is therefore re-based onto a live
> Postgres, which the `schema-gates` job already runs as a service container.

---

## 1. What is generated, and what is hand-written

| Artefact | Generated | Hand-written | Checked by CI |
|---|---|---|---|
| `CONVENTIONS.md` | — | all of it | Partly: the conventions that are mechanical (`tenant_id` present, `UNIQUE (tenant_id, id)`, composite FKs, no `numeric` without scale, no Postgres `ENUM`) are enforced as **model assertions**, not as prose |
| `DATA_DICTIONARY.md` | column names, types, nullability, FK targets, CHECK-enum value sets, uniqueness | purpose sentence, tier, lifecycle, retention class, notes | **Yes** — coverage + every generated fact |
| `ERD.md` | every `erDiagram` block | the prose between diagrams | **Yes** — every model FK must appear as an edge |
| `OWNERSHIP_AND_RETENTION.md` | the table list per domain, **and §3's class column from `retention_policies`** | owner, approver, notes | **Yes** — every table has a `retention_policies` row, and the document matches it |
| `HOW_TO_CHANGE_THE_SCHEMA.md` | — | all of it | No — it is a runbook, not a description of the model |
| `ANTI_PATTERNS.md` | — | all of it | No — it is history, and history does not drift |
| `KEEPING_DOCS_HONEST.md` | — | all of it | No |

**The split that matters:** a machine can tell you a column exists and what type it is. It cannot
tell you *why the table exists*, *who owns it* or *how long the data must live*. Generate the
first; make CI refuse to let the second go missing.

---

## 2. The proposal: one golden-file test, no new project

Put the generator **inside the existing test project**, as a golden-file test with a write mode.
This is deliberately the cheapest thing that works:

- It ships **nothing** into the production image (the test project is not deployed).
- It needs **no CI workflow change** — it runs in the existing `backend-tests` job.
- It follows the pattern the repo already uses and already trusts:
  `Zayra.Api.Tests/Security/OrphanEntityRatchetTests.cs`, `QueryFilterBypassRatchetTests.cs`,
  `RawSqlExecutionRatchetTests.cs`.

**New file:** `integration/backend-dotnet/Zayra.Api.Tests/Schema/SchemaDocsSyncTests.cs`
**New generated output:** `docs/schema/generated/*.md` — committed, and never hand-edited.

### Commands

```bash
cd integration

# Verify (what CI runs)
dotnet test backend-dotnet/Zayra.Api.Tests/Zayra.Api.Tests.csproj --filter "FullyQualifiedName~SchemaDocsSync"

# Regenerate after a schema change, then commit the diff
SCHEMA_DOCS_WRITE=1 dotnet test backend-dotnet/Zayra.Api.Tests/Zayra.Api.Tests.csproj \
  --filter "FullyQualifiedName~SchemaDocsSync"
```

The second command is the one an engineer runs. It is in the `HOW_TO_CHANGE_THE_SCHEMA.md`
checklist, and the failure message prints it verbatim — a gate that does not tell you how to fix it
gets disabled.

### How it reads the model

**Re-based: read `pg_catalog`, not the EF model.** The four new assertions (policy coverage,
partition declarations, row-stamp exemptions, retention rows) are invisible to EF, so the test moves
to the `schema-gates` job, which already has a `postgres:16-alpine` service container and a freshly
migrated database. The EF-model reads below remain useful for the entity-to-table mapping:

```csharp
var options = new DbContextOptionsBuilder<ZayraDbContext>()
    .UseNpgsql("Host=localhost;Database=none")   // never opened; the model is built lazily
    .Options;
using var db = new ZayraDbContext(options);
var model = db.Model;

foreach (var et in model.GetEntityTypes().Where(e => e.GetTableName() is not null))
{
    var table   = et.GetTableName()!;                       // snake_case table name
    var columns = et.GetProperties()
                    .Select(p => (p.GetColumnName(), p.GetColumnType(), p.IsNullable));
    var fks     = et.GetForeignKeys()
                    .Select(fk => (Cols: fk.Properties.Select(p => p.GetColumnName()),
                                   To:   fk.PrincipalEntityType.GetTableName(),
                                   fk.DeleteBehavior,
                                   fk.IsUnique));
    var checks  = et.GetCheckConstraints()
                    .Select(c => (c.Name, c.Sql));          // CHECK-enum value sets live here
}
```

`GetCheckConstraints()` is what makes the enum check possible: the value list in
`DATA_DICTIONARY.md` is compared against the `IN (…)` literals parsed out of the actual constraint.
That is the drift nobody would otherwise notice — a value added to a CHECK and not to the docs is
invisible until a support ticket.

---

## 3. The nineteen assertions

Each one fails the build with a message naming the table, the fact, and the fix command.

| # | Assertion | Catches |
|---|---|---|
| 1 | **Generated files match.** `docs/schema/generated/*.md` regenerated from the model is byte-identical to what is committed. | Any schema change shipped without regenerating: a new table, column, type change, FK, or CHECK value. |
| 2 | **Dictionary coverage.** Every table in the model has a `### \`table_name\`` heading in `DATA_DICTIONARY.md`, and every such heading names a table in the model. | A new table with no purpose sentence; a documented table that was dropped. |
| 3 | **ERD coverage.** Every FK in the model appears as an edge in some `erDiagram` block in `ERD.md`, and every edge names two real tables. | A new relationship nobody drew; a diagram edge for a relationship that was removed. |
| 4 | **Retention/document agreement.** The class letter in `OWNERSHIP_AND_RETENTION.md` §3 agrees with the seeded row's `trigger_event`/`disposition` per §2.1. (Coverage itself is assertion 12.) **Additionally: the retention window and the §19.3 partition window must not contradict each other.** They did, on `attendance_days` and `timesheet_entries` (24 vs 36 months); revision 6 resolved both to 24. | The document drifting from the rule the purge job runs — and the next such disagreement, which will not announce itself either. |
| 5 | **Enum value sets match.** Every CHECK-enum column's value list agrees across three places: the model's check constraint, the C# constants class of the same name, and `TARGET_SCHEMA.md` §9. **The constraint *name* is part of the assertion**: §9's 36 numbered rows keep their `chk_<table>_<column>` spelling (the constants class is named for it), everything else is `ck_<table>__<assertion>`, and **no constraint name may appear on two tables**. | A widened CHECK the docs do not know about; a constants class that drifted from the database; a rename that breaks the three-way comparison; and a §9 row that names one constraint for two tables, which Postgres can only ever store as two objects. |
| 6 | **Tenancy invariant.** Every tenant-tier table has `tenant_id NOT NULL`, an alternate key on `(tenant_id, id)`, and every FK to another tenant-tier table is composite on `tenant_id`. Nullable `tenant_id` is allowed **only** on the **seven** named tables (`CONVENTIONS.md` §3) — an eighth fails. | The isolation guarantee, enforced against the model rather than against 900 remembered code paths. Seven, not six: `background_job_items` inherits its job's NULL tenant and revision 6 had left it out. |
| 7 | **No banned constructs.** No Postgres `ENUM` type; no soft-delete column outside `tenants`/`employees`/`users`/`companies`; no `numeric` without explicit precision; no per-row currency column; no counter on a settings table; no FK with an unspecified `ON DELETE`; no FK cycle; **no bare `ON DELETE SET NULL` on a composite FK** (it nulls `tenant_id` and raises `23502` at runtime, not at DDL time); **no nullable column inside an `EXCLUDE` subject list or a `UNIQUE` without `COALESCE` or `NULLS NOT DISTINCT`** (a NULL operand exempts the row from the constraint, silently); **no `hours` column and no bare `period` column** (durations are minutes, periods are `year` + `month`). | `CONVENTIONS.md` §3, §4, §5, §6, §7 and `ANTI_PATTERNS.md` §6. The three new clauses are revision 7's, and the first two are defects the baseline implementation found that no review had. |
| 8 | **Table inventory, not budget.** Decision 9 dropped the hard cap, so counting is no longer the check. Instead: every table has a `COMMENT ON TABLE` carrying `@owner:` and `@retention:` tags, and a `DATA_DICTIONARY.md` purpose sentence — i.e. **every table names the capability it serves**, which is what the principle actually requires. | `TARGET_SCHEMA.md` §7 and §6 Risk 1 — a principle is a weaker brake than a number, so it needs a mechanical floor. |
| 9 | **RLS policy coverage, shape *and arithmetic*, over `relkind IN ('r','p','v')`.** Every relation in that population has RLS enabled and forced; the generator's `table → shape` manifest (a / b / p_auth / c / grant-only / self-tenant) matches live `pg_policies` text and `role_table_grants`; no table without an entry, no entry without a table; **and the six populations must sum to the table count**. | A table with no policy; a table with RLS on and the **wrong** policy — twice now, and both times it looked *empty* rather than *wrong* (`retention_policies`, then `background_job_items`); a view with no policy story, because the population includes `'v'`; and **a count that does not close**, which is how revision 6 shipped 66+6+4=76 while `platform_users` and `data_protection_keys` sat in no shape at all. |
| 10 | **Partition declarations, bounds and children.** The five tables of §19.3 are `relkind='p'` with the stated key; no sixth is partitioned without a doc entry; every unique contains the partition key; each parent has a `DEFAULT` partition and ≥ 2 months of headroom; **every `timestamptz` bound carries an explicit `+00`**; and **the nine FKs `030` restates verbatim from `021` are identical in both files**. **For every child: RLS enabled and forced, and zero direct grants to any login role.** | A unique that silently lost its guarantee; a quiet headroom alarm; the revision-5 hole — a child partition that `PartitionMaintenance` granted, an unfiltered copy of millions of rows; a bound written under a different `TimeZone`, leaving a few hours each month that route into `DEFAULT` unannounced; and the duplication `CREATE TABLE … LIKE` forces, because `LIKE` never copies foreign keys. |
| 11 | **Row-stamp exemptions.** Every mutable table has all four stamp columns and the `trg_row_stamp` trigger; every table on the §Conventions exempt list has `created_at` and its actor column **only**. Both directions fail. | A new table with no stamping, and an append-only table that grew an `updated_by` nobody maintains. |
| 12 | **A retention row and an owner per table.** Every table has a `retention_policies` row (matched on `entity_name`, directly or via a group row), a non-null `owner_role` from the four values of §12.4, and an `@owner:` comment that agrees with it. | **A table shipped with no retention class, no owner, or an owner the comment and the data disagree about.** This is the assertion the coordinator asked for, and it is the one with legal consequences. |

**Assertions 1–5 are documentation gates. 6–12 are design gates that happen to live here** because
this is the only test that already has both the model and a live schema in hand. Splitting them into
a second file would be tidier and would mean one of them never gets written.

| 13 | **`security_invoker` on every view.** `pg_class.reloptions` contains `security_invoker=true` for every view in the baseline. | The P0 of `ANTI_PATTERNS.md` §12: a view without it reads its base tables as `kynex_owner`, which holds `BYPASSRLS`, so the **mandated** way to read a leave balance returns every tenant's. `FORCE ROW LEVEL SECURITY` does not cover this. |
| 14 | **No `SECURITY DEFINER` function executable by `PUBLIC` or `kynex_ro`**; every one declares `SET search_path = pg_catalog, app`; **every one that reads a credential column names `app.current_tenant()` in its body**; and **`app.is_platform()` is *not* `SECURITY DEFINER` and tests `'MEMBER'`, not `'USAGE'`**. | `EXECUTE` is granted to `PUBLIC` **by default**, so a support session could call `app.resolve_login` and receive `password_hash`, defeating the column-level revoke. An unpinned `search_path` is hijackable. **Without the tenant check, `app.resolve_login` is a cross-tenant credential read** — an authenticated tenant-A session passes tenant B's slug and gets B's hash. And inside a definer function `current_user` is the *owner*, so a `SECURITY DEFINER` `is_platform()` would return true for everyone, while `'USAGE'` is false for a `NOINHERIT` membership and would silently demote a real operator. |
| 15 | **No login role reaches a `BYPASSRLS` role except `kynex_migrator`.** Walks `pg_auth_members` **recursively** from every `rolcanlogin` role. | The check that a direct `pg_roles.rolbypassrls` test cannot make: `GRANT kynex_owner TO kynex_app` defeats the naive version while every row still reads `rolbypassrls = false`. |
| 16 | **The external-id convention where declared.** Every table the docs mark integration-facing carries `external_system`, `external_id`, `external_synced_at` — and **only** those tables do. Revision 7 settled §18 against §2: the trio is **additive**, and it does **not** replace `qiwa_contract_no`, `submission_reference` or `gosi_employee_no`, which are typed statutory identifiers with their own §13.3 length bounds. No table in the 76 carries the trio today, so this assertion currently holds vacuously and is written to stay true when the first one does. | The convention decaying back into one column name per integration — and the opposite error, a statutory identifier dissolved into an opaque `external_id` that loses its type, its bound and its meaning in a filing. |

| 17 | **FK register coverage, both ways.** Every row of `TARGET_SCHEMA.md` §8.2 is declared by exactly one constraint in `020`/`021`/`022`, and every FK in `pg_constraint` matches a register row. The register is **one row per constraint** — revision 7 split the two rows that named two columns each, precisely so this is countable. **Plus: every tenant-tier table reaches `tenants` by at least one FK on a `NOT NULL` column**, which is what `gl_mappings`, `approval_workflows` and `document_templates` failed. | The arithmetic that went wrong in revision 6: the register read as 160, the SQL applied 156, and six rows were parked in a file footnote where nothing counted them. Also a tenant table whose only path to `tenants` runs through a nullable `company_id` — no tenant FK at all on exactly the default rows. |
| 19 | **No world-readable relation outside the allow-list.** No relation in `pg_catalog` or an extension schema is readable by `PUBLIC` unless it is explicitly allow-listed — `pg_stat_statements` is revoked. | The class of cross-tenant read that **no RLS policy covers**, because the object is not a table in this schema. `pg_stat_statements.query` carries the literals of every statement the server has run, from every tenant. |
| 18 | **Drift-gate normalisation is narrow.** `schema-normalise.sh` removes only `\restrict`/`\unrestrict` guard lines, the version banner and `SET`/empty-comment lines, and the test proves it is a no-op on statement text. | A normaliser widened until the byte-diff gate passes on anything — which is the usual end of a gate that was red on day one because `pg_dump` 16+ regenerates a nonce on every invocation. |

**9–12 were the four asked for after revision 5; 13–16 are revision 6's; 17–19 are revision 7's, written from what the first implementation of the baseline actually hit.** They share one property
worth stating: each fails **by absence**. A reviewer has to *notice* a missing policy, an ungranted
child, a view without `security_invoker`; a ratchet cannot fail to.

**13 and 15 also carry a lesson about gates themselves.** Both exist because a narrower check
already looked green: the coverage ratchet scanned `relkind IN ('r','p')`, so a view was not
unprotected — it was **not in the population**. A direct `rolbypassrls` test returns false for every
row while a `GRANT` hands over the same power. **When you add a new kind of object, widen every
ratchet written before it existed**, because a gate's blind spot reports green by definition. This
repo has shipped a lint that passed vacuously and a health check that always returned zero.

### Failure message shape

```
SchemaDocsSync — 2 failures.

[1] docs/schema/generated/TABLES.md is stale.
    payroll_inputs: column `settlement_batch_id uuid NULL` is in the model and not in the docs.
    Fix:  SCHEMA_DOCS_WRITE=1 dotnet test --filter SchemaDocsSync   (then commit the diff)

[4] Table `payroll_reversals` has no retention class.
    Add a row to OWNERSHIP_AND_RETENTION.md §3 with a class from {R,C,P,O,S,E,T}.
    A table with personal data and no class is a PDPL gap, not a docs gap.
    Approver for this domain: Payroll Engineering + DPO.
```

---

## 4. Why a golden file and not a doc generator run in CI

The obvious alternative is for CI to regenerate the docs and commit them. Rejected:

| Option | Why not |
|---|---|
| CI writes the docs | CI pushing to `main` needs a write token on a branch that is currently unprotected. A worse trade than a failing test. |
| Generate at build time, do not commit | The docs stop being readable on GitHub, which is where people actually read them. |
| Nightly job that opens an issue | A nightly is noticed by nobody. The gate must be in the PR that caused the drift. |
| Golden file, committed, checked in the PR | The diff is **reviewable**. A reviewer sees "this PR adds a nullable column to `payroll_slips`" in the docs diff, next to the code. That review is worth more than the check. |

The last row is the real argument. The generated diff turns a schema change into something a
reviewer can see without reading a migration.

---

## 5. What still rots, and the one manual practice that stops it

CI cannot check a sentence. These decay silently:

| At risk | Mitigation |
|---|---|
| Purpose sentences in `DATA_DICTIONARY.md` going stale as a table's role widens | The doc-sync diff appears in the same PR; the reviewer is asked to re-read the purpose sentence for any table whose columns changed |
| `ERD.md` prose between the diagrams | Owned by the domain owner in `OWNERSHIP_AND_RETENTION.md` §3 — a named owner, not "the team" |
| Retention **classes** drifting from what the purge job actually does | **Revision 3 already solved this**: `retention_policies` is the rule, the purge job reads it, and assertion 4 makes the document match the table. Generate §3's class column **from** the seeded rows rather than typing it. |
| `CONVENTIONS.md` rationale | Reviewed once per quarter by the Data Architect; a convention nobody can justify should be deleted, not kept |
| `ANTI_PATTERNS.md` | Grows only. An anti-pattern is added when a post-mortem names one. Never pruned — the history is the point. |

**The one manual practice:** the `HOW_TO_CHANGE_THE_SCHEMA.md` §7 checklist is pasted into every
schema-change PR description. Three of its lines are the doc updates, and a reviewer can see at a
glance whether they were ticked. The checklist costs thirty seconds and is the only part of this
system that catches a *wrong* sentence rather than a *missing* one.

---

## 6. Implementation order

| # | Step | Effort | Blocks |
|---|---|---|---|
| 1 | `SchemaDocsSyncTests` with assertions **1, 2, 12** (generated files, dictionary coverage, retention-row-and-owner per table) | Half a day | Nothing — do it with the baseline, while the schema is 76 clean tables |
| 2 | Add assertions **6, 7, 8, 11** (tenancy invariant, banned constructs, table inventory, row-stamp exemptions) | Half a day | Nothing |
| 3 | Add assertions **9, 10, 13, 14, 15** (policy coverage and shape over r/p/v, partition children, `security_invoker`, `SECURITY DEFINER` grants and `search_path`, recursive `BYPASSRLS` reachability) | One day | The baseline SQL must exist; runs in `schema-gates`, not `backend-tests`. **Do 13 and 15 first — they are the two that were already green while being wrong.** |
| 4 | Add assertions **3, 5** (ERD edges, enum value sets) — these need a small Markdown parser | One day | Needs 1 |
| 5 | Generate `DATA_DICTIONARY.md`'s column facts from `information_schema` + `COMMENT ON` (§19.1 gate 3), and §3's class column from the seeded `retention_policies` rows | Two days | Needs the baseline's `COMMENT ON` coverage and the DPO's seed data |

**The day-one finding assertion 4 was written for is already resolved**: revision 6 settled
`attendance_days` and `timesheet_entries` at 24 months in both §12.4 and §19.3. The assertion stays,
because the next such disagreement will not announce itself.

**Do step 1 before the baseline migration merges, not after.** These documents are correct exactly
once — today, against a schema that does not exist yet. Every day the check is missing, they drift,
and the drift is unbounded because nothing is measuring it. Retrofitting the check onto 74
already-divergent tables is a different and much worse job than starting it at zero.

**A worked demonstration of why.** `TARGET_SCHEMA.md` moved **72 → 74 → 76 tables in a single
day**, across revisions 2, 3 and 4, *while these documents were being written*. Four tables
appeared; `employees` gained four PDPL columns and lost `dependents`; `users.tenant_id` went from
nullable to `NOT NULL` and platform operators moved to their own table; 26 per-row `currency`
columns were deleted; the soft-delete rule inverted twice. Each of those would have been a silent
contradiction within hours, and one of them — the `users` nullability change — **was still wrong in
the source's own §6 Risk 3 for two revisions**, which is precisely the class of error a human
reviewer does not catch and a model-derived check does. Revision 7 makes the same point with twelve
more: none of the contradictions it settles was found by three adversarial review passes, and all
twelve were found within days by writing the DDL. Assertions 1, 2, 4 and 6 catch all of them in the PR that causes
them.

---

## 7. Where these documents belong in the repo

**They live in `docs/schema/`, in the repository.** Moved there on 2026-09-23 together with
`.claude/skills/kynexone-schema/SKILL.md`, so CI can read them, the sync gate can compare against
them, and a PR shows them change. Before that they sat outside the repo, where nothing could check
them — and a document nothing checks is a document that is wrong (§1).

`TARGET_SCHEMA.md` stays at the repository root: these documents cite it as `../../TARGET_SCHEMA.md`,
and the audits cite it too.

### Why `docs/schema/`

Four reasons, recorded so the location is not relitigated:

| Reason | Detail |
|---|---|
| Next to what it describes | `integration/` already carries a dozen `*_AUDIT.md` files at its root. `docs/schema/` is where an engineer looks for durable documentation rather than point-in-time audits. |
| One directory the gate can name | The sync test needs a single stable path. `docs/schema/` is short enough to hard-code and unambiguous enough not to need configuration. |
| It reads as a set | Seven documents with one subject belong in one directory named for that subject, not scattered into `docs/`. |
| Room for the generated half | `docs/schema/generated/` is the obvious home for the `information_schema`-derived output of §19.1's comment-coverage gate, beside the hand-written half rather than somewhere else. |

**Proposed layout:**

```
docs/schema/
  README.md                     <- new: one page, what each document is for, which to read first
  CONVENTIONS.md
  DATA_DICTIONARY.md
  ERD.md
  OWNERSHIP_AND_RETENTION.md
  HOW_TO_CHANGE_THE_SCHEMA.md
  ANTI_PATTERNS.md
  KEEPING_DOCS_HONEST.md
  generated/                    <- committed, never hand-edited
    TABLES.md  RELATIONSHIPS.md  ENUMS.md  POLICIES.md
```

**`TARGET_SCHEMA.md` stays at the repository root.** It is the design of record and is cited from
outside `docs/`; moving it would break every reference in these seven files and in the audits.

### Two things that must change in the same commit

1. **The relative links.** These documents cite each other by bare filename, which keeps working
   inside one directory, and cite `../../TARGET_SCHEMA.md`, which does **not** — from `docs/schema/`
   the root is `../../`. Every such reference needs rewriting, and the sync gate should assert that
   **every intra-doc link resolves**, so this cannot rot again.
2. **The skill.** `.claude/skills/kynexone-schema/SKILL.md` points at `schema-docs/*` paths that
   will not resolve once these are committed.

### What `SKILL.md` must say

The skill currently has a "Read before changing anything" table with seven `schema-docs/…` paths.
**Every one needs the `docs/schema/` prefix.** Three further edits are needed, and they matter more
than the paths:

| Edit | Why |
|---|---|
| **Paths** → `docs/schema/CONVENTIONS.md` etc.; `TARGET_SCHEMA.md` stays root-relative. | They will not resolve otherwise. |
| **Rule 3's "330 bypasses"** → state the denominator: *346 production sites in `Zayra.Api`, 330 ratchet-approved, tests excluded.* | The skill quotes a bare number that three different counts claim. §19.2 now fixes the denominator; the skill should not reintroduce the ambiguity. |
| **Rule 4** → add the two facts a reader will otherwise get wrong: **views must be `security_invoker`** (the mandated read path was a leak by default), and **the ratchet asserts the policy *shape*, not merely that RLS is on**. | These are the two RLS failures that look green. A skill that says "policies are FORCEd on every table" implies `FORCE` is sufficient — and `BYPASSRLS` outranks `FORCE`. |
| **Rule 1** → "…an owner, a retention class, **an RLS policy shape** and a `retention_policies` row before it ships". | Matches what assertions 9 and 12 actually enforce. |
| **The pre-PR command** → it currently filters `~Schema`, which will not match `SchemaDocsSyncTests` in `schema-gates`. Name both: the fast `backend-tests` filter and the `schema-gates` job that runs the catalog assertions. | A command that silently runs half the gates is worse than none. |
| **The header sentence** → "636 of 653 relationships unenforced" is right; add that the design is **revision 7, approved, and only partly built** — the table and constraint DDL exists, RLS, partitions, indexes, triggers and seeds do not — so a reader does not expect the database to match yet. | The single most likely misreading of the whole set. |

**Do not move anything yet.** The move happens on the build branch, in one commit, with the link
rewrite and the skill edit in the same change — otherwise the skill points at nothing for however
long the two commits are apart.
