# Multi-Client Deployment Playbook

> Audience: Ops / engineering lead onboarding a new client. This is the authoritative decision guide for HOW we ship Zayra AI Workforce to a client and WHICH deployment shape to use. Read the Hard Rule (§2) before anything else.

## 1. Architecture in one paragraph (why the rules exist)

Zayra runs on a **single shared PostgreSQL schema**. Every business entity carries a `TenantId` discriminator, and a **global EF Core query filter** scopes every query to the caller's tenant. The tenant is resolved from the JWT `tenant_id` claim at request time; an optional `X-Company-Id` request header applies a **secondary company scope that can only narrow** the tenant scope, never widen it. Tenant JWTs are HMAC-signed with `Jwt__SigningKey` and carry `Jwt__TenantAudience`; platform-admin JWTs carry `Jwt__PlatformAudience`. The database connection comes from `ConnectionStrings__Default`, and in Production a missing value is a hard boot failure (fail-fast), not a silent fallback. Because isolation is enforced **in the application layer by `TenantId`**, and all clients' rows live in one schema, giving any client direct database access to the shared instance would expose every other client's data.

## 2. THE HARD RULE

> **Any deployment where the client has database access MUST be a dedicated, single-tenant database.**
>
> **Never** point a client at, or grant a client credentials to, the shared multi-tenant database. In the shared DB, rows for **all** clients live in **one schema, separated only by `TenantId`** and the application-layer global query filter. A direct SQL connection bypasses that filter entirely — a client with DB access could read or modify every other client's employees, payroll, and PII. DB access ⇒ dedicated single-tenant DB. No exceptions, no "read-only" carve-outs on the shared instance.

## 3. The two SKUs

| | **(a) Shared SaaS** | **(b) Dedicated / client-hosted DB** |
|---|---|---|
| Infrastructure | Our infra, our region | Same codebase, single-tenant config; client's Postgres in their cloud/region/VPC |
| Tenancy | Client is **one tenant** in the shared multi-tenant DB | **Exactly one tenant** in a database dedicated to that client |
| DB access for client | **None** (never) | Client owns/controls the database |
| `ConnectionStrings__Default` | Points at shared cluster (ours) | Points at the client's dedicated Postgres |
| Data residency | Our region | Client's chosen region/cloud |
| Upgrades & migrations | We run centrally | Coordinated; migrations applied via a controlled `--migrate` step (§6) |
| Ops overhead | Lowest | Higher (per-client secrets, migrations, backups, monitoring) |

### When to choose which

- **Choose Shared SaaS (default)** when: the client accepts our hosting and region, needs no database access, wants the fastest onboarding and lowest cost, and is fine with us managing upgrades, backups, and DR centrally.
- **Choose Dedicated / client-hosted DB** when the client requires **any** of: data residency in a specific region/cloud; the database inside their own cloud account/VPC or private network; contractual database ownership or direct DB access; regulated-industry or audit requirements that mandate DB-level control; or air-gapped / private-link connectivity. This SKU is **always single-tenant** — it is the only shape in which a client may have DB access (§2).

## 4. The three tiers of seeded data

On boot the app runs a chain of seeders (see `backend-dotnet/Zayra.Api/Program.cs`, startup scope). They fall into three tiers with very different data-safety properties:

| Tier | What it is | Seeder(s) | Runs when | Shared SaaS | Dedicated / client DB |
|---|---|---|---|---|---|
| **1. Reference / statutory** | Country labour-law packs, GOSI, statutory rules, pricing/module catalog — **product config**, idempotent & additive | `AuthSeeder.EnsureGlobalCountryRules`, `GosiRuleSeeder`, `StatutoryRuleSeeder`, `DemoDataSeeder.SeedPricingConfigAsync` | **Always** (every boot, incl. Production) | Present | Present |
| **2. Bootstrap org + admin** | The tenant record, the system roles, the permission catalog, and **one** admin user | `AuthSeeder.SeedAsync` (core path) | Always (env-driven from `SeedAdmin__*`) | One tenant provisioned **per client** in the shared DB | **Exactly one** tenant = the client |
| **3. Demo** | Sample company/branches/employees/attendance/payroll and demo tenants (KSA, IntelliFlow, enterprise groups) | `DemoDataSeeder`, `CleanDemoKsaSeeder`, `IntelliFlowDemoSeeder`, `EnterpriseGroupSeeder`, demo-cleanup zone | **Only** when a demo flag is set | **Off** | **Off** |

