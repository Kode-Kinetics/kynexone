# SECURITY POSTURE — KynexOne (SOC 1 readiness)

_Last updated: 2026-06-08. Companion to RBAC_TENANT_ISOLATION_AUDIT.md and LIVE_READINESS_CHECKLIST.md._

This document records the control posture relevant to a SOC 1 (and baseline SOC 2)
examination: multi-tenant isolation, authentication, RBAC, CSRF, CORS, and HTTP security
headers — what is implemented, and what remains.

## 1. Multi-tenant architecture & isolation

**Model:** single shared database, row-level isolation by `tenant_id`. Every tenant-owned
table carries `tenant_id` (227 of 233 tables; the 6 without are global/identity tables —
`tenants`, `permissions`, and the user/role join + token tables).

**Enforcement (defence in depth):**
1. **Token-scoped context** — the JWT carries a `tenant_id` claim; `ZayraDbContext` reads it
   via `IHttpContextAccessor` into `_tenantId` per request.
2. **Global EF query filter** — `ZayraDbContext.OnModelCreating` applies
   `HasQueryFilter(e => _tenantId == null || e.TenantId == _tenantId)` to **every** entity
   exposing a `TenantId`. A forgotten `.Where(TenantId == …)` can no longer leak across
   tenants by default. The `_tenantId == null` branch bypasses the filter only when there is
   no HTTP context (startup seeding, login/refresh, background jobs).
3. **Explicit per-query filtering** — controllers/services also filter `TenantId` explicitly
   (verified across all 69 controllers).
4. **Tenant resolution at login** — users are resolved by email **within a tenant slug**;
   the issued token is bound to that tenant.

**Verified:** login, then authenticated reads (`/employees`, `/departments`, `/roles`,
`/dashboard/*`) return only the caller's tenant data; a previously-found cross-tenant leak
in the dashboard and a Feedback360 gap were fixed.

**Residual / follow-up:**
- **Negative cross-tenant test** (log in as tenant B, confirm tenant A's rows are invisible)
  requires a second seeded tenant — recommended before the SOC examination.
- **Background/async work** runs without an HTTP context (filter bypassed); any future batch
  job MUST pass an explicit tenant scope. (No such jobs run today.)

## 2. Authentication

- **JWT bearer** access tokens (30 min) + refresh tokens (14 days), PBKDF2 password hashing.
- Token validated for issuer, audience, lifetime, and signing key on every request.
- All controllers `[Authorize]`; only `login`, `refresh`, `forgot-password`,
  `reset-password`, `accept-invitation` are `[AllowAnonymous]`.
- **Action:** the JWT signing key and seed admin password are still placeholders in config
  (`CHANGE_ME_…`, `ChangeMe123!`) — these **must** be set to strong secrets via environment
  variables / a secret store before production. DataProtection keys should be persisted to a
  mounted volume or vault (currently container-local).

## 3. RBAC

- **10 roles** (Admin, HR Director/Manager/Officer/Assistant, Payroll Manager/Officer,
  Finance Approver, Compliance Officer, Manager, Supervisor, Recruiter, Auditor, Kiosk
  Operator, Employee) and **45 permissions**, seeded per tenant.
- **Enforcement:** method-level `[Authorize(Roles=…)]` on sensitive endpoints across every
  controller; the AI assistant also blocks sensitive intents for non-HR/Admin and logs the
  attempt. The frontend mirrors this with permission-keyed route guards (`ProtectedRoute`).
- Roles are tenant-scoped (`roles.tenant_id → tenants` FK enforced at DB level).

## 4. CSRF

**Posture: structurally not applicable to the API.**
- Authentication is **bearer-token only**, sent in the `Authorization` header and stored in
  `localStorage`. There is **no cookie-based session, no `SameSite` cookie, no ambient
  credential** that a browser would attach to a forged cross-site request.
- A CSRF attacker cannot read `localStorage` from another origin, and a forged request will
  not carry the bearer token — so classic CSRF (cookie-replay) cannot succeed.
- **CORS is locked to an allowlist** (see §5) and **`X-Frame-Options: DENY`** (see §6)
  mitigates clickjacking-based request forgery.
- **Trade-off noted for the auditor:** `localStorage` tokens trade CSRF exposure for XSS
  exposure. The mitigation is a strict frontend (no `dangerouslySetInnerHTML` on untrusted
  data) and the security headers in §6. If the org later moves refresh tokens to `httpOnly`
  cookies, anti-forgery tokens MUST be added at that point.

## 5. CORS

- Was `AllowAnyOrigin()` — **now locked to an explicit allowlist** read from
  `Cors:AllowedOrigins` (default `http://localhost:5173`; set production origins via config/
  env). `AllowCredentials` is intentionally not enabled (bearer auth, no cookies).

## 6. HTTP security headers

Added globally in the request pipeline:
- `X-Content-Type-Options: nosniff`
- `X-Frame-Options: DENY` (anti-clickjacking)
- `Referrer-Policy: strict-origin-when-cross-origin`
- `X-Permitted-Cross-Domain-Policies: none`

A full `Content-Security-Policy` is **not** yet set on the SPA host (recommended next; must
be authored against the built asset hashes to avoid breaking the app).

## 7. Clean seed / no demo data in production

- `AuthSeeder` always seeds **only**: tenant, roles, permissions, and the admin user.
- Sample business data (demo company, branches, departments, grades, policies, workflows) is
  now gated behind **`SeedAdmin:SeedDemoData`** (default **false**). Fresh production tenants
  start clean and configure their own organisation via Setup.
- To enable sample data in a dev/demo environment, set `SeedAdmin__SeedDemoData=true`.
- **Note:** gating affects *new* seeds; an existing database is not retroactively cleaned.
  For a pristine instance, recreate the DB volume and start with `SeedDemoData=false`.

## 8. Audit logging

- General `audit_logs` plus module-specific audit tables (auth, employee, payroll, bonus,
  recruitment, admin). The AI layer logs every query (incl. blocked) to `ai_hr_query_logs`
  with intent, tokens, latency and advisory flag.

## 9. Outstanding before SOC examination (priority)
1. Replace placeholder secrets (JWT signing key, admin password) with vault/env-managed
   strong values; persist DataProtection keys.
2. Add DB foreign keys on key business relationships (integrity is app-level today — see
   docs/DATABASE_CONNECTIVITY_AUDIT.md).
3. Negative cross-tenant isolation test with a second tenant.
4. Author a Content-Security-Policy for the SPA.
5. Move schema management from `EnsureCreated` to versioned EF migrations.
