# Night Shift Progress — 2026-08-26

Wave 1: security gate, observability, durability, zero-state test foundation.

> Ledger convention: every entry records what was **executed and observed**, not what was intended.
> `IMPLEMENTED — CI PENDING` never becomes `COMPLETE` without a green run on a named SHA.

---

## Checkpoint 1 — 04:30Z — Phase A1, baseline verification

**Branch:** `wave0/engineering-baseline` (read-only inspection) · **main SHA:** `f23a0009`

**Completed**

- Repository remote confirmed: `Kode-Kinetics/zayra-ai-workforce` (public).
- `origin/main` = `f23a00094992fa1387818f4640b4d50d3dad237b` — **matches the expected baseline**.
- Wave 0 merge commit `f23a0009` (PR #43) confirmed merged.
- Post-merge CI: **9/9 jobs success**.
- Post-merge CodeQL: **Analyze (csharp) success, Analyze (javascript-typescript) success**.
- Production migration `20260816200703_AddLiveSeparationUniqueIndex` confirmed applied (`Done.`, no error).
- `/health/live` → 200. `/health/ready` → 200, database ok (67 ms), 55 tenants / 5 active.

**Defect found — P1, deployment truth**

Production is running **older code than `main`**. Committed `main` returns a `commit` field on
`/health/live` and `pendingMigrations`/`queues`/`workers` on `/health/ready`; production returns neither.
Root cause: `deploy-backend` fires a webhook and exits `0` without waiting for or verifying the deploy, so
a green deploy job carries no information about whether the release actually shipped. Recorded as the
incident in `docs/REPOSITORY_AND_DEPLOYMENT_GOVERNANCE.md` §5.

**Note:** the live system reports **55 tenants (5 active)**. Treated as production data throughout — no
writes, no test tenants, no destructive probes against it.

**Next:** Phase A2 worktrees, A3 branch protection, A4 production gating.

---

## Checkpoint 2 — 04:40Z — Phases A2, A3, A4 implemented

**Branch:** `wave1/deployment-governance` @ base `f23a0009`

**Completed**

- **A2.** Pruned five stale worktree admin entries left by the repository directory rename (they pointed
  at `/Users/zackkhan/Downloads/zayra-ai-workforce`, which no longer exists). Only dead metadata was
  removed — all four `worktree-agent-*` branch refs are preserved and no working files were touched.
  Created clean worktrees from `origin/main`:
  `wave1/security-scope-browser-gate`, `wave1/deployment-governance`.
  The primary checkout retains **153 modified tracked files** of unrelated in-flight work, untouched.

- **A3. Branch protection — ACTIVE and PROVEN.** Ruleset `main-protection` (id `21530403`), zero bypass
  actors, 8 required status checks, strict up-to-date policy, force-push and deletion blocked,
  conversation resolution required, stale approvals dismissed.
  **Proof:** an admin-authored direct push to `main` was rejected —
  `Changes must be made through a pull request` / `8 of 8 required status checks are expected`.
  Probe commit discarded; worktree restored to `f23a0009`.
  *Deliberate deviation:* `required_approving_review_count: 0` (single maintainer). Recorded as
  release blocker G-3.

- **A4. Production deployment — GATED.** Environment `Production` configured with required reviewer
  `kodekinetics79` and a protected-branches-only deployment policy. `migrate-backend` and
  `deploy-backend` now declare `environment: production` and cannot run without approval.
  `deploy-backend` additionally gained: a rollback anchor (records the currently-deployed commit before
  releasing), and a **verification step** that polls `/health/live` for the expected SHA and then requires
  `/health/ready` to be 200 — failing the job on mismatch or 15-minute timeout. `build-image` now records
  the immutable image digest.

**Tests executed:** YAML parse of `.github/workflows/ci.yml` — OK, 9 jobs, gated jobs
`['migrate-backend', 'deploy-backend']`. Live protection probe — rejected as designed.

**Review findings:** none yet (independent review runs at the Gate 0 boundary).

**Open blockers:** G-1 no staging; G-2 built image is not the deployed artifact; G-3 approvals = 0;
G-4 production secrets not environment-scoped; G-5 Render deploy status unobservable from CI;
G-6 Chrome gate not yet a required check.

**Current activity:** opening the governance PR.
**Next:** Phase B1 — one authoritative entity-scope resolver, on `wave1/security-scope-browser-gate`.


---

## Checkpoint 3 — 05:00Z — Phase A complete, Gate 0 B1/B2 implemented

**Branch:** `wave1/security-scope-browser-gate` @ base `5583b12` · **main SHA:** `5583b12`

**Completed**

- **PR #44 merged.** All 8 required checks + both CodeQL analyses green. `main` `f23a0009` → `5583b12`.
  Branch protection and the production environment gate are now in force **in the workflow**, not just
  in the API. The resulting push to `main` runs the new workflow, so `migrate-backend` and
  `deploy-backend` sit awaiting production approval. **They were not approved** — no production deploy
  this shift.
- Annotated tag `wave0-engineering-baseline-2026-08-26` pushed on `f23a0009`.
- **B1 — authoritative resolver.** `IRequestEntityScopeResolver` + immutable `RequestEntityScope`.
  All three previously divergent call sites now read one cached per-request decision. Architecture
  ratchet added. The claim parser was deliberately NOT rewritten — it already failed closed; the defect
  was in the callers.
- **B2 — payment-batch matrix** written and verified route by route.

**Tests executed**

| Suite | Result |
|---|---|
| `RequestEntityScopeResolverTests` | **25 passed** |
| `EntityScopeResolutionRatchetTests` | **2 passed** |
| `QueryFilterBypassRatchet` + `BypassLint` | **8 passed** |
| Full backend suite | **1709 passed, 0 failed, 0 skipped** (from 1682) |
| EF model drift | **None** |

**Defect found and fixed — P1, live in production**

Device attendance ingest matched **zero employees**. `/api/attendance/ingest` is `[AllowAnonymous]`;
`Employee` is `ICompanyScopedOperational`; the company clause of the read filter derives from the
request principal and is independent of the system-scope bypass. Under strict mode (forced in
Production) an anonymous request resolves to an empty company scope, so `ResolveEmployee` returned null
for every punch — every punch unmatched, `processedDays` 0, no daily record ever created from a device.
Raw events ARE persisted, so the data is recoverable by reprocessing. Fixed; bypass ratchet 2 → 4.

**Deliberately NOT closed**

Invariants 15/16 (system scope must be explicit, not the default for any unauthenticated request) are
**PARTIAL**. A bounded blast-radius scan established that tightening the gate today breaks: login,
refresh, forgot/reset password, invitation acceptance, tenant AND platform MFA verification, all SCIM,
SAML/OIDC metadata (500 + unique-index pollution), pre-auth localization, device ingest — and silently
forks the audit hash chain on every anonymous-origin audit row. Correct order is **wrap, then tighten**.
Wrap list: `AuthService` (7 pre-auth methods), `MfaService` (5), `EnterpriseIdentityService` (7),
`AuditService` chain-tail read, `SmtpEmailService` ambient config load, `AttendanceService`
`ProcessEmployeeDay` reads, `TenantAdminController` localization, `ProductionReadinessEvidence` SMTP probe.

**Open blockers:** G-1…G-6 (governance gaps), PB-1…PB-3 (batch matrix), invariants 15/16 partial.

**Current activity:** pushing Gate 0 B1/B2 and opening its PR.
**Next:** B3 real Chrome role/isolation gate.


---

## Checkpoint 4 — 05:45Z — Gate 0 B3 complete, real Chrome gate green

**Branch:** `wave1/security-scope-browser-gate` · **main SHA:** `5583b12`

**A4 PROVEN, with evidence.** Merging PR #44 triggered the new workflow on `main`:
`Apply DB Migrations (deploy gate)` entered state **`waiting`**, with 1 pending deployment on
environment **Production** requiring approver `kodekinetics79`. **Not approved** — no production
deploy this shift. Production migration and deploy now require a human, which is what A4 asked for.

**B3 — real Chrome role and isolation gate.** A full real stack was stood up locally: Postgres 16,
the real backend with `EnterpriseGroupSeeder` (4 tenants / 15 companies / 58 users), and a
**production** `next build` + `next start`. No mocked business APIs, no fixtures, no demo data.

| Run | Result |
|---|---|
| Pre-existing `group-company` suite (baseline) | 16 passed, 2 failed, **9 skipped** |
| New `playwright.security.config.ts` gate | **22 passed, 0 failed, 0 skipped** |
| Full backend suite | **1709 passed, 0 failed, 0 skipped** |

**Two of my own tests were passing for the wrong reason — caught before claiming done.**

1. UI tests navigated to `/employees`, which **does not exist** (the route is `/people`). One failed
   for the wrong reason; the other **passed against a 404 page**, "proving" no sibling-company data was
   visible on a page that rendered nothing.
2. Setup wrote tokens to `accessToken`/`token`; the app reads **`zayra_access_token`** /
   **`platform_access_token`**. Every browser context was silently **anonymous**, so a suite of
   "cross-company data is not visible" assertions passed because *no* data was visible.

Both fixed. The company-isolation assertion is now load-bearing in both directions and was
negative-tested: substituting a group session into the company-scoped slot fails the gate with
*"the employees page rendered a sibling company's codes"*.

**Product changes made to support it (not test-only hacks)**

- Platform-owner bootstrap decoupled from demo-data seeding. It previously lived inside the demo block,
  so Production and dedicated deployments — where demo seeding is correctly refused — had **no
  supported way to create the first platform operator**. Now inert unless `PLATFORM_ADMIN_PASSWORD` is
  supplied, a no-op once any platform user exists, and on Production additionally gated behind
  `PLATFORM_ADMIN_BOOTSTRAP=true`.
- Removed a Playwright artifact (`frontend/test-results/.last-run.json`) that was tracked on `main`,
  and gitignored `node_modules/`, `test-results/`, `playwright-report/`, `blob-report/` under `frontend/`.

**Rate limiting respected, not raised.** The old suite trips `429` because it logs in per spec file.
The limiter (10/60s) is left exactly as production runs it; the nine roles authenticate once each,
paced, and reuse sessions. Storage states and tokens are gitignored and excluded from CI artifacts.

**CI:** `Chrome Security Gate (roles + isolation)` job added — real Postgres service, real backend with
seed, production frontend build, `retries: 0`, failure evidence uploaded with `.auth` excluded.

**Gaps recorded, not glossed:** GAP-B3-1 (`Manager`/`Employee` not seeded, so those journeys are absent
rather than faked), B3-2 (mid-session revocation needs a contract), B3-3 (batch pages API-covered only),
B3-4 (impersonation/break-glass not end-to-end), B3-5 (gate not yet a required check — governance G-6),
B3-6 (console-error assertions not global).

**Current activity:** pushing B3 and updating PR #45.
**Next:** Phase C (G3 observability) in a separate worktree.


---

## Checkpoint 5 — 06:10Z — Chrome gate green in CI; G3 slice 1 delivered

**Branches:** `wave1/security-scope-browser-gate` (PR #45) · `wave1/observability-foundation` (PR #46, draft)

**PR #45 — Gate 0.** Backend Tests **pass**, Schema Gates **pass**, Frontend Typecheck/Build **pass**,
gitleaks **pass**, Dependency Scan **pass**, CodeQL javascript-typescript **pass**, and — the one that
matters — **`Chrome Security Gate (roles + isolation)` PASSED in CI in 6m4s**, running real Postgres +
real backend with the enterprise seed + a **production** frontend build + Playwright. CodeQL csharp
still running.

**PR #46 — G3 slice 1 (draft, stacked on Gate 0).** Correlation IDs, provider-neutral OpenTelemetry,
and the telemetry PII/cardinality guards. Full suite **1730 passed, 0 failed, 0 skipped** (from 1709).

**Deliberate omission worth naming:** EF Core OTel instrumentation was added and then REMOVED. The
package is prerelease-only and had dropped the `SetDbStatementForText` switch that disables SQL
capture; on this schema a captured statement carries salaries and IBANs. A beta dependency whose
redaction behaviour cannot be pinned is the wrong trade for query timings (GAP-G3-1).

**Production is still running stale code.** 75+ minutes after the Wave 0 deploy hook fired,
`/health/live` still lacks the `commit` field that `main` has emitted since `f23a0009`. The Render
deploy did not land. The A4 verification step added in PR #44 would have failed that build instead of
reporting green — which is precisely why it exists. **No remediation attempted: that requires a
production deploy, which is out of bounds for this shift.**

**Current activity:** awaiting independent review findings and CodeQL.
**Next:** fix any Critical/High review finding, then the morning report.