**Tier-1 detail:** Tier-1 seeders are idempotent and additive (they "ensure" rows and skip when present). They run even in Production so the statutory rule sets, GOSI defaults, and the pricing/module catalog always exist. They never delete or mutate customer records.

**Tier-2 detail — first-run posture and the default-admin trap:** `AuthSeeder.SeedAsync` creates the tenant (from `SeedAdmin__TenantName` / `SeedAdmin__TenantSlug`) plus roles/permissions plus one admin (from `SeedAdmin__Email` / `SeedAdmin__Password`). The admin is created `Status = "Active"` and `IsEmailConfirmed = true` (`AuthSeeder.cs:45-57`), and the seeder does **not** set `MustChangePassword`, so the account is immediately usable at first boot.

> **There IS a committed default password fallback, and it is NOT enforced by any boot fail-fast.** `appsettings.json:18` (shipped inside the image) sets `SeedAdmin:Password` = `ChangeMe123!`, and `SeedAdminOptions.Password` defaults to `ChangeMe123!` in `AuthOptions.cs:28`. Unlike `Jwt__SigningKey` and `ConnectionStrings__Default` — which the `Program.cs` fail-fast blocks the boot on — **there is no fail-fast on `SeedAdmin__Password`**. If an operator omits `SeedAdmin__Password`, Production **boots successfully** and **silently provisions an Active, email-confirmed tenant Admin with the well-known credential `ChangeMe123!`**. Nothing crashes; nothing warns loudly enough to block go-live. **Setting a unique, strong `SeedAdmin__Password` is mandatory — it is the operator's only defence against a default-credential admin account.** After first login, treat the seed value as a one-time bootstrap secret and rotate it out of the environment (§7).

**Tier-3 detail (fail-safe gating):** Demo mutation is gated so it can **never** touch a client/prod DB. Demo seeding runs only if `SEED_DEMO_DATA=true` **OR** `SeedAdmin__SeedDemoData=true`; the enterprise-group demo tenants run only if `SEED_ENTERPRISE_TEST_DATA=true` (a **separate** flag). The demo-only cleanup zone (which can deactivate/rename tenants) is inside the same `if (seedDemoData)` guard, so with demo off **nothing** in that block runs. **Keep all three false/unset on every client and production database.** Demos belong on a separate, isolated demo/staging service.

## 5. Environment-variable matrix — Dedicated deployment

Names below are the **canonical container / .NET config keys** (double underscore) that the app actually reads — use these on Render, Kubernetes, or any raw container env. The `docker-compose.yml` shell aliases (single-underscore, e.g. `JWT_SIGNING_KEY`, `SEED_ADMIN_EMAIL`) map onto the same keys and are shown in the Notes column where they differ.

