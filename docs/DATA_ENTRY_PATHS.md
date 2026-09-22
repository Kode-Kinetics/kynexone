# Data entry paths

Owner's rule: every tenant, user, test account and demo account is created **only** through the
platform admin. Nothing enters the database through a seeder, fixture, demo runner or boot-time
side door. `Zayra.Api.Tests/Security/NoSideDoorDataTests.cs` fails the build if a
`*Demo*/*Fixture*/*Sample*` `Seeder`/`Runner` type appears in `Zayra.Api`, or if `Program.cs`
references one, `SEED_DEMO_DATA`, `SEED_ENTERPRISE_TEST_DATA`, `--purge-demo` or `EnsureCreated`.

## 1. One-time platform-owner bootstrap
`Infrastructure/Seed/PlatformOwnerBootstrap.cs`, called from `Program.cs` at boot.
- Inert unless `PLATFORM_ADMIN_PASSWORD` is set; no-op once any platform user exists.
- On Production / `DEDICATED_DEPLOYMENT` / `CLIENT_DEPLOYMENT` it also needs
  `PLATFORM_ADMIN_BOOTSTRAP=true` and a password of 16+ characters that is not a known default.
- Creates exactly one `PlatformUser` (role Owner). Unset the variables after first boot.

## 2. Platform-admin endpoints (`/api/platform`, policy `PlatformAdmin`)
`Controllers/PlatformController.cs` is the only code that creates a `Tenant`:
- `POST /api/platform/tenants` — tenant + its first company + its first admin user.
- `POST /api/platform/leads/{id}/convert` — tenant + first admin from a sales lead.
- `POST /api/platform/tenants/{tenantId}/users` — further tenant users (incl. test accounts).
- `POST /api/platform/team` — platform operators.

Once a tenant exists, its own authenticated administrators add users and business data through
the tenant API (access management, employee portal access, migration import, SCIM provisioning
from the tenant's own identity provider). Those are product features, audited, and scoped to one
tenant; none of them can create a tenant.

## 3. Per-tenant provisioning defaults
Applied when the platform admin creates a tenant, and idempotently afterwards:
- `TenantProvisioningBundle.ProvisionAsync` (country packs, master data, default policies,
  notification templates, pay components).
- `GlDriverSeeder.SeedTenantDefaultsAsync`, `PayComponentSeeder.SeedTenantDefaultsAsync`.
- `AuthSeeder.EnsureTenantRolesAsync` (standard role set) and `EstablishmentSeeder`.
- `TenantDefaultsBackfill` at boot: insert-if-absent HR letter templates and timesheet route for
  tenants that already exist. Kill switch `TenantDefaults__Backfill=false`.
- Admin re-install actions: `POST /api/hr-letters/templates/seed-defaults`,
  `POST /api/finance/gl/seed-defaults`.

## 4. Reference-data seeders (global, version-controlled, run every boot, idempotent)
- `AuthSeeder.SeedAsync` — permission catalogue and the Admin-role permission backfill only.
  It creates no tenant, company or user and never calls `EnsureCreated`.
- `GosiRuleSeeder`, `StatutoryRuleSeeder`, `NitaqatReferenceSeeder` — statutory rules.
- `PricingConfigSeeder` — pricing parameters and module catalogue.

## 5. Schema
EF Core migrations only (`dotnet Zayra.Api.dll --migrate`).

## 6. Test and demo data (the e2e fixture world)
There is no seeder, so a migrated database has no tenant to test against.
`frontend/e2e/bootstrap/` builds the whole fixture world through §1 + §2 and the tenants' own APIs —
no SQL, no side door — and CI runs it after migrations, before any spec lane:

```bash
cd frontend && npx playwright test -c e2e/bootstrap/playwright.bootstrap.config.ts
```

- `frontend/e2e/world.ts` is the single declaration of every fixture identity; the specs and the
  bootstrap both read it, so they cannot drift.
- Idempotent and non-destructive: re-running it is a no-op, and it never deletes a tenant.
- Guarded by `disposable-host.guard.ts` — loopback (or an explicitly allowlisted throwaway host)
  only, because it writes tenants and users.
- Passwords come from the environment. CI generates them per run; outside CI a documented local
  default is used. Nothing here is a production credential.

## Repair passes (create nothing)
`Infrastructure/Boot/CompanyScopeBackfill.cs` runs at boot and only assigns rows with a null
`CompanyId` to the tenant's existing company. A tenant with no active company is logged and
skipped — its first company comes from `POST /api/platform/tenants`. Kill switch
`CompanyScope__Backfill=false`.
