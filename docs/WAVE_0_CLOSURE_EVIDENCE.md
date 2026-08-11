# Wave 0 — Closure Evidence

**Scope:** Wave 0 establishes the **engineering baseline** only. It does not certify the HRM, and it
does not establish pilot or production readiness. Wave 1 establishes the operational floor.

**Branch:** `wave0/engineering-baseline` (cut from `main` @ `1986999`)
**Environment:** macOS (darwin 25.6.0), .NET 8.0, Docker running, Postgres 16-alpine (Testcontainers + two throwaway containers)
**Date:** 2026-08-10

> Every claim below was **reproduced in this session**. Claims inherited from the previous session
> were treated as unverified until re-run.

---

## 1. Commit SHAs

| # | SHA | Title | Files | Net |
|---|---|---|---|---|
| 1 | `9d7e1d9ea3dc4b9c8247c9e5bd2f6a0faac5a814` | POD-D month-end hand-off + Wave 0 baseline repairs | 57 | +79,634 / −357 |
| 2 | `cf3f9d7e8a720e632807b0010e5581732a82e8df` | Wave 0 governance: freeze bypass surface + startup contract | 3 | +262 |
| 3 | _(this document + registers)_ | Wave 0 evidence and registers | — | — |

Commit 1's insertion count is dominated by regenerated EF `*.Designer.cs` and the model snapshot
(auto-generated). Hand-written change is ~4,500 lines.

**Split rationale.** A three-way split was attempted. `NotificationDeliveryWorker` depends on
`ScopedBypass`, and `Program.cs` carries POD-D's service registrations interleaved with the DI-validation
block, so separating the feature work from its repairs would have produced a **non-compiling intermediate
commit**. Per the "do not risk the verified state" rule, repairs ride with the feature work in commit 1;
the guards, which are independently green, are commit 2.

**Deliberately NOT committed** (unrelated agent-harness state, preserved untouched):
`.claude/settings.local.json`, `.claude/worktrees/*` (4 modified gitlinks, 1 deleted).

---

## 2. Reproduced verification battery

| Check | Command | Result |
|---|---|---|
| Whitespace/conflict | `git diff --check` | **Clean** |
| Build — API | `dotnet build Zayra.Api` | **0 errors** |
| Build — Tests | `dotnet build Zayra.Api.Tests` | **0 errors** |
| Backend tests (final) | `dotnet test` full suite | **1604 passed / 0 failed / 0 skipped** |
| Postgres integration tests | Testcontainers, within full run | **Executed, not skipped** — verified per class (see §3) |
| Skipped/excluded tests | `Skip=` scan + `Skipped` count | **0 skip attributes, 0 skipped tests** |
| Test projects enumerated | `find *.csproj` | **2 projects total** (`Zayra.Api`, `Zayra.Api.Tests`) — one test project; nothing silently excluded |
| Frontend typecheck | `npx tsc --noEmit` | **Pass** |
| Frontend production build | `npx next build` | **Pass** — all routes compiled |
| Secrets scan | `gitleaks 8.18.4 detect --no-git --config .gitleaks.toml` | **0 leaks** (see §5) |
| EF model drift | `dotnet ef migrations has-pending-model-changes` | **No changes** |
| DI container | boot with `ValidateOnBuild` + `ValidateScopes` | **Valid — no resolution or captive-scope errors** |

**Baseline movement:** 1600 → 1604. The four added tests are the two ratchet guards and the two
startup-contract lints. No pre-existing test was deleted or weakened.

---

## 3. Postgres test execution proof

Testcontainers tests can silently no-op if Docker is unavailable. Verified by name from the run log:

| Test class | Passed |
|---|---|
| `PayrollAuditHashChainPostgresTests` | 5 |
| `PostgresDateTimeIntegrationTests` | 4 |
| `TenantIsolationHardeningTests` | 11 |
| `CrossCompanyAccessTests` | 7 |
| `GosiReconciliationTieOutTests` | 9 |
| `TenantPurgeCompanyDataTests` | 1 |
| `PayrollProcessAtomicityTests` | 1 |

28 source files depend on the shared Postgres fixture. **Zero tests skipped anywhere in the run.**

---

## 4. Migration proof — fresh and upgrade

### 4.1 Fresh deployment from zero
Container `wave0-fresh`, empty `zayra_fresh` database.

- `dotnet ef database update` → **53 migrations applied, exit 0**
- All 6 new tables present: `employee_final_settlements`, `final_settlement_lines`,
  `gl_journal_exports`, `gl_journal_export_lines`, `bank_payment_confirmations`,
  `notification_deliveries`
- `payroll_runs.settles_final_settlements` → `boolean`, `NOT NULL`

### 4.2 Upgrade from previous accepted release
`main @ 1986999` is the previous accepted release; its last migration is
`20260805014944_AddPayrollProrationAndArrears`.

| Step | Result |
|---|---|
| Apply release schema to `zayra_upgrade` | **50 migrations** |
| Populate representative data | tenant + company + employee + payroll run (generated against actual NOT NULL constraints) |
| Apply new migrations | **50 → 53**, exit 0 |
| Data preserved | tenants 1, companies 1, employees 1, runs 1 — **all intact** |
| New NOT NULL column backfill | `settles_final_settlements = false` on the pre-existing row — **safe default, no rewrite failure** |
| New tables | **6 of 6** |

### 4.3 Fresh vs upgrade schema parity
The upgrade path must converge on the fresh schema, or the two populations diverge permanently.

| Dimension | Fresh | Upgrade | Diff |
|---|---|---|---|
| Columns | 4,295 | 4,295 | **identical** |
| Indexes | 835 | 835 | **identical** |
| Constraints | 322 | 322 | **identical** |