| Variable | Required? | Purpose / notes |
|---|---|---|
| `ConnectionStrings__Default` | **Required** | Connection string to the client's **dedicated** Postgres. Format: `Host=<host>;Port=5432;Database=zayra;Username=<user>;Password=<pass>;SSL Mode=Require;Trust Server Certificate=true;`. In Production a missing value **fails fast on boot** (`Program.cs:132-133`: throws `Missing required env var: ConnectionStrings__Default`). |
| `ASPNETCORE_ENVIRONMENT` | **Required** | Set to `Production`. This is what turns on the `ConnectionStrings__Default` fail-fast and the production security posture (strict company scope). |
| `Jwt__SigningKey` | **Required** | **Unique per client.** ≥64-char random secret (`openssl rand -base64 64`). Boot **fails fast** in any non-Development environment if it is missing, still `CHANGE_ME…`, or <64 chars (`Program.cs:70-73`). Compose alias: `JWT_SIGNING_KEY`. **Reuse is dangerous — see §7.** |
| `Jwt__TenantAudience` | **Required** | **Unique per client.** Audience for tenant/user JWTs. Boot **fails fast** if unset, equal to the dev default (`kynexone-tenant`), or equal to `Jwt__PlatformAudience` (`Program.cs:66-75`). (Not optional — omitting it crashes the service.) |
| `Jwt__PlatformAudience` | **Required** | **Unique per client.** Audience for platform-admin JWTs. Same fail-fast rules; must differ from the dev default (`kynexone-platform`) and from `Jwt__TenantAudience`. |
| `Jwt__Issuer` | Optional | Defaults to `Zayra.Api`. |
| `SeedAdmin__TenantName` | **Required** | Display name of the client organisation (the single tenant). Compose alias: `SEED_TENANT_NAME`. |
| `SeedAdmin__TenantSlug` | **Required** | Lower-case slug identifying the seeded tenant. Compose alias: `SEED_TENANT_SLUG`. |
| `SeedAdmin__Email` | **Required** | Bootstrap admin email. Compose alias: `SEED_ADMIN_EMAIL`. |
| `SeedAdmin__Password` | **Required (critical)** | Strong **unique** bootstrap password. **There is NO fail-fast on this variable.** If you omit it, the deploy does **not** crash — instead the app boots and creates an Active, email-confirmed Admin with the committed default `ChangeMe123!` (`appsettings.json:18` / `AuthOptions.cs:28`), i.e. a live default-credential admin an attacker can log in to. Setting a unique strong value is the **only** thing that prevents this (§4 Tier-2, §7). Compose alias: `SEED_ADMIN_PASSWORD`. |
| `SeedAdmin__FullName` | Optional | Admin display name. Compose alias: `SEED_ADMIN_NAME`. |
| `SeedAdmin__SeedDemoData` | **Required = `false`** | Master demo gate. Keep `false` on every client DB. |
| `SEED_DEMO_DATA` | Optional (leave unset / `false`) | Env-level demo gate; **also** enables demo when `true`. Keep off. |
| `SEED_ENTERPRISE_TEST_DATA` | Optional (leave unset) | **Separate** flag that seeds enterprise-group demo tenants. Must **not** be `true` on a client DB. |
| `PLATFORM_ADMIN_EMAIL` | **Leave unset** | Cross-tenant god-mode. `PlatformController` authenticates the platform-admin login against `PLATFORM_ADMIN_EMAIL` / `PLATFORM_ADMIN_PASSWORD` at request time (`PlatformController.cs:151-179`) and `DemoDataSeeder` seeds a `PlatformUser` from them (`DemoDataSeeder.cs:130-149`). On a dedicated **single-tenant** deployment there is no fleet to administer — leave this **unset**. |
| `PLATFORM_ADMIN_PASSWORD` | **Leave unset** | With both platform vars unset, the platform-admin login returns **HTTP 503** ("credentials are not configured", `PlatformController.cs:155-156`) — the **desired disabled state**. **Do not** copy these from a shared template: setting them enables a cross-tenant platform-admin account whose JWT is signed with the same `Jwt__SigningKey` as tenant tokens. See §7. |
| `DEDICATED_DEPLOYMENT` | Optional (label only) | **Operational marker, not consumed by application code today.** Set to `true` on the dedicated service purely to make single-tenant intent explicit in the client's env inventory / your provisioning tooling. It does not change app behaviour — do not rely on it for isolation; isolation comes from the dedicated DB + single tenant. |
| `PORT` | Recommended | Listen port. Takes precedence over `ASPNETCORE_URLS` (`Program.cs:84-88` reads `PORT` first). The image `EXPOSE`s 8080; health check path is `/health`. |
| `ASPNETCORE_URLS` | Optional | Fallback listen URL (e.g. `http://0.0.0.0:8080`) used only when `PORT` is unset. |
| `REDIS_URL` | Optional | Distributed cache. When set, uses Redis; otherwise an in-memory cache. Set it if you run **more than one** app instance; a single-instance dedicated deployment can omit it. |
| `Database__RunMigrationsOnStartup` | Optional = `false` | Keep `false` (§6). The web process must not migrate on startup. |
| `CORS_EXTRA_ORIGINS` | Optional | Comma-separated extra allowed origins — add the client's frontend URL here if it is not already whitelisted. |
| `AI_PROVIDER` | Optional | `none` disables AI features (AI is opt-in). |

