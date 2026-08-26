# Chrome Security Gate — roles and isolation

**Wave 1 Gate 0 (B3).** Verified 2026-08-26 against a real local stack.
**Config:** `frontend/playwright.security.config.ts` · **Specs:** `frontend/e2e/security-gate/`
**CI job:** `Chrome Security Gate (roles + isolation)`

---

## 1. Why this is a separate suite

A `group-company` e2e suite already existed and is genuinely useful. It is also, by its own
description, **"CI-safe by design"**: it probes the stack in `beforeAll` and calls `test.skip()` when
the probe fails. Running it against this stack produced **16 passed, 2 failed, 9 skipped**.

That behaviour is correct for an advisory suite and disqualifying for a gate. A required check that
turns green when the backend is dead is worse than no check, because it converts "nobody verified this"
into "verified". So the gate is a **separate config that fails instead of skipping**, and the advisory
suite is left alone.

The gate also runs with `retries: 0`. A security boundary that only holds on the second attempt is a
flaky boundary, and retrying hides exactly the intermittent authorization bug the suite exists to catch.

---

## 2. What "real" means here

| Layer | What runs |
|---|---|
| Database | Real PostgreSQL 16, migrated and seeded |
| Backend | The real API, `EnterpriseGroupSeeder` (4 tenants, 15 companies, 58 users) |
| Frontend | **`next build` + `next start`** — a production build, not the dev server |
| API calls | Real, proxied through the frontend exactly as a browser reaches them |
| Auth | Real login against the real limiter |

No mocked business API. No fixtures standing in for endpoints. No hidden demo data — the platform
operator is bootstrapped separately (§4) precisely so the gate does not depend on demo tenants.

---

## 3. The rate limiter is respected, not raised

The API permits **10 login attempts per 60-second window**. The pre-existing suite logs in per spec file
and hits `429` — which is the limiter working correctly.

The tempting fix is to raise `RateLimit:LoginPermitLimit` for tests. That weakens a production
brute-force control for the convenience of the suite, and the brief forbids it.

Instead: **each of the nine roles authenticates exactly once**, paced at `E2E_LOGIN_PACING_MS` (7s
default) so nine logins spread across ~63 seconds, and every spec reuses the stored session. The
limiter runs in CI exactly as it runs in production.

Storage states and bearer tokens are written to `frontend/e2e/.auth/`, which is **gitignored** and
**excluded from the CI artifact upload**. No session material is ever committed or published.

---

## 4. Roles

Nine identities, all real seeded users:

| Key | Role | Scope |
|---|---|---|
| `platform-admin` | Platform Admin | platform audience |
| `tenant-owner` | Tenant Owner / Admin | group |
| `group-hr` | HR Director | group |
| `company-hr-dairy` | HR Manager | ALM-DAIRY-KSA only |
| `company-hr-bakery` | HR Manager | ALM-BAKERY-KSA only |
| `payroll-maker` | Payroll Officer | ALM-DAIRY-KSA |
| `payroll-checker` | Finance Approver | group |
| `auditor` | Auditor | group, read-only |
| `scoped-admin` | HR Manager | 2 of 5 companies |

**A product change was required to make this possible.** The platform-owner seed lived *inside* the
demo-data block, so you could not obtain a platform operator without also fabricating demo tenants —
and because demo seeding is (correctly) refused on Production and dedicated deployments, those
environments had **no supported way to create the first platform operator at all**. Bootstrapping an
operator and fabricating demo tenants are different acts and are now gated separately: the bootstrap is
inert unless `PLATFORM_ADMIN_PASSWORD` is explicitly supplied, no-ops once any platform user exists, and
on Production additionally requires `PLATFORM_ADMIN_BOOTSTRAP=true`.

---

## 5. What the gate proves

22 tests, all passing. Every boundary is asserted through the **direct API** and, where it is a UI
concern, through the **browser** as well — because a hidden nav item is not authorization. Rule 15.

