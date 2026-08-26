# Wave 1 — Operational Durability Foundation

**Prerequisite:** Wave 0 protected (PR #43, commits `9d7e1d9` · `cf3f9d7` · `6acbad7` · `7d14b06`).
**Scope:** the operational floor beneath the product. Wave 1 does **not** add HR capability.
**Rule:** Benefits and other feature modules stay closed until G1–G5 are complete.

---

## 1. Sequencing (revised by independent review)

The original Wave 0 ordering was challenged and **changed**. Three corrections, each with a reason:

| Change | Why |
|---|---|
| **G3 observability moves before G2** | The baseline's own justification for G3 was "this is what makes every later wave debuggable" — and then sequenced it *after* the hardest wave. Building durable job execution with `ILogger` and no correlation ID is how a two-week job becomes three. |
| **G7 (WPS/Mudad acceptance) starts day 1 as a lead-time track** | It is the only board item that cannot be bought with engineering hours. Bank/Mudad sandbox onboarding is weeks of counterparty calendar and ~zero engineering days. Starting it in week 8 adds four weeks to the pilot no matter how fast G1–G4 go. Owned by BD, not engineering. |
| **G6 merges into G1; DataProtection merges into G4** | Residency has no independent engineering content — it is one assertion in the same function that already validates bucket and keys. DataProtection key persistence was **lost** between `PILOT_ACCEPTANCE_SPEC.md` 6.4 and the Wave 0 register; it is a live recurring defect, not a hypothetical. |

### Track L — lead time, starts immediately, not engineering-blocked
- **L1 — G7 WPS/Mudad sandbox acceptance.** Submit one real file; record the acceptance reference against the batch, matched to the stored SHA-256.
- **L2 — Two scoping questions to the design partner, before scope is signed.** Both are free and both change the plan:
  1. **Does the group have a Kuwait, Oman or Bahrain entity?** Only SAU/ARE/QAT packs are registered. That entity's payroll 422s and it silently drops out of the pilot — discovered in week 3, not at scoping.
  2. **Does the group need multi-currency consolidated reporting?** Per-entity GL in its own currency works; there is **no FX translation**, so a group figure spanning SAR and AED is meaningless. If yes, multi-currency is a P0 and the baseline is wrong by four priority levels.

### Track P — engineering, in this order
1. **G3** — correlated logs, metrics, traces
2. **G2** — durable job execution
3. **G4** — backup, tested restore, **DataProtection key persistence**
4. **G1 + G6** — durable, region-pinned object storage
5. **G5** — permanent CI/release gates

---

## 2. G3 — Observability *(first, because everything after it depends on it)*

**Instrument:** tenant provisioning, storage, payroll, jobs, notifications, database, authorization.

**Acceptance.** From one failed payroll or document operation, an operator identifies within ~60s:
request · tenant/company · user or system actor · job/run id · trace id · failure point · retry state ·
affected records · correct recovery action.

**Must not log:** payroll values, bank details/IBAN, identity documents, access tokens, employee PII.

> **Known violation to fix here:** `SmtpEmailService` logs the full recipient address plus subject at
> Information on every send, while the same wave masks the destination in the database. The database is
> scrubbed; the log aggregator is not. Fix before shipping any log sink, or the first thing observability
> does is centralise a PII leak. (Ledger D5.)

---

## 3. G2 — Durable job execution

Move payroll and other long-running critical operations off request-bound execution.

**Job model:** job id · tenant/company · idempotency key · state · attempts · lease + heartbeat ·
progress · retry · dead-letter · correlation/trace id · durable result/failure record.

**Adopt the `QiwaSyncWorker` shape** — DB-backed status, claim-before-work lease, retry — it is the
existing correct pattern.

**Proof required:**

| # | Must demonstrate |
|---|---|
| 1 | Browser refresh does not affect the job |
| 2 | API restart does not lose it |
| 3 | Worker restart releases/reclaims safely |
| 4 | Duplicate requests do not create duplicate payroll |
| 5 | Retry does not double-apply calculations, settlements, ledger entries or notifications |
| 6 | Concurrent payroll for the same company/period is controlled |
| 7 | Failed jobs are visible and safely re-drivable |
| 8 | Results stay deterministic and reconcilable |

> **Carry the Wave 0 lesson in.** The lease reclaimer's `Take`-before-`Where` bug proves a bounded batch
> is only a control if the bound is applied **after** the predicate and with a deterministic order.
> Every claim query in the job runner must filter → order → bound, and must have a test with more
> non-matching rows than the batch size. `ScopedBypass.SystemWide` is the enforced shape.

---

## 4. G4 — Backup, restore drill, DataProtection keys

**Drill must cover:** PostgreSQL · object storage · document hashes/references · job state · payroll
records · audit records · application startup after restore.

**Record:** actual achieved RPO, actual RTO, missing data, runbook corrections. Re-run the audit-chain
integrity check after restore and reconcile the last locked run to pre-drill totals.

**Also in scope (was lost from the register):** `AddDataProtection()` has no `PersistKeysTo`. On a
scale-to-zero dyno this already fails on **every** restart — previously encrypted integration secrets
become undecryptable and the failure is swallowed. One line plus a drill: encrypt → restart → decrypt.

---

## 5. G1 + G6 — Durable, region-pinned object storage

Provision S3/R2 in a **named approved GCC region**, set `Storage__Provider=s3`, and **remove
`Storage__AllowEphemeral`**. Add the region assertion to the same `ResolveAndValidate` that already
checks bucket and credentials — `Region` currently defaults to `auto` and is never validated.

**Acceptance:** upload through the real browser/API · bytes outside ephemeral disk · metadata, ownership,
hash, version, state and retention in the DB · **restart API and workers, document still available** ·
authorized tenant/company user can access · unauthorized company and cross-tenant access denied ·
short-lived access only, no permanent public URL · interrupted upload, missing object, orphan object,
deletion, legal hold, audit and restore paths all tested.

---

## 6. G5 — Permanent deployment/migration safety gates

CI today runs: backend tests, frontend **typecheck only**, secret scan, dependency scan, CodeQL.
Everything below is missing and was proven manually in Wave 0 — automate exactly those proofs.

| Gate | Status | Wave 0 proof to automate |
|---|---|---|
| EF model/migration drift | **missing** | `has-pending-model-changes` → none |
| Fresh Postgres deployment | **missing** | 53 migrations from zero |
| Upgrade from previous release | **missing** | 50 → 53 with data preserved |
| Fresh vs upgrade schema parity | **missing** | 4,295 cols / 835 idx / 322 constraints identical |
| App startup + DI validation | **missing** | boots `ready`, `ValidateOnBuild` + `ValidateScopes` clean |
| Frontend production build | **missing** (typecheck only) | `next build` passes |
| Browser smoke (Playwright) | **missing** | `e2e/` exists but CI never runs it |
| Query-filter bypass guard | present locally | ratchet + lint |

> **Fix the guards before trusting them.** Both isolation suites `return;` (pass) when path resolution
> fails, so a different CI output layout would disable them silently while reporting green. Make an
> unresolvable source root a **failure**, not a skip. The bypass lint also accepts a bare
> `// SYSTEM CONTEXT` with no reasoning, and the ratchet counts *lines*, not calls. (Ledger D14.)

---

## 7. Truthful feature states — required before pilot

| Feature | Required state |
|---|---|
| **SSO** | 501 with **no UI anywhere**, but the SAML metadata and OIDC discovery endpoints serve **valid documents** — an IdP admin can build a connection that can never complete. **404 those two routes.** Keep SCIM, which is real. |
| **SMS / WhatsApp / Push** | All three providers are `Null*` and honestly report `not_configured` — but `SetupPage.tsx` offers WhatsApp and SMS in the template channel dropdown with no signal they cannot deliver, and the honest `/deliveries` ledger is **never called by the frontend**. Filter the dropdown by an `IsConfiguredAsync` probe, or ship the deliveries view. |
| **Email** | Not "complete" — SMTP is unconfigured in `render.yaml` and sends are dropped. **CONFIGURE**, then prove. |
| **Mobile BFF** | 9 authenticated, unexercised endpoints including `payslips/{employeeId}`, with no client. **Feature-flag the route group off** for the pilot — it is attack surface with no user. |
| **WPS/bank/Mudad** | Remains externally unverified until L1 produces a real acceptance reference. |
| **Benefits** | Not "connect it". The model cannot represent GCC medical insurance and has no exit cascade, so a leaver stays enrolled and the premium keeps being paid. **Feature-flag off**; redesign later. |

---

## 8. Wave 0 carry-over defects — fix inside Wave 1

Full detail in `WAVE_0_CLOSURE_EVIDENCE.md` §9.4.

| # | Sev | Defect | Note |
|---|---|---|---|
| **D1** | **P0** | Terminate path creates an **unsettleable dead end** | Surfaced by Wave 0's own contract hardening. Also blocks migrating already-terminated staff from a prior system. Fix first. |
| **D2** | **P0** | `includeUnattributed=true` exposes tenant-wide GL rows to a company-scoped caller | Query-string flag validated nowhere against group level |
| **D3** | **P0** | `BankConfirmationsController` has **no company authorization**; the voided-run guard also fails open cross-company | |
| D4 | P1 | Revocation defects: checklist flag settable with no revocation; group over-revocation; fires at Complete not LWD; 30-min JWT window; terminate path revokes nothing | |
| D6 | P1 | Exactly-once conversion: no lineage column, divergent accept endpoints, double `FilledCount` | |
| D7 | P1 | EOSB parity holds only because every fixture sets `IsActive = true` | |
| D8 | P1 | `GCCComplianceSettings` read with no company/country/order — a group picks its labour law by row order | |
| D9 | P1 | Leave encashment: Draft policy authorises cash; pending requests reserve nothing | |
| D10 | P1 | No return-correction posting path; `ClosePeriod` has no gate, so the month closes over a known-wrong GL | |
| D13 | P2 | Remove `EmployeeModuleSchemaBootstrapper` / `MissingTableCreator` — live-registered MySQL runtime DDL against Postgres that swallows exceptions | Exactly the defect class W0-3 was raised for |

**Also:** `PILOT_ACCEPTANCE_SPEC.md` criteria 1.5/1.6/1.7 still read GAP and its sponsor sign-off block
still lists them as blocking, although POD-B2/C3 closed them. **That spec is the CFO-signed artifact.**
Update it, or the Wave 0 and POD work is invisible to the only reader who matters.

---

## 9. Definition of done for Wave 1

Wave 1 is complete when a payroll run for a 5,000-employee tenant survives a worker restart mid-run and
completes exactly once with no duplicate slips, GL entries, payments or notifications; a document
uploaded before a redeploy is byte-identical after it; a restore drill has been performed and signed
with measured RTO/RPO; an operator can diagnose an induced failure end-to-end from one trace; and every
gate in §6 runs on every PR.

**Wave 1 establishes the operational floor. It does not certify the HRM.** No capability may move to
VERIFIED COMPLETE until its zero-state journey has actually been run.


---

## 10. Wave 1 progress — evidence log

Recorded as it happens, so the plan and the state never diverge. **Nothing below certifies a
capability; each line is one proof performed.**

### 10.1 Browser smoke — the battery item Wave 0 skipped

Wave 0's battery listed browser smoke tests but never ran them. Run for the first time here,
against a real stack (Postgres 16 container, API on 5117 with demo seeding, `next start` on 5173):

**All 27 tests failed.** The cause was not the routes:

- `platformLogin` ran in `beforeEach`, so a 17-route sanity spec issued 17 logins within seconds.
- The API rate-limits platform login to `RateLimiting:PlatformLoginPermitLimit` (**default 5**) per
  window, so most were correctly rejected.
- Every failure was inside the login helper (`helpers.ts:33`), not on the page under test.

The limiter was doing its job; **re-authenticating per test was the defect.** A separate finding
surfaced first: the platform admin is bootstrapped from `PLATFORM_ADMIN_EMAIL` /
`PLATFORM_ADMIN_PASSWORD` **environment variables**, not from any seeder, and `platform_users` is
empty without them — so the suite cannot run at all unless CI provides them.

**Fixed for the platform half** with the standard pattern: `auth.setup.ts` authenticates once and
persists storage state; the chromium project depends on it and reuses the session.

| | Before | After |
|---|---|---|
| Platform smoke | 2 failed + 2 flaky, 2m | **14 passed, 11.8s** |

Every platform route was then proven to load clean — they were never broken.

**Still red:** the tenant half of `demo-sanity` calls `tenantLogin` per test against a 10/window
limit. It needs per-tenant storage states. Recorded as outstanding work for the browser-smoke gate
rather than left looking like a product failure.

### 10.2 D14 — the isolation guards passed vacuously

`BypassLintTests`, `QueryFilterBypassRatchetTests` and `StartupContractLintTests` resolved their
source root by walking up six directories and **`return`ed (passed)** when that failed. Any CI
output-layout change would have silently disabled every isolation lint while reporting green. They
now throw: an unresolvable source root is a build failure, never a skip.

### 10.3 D5 — PII in logs, fixed before instrumenting anything

`SmtpEmailService` logged the full recipient address and the message subject at Information on every
send, while the same wave masked that destination in the delivery ledger. Recipient is now masked;
the subject is dropped entirely (a template subject routinely carries the employee name or payroll
period). `PiiLoggingLintTests` scans for a raw recipient, phone or IBAN passed as a log argument.

**Negative-tested:** restoring the old call produces `SmtpEmailService.cs:72 — logs 'toAddress'`.

This deliberately lands **before** G3. Instrumenting a system that leaks PII into logs means the
first thing observability does is centralise the leak.

### 10.4 G5 — gates implemented

| Gate | Status |
|---|---|
| EF model/migration drift | **implemented** |
| Fresh Postgres deployment from zero | **implemented** |
| Upgrade from previous accepted release, with data, asserted non-destructive | **implemented** |
| Fresh vs upgrade schema convergence (columns + indexes) | **implemented** |
| App startup + DI validation (`/health/ready`, `pendingMigrations:0`, no captive scope) | **implemented** |
| Frontend production build | **implemented** — CI previously ran typecheck only |
| Backend tests · secrets scan · dependency scan | already present |
| Query-filter bypass ratchet | runs inside the backend test job |
| Browser smoke | **not yet gated** — blocked on §10.1 tenant-half work + CI credentials |

**Green in CI.** Full check set on head commit `4dc8bce` (run `31451611156` + CodeQL `31451611151`):

| Job | Result |
|---|---|
| Schema Gates (drift, fresh deploy, upgrade) | **pass** (4m21s) |
| Backend Tests (security gate) | **pass** (4m59s) |
| Frontend Production Build | **pass** (1m15s) |
| Frontend Typecheck | pass (38s) |
| Secret Scan (gitleaks) | pass (13s) |
| Dependency Vulnerability Scan | pass (31s) |
| CodeQL — Analyze (csharp) | **pass** (13m37s) |
| CodeQL — Analyze (javascript-typescript) | **pass** (1m23s) |
| Deploy / migration jobs | correctly **skipped** on a PR |

Every check green, SAST included — no job pending or skipped-by-failure.

The gate proved real work, not a trivial pass. From its log: it resolved the previous accepted
release on the merge base (`20260804221216_AddPayrollRecoveryArtifacts`), ran both paths, and
reported **"Schemas converge: 4295 columns, 834 indexes"** — matching the manual Wave 0 proof
(834 rather than 835 because the `GlJournalExport` company index was reverted after review).

**It took three runs, and each failure was a defect in the gate itself:**

1. The runner image ships no `psql`. Fixed by installing `postgresql-client` and dropping the
   manual `CREATE DATABASE`, which `ef database update` performs anyway.
2. `dotnet-ef` is pinned as a **local** tool (`dotnet-tools.json` at the repo root), so the global
   install was shadowed and every ef command failed with *"Run 'dotnet tool restore'"*.
3. **The drift check ran with `|| true`, so it swallowed that failure and reported "no drift".**
   The gate was green for the wrong reason — the exact class of false assurance this wave exists to
   remove. It now captures the exit code separately and fails on a non-zero result as well as on
   detected drift.

Finding (3) is the argument for building gates rather than trusting one-off manual proofs: a check
that cannot fail is not a check, and only running it revealed that.

### 10.5 Not started

**G1** (durable storage), **G2** (durable jobs), **G3** (observability instrumentation itself), and
**G4** (restore drill + DataProtection keys) are **not started**. The three P0 carry-overs from Wave 0
(D1 unsettleable terminate, D2 cross-company GL exposure, D3 unauthorized bank-confirmation writes)
are **not fixed**.