> **Boot-blocking correctness note:** `Jwt__SigningKey`, `Jwt__TenantAudience`, and `Jwt__PlatformAudience` are **all** enforced by the fail-fast in `Program.cs:46-81` for every non-Development environment. Provisioning only `Jwt__SigningKey` (and leaving the audiences at their committed dev defaults) will **crash the deploy**. Provision all three. In contrast, `SeedAdmin__Password` is **not** fail-fast-enforced — omitting it does not crash the deploy, it silently creates a default-credential admin (§4 Tier-2). These are two different failure modes: one blocks the deploy, one lets it succeed insecurely.

## 6. Migration flow for a client-hosted DB

The schema is applied by EF Core migrations shipped inside the image. Apply them with a **controlled one-off command**, never on web startup:

```
# One-off migration job (exits 0 when done):
dotnet Zayra.Api.dll --migrate
```

- **`--migrate` mode** runs `Database.MigrateAsync()` and then **exits** (`Program.cs:577-581`: `return` → exit 0), so it is safe as a Render pre-deploy job, a Kubernetes Job, or a one-shot container run. Point its `ConnectionStrings__Default` at the client DB. Because the JWT/env fail-fast (§5) runs at builder time **before** the migrate step, a bad JWT/env config also surfaces here — the `--migrate` job doubles as a config smoke-test.
- **`Database__RunMigrationsOnStartup` stays `false`.** The web process must never migrate on boot: if the client DB is briefly unreachable or locked, an on-startup migration would crash-loop the web service and take the app offline. Migrations are a deliberate, observable step, decoupled from serving traffic.

### 6.1 HAZARD — never boot the web service against an un-migrated (empty) client DB

This is the single most dangerous ordering mistake for a dedicated client, and it is silent until it has already bricked the database.

On a **normal (non-`--migrate`) web boot**, `AuthSeeder.SeedAsync` runs (`Program.cs:617`) and its **first line unconditionally calls `Database.EnsureCreatedAsync()`** (`AuthSeeder.cs:26`). Against an existing-but-empty Postgres, `EnsureCreatedAsync` creates the **entire schema from the current model** — but it does **NOT** populate `__EFMigrationsHistory`. The database now has tables but no migration history. A subsequent `dotnet Zayra.Api.dll --migrate` then tries to apply the initial migration, hits objects that already exist, and **fails with Postgres `42P07` ("relation already exists")**. The client DB is left with a schema EF cannot migrate forward — **permanently un-migratable** without manual surgery. When the client owns the database and you cannot freely inspect or reset it, this is a hard incident.

**The safe sequence — do it in exactly this order:**

1. **Provision an empty database** (no tables). `SSL Mode=Require`.
2. **Run `dotnet Zayra.Api.dll --migrate` to completion** (exit 0). This creates the schema **with** `__EFMigrationsHistory`. `--migrate` returns before any seeder runs (`Program.cs:577-581`), so it never calls `EnsureCreatedAsync`.
3. **Only then start the web service.** Because the tables already exist, the web boot's `EnsureCreatedAsync` is a safe no-op, and the DB stays migration-tracked.

> **Rule:** the web service must **never** connect to the client database before `--migrate` has completed. Run the migrate job first (or as the first step of a coordinated release); route the web service at the DB only after it succeeds.