| Boundary | API | Browser |
|---|---|---|
| Tenant user cannot reach platform administration | `/api/platform/tenants` refused | `/platform/dashboard` not reachable by direct URL |
| Anonymous caller refused | `/api/employees` → 401 | `/people` renders no employee codes and does not stay on the route |
| Company-scoped user sees only their company | no sibling codes in `/api/employees` | `/people` shows own codes, never sibling codes |
| Switcher may only narrow | unauthorized `X-Company-Id` cannot widen | — |
| Malformed / zero / injection `X-Company-Id` | never 500, never widens | — |
| Selected-companies user | strict subset, never all five | — |
| Group user | all five (this is what makes the scoped cases meaningful) | — |
| Auditor is read-only | `POST /api/employees` → **403**, not 400 | — |
| Payroll maker cannot approve | approve endpoint refused | — |
| Session cleared | — | cleared storage cannot reach `/people` or render data |
| Invalid bearer token | → 401 | — |

### Negative-tested, not assumed

Two of these tests were **passing for the wrong reason** and were caught before this was called done:

1. The UI tests pointed at `/employees`, which **does not exist** — the route is `/people`. One test
   failed for the wrong reason and one *passed* against a 404 page.
2. The setup wrote tokens to `accessToken` / `token`. The app reads **`zayra_access_token`** and
   **`platform_access_token`**. Every browser context was therefore silently anonymous, and a suite of
   "cross-company data is not visible" assertions passed because **no** data was visible.

Both are fixed, and the company-isolation assertion is now load-bearing in both directions: it asserts
the caller's own data **is** rendered as well as that sibling data is not, so a blank or error page
cannot pass. Proven by substituting a group session into the company-scoped slot — the gate fails with
*"the employees page rendered a sibling company's codes"*.

---

## 6. Running it locally

```bash
# 1. Postgres
docker run -d --name wave1-e2e-pg -e POSTGRES_PASSWORD=wave1 -e POSTGRES_USER=postgres \
  -e POSTGRES_DB=zayra -p 55433:5432 postgres:16-alpine

# 2. Backend, with the enterprise seed AND a platform operator
cd backend-dotnet/Zayra.Api
ConnectionStrings__Default="Host=localhost;Port=55433;Database=zayra;Username=postgres;Password=wave1" \
Jwt__Issuer="Zayra.Api" Jwt__TenantAudience="kynexone-tenant" Jwt__PlatformAudience="kynexone-platform" \
Jwt__SigningKey="LOCAL_ONLY_NOT_A_PRODUCTION_KEY_0123456789_ABCDEFGHIJKLMNOP" \
SeedAdmin__SeedDemoData=false SEED_ENTERPRISE_TEST_DATA=true \
PLATFORM_ADMIN_EMAIL="admin@platform.local" PLATFORM_ADMIN_PASSWORD="YourPassword123!" \
ASPNETCORE_URLS="http://localhost:5117" dotnet run --no-launch-profile

# 3. PRODUCTION frontend build
cd frontend && NEXT_PUBLIC_API_BASE_URL=http://localhost:5117 npx next build
NEXT_PUBLIC_API_BASE_URL=http://localhost:5117 npx next start -p 5173

# 4. The gate
PLAYWRIGHT_BASE_URL=http://localhost:5173 npx playwright test --config=playwright.security.config.ts
```

---

## 7. Gaps — not claimed as covered

| # | Gap |
|---|---|
| **GAP-B3-1** | `Manager` and `Employee` roles exist in the product but are **not seeded** into the enterprise-group tenant, so "Employee cannot access HR administration" and "Manager sees only their reporting scope" are **not** covered. Faking those identities would have produced a green test that proves nothing. They need seeder support first. |
| **GAP-B3-2** | Permission **revocation mid-session** is not tested — it needs a defined session-revocation contract to assert against. |
| **GAP-B3-3** | Payment-batch pages, downloads, imports and confirmations are covered at the API layer by `PaymentBatchScopeTests`, but not yet through the browser. |
| **GAP-B3-4** | Impersonation and break-glass journeys are not exercised. The resolver invariants for them are unit-tested (`RequestEntityScopeResolverTests`); the end-to-end flow is not. |
| **GAP-B3-5** | The gate is not yet in the `main-protection` required-checks list — add it once the job name is stable on `main` (governance gap **G-6**). |
| **GAP-B3-6** | Console-error and failed-request assertions are applied only on the company-scope UI test, not globally across every navigation. |
