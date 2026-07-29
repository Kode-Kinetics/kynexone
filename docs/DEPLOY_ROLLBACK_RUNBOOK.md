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
   returns **503 while `GetPendingMigrations()` is non-empty** (`ProductionReadinessEvidence.ResolveStatus`),
   so an image whose migrations have not been applied is **never promoted to serve traffic** — the
   previous instance keeps serving. This backstop works on any plan, including `plan: free`.

### Required GitHub secrets
- `PROD_DATABASE_URL` — production Neon connection string (used only by `migrate-backend`).
- `RENDER_DEPLOY_HOOK_URL` — Render deploy hook for the web service.

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