### 6.2 Schema-drift risk when you lack DB access

The client DB's schema must stay migration-tracked (§6.1). Note that EF has two schema-creation paths — the migration path (`--migrate` → `MigrateAsync`, which records history) and the `EnsureCreatedAsync` fallback in `AuthSeeder` (which does **not** record history). We **rely on migrate-first ordering** to guarantee the schema is only ever created by the migration path; the operational guarantee that keeps a client DB healthy is **"migrate first, and never boot the app against an un-migrated/empty database,"** not an assumption that migrations are the only code path that can create tables.

If model/entity changes ship **without** a matching EF migration, the client DB is missing columns and every affected query throws Postgres `42703` ("column does not exist") → 500s. When the client owns the database and you cannot inspect it directly, this is the biggest ongoing failure mode. Mitigations:

1. **Ship every schema change as an EF migration** and apply it via `--migrate` on each release — never hand-edit the client schema, and never let a first web boot create the schema via the `EnsureCreatedAsync` fallback (§6.1).
2. **Order the release migrate-first**, so the running code never sees a schema older than it expects.
3. **Verify post-migration** using the health endpoints — `GET /health` reports a live table count (`Program.cs:517-535`), and `GET /health/ready` returns readiness evidence (HTTP 503 until ready, `Program.cs:492-498`). Confirm both before routing traffic.

> Footnote: a `--purge-demo` one-off also exists (`Program.cs:593-599`: deactivates demo tenants, then exits). It is irrelevant to a clean dedicated deployment and should never be run against a client DB.

## 7. Secrets, rotation, residency & first-run

### Secrets are per-client
Every client gets its **own** `Jwt__SigningKey`, `Jwt__TenantAudience`, `Jwt__PlatformAudience`, database credentials, and `SeedAdmin__Password`. Store them in the client's secret manager (or ours, scoped per client) — **never** in `appsettings.json`, the repo, or a shared vault path.

**Why reusing `Jwt__SigningKey` across clients is dangerous:** tokens are HMAC-signed with this key, and the app validates any token that verifies against it. Anyone holding the key can **forge** a valid admin token. If two deployments share a key, a token minted for (or leaked from) client A is cryptographically valid at client B — cross-client account takeover. A unique key per deployment contains any key compromise to that one client. Unique per-client audiences add a second boundary (a tenant token is rejected on platform routes and vice-versa).

**Do not enable the cross-tenant platform admin on a dedicated deployment.** Leave `PLATFORM_ADMIN_EMAIL` and `PLATFORM_ADMIN_PASSWORD` **unset**. With them unset the platform-admin login returns HTTP 503 (`PlatformController.cs:155-156`) and no `PlatformUser` is seeded — the intended disabled state for a single-tenant client. If you copy them from a shared template you create a god-mode account that can act across tenants and whose JWT is signed with the **same** `Jwt__SigningKey` as ordinary tenant tokens — exactly the boundary the dedicated SKU exists to remove.

### Rotation
- **JWT signing key / audiences:** rotate on suspected exposure or on a schedule. Rotating the signing key invalidates existing tokens, so users simply re-authenticate — plan a low-traffic window.
- **Database credentials:** rotate the Postgres user/password in the client's secret store, then update `ConnectionStrings__Default` and redeploy.
- **Bootstrap admin password:** see first-run below.

### Data residency & compliance (HR PII)
The app stores sensitive HR PII — employees, salaries, national IDs, passports, payroll. The **Dedicated / client-hosted DB** SKU is what lets a client keep that PII in their own region/cloud/VPC to satisfy residency and regulatory requirements; **Shared SaaS** keeps it in our region. Sensitive fields are gated behind the `employees.sensitive` permission and masking (see `SECURITY_POSTURE.md` / `SECURITY_BLOCKERS.md`). Confirm the client's residency and retention requirements **before** picking the SKU — it is the primary driver of the choice (§3).

