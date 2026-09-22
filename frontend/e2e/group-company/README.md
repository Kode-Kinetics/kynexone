# Group→Company E2E suite

Playwright specs for the Group→Company (multi-company tenant) release.
They exercise group dashboards, the TopBar company switcher, company scoping,
compliance profiles, security fail-closed behavior and platform-admin group
tenant lifecycle — through the frontend (`/api` is proxied to the backend by
`next.config.ts`).

**Stack unreachable is a FAILURE, not a skip.** Every suite calls
`assertStackReachable()` in `beforeAll` (`GET <baseURL>/api/auth/me` — a 401
proves frontend+backend are alive and talking). It **throws**, which fails every
test in the suite.

> This paragraph used to read "CI-safe by design: ... all tests in the suite
> `test.skip()` ... instead of failing." That was not safe, it was blind — a
> completely dead backend produced a **green** run across all seven suites, with
> nothing in the output distinguishing "28 boundaries verified" from "the API was
> never contacted". A suite that cannot reach the system it tests has not passed;
> it has failed to run. Changed 2026-09-17 (`test/integrity-hardening`).

**Fixture data absent is also a FAILURE now.** Every suite calls
`assertFixtureWorld()` in `beforeAll`, which throws with instructions.

> This used to be `groupSeedMissingReason()` feeding `test.skip(...)`, justified
> by the group seed being env-gated behind `SEED_ENTERPRISE_TEST_DATA=true` — "a
> configuration statement, not a broken system". Both halves of that are now void:
> the seeder was deleted (`docs/DATA_ENTRY_PATHS.md`), there is no env gate, and
> the world is provisioned by a bootstrap CI runs unconditionally. Meanwhile the
> skip was hiding the failure that matters — seven spec files reported green
> against a database with no `almarai-test` tenant in it, which reads in a CI
> summary exactly like seven suites of verified isolation boundaries.

What still skips, deliberately, and why it is a different thing:
- **UI surfaces still being built** (the company switcher, `/group`) skip with a
  clear message when absent, while the API-level equivalents assert strictly.
  That is a statement about the frontend build under test, not about the data.

## How to run

### 0. Provision the fixture world

The database starts EMPTY — there is no seeder. Nothing in this suite can log in
until the bootstrap creates the tenants, companies, users, entity grants and
employees through the platform-admin API:

```bash
cd frontend && npx playwright test -c e2e/bootstrap/playwright.bootstrap.config.ts
```

The backend must have been started with `PLATFORM_ADMIN_EMAIL` and
`PLATFORM_ADMIN_PASSWORD` set, so the one platform operator exists to do it.

### 1. Start Postgres

```bash
# from the repo root — uses the repo's compose stack
docker compose up -d db        # or: docker compose up -d (whole stack)
```

Any Postgres works (incl. a testcontainers-style throwaway) as long as
`ConnectionStrings__Default` points at it.

### 2. Run the backend with the enterprise-group seed

From `backend-dotnet/Zayra.Api` (values below mirror `appsettings.json`
defaults — override with your local secrets):

```bash
ConnectionStrings__Default="Host=localhost;Port=5432;Database=zayra;Username=postgres;Password=postgres" \
Jwt__Issuer="Zayra.Api" \
Jwt__TenantAudience="kynexone-tenant" \
Jwt__PlatformAudience="kynexone-platform" \
Jwt__SigningKey="CHANGE_ME_TO_A_64_CHARACTER_PRODUCTION_SECRET_KEY_1234567890" \
PLATFORM_ADMIN_EMAIL="admin@platform.local" \
PLATFORM_ADMIN_PASSWORD="YourPassword123!" \
dotnet run
```

- `PLATFORM_ADMIN_*` is the ONLY bootstrap the backend performs: it creates the
  first platform operator (`PlatformOwnerBootstrap`), and nothing else. Every
  tenant, company, user and employee below is then created by that operator
  through the platform-admin API — see step 0.
- `SeedAdmin__*` and `SEED_ENTERPRISE_TEST_DATA` used to be here. The seeders they
  drove were deleted (`docs/DATA_ENTRY_PATHS.md`) and
  `Zayra.Api.Tests/Security/NoSideDoorDataTests.cs` fails the build if one returns,
  so setting them now does nothing at all.
- **JWT env vars are mandatory** — missing `Jwt__*` values crash the API at
  startup.

### 3. Start the frontend

```bash
cd frontend
npm run dev            # or: npm run build && npm run start
```

The dev server must listen on the Playwright `baseURL`
(default `http://localhost:5173`; the backend default is `http://localhost:5117`,
already wired in `next.config.ts`). Override the API target with
`NEXT_PUBLIC_API_BASE_URL` and the test baseURL with `PLAYWRIGHT_BASE_URL`.

### 4. Run the suite

```bash
cd frontend
npx playwright test e2e/group-company            # whole suite
npx playwright test e2e/group-company/security.spec.ts   # one file
npx playwright test e2e/group-company --list    # parse check, no run
```

## Verified run requirements (learned from the first live run)

- **Raise the login rate limit for the suite**: the backend throttles `/api/auth/login`
  (default 10/window) and the suite logs in dozens of times. Run the backend with
  `RateLimit__LoginPermitLimit=1000` or probes will see 429s and suites will skip.
