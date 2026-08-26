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
| 3 | `6acbad7` | Wave 0 registers: capability baseline, bypass register, closure evidence | 3 | docs |
| 4 | `7d14b06` | Wave 0 review round: fix two self-inflicted defects, correct falsified claims | 8 | +410 / −60 |
| 5 | `811f8a5` | Wave 1 plan: operational durability foundation | 1 | docs |

**PR:** [#43](https://github.com/Kode-Kinetics/zayra-ai-workforce/pull/43) → `main`. **Not merged** —
held pending review sign-off, per the Wave 0 rule.

**CI on head commit (`811f8a5`):**

| Job | Result |
|---|---|
| Backend Tests (security gate) | **pass** (4m29s) |
| Frontend Typecheck | **pass** (43s) |
| Secret Scan (gitleaks) | **pass** (15s) |
| Dependency Vulnerability Scan | **pass** (28s) |
| Deploy / migration jobs | correctly **skipped** on a PR |

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
| Backend tests (final) | `dotnet test` full suite | **1616 passed / 0 failed / 0 skipped** |
| Postgres integration tests | Testcontainers, within full run | **Executed, not skipped** — verified per class (see §3) |
| Skipped/excluded tests | `Skip=` scan + `Skipped` count | **0 skip attributes, 0 skipped tests** |
| Test projects enumerated | `find *.csproj` | **2 projects total** (`Zayra.Api`, `Zayra.Api.Tests`) — one test project; nothing silently excluded |
| Frontend typecheck | `npx tsc --noEmit` | **Pass** |
| Frontend production build | `npx next build` | **Pass** — all routes compiled |
| Secrets scan | `gitleaks 8.18.4 detect --no-git --config .gitleaks.toml` | **0 leaks** (see §5) |
| EF model drift | `dotnet ef migrations has-pending-model-changes` | **No changes** |
| DI container | boot with `ValidateOnBuild` + `ValidateScopes` | **Valid — no resolution or captive-scope errors** |

**Baseline movement:** 1600 → 1616. The sixteen added tests are two ratchet guards, two
startup-contract lints, and twelve `ScopedBypass` tests added after review found the helper had shipped
with none. No pre-existing test was deleted or weakened.

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
- `gl_journal_exports` briefly gained `(tenant_id, company_id)` from the `ICompanyScopedOperational`
  change; that change was **reverted** after review (§9.1 R2) and migration
  `DropGlJournalExportCompanyScopeIndex` removes the index again. Model drift re-verified as zero after
  the revert.

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

Four bounded read-only reviewers challenged this baseline. **They falsified several of its claims,
including two defects in the Wave 0 work itself.** Every finding below was independently re-verified
against the code before being accepted; nothing here is taken on the reviewer's word.

### 9.1 ACCEPTED — defects introduced by Wave 0 itself (fixed in this session)

| # | Finding | Verified | Resolution |
|---|---|---|---|
| **R1** | `ScopedBypass.SystemWide` applied `Take(n)` **before** the caller's `Where`, so EF emitted `SELECT * FROM (SELECT * FROM t LIMIT n) WHERE …`. An arbitrary n rows were chosen first and filtered after. Once the table exceeds the batch size — i.e. immediately in production — **the lease reclaimer would sweep almost nothing**. The "bounded batch" presented as a safety control was silently a correctness bug | Confirmed by reading the helper and its call site | API redesigned so it **cannot** be composed wrongly: `SystemWide` now takes the predicate **and a required order key**, applying filter → order → bound. Regression test `ClaimsMatchingRows_EvenWhenNonMatchingRowsOutnumberTheBatch` (200 non-matching rows, batch 50, 3 stuck rows → must find 3) |
| **R2** | Making `GlJournalExport` `ICompanyScopedOperational` **broke group-level exports.** A group export legitimately has `CompanyId = null`; the write guard refuses a null company in a multi-company tenant, so the primary use case threw `company_scope_required`. In a single-company tenant the auto-stamped id broke the supersede probe (which matches `CompanyId == null`), so prior exports were never superseded | Confirmed at `ZayraDbContext.cs:479-482` — group-level actors skip the non-group branch and hit the throw | **Reverted.** The original reasoning was wrong: NULL here is not a backfill transient, it is the *meaning* of a group-wide export. Now a justified `CompanyScopeBootAssertion.AllowList` entry, matching the `FinanceGlEntry` precedent. Compensating control recorded: every endpoint checks an explicit `finance.gl.*` permission **and** `ScopeError` → `CanAccessCompany` |
| **R3** | `ScopedBypass` shipped with **zero tests** | Confirmed | `ScopedBypassTests` added — 12 tests covering ordering, batch bounds, justification floor, fail-closed empty company set, and tenant/company exclusion |

### 9.2 ACCEPTED — this document's own claims were wrong

| # | Claim as written | Truth | Correction |
|---|---|---|---|
| **R4** | "Access revocation on exit is a boolean checkbox" (**G8, ranked P0**) | **False.** `OffboardingController.RevokeEmployeeAccessAsync` performs real revocation — user deactivated, `AccessMode = NoLogin`, every `EmployeeUserAccount` link disabled, **all refresh tokens revoked** — and is covered by `LeaverAccessRevocationTests` | **G8 struck as a P0.** Replaced by narrower, real defects (§9.4 D4) |
| **R5** | "The lease reclaimer is the ONLY genuinely cross-tenant read in the notification path" | **False.** The main drain query (`NotificationDeliveryWorker.cs:77`) has **no tenant predicate either** | Register corrected. Two cross-tenant reads exist, not one |
| **R6** | "31 POD-D bypasses, all reviewed" | Undercount — the true POD-D figure is **37**; 6 sites were never enumerated, including the cross-tenant drain | Register corrected; the ratchet's per-file counts were already right |
| **R7** | "Bypasses only the company filter" on the finance sites | **Inverted risk model.** `FinanceGlEntry`, `GlJournalExportLine`, `NotificationDelivery`, `PayrollPaymentBatch/Record`, `BankPaymentConfirmation` are `ITenantOwned` **only** — they have no company filter. What those calls actually drop is the **tenant** filter, hand-re-applied in the `WHERE` | Register corrected. Predicates are right today, but each is one deleted `Where` from a cross-tenant read with no backstop |
| **R8** | "Notifications — email: VERIFIED COMPLETE" | Category error. SMTP is unconfigured in `render.yaml`; `SmtpEmailService` logs `"SMTP not configured — dropped"` and returns. **All four channels are unconfigured out of the box** | Reclassified **PRESENT BUT UNPROVEN / CONFIGURE** |
| **R9** | "Standing risk: ~3,750 lines uncommitted" | Stale — resolved by commit 1 | Removed |

### 9.3 ACCEPTED — VERIFIED COMPLETE ratings that do not survive the code

| # | Row | Finding | New state |
|---|---|---|---|
| **R10** | Single authoritative EOSB engine | `ComputeEndOfServiceAsync` hardcodes `const string jur = "mainland"` and is the **only** call site of `ResolveEndOfServiceCalculator`. The DIFC calculator registered under `"ARE:UAE-DIFC"` is therefore **unreachable** — DIFC/DEWS EOSB is dead code for the exact multi-entity GCC profile being sold. Unknown countries fall through to `DefaultEndOfServiceCalculator` → **silent 0 gratuity**, with no fail-closed guard (payroll `Process` and WPS both have one; EOSB does not). Wage base is basic-only, which the code's own comment concedes is not Art. 84 "last wage" and awaits legal sign-off | **PARTIALLY IMPLEMENTED / COMPLETE** |
| **R11** | Monthly EOSB liability provisioning | `GlEventTypes.EosbProvisionAccrual` is **written by nothing**. `EosbProvisionLedger` consumes a ledger no code produces | **MISSING / BUILD** (was PRESENT BUT UNPROVEN) |
| **R12** | Settle + remit / control accounts clear | `GlControlAccounts.CheckAsync` covers five drivers and omits both new ones — `SETTLEMENT_PAYABLE` (2320) and `EOSB_PROVISION` (2310) | **PARTIALLY IMPLEMENTED** |
| **R13** | Constitution #11 "unsupported countries blocked — PASS" | Payroll and WPS block; **EOSB does not** | **VIOLATED** |
| **R14** | RBAC permission-first "VERIFIED COMPLETE" | Measured: **1,034** `[Http*]` actions, **128** `[HasPermission]`, **371** `[Authorize(Roles=…)]` across 77 of 102 controllers. String-role auth is not permission-first, and a tenant custom role does not satisfy those endpoints | Constitution #9 → **VIOLATED**; row → PARTIALLY IMPLEMENTED |
| **R15** | Tenant/company isolation **GO** | Not defensible while the register's own text says 179 of 209 sites are "debt, not assurance" with an open recorded risk on two user-reachable controllers. Both guard suites also **pass vacuously** if `ResolveSourceRoot()` returns null | **CONDITIONAL GO** |

### 9.4 ACCEPTED — new defects for the ledger (not fixed in Wave 0)

| # | Sev | Defect |
|---|---|---|
| **D1** | **P0** | **Terminate creates an unsettleable dead end.** `POST /employees/{id}/terminate` sets status `Terminated` and creates **no** offboarding record; `POST /api/offboarding` admits only `Active/Offboarded/Suspended`; the hardened `/final-settlement` requires an offboarding record. Anyone terminated via the Employees module can never be settled and cannot be routed back. **This is a regression surfaced by the Wave 0 contract hardening** — pre-C1, `/final-settlement` would settle them. Also blocks migration of already-terminated staff from a prior system |
| **D2** | **P0** | **Cross-company data exposure via `includeUnattributed=true`.** A query-string flag, validated nowhere against group level, adds all NULL-company `FinanceGlEntry` rows tenant-wide to a company-scoped caller's export. `ScopeError` validates only `companyId` |
| **D3** | **P0** | **`BankConfirmationsController` has no company authorization at all** — tenant + `payroll.export` only. A Company-A officer can import a bank response against a Company-B batch and flip that company's payment statuses. The voided-run guard also fails open cross-company, because `PayrollRun` *is* company-filtered while the batch is not, so `run` comes back null and the refusal is skipped |
| **D4** | P1 | Revocation defects (replacing the struck G8): checklist `AccessRevoked` settable by request body with **no revocation performed**; group over-revocation disables the whole `User` across sibling companies; revocation fires at `Complete`, not last working day; issued JWTs stay valid up to 30 min with no revalidation; `terminate` path revokes nothing |
| **D5** | P1 | **PII in logs.** `SmtpEmailService` logs the full recipient address plus subject at Information on every send, while the same wave masks the destination in the database. The DB is scrubbed; the log aggregator is not |
| **D6** | P1 | **Exactly-once conversion is worse than "unproven"** — no lineage column exists to key idempotency on; two accept endpoints with divergent guards (one explicitly commented "not idempotent"); no status guard on offer accept, so double-click **double-increments `FilledCount`** and can close a half-open requisition |
| **D7** | P1 | **EOSB parity holds only because every fixture sets `IsActive = true`.** `/eosb/calculate` filters on `IsActive`; `/final-settlement` does not — and the exit cascade deactivates the salary row. Same employee, same date, two gratuity figures |
| **D8** | P1 | `GCCComplianceSettings` is read with `FirstOrDefaultAsync(x => x.TenantId == …)` — no company, no country, **no ordering**. A group with KSA and UAE entities picks the labour law by whichever row Postgres returns first |
| **D9** | P1 | **Leave encashment overpayment**: a **Draft** company policy outranks an Active tenant default and authorises cash; pending `LeaveEncashmentRequest` rows reserve nothing, so the same days can be paid twice |
| **D10** | P1 | **No return-correction posting path.** `GlEventTypes.NetSettlementReturn` exists only in comments. After a returned salary, Cash/Bank is overstated and 2100 understated with no way to correct — and `ClosePeriod` has no gate, so the month closes over it |
| **D11** | P1 | **DataProtection keys not persisted** (`AddDataProtection()` with no `PersistKeysTo`). Present in `PILOT_ACCEPTANCE_SPEC.md` 6.4, **lost from this baseline's register**. Already fails on every restart |
| **D12** | P1 | `SetupPage.tsx` offers **WhatsApp/SMS** in the notification-channel dropdown with no signal they cannot deliver; the honest `/deliveries` ledger the notification pod built is **never called by the frontend** |
| **D13** | P2 | `EmployeeModuleSchemaBootstrapper` / `MissingTableCreator` are live-registered **MySQL runtime-DDL** paths against Postgres that swallow exceptions — dead code today, and exactly the defect class W0-3 was raised for. **Remove** |
| **D14** | P2 | Both guard suites `return;` (pass) when path resolution fails — a silent false negative that would disable the isolation lints entirely under a different CI layout. The bypass-lint keyword check also accepts a bare `// SYSTEM CONTEXT` with no reasoning |

### 9.5 REJECTED / MODIFIED

- **"Benefits is the highest ROI — CONNECT."** Rejected on review. Two independent reviewers showed the model cannot represent GCC medical insurance: no enrolment-dependant link (so no census, no mid-year newborn, no per-dependant premium), no insurer/policy number, no renewal or premium model, eligibility **fails open** when no rules exist, no duplicate-enrolment constraint, payroll integration is annotation-only, and no exit cascade (a leaver stays enrolled and the premium keeps being paid). Reclassified **INCORRECTLY DESIGNED / REDESIGN**; disposition for pilot is **feature-flag off**, not "build the UI".
- **Rehire "PRESENT BUT UNPROVEN"** → **MISSING / BUILD.** `RehireEligible` is written, displayed, and **read by nothing**; no rehire endpoint exists; the recruitment→draft→approve path has no duplicate-person probe. Certifying it on one unread boolean is the "a field is never a capability" error §0 forbids.
- **Gap ordering changed.** G3 observability moves **before** G2 (the baseline's own rationale — "what makes every later wave debuggable" — contradicted sequencing it third). G7 WPS acceptance moves to **day 1 as a lead-time track** owned by BD, since it is weeks of counterparty calendar and zero engineering days. G1 merges with G6 (residency is one assertion in the same function, not a workstream). G4 merges with D11. G5 demoted to P1 and reduced to "404 the SAML/OIDC metadata routes" — there is no SSO UI anywhere, so it is a procurement exposure, not a pilot one.
- **Two scoping questions promoted above all engineering**, because they are free and change the plan: does the design partner have a **Kuwait/Oman/Bahrain entity** (payroll 422s — that entity silently drops out of the pilot), and does it need **multi-currency group consolidation** (no FX translation exists, so a group figure spanning SAR and AED is meaningless)?

### 9.6 Effect on the Wave 0 verdict

The engineering-baseline verdict **stands at GO**, on a narrower basis than first written: builds clean, **1616/1616 with zero skipped**, zero model drift, fresh and upgrade migrations converge on identical schema, DI validated at boot, secret scan clean, and the two Wave 0 self-inflicted defects (R1, R2) found by review are fixed and regression-tested.

**Tenant/company isolation is downgraded GO → CONDITIONAL GO.** The other corrections change the *capability baseline*, not the engineering baseline: they are claims about product completeness, and every one of them moves a rating **downward**. None of the 14 new defects was introduced by Wave 0 except **D1**, which Wave 0's contract hardening surfaced and which is now the top carry-over item.


---

## 10. Wave 0 P0 closure — D1, D2, D3

**Authorized as an explicit follow-on.** These were the three P0 carry-overs recorded in §9.4.

### 10.1 Precondition deviation, recorded before any edit

The brief expected a clean working tree. **It was not clean.** HEAD was `a27a12c` and matched the
remote (so the branch had not advanced), but the working tree carried **155 modified files, +7,214
lines** of in-flight work that is not part of Wave 0 — document-storage hardening, `PublicId`
identity, refresh-token families, transactional HR invariants, e2e fixtures and four unapplied
migrations. It builds, and its own baseline is **1711 passed / 0 failed / 0 skipped**.

Nothing of it was reset, discarded, stashed or overwritten.

Consequence for D1: both files it must touch (`EmployeeManagementService.cs`,
`EmployeeManagementDtos.cs`) carry foreign `PublicId` edits whose backing property lives in a *still
uncommitted* file. Committing them wholesale would have produced **a commit that does not build**. The
D1 commit was therefore staged as *HEAD content plus this change only*, via `git hash-object` +
`git update-index`, leaving the working tree untouched. Verified: `git diff --cached` contained **zero**
`PublicId` lines, and the committed tree builds and tests green in an isolated worktree.

### 10.2 Commits

| Defect | SHA | Changed files |
|---|---|---|
| D1 | `576a471` | `EmployeeManagementService.cs`, `EmployeeManagementDtos.cs`, `Security/TerminateSeparationLifecycleTests.cs` |
| D2 | `2726c6f` | `Finance/GlJournalExportsController.cs`, `Security/GlJournalExportScopeTests.cs` |
| D3 | `e9aa84c` | `Finance/BankConfirmationsController.cs`, `Security/BankConfirmationScopeTests.cs` |

Starting head `a27a12c` → final head `e9aa84c`.

### 10.3 Root cause and invariant

| | Root cause | Invariant now enforced |
|---|---|---|
| **D1** | `terminate` set `Status="Terminated"` and created no separation; `/final-settlement` requires an offboarding with a last working day, and `/api/offboarding` refuses non-occupying statuses — so the employee was unsettleable **and** unroutable | A transition into an exit status always leaves exactly **one live separation**, created inside the existing offboarding domain and committed in the **same transaction** as the status change and both history rows. A live record is preserved verbatim; `Completed` is excluded so a rehire opens a **new** service period; a retry is a no-op |
| **D2** | `includeUnattributed` added every NULL-company ledger row tenant-wide; `ScopeError` validated only `companyId`, so a caller passing their **own** company passed and the flag widened behind it | `includeUnattributed=true` requires an explicitly **group-scoped** caller, checked at preview, create and reconciliation **before** any query, build, persist, stamp or export |
| **D3** | Tenant + permission only; the batch tables are `ITenantOwned` with no company filter. The voided-run guard also failed **open** cross-company, because `PayrollRun` *is* filtered so the run read back null and the refusal was skipped | Entity derived from `batch → run → PayrollRun.CompanyId`, never from the client; enforced **before** the uploaded body is read; NULL-company runs are group-only; missing/inconsistent ownership fails closed; cross-tenant returns `NotFound` |

### 10.4 Results

| Check | Result |
|---|---|
| Targeted D1 / D2 / D3 | **8 / 6 / 8 passed** |
| Full suite — working tree | **1733 passed, 0 failed, 0 skipped** (from 1711) |
| Full suite — **committed tree**, isolated worktree | **1648 passed, 0 failed, 0 skipped** |
| EF model drift — working tree and committed tree | **None** |
| Isolation / bypass / startup / PII guards | **26 passed** |
| Frontend typecheck + production build | **pass** |

Every guard was **negative-tested** rather than trusted: removing D1's separation block fails 6 of 8,
removing D2's guard fails exactly its 3 negative tests, removing D3's guard fails 5 of 8.

> The two totals differ because the uncommitted foreign work carries ~94 tests of its own. **1639 is the
> number CI sees**, and it is the one that matters for this PR.


### 10.5 Independent review — findings and disposition

Three bounded read-only reviewers (HR/offboarding lifecycle, multi-entity authorization,
database/transaction/idempotency) challenged the diff. **They found Critical defects in two of the
three fixes.** Every Critical and High was fixed in `5a664e7`; nothing was waived.

| # | Sev | Finding | Disposition |
|---|---|---|---|
| R1 | **Critical** | **D2 covered only half the surface.** The flag reaches the builder from a STORED artifact too — `Get`, `Download`, `Confirm`, `Reject` rebuild a filter from `GlJournalExport.IncludeUnattributed` and checked only the company. A group caller creates an export for company A with the flag; a company-A caller then reads the tenant-wide rows out of the artifact. `Confirm`/`Reject` additionally stamp `ErpPostingStatus` onto sibling-entity rows — a cross-entity **write** | **FIXED.** All four re-check the stored flag; 4 tests added. My original test docstring claimed to pin "EVERY entry point" — it was false and is corrected |
| R2 | **Critical** | **D1 excluded the population it existed for.** The trigger also required the previous status to be non-exit, so anyone already at Terminated/Archived/Exited with no separation — every pre-existing and migrated leaver — could not be remediated at all | **FIXED.** Clause removed; the liveSeparation lookup already provided idempotency. Test added starting from `Terminated` |
| R3 | **High** | **Rehire left a stale separation.** Nothing cancels the open offboarding on reactivation, and `Complete` is unreachable without a paid settlement. The next termination would settle against the **prior period's** last working day — a statutory underpayment | **FIXED.** Reactivation closes any open separation with no settlement drafted against it |
| R4 | **High** | **Idempotency had no database behind it.** Check-then-act with no constraint; settlements de-duplicate by `OffboardingId`, so two concurrent terminates ⇒ **two EOSB accruals** | **FIXED.** Partial unique index `(tenant_id, employee_id) WHERE status NOT IN ('Cancelled','Completed')`, added via the named overload so the plain lookup index survives |
| R5 | **High** | **`SeparationType` was unvalidated free text deciding a statutory award**, and reachable via `PATCH /status` (`employees.write`) while `/terminate` is `employees.approve` | **FIXED.** Closed vocabulary, fails closed; stripped on the write-level endpoint |
| R6 | High | `GetEntityScope()` resolves with `strictMode:false` and ignores `NarrowTo`, while the DB layer is strict in production | **RECORDED, not fixed.** A shared auth helper used app-wide; changing it inside this PR without its own review is riskier than the exposure. Logged as a new defect for the next security block |
| R7 | Medium | Sibling routes on `PayrollController` (`payment-batches/{id}/records`, `wps-status`, `settle/reverse`) are tenant-only and defeat D3's read/write goal from a different URL | **RECORDED, not fixed.** Outside the three authorized defects; the same `BatchScopeErrorAsync` pattern applies and is the obvious next task |
| R8 | Medium | Legacy runs with `CompanyId == null` become group-only for import/history — a rollout consideration for tenants that never backfilled | **RECORDED.** Intentional per the brief ("a legacy run with CompanyId=null must be group-only"); flagged as a migration/rollout note |
| R9 | Medium | Other terminal-ish states (`Inactive` via bulk deactivate, free-text statuses) still produce no separation | **RECORDED.** Real, but a vocabulary problem wider than D1 |
| R10 | Medium | Soft-delete has no guard against deleting a terminated-but-unsettled employee | **RECORDED.** Pre-existing; now more consequential because a live separation always exists |
| R11 | Low | Audit is written outside the business transaction | **RECORDED.** Pre-existing pattern across the service |

> **Honest note on my own tests.** Two of the original D1 tests passed *for the wrong reason* — the
> idempotency test only exercised a status short-circuit, and the rehire test hand-built a state the
> product cannot reach. Both were rewritten to drive the real paths. This is exactly what the
> independent round was for, and it is why "green" was not treated as done.


---

## 11. Wave 0 P0 closure — D1–D3 review round 2

**Precondition truth, recorded before any edit.** The brief expected head `a27a12c` and a clean tree.
Neither held. The branch had already advanced to `f4853d9` (local == `origin/wave0/engineering-baseline`),
carrying the first D1–D3 round and its review fixes, and **CI was already green on that head, both CodeQL
analyses included**. The working tree carried **153 modified tracked files** of another agent's in-flight
work (document storage, `PublicId` identity, refresh-token families, transactional HR invariants, leave
encashment, GL currency readiness) plus five untracked migrations. Nothing of it was reset, stashed or
overwritten. Per the brief's "rebase the plan on runtime truth", this round **verified the existing work
rather than re-implementing it**, then fixed what independent review found.

> **A note the previous round's evidence got wrong.** §10.4 recorded both "1648" and "1639" for the same
> quantity. Re-run here on an isolated export of the committed tree: **1648 passed / 0 failed / 0 skipped**.
> 1648 is correct; the 1639 line was wrong. §10.2's "final head `e9aa84c`" was also stale — `5a664e7`
> (review fixes) and `f4853d9` (evidence) came after it.

### 11.1 What independent review found

Four reviewers (HR/offboarding lifecycle, multi-entity authorization, database/transaction/idempotency,
and an SDET on test quality) examined the committed diff. **Two of them independently found the same
Critical**, which is the strongest signal in this round.

| # | Sev | Finding | Disposition |
|---|---|---|---|
| A1 | **Critical** | **A Draft — or even Cancelled — settlement pinned the prior period's separation live forever.** The reactivation carve-out excluded any offboarding named by ANY `EmployeeFinalSettlement` row, with no status filter, while the repo's own accrual predicate is `Approved/Disbursing/Paid`. One `/final-settlement` preview persists a Draft. So: terminate → preview → rehire (carve-out skips the cancel) → terminate again → the live lookup finds the OLD record, creates nothing, and the NEW service period is settled against the **OLD last working day**. Worked example: 26,500 SAR underpaid on a 10,000 SAR basic. The new unique index meant no correct second separation could be created either, and a *Cancelled* settlement pinned it permanently — so the documented remedy could never release it. **Found independently by the HR and DB reviewers.** | **FIXED.** Only `Approved/Disbursing/Paid` protect a separation. An accrued-but-open one now **refuses the reactivation** with an actionable message rather than silently reusing the record later. |
| A2 | **Critical** | **Re-terminating a completed, paid leaver minted a second full-service gratuity.** Round 1 removed the `oldStatus` condition so the migrated-leaver backlog could be remediated — but that left the command unable to distinguish "Archived with NO separation" (the backlog it exists for) from "Archived with a COMPLETED, PAID separation" (every properly offboarded leaver). The commit's own advice is to re-run terminate across the archive; doing so minted a separation dated today, and `/final-settlement` measures service from `JoiningDate` with its settle-twice guard keyed on `OffboardingId` — 91,500 SAR accrued a second time over a period already paid, per employee. It also dropped ex-employees into the next live payroll run as leavers owed a prorated wage. | **FIXED.** A new separation is created only if a new period was actually served (the status being left was occupying), or there is no completed separation at all — which preserves the backlog case exactly. |
| A3 | **High** | **The claimed one-transaction atomicity was false for the payroll footprint.** The brief requires the status transition, separation, history *and payroll-footprint* changes to be atomic; they ran as five separate transactions. A client disconnect (no crash needed — the cancellation token is threaded throughout) left an employee Terminated, with a live separation, and still `WpsEligible = true`, which is the only gate `WpsSifValidator` applies. | **FIXED.** The cascade is split into a stage-only half and staged into the status transition's own `SaveChanges`. The saving wrapper is kept for the soft-delete and offboarding-complete callers. |
| A4 | **High** | **Nine batch-specific routes on `PayrollController` defeated D3's stated outcome from a different URL.** D3 round 1 authorised `BankConfirmationsController` and stopped. But `PayrollPaymentBatch`, `PayrollPaymentRecord`, `FinanceGlEntry` and `SIFFileRecord` are `ITenantOwned` only. `GET payment-batches/{id}/records` returned a sibling entity's per-employee amounts, WPS/bank references and **full unmasked IBANs**; `GET payment-batches` listed every entity's batch totals; `settle/reverse` reversed another company's net-pay settlement GL. The run-status guards were no defence — they read the company-filtered `PayrollRun`, so cross-company the run came back null and `run?.Status == "Voided"` **failed OPEN**. Round 1 recorded this as R7/Medium; that understated it. | **FIXED.** All seven single-batch routes plus the list now derive the entity through the same rule. |
| A5 | **Critical (tests)** | **"Transaction failure cannot leave status and separation inconsistent" had no coverage.** The test forced no failure — it ran one *successful* terminate and asserted both rows exist, a strictly weaker duplicate of another test. Meanwhile the product has a genuinely reachable mid-operation throw (`NormalizeSeparationType`, after the status and both history rows are staged) that the suite tested as a pure function instead. | **FIXED.** Driven through the real command, asserted on a genuinely fresh context. |
| A6 | **Critical (tests)** | **D2's positive claim was unfalsifiable.** The file never created a single `FinanceGlEntry`, so `BuildAsync` short-circuited at 422 Empty and "a group caller may include unattributed rows" was asserted only as "was not refused" — it would still have passed if the flag had been fixed by making it a **no-op for everyone**. | **FIXED.** A real two-row ledger (one attributed, one NULL-company) with exact counts and amounts asserted on both sides. |
| A7 | High | `Reject` — a required D2 entry point performing a cross-entity write — had **zero** tests; deleting its guard broke nothing. | **FIXED.** Test added. |
| A8 | Medium | The `List` route still advertised group-created `includeUnattributed` artifacts to a company-scoped caller. `Summary` exposes `IncludeUnattributed`, `TotalDebits` and `TotalCredits`, handing back in aggregate what Get/Download/Confirm/Reject refuse in detail. | **FIXED.** Non-group callers no longer see flagged artifacts. |
| A9 | Medium | The unique-index migration had **no guard against pre-existing duplicates**. `CREATE UNIQUE INDEX` aborts with 23505 if any tenant already holds two live separations — a blocked deploy, or a host that will not boot where `RunMigrationsOnStartup` is true. | **FIXED.** The migration cancels older duplicates first, keeping the newest — the same row the service's own lookup treats as live. |
| A10 | Medium | Concurrent terminates produced an unhandled **500**, and `/terminate` had no exception handling at all, so the deliberate vocabulary and date refusals surfaced as `internal_error`. | **FIXED.** 409 `separation_already_open` on the index violation; 400 on the deliberate refusals. |
| A11 | Medium | The audit recorded `request.SeparationType` (`"ARTICLE80"`) while the row stored the canonical `"Article80"`, and the reactivation cancelled separations **with no audit at all**. | **FIXED.** One normalized value feeds both; cancelled ids are audited. |
| A12 | Medium | The vocabulary admitted `"Abscondment"`, which `NormalizeTerminationReason` does not recognise and therefore pays a **full Art. 84 award** — the exact case the doc-comment cites as its reason for being closed. | **FIXED.** Removed. |
| A13 | Medium | The reactivation **overwrote** the original separation `Reason`, destroying why the person left. | **FIXED.** Appended. |
| A14 | Low | A `[Required]` non-nullable `DateOnly` binds an omitted value to `0001-01-01`, minting a separation that settlement rejects while the live lookup makes the corrected retry a no-op. | **FIXED.** A last working day before the joining date is refused. |

**Recorded, deliberately not fixed** (each is real, and each is outside the three authorized defects —
expanding into them without their own review is the larger risk):

| # | Sev | Finding | Why recorded |
|---|---|---|---|
| B1 | High | `GetEntityScope()` resolves with `strictMode:false` and ignores `NarrowTo`, while the DB layer is strict in production. | Round 1's R6. A shared auth helper used app-wide. Reviewer confirmed reachability is bounded: every issuance path emits the v2 claim. |
| B2 | High | `NoticePeriodDays = 0` on a direct termination silences the Art. 75/76 payment-in-lieu prompt, which is gated on `> 0`. A 60-day notice at 10,000 SAR is 20,000 short, and nothing says so. | Real, and a statutory-advice change. The brief scopes D1's derivation to last working day and separation type; a correct notice figure is a product decision, not a guess this code may make. |
| B3 | High | `PATCH /status` (`employees.write`) still supplies the effective date that becomes the last working day, unbounded into the future, while `/terminate` is `employees.approve`. | Partially mitigated (dates before joining are now refused). A future-date bound is a policy choice. |
| B4 | Medium | `already_imported_to_batch` returns `priorBatchIds` probed tenant-wide, disclosing sibling-entity batch ids. | In D3's path. Its chaining payoff is removed by A4's fix; the remaining disclosure is an id. |
| B5 | Medium | `GET /finance/gl/control-accounts/health` returns tenant-wide and NULL-company aggregate GL balances to a company-scoped caller. | A different endpoint and a different feature from the `includeUnattributed` flag. |
| B6 | Medium | Soft delete and bulk deactivate still produce no separation, and soft delete makes settlement permanently impossible. | A status-vocabulary problem wider than D1. |
| B7 | Medium | Reactivation racing termination can still yield **zero** live separations. A unique index forbids duplicates, not absences. | Needs a locking strategy (advisory lock or concurrency token), not a constraint. |
| B8 | Medium | `Article80` changes nothing in the UAE and Qatar packs — only KSA reads the termination reason. | Pre-existing country-pack asymmetry. |
| B9 | Low | Audit still writes outside the business transaction. | Round 1's R11; a pattern across the service. |

### 11.2 Verification

Every number below was produced in this session against an **isolated export of the committed tree**,
because the working tree's 153 in-flight files inflate the count by ~94 tests and would not be what CI sees.

| Check | Result |
|---|---|
| Targeted D1 / D2 / D3 | **22 / 14 / 13 passed** |
| Full backend suite (committed tree) | **1674 passed, 0 failed, 0 skipped (from 1648)** |
| EF model drift | **None** — "No changes have been made to the model since the last migration." |
| Frontend typecheck (`tsc --noEmit`) | **pass** |
| Frontend production build (`next build`) | **pass** — compiled successfully, 59 static pages |
| Isolation / bypass / PII-logging guards | **pass**, within the full run |
| Raw-bypass ratchet | **unchanged** — `ScopedBypass` is the sanctioned helper, not a raw `.IgnoreQueryFilters()` |

**Negative-tested, not trusted.** Defeating the D1 service-period guards fails exactly the 4 tests written
for them; defeating the 7 `PayrollController` batch guards fails 8 of the 13 D3 tests. A guard whose
removal breaks nothing is not a guard, which is how A7 was found in the first place.

`PaymentBatchScopeTests.EveryBatchSpecificRoute_CallsTheSharedCompanyGuard` is a source-level ratchet: it
scans both controllers and fails if any route whose template carries a batch id ships without the check.
The failure mode here is a *new* endpoint added later — which is exactly how the nine routes in A4 came to
be tenant-only.

### 11.3 One implementation, not two

The batch authorization rule lives once, in `PaymentBatchScopeExtensions.PaymentBatchScopeErrorAsync`.
`BankConfirmationsController`'s private helper was rewired to delegate to it rather than keeping a second
copy that could drift.

### 11.4 A landmine the next session must know about

The working tree **deletes** the committed migration `20260816200703_AddLiveSeparationUniqueIndex.{cs,Designer.cs}`
and carries an untracked `20260816200334_AddLiveSeparationUniqueIndex.{cs,Designer.cs}` pair with the **same
class name**. If both ever land in one build it is a duplicate-partial-class compile failure. That work was
left untouched. Whoever commits the in-flight branch must reconcile the two, and must carry this round's
`PayrollController` and `EmployeeManagementService` changes forward — those two files exist in the commit as
`HEAD + this change`, while the working tree holds `HEAD + foreign work`, so a wholesale commit of the tree
would revert the D3 batch guards.

### 11.5 Round 3 — independent review of round 2's own changes

Round 2's changes were themselves put through two independent reviewers before this was called done. They
found **two Criticals in my own fixes**, which is the reason this section exists rather than a claim that
round 2 closed it.

| # | Sev | Finding | Disposition |
|---|---|---|---|
| C1 | **Critical** | **Closing the prior period was gated on `request.Status == "Active"`, but occupancy is {Active, Offboarded, Suspended}** — and the establishment guard in the *same method* already charges a seat for any transition into occupancy and calls it a reactivate. The two disagreed about what a rehire is, and the gap was two ordinary PATCHes wide: `Terminated → Suspended` (branch skipped) `→ Active` (oldStatus now occupying, skipped again) left the old separation live, so the next termination reused it and settled the NEW period against the OLD last working day. The exact underpayment round 2 claimed to have closed. | **FIXED.** Any return to occupancy closes the prior period. |
| C2 | **Critical** | **"Was a new period served?" cannot be read off `oldStatus`.** `Inactive` is a product-supported bulk-deactivate target and is not occupying, so a REHIRED employee deactivated before being terminated was misread as "period already closed" and got **no separation at all** — the original D1 dead end, restored for precisely the population that had been rehired. `Draft` and `Invited` behave the same. | **FIXED.** The status history is the evidence: a period was served if the employee returned to an occupying status after the last completed separation's last working day. |
| C3 | High | **Narrowing the block to ACCRUED settlements overshot.** A Draft or PendingApproval settlement had its offboarding cancelled out from under it — which `OffboardingController.Cancel` explicitly refuses (`final_settlement_live`). `ApproveFinalSettlement` has no offboarding precondition, so the approver would post an accrual against a separation the system says never happened, and the settlement run admits employees whose status is `Active` — disbursing an exit payment to somebody presently employed. | **FIXED.** A LIVE settlement (anything not Cancelled, the domain's own `IsLive`) blocks and says so. A Cancelled one still releases, which was round 2's whole point. |
| C4 | High | **`unattributedExcluded { count, total }` handed a company-scoped caller the size and value of the tenant-wide NULL-company pool** — on `Preview`, `Create` and `Reconciliation`, with no flag set and no artifact needed. One request per period walks the whole residue. This is the aggregate form of exactly what D2 refuses in detail. | **FIXED.** The magnitude is group-only; a company caller still learns rows *were* excluded, which is what they need to escalate. |
| C5 | High | **`GET /reconciliation` re-listed the flagged artifacts `List` now hides**, with their totals, line/entry counts, hashes and ids — the round-2 `List` fix was applied in one place and the reconciler reads the same table. | **FIXED.** Same rule, passed in explicitly: the reconciler is a service with no principal of its own, so there is deliberately **no default** parameter to inherit a fail-open. |
| C6 | Medium | A company-scoped caller could **supersede** a group-created flagged artifact via `Create` — a write to a row they are refused sight of, visible only in the audit log. | **FIXED.** The supersede probe honours the same predicate as the reads. |
| C7 | Medium | The `DbUpdateException` → 409 catch landed on **`Activate`** (which only cancels separations) instead of **`PATCH /status`** (which reaches the identical separation insert). Concurrent PATCHes still 500'd; Activate mislabelled generic failures. | **FIXED.** Moved to the endpoint that actually inserts. |
| C8 | Low | The docblock claimed an unauthorised caller "must not even get their payload consumed". For a `[FromBody]` action ASP.NET Core has already model-bound the body before the action runs — no action can change that. Two routes also validated the bound body *before* the entity check. | **FIXED.** Guard moved ahead of body validation; the docblock now states the limit honestly and names where the stronger claim does hold. |
| C9 | Low | A comment justified the sanctioned bypass by saying the ambient filter "would silently fail OPEN for the null-company case". It would fail **closed**. | **FIXED.** The bypass is justified on its real grounds — the run's `CompanyId` is the input to the decision, and 403 must not be conflated with 404. |

**And a finding against my own test.** The round-2 source ratchet bounded each action at the next `[Http`
attribute, so for the **last** action in a file the region ran to end-of-file and swallowed the private
helper whose name it greps for: deleting the real guard in `BankConfirmationsController` left it green.
It now brace-matches the actual method body and discovers controllers across the tree. Fixing it exposed a
second bug of the same family — the attribute line's own braces, from the route template `{batchId:guid}`,
stopped the matcher dead — which is why the test now also asserts a **minimum discovery count**, so a scan
that silently stops matching cannot read as success.

**One test was testing the wrong thing, and said so.** The round-2 draft-settlement test shared a single
`DbContext` across what are really three separate requests. A refused reactivation throws *after* the
status change is staged, so the next `SaveChanges` on that context committed the refused transition. It
now uses one context per request, and asserts explicitly that nothing of the refused call survived it.

### 11.6 Round 3 results

| Check | Result |
|---|---|
| Targeted D1 / D2 / D3 | **65 passed** across the four suites |
| Full backend suite (committed tree) | **1682 passed, 0 failed, 0 skipped** (1648 → 1674 → 1682) |
| EF model drift | **None** |
| Frontend typecheck | **pass** (no frontend file changed in round 3) |
| Negative-tested | defeating the round-3 D1 guards fails exactly the 5 tests written for them |

### 11.7 The top carry-over, stated plainly

**A rehire still pays the whole original tenure a second time.** `/final-settlement` measures service from
`Employee.JoiningDate` (`ServiceStartDate = DateOnly.FromDateTime(employee.JoiningDate)`) and its
settle-twice guard keys on `OffboardingId`, and nothing anywhere subtracts a previously paid settlement. So
an employee who joined 2016, was settled in 2022 for 3.5 months of gratuity, was rehired, and leaves again
in 2026 is settled for **10.65 years** — roughly 60,000 SAR overpaid on a 10,000 basic.

This is **not** introduced by D1: it is reachable through the canonical `POST /api/offboarding` path just
as it is through terminate, and it lives in the settlement engine's service-period computation, not in the
separation lifecycle. It is recorded here rather than fixed because the correct behaviour is a statutory
and product determination (does a rehire start a new service period for end-of-service purposes, and is
the prior award offset or the clock simply reset?), and changing `ServiceStartDate` blindly would move
money on **every** settlement, including single-period ones, in a direction nobody has signed off.

It is the single most valuable thing on the carry-over list and should be the next defect opened.
