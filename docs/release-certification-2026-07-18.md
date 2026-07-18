# Release Certification Evidence - 2026-07-18

## Baseline Freeze

- Branch: `main`
- Frozen commit: `4d2455146909f699a285f47a4a30a9a37a1d12e6`
- `origin/main`: `4d2455146909f699a285f47a4a30a9a37a1d12e6`
- Last commit: `4d24551 Scope approval decisions by caller`
- Product worktree status: clean. Only existing `.claude/worktrees/*` metadata entries are modified and excluded from release evidence.
- Protected slice: approval-accountability routing and decision scoping at `4d24551`; do not redesign unless certification evidence proves a regression.

## Surface Inventory

- Frontend route/layout files discovered under `frontend/app`: tenant workspace, platform admin, public/auth/legal, payroll, performance, HR, security, compliance, setup, people, reports, and support surfaces.
- Frontend source/view/component files discovered under `frontend/app`, `frontend/src/views`, `frontend/src/components`: 140 TypeScript/React files.
- Backend code files discovered under controllers, models, infrastructure, and application layers: 305 C# files.
- EF migration files discovered: 58 C# migration artifacts.
- Automated test files discovered across backend and frontend e2e: 129 files.

## Certification Matrix

| Area | Evidence Target | Status | Result |
| --- | --- | --- | --- |
| Baseline integrity | `git status`, `git rev-parse HEAD`, `git rev-parse origin/main` | Complete | Pass |
| Backend unit/integration tests | `dotnet test backend-dotnet/Zayra.Api.Tests/Zayra.Api.Tests.csproj --no-restore` | Complete | Pass: 967 passed, 0 failed, 0 skipped |
| Frontend production build | `npm run build` from `frontend` | Complete | Pass: Next.js production build compiled 64 routes |
| Frontend e2e journeys | `npx playwright test --list` plus focused browser probes | Partial | 132 tests discoverable; focused employee create/edit modal probes passed |
| Security source sweep | auth defaults, tenant/company scope, anonymous endpoints, placeholder/demo paths | Partial | Default-deny auth present; dangerous local DB/demo defaults fixed |
| DB certification | migrations, tenant/company filters, soft delete/retention, import shape | Partial | Fresh local Postgres migrated successfully; 283 tables visible via health |
| Cross-module business journeys | employee, hierarchy, approvals, leave, attendance, payroll, appraisal, DSAR | Partial | Employee create/edit verified locally; wider journeys still need seeded scenario execution |
| Nonfunctional readiness | build/start health, API health, audit/accountability behavior | Partial | API boot and health/ready passed on isolated local DB |

## Automated Evidence

- Backend tests passed with warnings only. Notable warnings: QuestPDF resolved `2025.1.0` for requested `2024.12.4`, nullable assignment warnings in setup/platform/recruitment files, and xUnit analyzer style warnings. No failing backend tests.
- Frontend production build passed. Next.js warned that it inferred `/Users/zackkhan/package-lock.json` as workspace root because multiple lockfiles exist; this is a configuration hygiene item, not a build failure.
- After setting `outputFileTracingRoot`, frontend production build passed again and the workspace-root warning disappeared.
- E2E discovery found 132 runnable Playwright tests across 18 files. Several group-company Playwright tests intentionally call `test.skip()` for missing seed/UI-in-flight conditions, so e2e coverage is not yet a strict release-certification gate.
- `docker compose config` now resolves to local `postgres:16-alpine`, `ConnectionStrings__Default=Host=postgres;Port=5432;...`, and `SeedAdmin__SeedDemoData=false`.
- Fresh local Postgres migration check passed with `dotnet run --project backend-dotnet/Zayra.Api/Zayra.Api.csproj --no-build -- --migrate`.
- API boot check passed against isolated local Postgres. `/health` returned healthy with DB connected and 283 tables; `/health/ready` returned ready with database OK, fallback-memory Redis mode, sandbox Qiwa, SMTP not configured, and 1 active tenant.
- Tenant login check passed for seeded admin `admin@zayra.local` on slug `zayra`; access and refresh tokens were returned.
- Focused Playwright create-modal probe passed: typed `ABC123 Focus Check` into Add Employee without focus loss and saved a Draft employee into the isolated DB.
- Focused Playwright edit-modal probe passed: typed `Preferred Typing Check` without focus loss, saved the edit, and confirmed `Emirates ID` was not present in that edit modal state.

## Findings

- RC-001 Coverage gap: frontend e2e contains conditional skips for group-company setup, compliance profile selectors, company switcher, and seed-dependent flows. This prevents claiming complete enterprise parity from automated e2e evidence alone.
- RC-002 Fixed: local infrastructure defaults were inconsistent with the real backend provider. `docker-compose.yml`, `.env`, `.env.example`, and `appsettings.json` now use Postgres rather than stale MySQL defaults.
- RC-003 Fixed: normal local Compose runs previously allowed demo seeding by default. `SeedAdmin__SeedDemoData` now defaults false; demo data requires explicit `SEED_DEMO_DATA=true` against an isolated database.
- RC-004 Fixed locally: a gitignored `docker-compose.override.yml` on this machine pointed the backend to a shared external database. It has been neutralized to an empty safe placeholder so local certification cannot mutate shared tenant data by accident.
- RC-005 Fixed: frontend build tracing had an ambiguous workspace root because of multiple lockfiles. `frontend/next.config.ts` now sets `outputFileTracingRoot` to the frontend directory, and the warning is gone.
- RC-006 Remaining certification gap: full mutating e2e should be run only after creating an isolated seeded enterprise scenario. Running the existing demo-suite against shared or live data would be unsafe and could recreate the tenant-data-loss risk.
