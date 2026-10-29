# Data entry paths

Owner's rule: the installation's organisation is created **only** by the one-time setup command, and
every user, test account and demo account is created by that organisation's own administrators through
the product. Nothing enters the database through a seeder, fixture, demo runner or boot-time side
door. `Zayra.Api.Tests/Security/NoSideDoorDataTests.cs` fails the build if a
`*Demo*/*Fixture*/*Sample*` `Seeder`/`Runner` type appears in `Zayra.Api`, or if `Program.cs`
references one, `SEED_DEMO_DATA`, `SEED_ENTERPRISE_TEST_DATA`, `--purge-demo` or `EnsureCreated`.

KynexOne is client-installed: one installation serves one organisation (which may own several
companies). There is no platform console, platform account or vendor-side tenant creation.

## 1. One-time organisation setup
`dotnet Zayra.Api.dll --setup-organisation [setup.json]` (`Infrastructure/Provisioning/OrganisationSetupCommand.cs`).
- Refuses unless the database is reachable and fully migrated (the same migration-parity check as
  `/health/ready`).
- Calls `OrganisationProvisioningService` — the **only** code that inserts the organisation row
  (`Zayra.Api.Tests/Security/OrganisationCreationDoorTests.cs`). It creates, in one transaction:
  the organisation, its localisation (country, operating currency and time zone from the home
  country), the first company, the standard role set, GL defaults, pay components, the provisioning
  bundle, the first administrator and two audit rows.
- Refuses when an organisation already exists. A transaction-scoped advisory lock serialises
  concurrent runs, so two setups on an empty database create one organisation.
- Input: the JSON file, then `OrganisationSetup__*` environment variables for anything the file leaves
  out. The administrator password is prompted (never echoed) when neither supplies it; it must be at
  least 10 characters and not a known default. The home country must have a country pack.

Until the database restructure the organisation is stored as the single row of `tenants`.

### 1a. Moving a customer off the retired hosted service (instead of 1)
`dotnet Zayra.Api.dll --import-legacy <dir>` (`Infrastructure/Provisioning/LegacyImportCommand.cs`) loads one
customer exported by the vendor's `tools/TenantExporter`. It is the only other way the organisation row comes
to exist, and it keeps the same invariant: it refuses unless the installation has no organisation, runs in
one transaction, and commits only when the exported payroll, leave, loan and GL totals match exactly. It is
raw SQL, so `OrganisationCreationDoorTests` (which scans for EF inserts) does not see it; this entry is its
registration.

## 2. The organisation's own administrators
Once the organisation exists, its authenticated administrators add users and business data through
the product: access management, employee portal access, migration import, the create-company wizard,
SCIM provisioning from the organisation's own identity provider, and opt-in features with their
configuration (`PUT /api/tenant-admin/features/{key}`). Those are product features, audited, and
none of them can create an organisation.

## 3. Per-organisation provisioning defaults
Applied by the setup command, and idempotently afterwards:
- `TenantProvisioningBundle.ProvisionAsync` (country packs, master data, default policies,
  notification templates, reference lists).
- `GlDriverSeeder.SeedTenantDefaultsAsync`, `PayComponentSeeder.SeedTenantDefaultsAsync`.
- `AuthSeeder.EnsureTenantRolesAsync` (standard role set) and `EstablishmentSeeder`.
- `TenantDefaultsBackfill` at boot: insert-if-absent HR letter templates and timesheet route.
  Kill switch `TenantDefaults__Backfill=false`.
- Admin re-install actions: `POST /api/hr-letters/templates/seed-defaults`,
  `POST /api/finance/gl/seed-defaults`.

## 4. Reference-data seeders (global, version-controlled, run every boot, idempotent)
- `AuthSeeder.SeedAsync` — permission catalogue and the Admin-role permission backfill only.
  It creates no organisation, company or user and never calls `EnsureCreated`.
- `GosiRuleSeeder`, `StatutoryRuleSeeder`, `NitaqatReferenceSeeder` — statutory rules.

## 5. Schema
EF Core migrations only (`dotnet Zayra.Api.dll --migrate`).

## 6. Test and demo data (the e2e fixture world)
There is no seeder, so a migrated database has no organisation to test against.
`frontend/e2e/bootstrap/` builds the fixture world through the product's own APIs — no SQL, no side
door — and CI runs it after migrations, before any spec lane:

```bash
cd frontend && npx playwright test -c e2e/bootstrap/playwright.bootstrap.config.ts
```

> **Being reworked (implementation prompt WP0/WP2.5).** The bootstrap still provisions through the
> retired platform API and must move to `--setup-organisation` plus the organisation's own APIs, with
> one organisation holding several companies.

- `frontend/e2e/world.ts` is the single declaration of every fixture identity; the specs and the
  bootstrap both read it, so they cannot drift.
- Idempotent and non-destructive: re-running it is a no-op.
- Guarded by `disposable-host.guard.ts` — loopback (or an explicitly allowlisted throwaway host)
  only, because it writes users and data.
- Passwords come from the environment. CI generates them per run; outside CI a documented local
  default is used. Nothing here is a production credential.

## Repair passes (create nothing)
`Infrastructure/Boot/CompanyScopeBackfill.cs` runs at boot and only assigns rows with a null
`CompanyId` to the organisation's existing company. An organisation with no active company is logged
and skipped — its first company comes from the setup command. Kill switch
`CompanyScope__Backfill=false`.