### 4.4 Settlement schema specifics
- Indexes on `employee_final_settlements`: PK, `(tenant_id, company_id)`, `(tenant_id, employee_id, status)`, `(tenant_id, payroll_run_id)`, plus the live-offboarding guard.
- **Cannot-settle-twice invariant is enforced in the database**, not only in code:
  `CREATE UNIQUE INDEX ix_employee_final_settlements_live_offboarding ON employee_final_settlements (tenant_id, offboarding_id) WHERE status <> 'Cancelled'`
- `gl_journal_exports` gained `(tenant_id, company_id)` — the index that follows from making it `ICompanyScopedOperational`.

### 4.5 Application startup against both databases
API booted against the **upgraded** database:
- `/health/live` → `{"status":"live"}`
- `/health/ready` → `{"status":"ready", database ok, "pendingMigrations":0}`
- With `ValidateOnBuild` + `ValidateScopes` enabled: **no DI failures**

### 4.6 Foreign keys — architectural finding, not a regression
The new tables declare **no foreign keys**. This is **consistent with the existing architecture**:
the whole schema has **17 FKs across 303 tables**. POD-D did not regress a convention.

> **Recorded risk (open, not closed):** with no FK enforcement, orphan rows are possible. This was
> observed directly during seeding — a `companies` row inserted successfully while its `tenants` row
> had been rolled back. Referential integrity currently depends entirely on application code. This
> warrants an ADR; it is **not** a Wave 0 blocker but should not be discovered later by an auditor.

---

## 5. Secret scan

First run reported **3 findings**, all `square-access-token` in
`backend-dotnet/Zayra.Api.Tests/TestResults/wave0.trx` — base64 blobs in a `.trx` **I generated during
this session**, not repository content. After removing the artifact: **0 findings, exit 0**.

Underlying defect (now fixed): `TestResults/` was not gitignored, so any developer running
`dotnet test --logger trx` would commit build artifacts *and* fail the secret gate on a false positive.
Added `backend-dotnet/**/TestResults/` and `*.trx` to `.gitignore`.

---

## 6. Query-filter governance

Full detail in **`docs/QUERY_FILTER_BYPASS_REGISTER.md`**. Summary:

- **210 → 209** raw `.IgnoreQueryFilters()` call sites across 55 files.
- **All 31 POD-D sites verified individually.** 30 bypass the *company* filter only and re-apply
  `TenantId` in the predicate; 1 (notification lease reclaim) is genuinely cross-tenant.
- **User-triggered paths verified against the "not TenantId alone" rule.** All 6 bypasses in
  `GlJournalExportsController` are preceded by an explicit permission check (`finance.gl.read` /
  `.export` / `finance.erp.confirm`) **and** `ScopeError` → `CanAccessCompany`, with `List` re-applying
  `AccessibleCompanyIds` for non-group callers.
- **Approved abstraction added:** `ScopedBypass.TenantWide` / `.ForCompanies` / `.SystemWide`. All three
  reject a justification under 20 characters; `ForCompanies` fails closed on an empty company set;
  `SystemWide` throws outside a 1..1000 batch bound.
- **Lease reclaimer hardened** onto `ScopedBypass.SystemWide`: bounded batch (50), lease
  owner/expiry/version, retry budget preserved, **traceable tenant context logged** (counts and tenant
  IDs only — never recipient, destination, or body), private to the hosted service with no user path.
- **Architecture guard added and negative-tested.** Lowering one pinned count to 7 against a file
  holding 8 produced both `grew from 7 to 8` and `found 210 vs approved 209`. The guard fires.

**Open debt, explicitly not closed:** 179 legacy call sites are frozen and comment-justified but have
**not** each been re-verified against the full metadata schema. `FinanceGlController` (20) and
`EmployeesController` (8) are user-reachable and are the first Wave 1 security task.

---

## 7. Wave 0 verdicts

These are **narrow and separate**. Wave 0 says nothing about pilot or production readiness.

| Verdict | Result | Basis |
|---|---|---|
| **Engineering baseline** | **GO** | Builds clean; 1604/1604 with zero skipped; zero model drift; fresh and upgrade migrations both prove out and converge on identical schema; DI validated at boot; secret scan clean; isolation guards green and negative-tested; work committed on a branch |
| **Pilot readiness** | **NO-GO** | G1–G8 untouched. Documents are still ephemeral in production, payroll still runs in-request, there is no observability, no tested restore, and no bank has ever accepted a WPS file |
| **Production readiness** | **NO-GO** | As above, plus 179 unmigrated legacy bypasses, no FK-level referential integrity, and three modules that are built but disconnected |

**What Wave 0 does and does not entitle anyone to say.** It entitles the claim that the codebase is
internally consistent, that its migrations are safe in both directions, and that its isolation controls
are enforced by tests that demonstrably fail when violated. It does **not** entitle any claim about the
product working end-to-end for a real customer. No capability moved from PRESENT BUT UNPROVEN to
VERIFIED COMPLETE in Wave 0, because no zero-state journey was run.

---

## 8. Independent review

Four bounded read-only reviewers (HR lifecycle, payroll/finance, security/privacy/SRE, enterprise
product) were commissioned to challenge this baseline, the gap priorities, the EOSB contract change,
the isolation claims, and the readiness verdicts. **Their disagreements and the resolutions are
recorded in §9 and are a required input to Wave 1 sequencing.**

---

## 9. Reviewer disagreements and resolutions

_Populated from the independent review round; see the follow-up commit._