### First-run admin (force password creation)
The bootstrap admin is created from `SeedAdmin__Email` / `SeedAdmin__Password` with no forced reset in code. Operational procedure for a dedicated client:
1. Set a **unique, strong** `SeedAdmin__Password` (never leave the `appsettings` fallback `ChangeMe123!` — because there is no fail-fast, omitting it silently ships a default-credential admin, §4 Tier-2).
2. **Verify the default credential does NOT work:** attempt to log the seeded admin in with the password `ChangeMe123!` and confirm it is **rejected**. Because nothing in code fails-fast on a missed `SeedAdmin__Password`, this manual check is your only automatic-catch substitute — do it before routing any traffic.
3. On first login (with your real seed password), immediately change the admin password.
4. Rotate the bootstrap value out of the environment afterward (or set `MustChangePassword` on the account via admin tooling so the next login forces a change).
5. Hand the client a fresh credential through a secure channel — never email the seed password in clear text.

## 8. Go-live checklist — onboarding a dedicated client

Ordered so the client DB is migrated **before** the web service is ever pointed at it (§6.1). Do the steps top to bottom.

**Decide & configure**
- [ ] SKU confirmed as **Dedicated / client-hosted DB** and residency/compliance requirements captured (§3, §7).
- [ ] Client Postgres provisioned in the agreed region/cloud as an **empty** database (no tables), `SSL Mode=Require`. **Do not start the web service against it yet.**
- [ ] `ConnectionStrings__Default` set to the dedicated DB and stored in the client's secret manager.
- [ ] `ASPNETCORE_ENVIRONMENT=Production` set.
- [ ] `Jwt__SigningKey` (≥64-char, unique), `Jwt__TenantAudience` (unique), `Jwt__PlatformAudience` (unique, ≠ tenant) all set.
- [ ] `SeedAdmin__TenantName` / `SeedAdmin__TenantSlug` / `SeedAdmin__Email` / `SeedAdmin__Password` set to the client's org and a **strong, unique** one-time password (never left to the default `ChangeMe123!`).
- [ ] `SeedAdmin__SeedDemoData=false`; `SEED_DEMO_DATA` and `SEED_ENTERPRISE_TEST_DATA` unset/false (no demo data on a client DB).
- [ ] `PLATFORM_ADMIN_EMAIL` and `PLATFORM_ADMIN_PASSWORD` left **unset** (no cross-tenant god-mode account; platform login stays 503) (§5, §7).
- [ ] `Database__RunMigrationsOnStartup=false`; `PORT` (and `CORS_EXTRA_ORIGINS` for the client frontend) set; `REDIS_URL` set if running >1 instance.

**Migrate first (before any web boot)**
- [ ] Run the one-off `dotnet Zayra.Api.dll --migrate` job against the **empty** client DB and confirm it completes (exit 0). This also exercises the JWT/env fail-fast, so a bad JWT/env config is caught **here**, before the web service starts (§6).
- [ ] Confirm the migrate job created `__EFMigrationsHistory` (schema is migration-tracked, not an `EnsureCreated` artifact — §6.1).
- [ ] **RULE observed:** the web service has **not** been connected to the client DB at any point before `--migrate` completed (§6.1).

**Start the web service & verify**
- [ ] Only **after** `--migrate` succeeded, start the web service.
- [ ] `GET /health` returns healthy with a non-zero table count; `GET /health/ready` returns ready (200).
- [ ] Tier-1 config present (statutory/GOSI/pricing); Tier-2 tenant + admin present; **no** demo tenants present.
- [ ] **Default-credential check:** attempt an admin login with `ChangeMe123!` and confirm it is **rejected** (proves `SeedAdmin__Password` was really set — there is no fail-fast to catch a miss, §4/§7).
- [ ] Admin first login completed with the real seed password, password changed, bootstrap secret rotated out (§7).

**Operationalise**
- [ ] Backups/PITR, monitoring, and DR configured on the client DB and app service; rotation runbook handed over.
