# Phase 2 — Scoped Access Cutover Runbook

## Migration `20260705114353_Phase2GrantModesAndInsightCompany`

Purely additive: `user_entity_accesses.grant_mode` (text, default `SelectedCompanies`)
with in-migration least-privilege backfill (`company_id IS NULL` rows →
`AllCurrentAndFutureCompanies`, behavior-preserving; rows with a company →
`SelectedCompanies`), plus `ai_insights.company_id` (nullable) + `(tenant_id, company_id)`
index. Rollback = `dotnet ef database update 20260705053606_Phase1BCompanyScopeFoundation`
(drops the two columns/one index; grant modes are recomputable from `company_id`).
Generate-only policy unchanged: applied by the standard deploy path, never by hand.

## Claim format v2

One `entity_scope` claim: `{"v":2,"m":"group"|"companies"|"none","c":[ids]}` — an
EXPLICIT issuance-time decision. Emitted by normal login, impersonation, and break-glass
support (all via `EntityScopeClaims.Resolve/Build`). Legacy v1 claims (`entity_access`,
`is_group_scope`) are still co-emitted during the rollout window so previous-release pods
enforce the same boundary; the v1 parser path is the documented compat path.
**Malformed v2 always fails closed** (never falls back to v1).

Grant-less tenant users resolve to `m=group` — the documented tenant default (tenant-wide
until per-user scoping is assigned), now explicit/auditable instead of a parser fallback.

## Forced re-authentication

No token-version infrastructure is required:

- Access tokens live `AccessTokenMinutes` (30 min); refresh rotation re-issues claims
  from the database, so every session picks up v2 automatically within one access-token
  lifetime of the deploy.
- Impersonation/support tokens live 1 hour and always carry `entity_scope_strict`.

**Operational step:** none for normal rollout. If an emergency invalidation is ever
needed, revoke refresh tokens for the tenant (existing platform tooling) — users
re-login and receive v2 tokens.

## StrictMode cutover checklist (global `EntityScope:StrictMode=true`)

StrictMode is NOT flipped in code (default remains false). Flip it via environment when:

1. ☐ Phase 2 deploy is live on all pods (v2 claims emitting — check `/api/auth/me` roundtrip).
2. ☐ ≥ 60 minutes elapsed since deploy (longest-lived access/impersonation token expired).
3. ☐ Strict-mode test suite green in CI (`StrictModeCutoverTests` — runs the whole claim
     matrix with strictMode:true; normal login, impersonation, support, legacy-v1 tokens).
4. ☐ Set `EntityScope__StrictMode=true` on Render; restart.
5. ☐ Smoke: normal login lists employees; impersonation session works; a claims-stripped
     request to a company-scoped endpoint returns empty/denied.
6. ☐ Rollback: unset the env var (fail-open only for legacy tokens, which no longer exist).

After cutover there is NO fail-open path: v2 tokens carry explicit scope; token absence
of claims → deny; malformed → deny; impersonation/support additionally carry the strict
marker independent of the global flag.

## NOT NULL promotion (CompanyId on operational tables) — GATED, not executed

Write-side stamping now guarantees new operational rows carry CompanyId (or fail
closed). Promotion to NOT NULL per table is safe only when production null counts are
zero. **Blocker: production counts cannot be verified from a development branch** — run
after this deploy has been live through one full payroll cycle:

```sql
-- Validation (run against prod read replica); repeat per table:
SELECT 'attendance_records' t, count(*) FROM attendance_records WHERE company_id IS NULL
UNION ALL SELECT 'leave_requests', count(*) FROM leave_requests WHERE company_id IS NULL
UNION ALL SELECT 'employee_loans', count(*) FROM employee_loans WHERE company_id IS NULL
UNION ALL SELECT 'salary_advances', count(*) FROM salary_advances WHERE company_id IS NULL
UNION ALL SELECT 'employee_bonuses', count(*) FROM employee_bonuses WHERE company_id IS NULL
UNION ALL SELECT 'payroll_runs', count(*) FROM payroll_runs WHERE company_id IS NULL
UNION ALL SELECT 'payslips', count(*) FROM payslips WHERE company_id IS NULL
UNION ALL SELECT 'employees', count(*) FROM employees WHERE company_id IS NULL;
```

- All zeros for N consecutive days → generate per-table `AlterColumn` NOT NULL migration.
- Non-zero → the backfill will repair on next boot (idempotent); investigate the writing
  code path first (it means a system-context writer skipped stamping — by design only
  seeders/workers can do that).
- Keep nullable forever on: audit tables, LeavePolicy/holiday calendars/config templates
  (null is their legitimate tenant-wide semantic).
