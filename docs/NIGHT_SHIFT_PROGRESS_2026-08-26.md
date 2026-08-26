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
