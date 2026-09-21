# Deploy & Rollback Runbook (P0-4)

Covers the migration-gated deploy pipeline and how to roll back a bad release without
tenant-wide `42703`/`42P01` outages. Read this before promoting or reverting a backend release.

## Deploy pipeline (how a commit reaches production)

1. Push to `main` runs `.github/workflows/ci.yml`:
   - `backend-tests` (build + full test/security suite), `frontend-typecheck`, `secret-scan`, `dependency-scan`.
2. `migrate-backend` (gated on all four above) runs `dotnet ef database update` against the
   production database (`ConnectionStrings__Default` = `PROD_DATABASE_URL` secret). **Schema is
   applied before the new code ships.** A non-zero exit blocks the deploy (fail-closed).
3. `deploy-backend` (gated on `migrate-backend`) POSTs the Render deploy hook (`RENDER_DEPLOY_HOOK_URL`).
   `render.yaml` has `autoDeploy: false`, so this hook is the **only** trigger — no double deploys.
4. Render brings up the new instance and polls `healthCheckPath: /health/ready`. That endpoint
   returns **503 while migrations are pending** (`ProductionReadinessEvidence.ResolveStatus`), so
   the previous instance keeps serving. This backstop works on any plan, including `plan: free`.

   > **This step did not work between 2026-08 and 2026-09-21, and the wording here said it did.**
   > `./Dockerfile` deletes `Migrations/` before publishing (a real fix for an 8 GB builder OOM),
   > and `GetPendingMigrationsAsync()` is *migrations compiled into the assembly minus applied
   > history*. In the deployed image that subtracts from an empty set, so it returned `0` pending
   > for **every** database, permanently. `/health/ready` reported `ready` against a production DB
   > missing twelve migrations, and a release was promoted onto an un-migrated schema on the
   > strength of it. Measured on the live service on 2026-09-21: `{"status":"ready",
   > "pendingMigrations":0}` while production had 70 of 72 migrations applied.
   >
   > It works now because the Docker build records the migration ids into an embedded
   > `Migrations.manifest` *before* stripping the classes, and the readiness check diffs that
   > against `__EFMigrationsHistory`. If neither the assembly nor a manifest knows any migrations
   > the check returns the `-1` unknown sentinel and reports `not_ready` — it **fails closed**
   > instead of reporting a comfortable zero. See `Infrastructure/Operations/MigrationManifest.cs`.

5. **Before any of the above**, two CI gates must pass. Both exist because the checks that were
   supposed to cover this ground did not:
   - `scripts/check_render_env.py` — every key `render.yaml` marks `sync: false` must actually be
     set on the Render service. `sync: false` is a note to a human; Render does not enforce it.
     An unset `Proxy__KnownNetworks` made the app's proxy guard throw, which killed the pre-deploy
     migration job ~10s in, twice, before it reached the database.
   - `scripts/check-migration-visibility.sh` — `ls Migrations/*.cs` must equal
     `dotnet ef migrations list`. Three migrations hand-written without a `[Migration]` attribute
     were invisible to EF — and therefore to *every* tool here, all of which are `dotnet ef`-based —
     for 70 days. 72 files on disk, 69 visible.

### Required GitHub secrets
- `PROD_DATABASE_URL` — production Neon connection string (used only by `migrate-backend`).
- `RENDER_DEPLOY_HOOK_URL` — Render deploy hook for the web service.
- `RENDER_API_KEY` — read access to `srv-d8slkb77f7vs73d2k92g`, for the required-env-var gate.

> **Secret placement is not currently a control.** `PROD_DATABASE_URL` and
> `RENDER_DEPLOY_HOOK_URL` are **repository** secrets, and the `production` GitHub environment
> holds **zero** secrets (verified 2026-09-21). The environment's approval gate therefore protects
> the *job*, not the *credential*: any workflow on any branch can reference either secret without
> an approval. Moving all three to the `production` environment is what would make the boundary
> real. `RENDER_API_KEY` should be created there from the start.

### Optional hardening (paid web instance)
Once the web service is on a Render **Starter** instance, enable the native pre-deploy migrate as
defence-in-depth (pre-deploy commands are not available on `plan:free`):

```yaml
preDeployCommand: dotnet Zayra.Api.dll --migrate
```

`--migrate` runs `MigrateAsync` and exits 0; a non-zero exit aborts the Render deploy. The
builder-time JWT/SeedAdmin fail-fasts also run in `--migrate` mode, so the service's normal
production secrets must be present (they already are on the service).

## Rollback procedure

### 1. Roll back the code (no schema change involved)
- Render dashboard → the web service → **Manual Deploy → Deploy a previous, known-good image**.
- Confirm `/health/ready` returns `{ "status": "ready", "pendingMigrations": 0 }` before re-enabling traffic.

### 2. Roll back a bad release that applied a migration
Our migrations are **additive-only** where possible, so most rollbacks need only step 1 (the old
code ignores the new, nullable columns). Only revert the schema if the new migration is actively
harmful.

- Identify the previous migration name:
  `dotnet ef migrations list --project backend-dotnet/Zayra.Api/Zayra.Api.csproj`
- Apply the tested down-migration via a Render one-off job (or CI, same connection string):
  `dotnet ef database update <PreviousMigrationName> --project backend-dotnet/Zayra.Api/Zayra.Api.csproj`
- Example — `AddRecruitmentCompanyScope`: its `Down()` only **drops** the three additive
  `company_id` columns and their `(tenant_id, company_id)` indexes on `candidates`,
  `job_applications`, `offer_letters`. No pre-existing column or row is touched; the only data lost
  is the new company assignments (re-derivable by `CompanyScopeBackfill` on the next boot).

### 3. Re-verify before restoring traffic
- `/health/ready` must read `ready` with `pendingMigrations: 0`.
- Never promote an image whose migration has not been applied — the `/health/ready` gate (and the
  optional `preDeployCommand`) enforce this automatically, but confirm manually after any manual deploy.

## Invariants
- **Schema leads code.** Migrations apply in `migrate-backend` before the deploy hook fires.
- **Single trigger.** `autoDeploy: false`; the CI hook is the only deploy path.
- **Fail-closed.** Missing `PROD_DATABASE_URL` or `RENDER_DEPLOY_HOOK_URL` fails the pipeline loudly.
- **Traffic gate.** `/health/ready` = 503 while migrations are pending, on every plan.