- **Use a PRODUCTION frontend** (`npm run build && npx next start -p 3000`): `next dev`
  lazy compilation makes the health probes flaky and its error overlay breaks selectors.
  Never leave a stale `.next` from a previous build under a dev server.
- **Platform suite credentials**: export `PLATFORM_ADMIN_EMAIL` / `PLATFORM_ADMIN_PASSWORD`
  to BOTH the backend and the Playwright process, or platform-admin specs skip.
- Verified result on 2026-07-05: 27/28 passing, 1 conditional skip (payroll register
  export probe when no inaccessible run id is discoverable).

## Environment variables

| Variable | Default | Used for |
|---|---|---|
| `PLAYWRIGHT_BASE_URL` | `http://localhost:5173` | frontend baseURL |
| `E2E_GROUP_PASSWORD` | `GroupDemo123!x` outside CI; generated per run in CI | every group-tenant user |
| `E2E_DEFAULT_TENANT_SLUG` | `zayra` | single-company regression tenant |
| `E2E_DEFAULT_ADMIN_EMAIL` | `admin@zayra.local` | single-company regression admin |
| `E2E_DEFAULT_ADMIN_PASSWORD` | `ChangeMe123!` | single-company regression admin |
| `PLATFORM_ADMIN_EMAIL` | `admin@platform.local` | platform-admin spec |
| `PLATFORM_ADMIN_PASSWORD` | `YourPassword123!` | platform-admin spec |

## Provisioned test data (frontend/e2e/bootstrap, declared in frontend/e2e/world.ts)

Password for **all** users below: `GroupDemo123!x`. Tenant slug doubles as the
login "Workspace" field. Employee codes follow `<COMPANY-CODE>-E<number>`.

### Tenants (AccountType = Group)

| Tenant | Slug | Companies |
|---|---|---|
| ALMARAI_TEST | `almarai-test` | ALM-DAIRY-KSA, ALM-POULTRY-KSA, ALM-BAKERY-KSA, ALM-DIST-KSA, ALM-UAE-TRD |
| TATA_TEST | `tata-test` | TATA-TCS-IN, TATA-MOTORS-IN, TATA-STEEL-IN, TATA-HOTELS-IN, TATA-JLR-UK |
| EMAAR_TEST | `emaar-test` | EMAAR-PROP-UAE, EMAAR-MALLS-UAE, EMAAR-HOSP-UAE, EMAAR-LEISURE-UAE, EMAAR-KSA-PROP |

### Users (same pattern per tenant; almarai-test shown)

| User | Email | Scope |
|---|---|---|
| Owner | `owner@almarai-test.local` | Group (all companies) |
| Admin | `admin@almarai-test.local` | Group |
| HR | `hr@almarai-test.local` | Group |
| Finance | `finance@almarai-test.local` | Group |
| Compliance | `compliance@almarai-test.local` | Group |
| Auditor | `auditor@almarai-test.local` | Group (read-only) |
| Scoped admin | `scoped.admin@almarai-test.local` | ONLY ALM-DAIRY-KSA + ALM-POULTRY-KSA |
| Company admin | `admin@alm-dairy-ksa.almarai-test.local` | ALM-DAIRY-KSA only |
| Company HR | `hr@alm-dairy-ksa.almarai-test.local` | ALM-DAIRY-KSA only |

### Other logins

| User | Email / password | Purpose |
|---|---|---|
| Default tenant admin | `admin@zayra.local` / `ChangeMe123!` (tenant `zayra`) | single-company regression |
| Platform admin | `admin@platform.local` / `YourPassword123!` | platform-admin spec |

## Spec map

| File | Covers |
|---|---|
| `single-company-regression.spec.ts` | default tenant unaffected: no switcher, no Group Overview, employees page loads |
| `group-admin.spec.ts` | owner: Group Overview nav, `/group` cards (5 companies), "All companies" option, switch→filter to ALM-DAIRY-KSA |
| `scoped-user.spec.ts` | selected-companies grant: switcher lists exactly 2 companies, no "All companies", no sibling leakage |
| `company-admin.spec.ts` | single-company admin + X-Company-Id tamper fails closed (empty data) |
| `security.spec.ts` | auditor read-only (POST 403), scoped CSV export leak check, payroll register export of inaccessible run, malformed X-Company-Id |
| `compliance.spec.ts` | KSA profile (SA, IqamaNumber missing>0, "not legal certification"), TATA-TCS-IN shows IN |
| `platform-admin.spec.ts` | create Group tenant (API + UI probe), group tenant detail, downgrade blocked with 409 `multiple_active_companies` |

## Notes / caveats

- `platform-admin.spec.ts` creates a throwaway tenant `e2e-group-<timestamp>`
  per run and suspends it afterwards (best-effort cleanup).
- The account-type downgrade test verifies almarai-test has >1 active company
  *before* attempting the downgrade, and restores `Group` if the API ever lets
  the downgrade through — the seed tenant is never left corrupted.
- Security spec (c) skips when no payroll run exists for an inaccessible
  company (the seeder may not create payroll runs) — that is expected.
- The suite runs with the existing `playwright.config.ts` (workers: 1,
  chromium, `testDir: ./e2e`), no config changes required.
