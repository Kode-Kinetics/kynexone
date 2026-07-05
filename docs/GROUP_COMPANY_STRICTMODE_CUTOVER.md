# StrictMode Cutover & Production Environment Checklist

**Summary.** StrictMode (`EntityScope__StrictMode=true`) removes the last fail-open path in company scoping: tokens with no scope claims go from backward-compat group access to default-deny. It is flipped **by environment variable, never in code** (the code default stays `false`), and only after every live token has been re-issued with claim v2 — which happens automatically within one access-token lifetime of the Phase 2 deploy. Rollback is unsetting the env var. This doc is the operator-facing version of the checklist in `backend-dotnet/Zayra.Api/Migrations/PHASE2_CUTOVER_RUNBOOK.md`, plus the full production environment-variable checklist for the Group→Company feature set.

---

## 1. Why no forced logout is needed

- Access tokens live `AccessTokenMinutes` (30 min); refresh rotation re-issues claims from the database, so every session picks up v2 claims automatically within one access-token lifetime of the deploy.
- Impersonation/support tokens live 1 hour and **always** carry `entity_scope_strict` — they are strict independent of the global flag (`Application/Common/EntityScopeContext.cs:29-35`).
- Emergency invalidation (if ever needed): revoke the tenant's refresh tokens with existing platform tooling; users re-login and receive v2 tokens.

## 2. Cutover procedure

1. ☐ **Deploy live on all pods** — Phase 2+ build emitting v2 claims everywhere. Verify with an `/api/auth/me` roundtrip (token contains `entity_scope`).
2. ☐ **Wait ≥ 60 minutes** since the deploy — the longest-lived access/impersonation token (1 h) has expired; no pre-v2 token can still be live.
3. ☐ **CI green on `StrictModeCutoverTests`** — the suite runs the whole claim matrix with `strictMode:true`: normal login, impersonation, support, legacy-v1 tokens.
4. ☐ **Set `EntityScope__StrictMode=true`** on Render; restart. (Render env-var caution: the PUT env-vars API **replaces all** variables — patch, don't overwrite.)
5. ☐ **Smoke test:**
   - normal login lists employees;
   - an impersonation session works;
   - a claims-stripped request to a company-scoped endpoint returns empty/denied.
6. ☐ **Monitor denials** for at least one business day — watch the log strings in §4. A spike in `company_scope_denied` from normal user traffic means a token population or grant misconfiguration; investigate before leaving strict on.
7. ☐ **Rollback if needed:** unset `EntityScope__StrictMode` and restart. This re-opens fail-open **only** for legacy tokens — which no longer exist — so rollback is behaviorally safe; it simply restores the compat window.

After cutover there is **no fail-open path**: v2 tokens carry explicit scope; claim absence → deny; malformed claim → deny; impersonation/support carry the strict marker regardless of the flag. Follow-up engineering task: delete the legacy v1 parser path (`EntityScopeContext.cs:82-108`) and v1 co-emission (`EntityScopeClaims.Build`).

## 3. Production environment checklist

| Variable | Setting | Why |
|---|---|---|
| `ZAYRA_AUDIT_HMAC_SECRET` | **Required. Set a strong random secret.** | Keys the HMAC-SHA256 salary/sensitive-value change markers in audit/history snapshots (`Application/Common/SensitiveValueMask.cs`). Salaries are low-entropy, so an unsalted digest would be reversible by enumeration. **Without the secret the marker degrades to `[REDACTED]`** — safe, but audit trails lose change-detection ("did the salary change?") fidelity. Rotate via deployment config; never stored in the DB. |
| `EntityScope__StrictMode` | `true` — after the §2 procedure | Removes the claim-absence fail-open. |
| `ZAYRA_COMPANY_SCOPE_ASSERT` | `strict` — after one clean deploy cycle | `CompanyScopeBootAssertion` (`Infrastructure/Boot/CompanyScopeBootAssertion.cs`) is strict (throw ⇒ failed boot) everywhere **except** Production, where it logs errors only until this var opts in. Once a deploy cycle has proven clean, flip it so a mis-declared `CompanyId` entity can never silently ship unfiltered. CI/tests are always strict. |
| `CompanyScope__Backfill` | Leave at default (enabled) | Idempotent default-company backfill on boot (`Infrastructure/Boot/CompanyScopeBackfill.cs`); no-ops once data is clean and self-heals stragglers. Set `false` only as a deliberate backfill-rollback step. |
| `SEED_ENTERPRISE_TEST_DATA` | **NEVER set in production** | Gates the enterprise test-data seeder (three demo Group tenants with the universal password `GroupDemo123!x`). Test/staging only. |

## 4. Observability — log strings to watch

All emitted by the write-side guard in `Data/ZayraDbContext.cs` (`EnforceCompanyScopeOnWritesAsync`):

| String | Meaning | Expected rate |
|---|---|---|
| `company_scope_denied` | An authenticated user attempted to read-validate/write a row in a company outside their scope (explicit company, employee-derived, or single-company tenant). | Near zero from legitimate traffic. A spike after StrictMode = token/grant misconfiguration; sustained low noise = probing or a frontend sending stale company context. |
| `company_scope_required` | An insert in a **multi-company** tenant arrived with no CompanyId and no way to resolve one (no employee linkage, ambiguous scope). | Zero in steady state. Any occurrence identifies a code path that must pass explicit company context — fix the caller. |
| `company_reassignment_blocked` | Something tried to change (or null out) a row's non-null CompanyId. | Zero — transfers are deliberately unsupported until the explicit transfer workflow ships. Occurrences are bugs or misuse. |

Also worth alerting on: `CompanyScopeBackfill complete` summary lines with non-zero `RowsDefaulted`/`RowsAssigned` after the first clean cycle (indicates a writer skipped stamping), and `Company-scope boot assertion failed` errors in production logs while `ZAYRA_COMPANY_SCOPE_ASSERT` is not yet `strict`.
