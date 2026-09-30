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

The seeders are gone; the world is provisioned by `frontend/e2e/bootstrap` and every step is guarded by
the **e2e preflight** (§6a). Export ONE set of values and give the same shell to the API and the tests:

```bash
export PLATFORM_ADMIN_EMAIL="e2e-owner@example.com" PLATFORM_ADMIN_PASSWORD="$(openssl rand -hex 16)"
export BUILD_COMMIT="$(git rev-parse HEAD)"   # baked into both builds; the preflight compares it

# 1. Postgres (disposable)
docker run -d --name e2e-pg -e POSTGRES_PASSWORD=e2e -e POSTGRES_USER=postgres \
  -e POSTGRES_DB=zayra -p 55433:5432 postgres:16-alpine

# 2. Backend, from this checkout
dotnet build backend-dotnet/Zayra.Api -c Release -p:SourceRevisionId="$BUILD_COMMIT"
ConnectionStrings__Default="Host=localhost;Port=55433;Database=zayra;Username=postgres;Password=e2e" \
Jwt__Issuer="Zayra.Api" Jwt__TenantAudience="kynexone-tenant" Jwt__PlatformAudience="kynexone-platform" \
Jwt__SigningKey="LOCAL_ONLY_NOT_A_PRODUCTION_KEY_0123456789_ABCDEFGHIJKLMNOP" \
Database__RunMigrationsOnStartup=true RateLimit__LoginPermitLimit=200 \
ASPNETCORE_URLS="http://localhost:5117" dotnet run --no-build -c Release --no-launch-profile \
  --project backend-dotnet/Zayra.Api

# 3. PRODUCTION frontend build (BUILD_COMMIT is served at /build-info)
cd frontend && NEXT_PUBLIC_API_BASE_URL=http://localhost:5117 npx next build
NEXT_PUBLIC_API_BASE_URL=http://localhost:5117 npx next start -p 5173

# 4. Preflight → provision + verify → the gate
export PLAYWRIGHT_BASE_URL=http://localhost:5173 E2E_API_BASE_URL=http://localhost:5117 E2E_EXPECTED_DATABASE=zayra
npx playwright test -c e2e/preflight/playwright.preflight.config.ts
npx playwright test -c e2e/bootstrap/playwright.bootstrap.config.ts
npx playwright test --config=playwright.security.config.ts
```

## 6a. Test identity contract and preflight (register F07)

One declaration: `frontend/e2e/identity/env.ts` (the variables) and `frontend/e2e/world.ts` (tenants
and the `PERSONAS` registry). The bootstrap, the preflight, the lanes and `security-gate/roles.ts` all
resolve identities through it. `PLATFORM_ADMIN_EMAIL` / `PLATFORM_ADMIN_PASSWORD` have **no default**:
the API creates that owner at boot, so the tests must present exactly the values the API was started
with. `E2E_DEFAULT_*` and `E2E_MIN_EMPLOYEES` are retired and refused.

| Variable | Required | Meaning |
|---|---|---|
| `PLATFORM_ADMIN_EMAIL`, `PLATFORM_ADMIN_PASSWORD` | always | the platform owner; same values as the API process |
| `E2E_INTELLIFLOW_PASSWORD`, `E2E_RASALMANAR_PASSWORD`, `E2E_GROUP_PASSWORD`, `E2E_EVOSTEL_PASSWORD` | CI | per-run fixture passwords (local defaults otherwise) |
| `PLAYWRIGHT_BASE_URL` (alias `E2E_BASE_URL`), `E2E_API_BASE_URL` | no | default `:5173` / `:5117` |
| `E2E_EXPECTED_COMMIT` | CI | the commit both builds must report (local default: `git HEAD`) |
| `E2E_EXPECTED_DATABASE` | no | database name the API must be attached to |
| `E2E_DESTRUCTIVE_HOST_ALLOWLIST`, `E2E_DATABASE_HOST_ALLOWLIST` | no | extra non-loopback hosts accepted as disposable |
| `E2E_ALLOW_UNVERIFIED_BUILD` | never in CI | local escape hatch when a stack cannot report its commit |

`frontend/e2e/preflight` runs in three phases and exits non-zero, naming each failed check:
**target** (first CI step, and before the bootstrap writes) — env contract complete; both hosts
disposable, never `*.vercel.app` / `*.onrender.com` / `*.render.com` / `*.neon.tech` / `kynexone.com`
(decided before any request); API `/health/live` and frontend `/build-info` report the expected commit;
the frontend proxies to the same API and database; that database (from the authenticated
`/api/platform/health`) is not `kynexone_clean` / `neondb` nor on a Neon/Render host; the platform owner
authenticates. **world** (after the bootstrap) — every tenant, legal entity and employee link exists; the
live role catalog equals `AuthSeeder.cs`; every persona signs in with exactly its role, scope and catalog
permissions; writes `e2e/.auth/preflight.json` (0600, gitignored) holding only the EXPECTED world
computed from env and the checkout plus the local verification time — no server data is written.
**lane** (every browser config's global setup) — target again; the recomputed expected world must equal
the recorded one; and, live, every declared tenant still exists and predates the verification, its legal
entities are intact and its role catalog is still AuthSeeder's. Each lane's setup project also checks
every persona session it mints against the same contract.

The role matrix (`security-gate/full-role-matrix.spec.ts`) is generated from `AuthSeeder.cs` by
`e2e/identity/role-catalog.ts`; `e2e/identity/role-policy.ts` holds the separation-of-duties rules,
checked browserlessly on every PR and against every signed-in persona in the gate. Each test's actor is
stamped into its annotations and printed by `e2e/identity/actor-reporter.ts`.

---

## 7. Gaps — not claimed as covered

| # | Gap |
|---|---|
| **GAP-B3-1** | *Narrowed.* Every system role now has a real signed-in persona and a permission/API/navigation matrix (`full-role-matrix.spec.ts`). Still NOT covered: "Manager sees only their reporting scope" — the Manager and Supervisor personas (IntelliFlow) are not linked to an employee and have no direct reports, so no team-scoped journey runs as them. The Employee persona IS employee-linked. |
| **GAP-B3-2** | Permission **revocation mid-session** is not tested — it needs a defined session-revocation contract to assert against. |
| **GAP-B3-3** | Payment-batch pages, downloads, imports and confirmations are covered at the API layer by `PaymentBatchScopeTests`, but not yet through the browser. |
| **GAP-B3-4** | Impersonation and break-glass journeys are not exercised. The resolver invariants for them are unit-tested (`RequestEntityScopeResolverTests`); the end-to-end flow is not. |
| **GAP-B3-5** | The gate is not yet in the `main-protection` required-checks list — add it once the job name is stable on `main` (governance gap **G-6**). |
| **GAP-B3-6** | Console-error and failed-request assertions are applied only on the company-scope UI test, not globally across every navigation. |
