# Backend-only PR #282 Release Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. One authorized source writer executes; independent compatibility and CI/security reviewers check the candidate.

**Goal:** Prepare a deployable, client-compatible backend correction on main, leaving the coupled PR #282/#287 feature graphs separate.

**Architecture:** Preserve the source-inventory baseline main `74d931129548cae5059ea34941dddd2cda631411` and reserve every source delta from `ad20a9ee584bd5c9c9640240189f000c939b78af` in an explicit ledger. Retain only caller-cancellation propagation/30-second timeout for the existing Qiwa adapter and fail-closed backend CI with build-once reuse. Preserve main business APIs, model/migrations, compiler policy and protected deployment dependencies. Publication base is `5d7d4dc666543fa7e2272e48b3adefa29c285ef3`, after validated maintenance PR #288 normally merged; rebase changed no backend runtime bytes.

**Tech Stack:** .NET 8, ASP.NET Core, xUnit, PostgreSQL/Testcontainers, Python stdlib, GitHub Actions.

**Spec:** Latest user authorization: isolated backend-only release after normal validated gates, preceded by the separately authorized narrow mobile dependency-security maintenance. No full #282/#287 merge or unverified deploy.

## Global Constraints

- Original PR/worktree stays untouched. Backend candidate is `release/backend-only-pr282` in its isolated worktree.
- No candidate frontend/mobile implementation or dependency changes. Validated maintenance PR #288 is inherited from publication main `5d7d4dc666543fa7e2272e48b3adefa29c285ef3`; it is not a backend delta.
- Main payroll processing/assignment, first-package/Grade restrictions, leave adjustments, setup/removal and company scope remain byte-identical.
- Main entity/model/migration sources, existing tests and security ratchets remain byte-identical. No copied designer snapshots or fabricated schema.
- Preserve every main required check name and non-backend job/step, including full Browser Pilot, independent Debug EF freshness/upgrade/drift, revision-marked Chrome, CodeQL/security/dependency scans and protected release needs.
- Preserve main compiler lifetime/GC policy: no `Directory.Build.targets` or compiler-knob experiment. Periodic wrapper adds diagnostics only.
- Only the coordinator publishes/merges/deploys after final scope review and exact-candidate required checks. No force push or protection bypass.

## Review Focus

1. No source 202 payroll/salary/leave proposal contract may reach existing main clients.
2. Caller cancellation propagates through token/push/status; independent HTTP timeout remains handled network failure.
3. Artifact/discovery/result corruption, absent/empty shards and cancelled lanes fail closed; all four integration shards/categories/coverage remain.
4. No unrelated source ratchet/allowlist, optional encryption/telemetry, frontend/browser behavior or production environment changes slip through extraction.
5. Main can advance only through reviewed maintenance changes before this release; final scope must bind the actual base and source SHA.

---

### Task 1: Exact scope and reservation ledger

**Files:** This plan; `docs/releases/backend-only-pr282-scope.json`; one-off `scripts/release/verify-backend-only-scope.py`; fixture tests `tests/ci/test_backend_release_scope.py`.
**Interfaces:** Exact Git trees → included/adapted/deferred path groups, explicit candidate allowlist and protected main method comparison.

- [x] Inspect main-client contracts and shared model/migration closure before extraction.
- [x] Write failing scope-verifier fixtures, then implement fail-closed path/method validation.
- [x] Record all 931 source-changed paths and run the concrete 23-path scope verifier against the current candidate/base.

### Task 2: Qiwa transport correction

**Files:** `Infrastructure/Qiwa/LiveQiwaApiAdapter.cs`; only the named-client timeout expression in `Program.cs`; new `Zayra.Api.Tests/QiwaTransportBoundaryTests.cs`.
**Interfaces:** Existing `IQiwaApiAdapter` methods unchanged; propagate caller cancellation, retain handled network-timeout results, keep live-adapter activation policy unchanged.

- [x] Reproduce swallowed caller cancellation using actual production source in a bounded native probe.
- [x] Retain all three source cancellation catches; set existing named client timeout to 30 seconds.
- [x] Run all six operation/cancellation/timeout regressions against linked actual production source; require full application checks in candidate CI.

### Task 3: Fail-closed CI and release validation

**Files:** Backend job block in `ci.yml`, independent C# manual build in `codeql.yml`; `scripts/ci/*`, `tests/ci/*`, `coverage.runsettings`; release evidence.
**Interfaces:** Exact-checkout/run/attempt Release handoff and unit+four-shard TRX evidence; independently built schema/Chrome/CodeQL.

- [x] Port validated guard regressions before implementation and observe the expected red run on main.
- [x] Extract complete artifact/discovery/TRX/monitor dependencies; retain only resource/privacy assertion from the source-specific compiler-policy test.
- [x] Replace only main backend job block, add preflight regressions and wrap the same Chrome build. Retain every other main lane/step and deployment protection.
- [x] Use independent C# CodeQL manual application build without analysis exclusions or Release artifact reuse.
- [x] Run 29 Python regressions, actionlint, diff/scope checks and six bounded native cases; submit the concrete candidate to both independent reviewers.
- [x] Rebase on validated maintenance main, refresh ledger, and deliver one concrete commit-ready candidate.
- [ ] Coordinator obtains exact-head candidate CI and normal protected release gates before merge/deployment.

## Complete groups deferred intact in PR #282

**CSV:** Source whitespace-aware unescape changes raw main import `'  =note`; making guard encoding reversible also changes raw apostrophe chains such as `''=note`. Native counterexamples reproduced both differences. No versioned CSV protocol is introduced here. All common CSV/helper/controller/test changes were restored to main; bank/WPS/ERP formats remain untouched.

**Compression:** The source auth-prefix policy misses credential responses outside those prefixes (CMI5 authentication token, ERP plaintext key, attendance device key, invitation/reset links). A global HTTPS compression feature needs a complete protected-response policy; no inference from `no-store` alone is accepted. Provider, middleware/registration and compression tests were removed from this candidate. Main HTTP security behavior remains unchanged.

**Coupled business/schema/client features:** Background payroll 202 jobs; manual salary/first-package staging changes; large leave-adjustment proposals; payroll rules and custom bank templates; setup/removal/code reuse; below-floor acknowledgement UX; device-ingest/bridge schema; audit-trigger/context graph; field-encryption/session-cache/optional observability and frontend/mobile product features remain in the source PR with their complete dependencies. The ledger lists exact paths, including shared files whose unrelated source hunks are reserved.

## Final runtime extraction allowlist

- `backend-dotnet/Zayra.Api/Infrastructure/Qiwa/LiveQiwaApiAdapter.cs` — three caller-cancellation catches only.
- `backend-dotnet/Zayra.Api/Program.cs` — existing named Qiwa HttpClient's 30-second timeout only.

No application package, controller, entity, DbContext, migration, CSV or HTTP middleware changes are included.
