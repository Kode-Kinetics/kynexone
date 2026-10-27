# KynexOne

**One Platform for Every Workforce Operation**

HRM and Workforce Operating System, installed on the organisation's own server.

Developed by [Kode Kinetics](https://www.kodekinetics.com/). Visit the company website for business enquiries; use this repository for technical documentation and development.

## How KynexOne is delivered
Each installation serves one organisation (with as many companies as its licence allows) on a server the organisation
owns. There is no hosted service: the organisation runs the database, holds the documents and takes the backups. What
an installation may do (employees, users, companies, modules, AI allowance, expiry) comes from a signed licence file.

- Installing and operating: [client installation guide](docs/CLIENT_INSTALLATION.md) and the installation kit in
  [`deploy/client/`](deploy/client/README.md).
- Building a release bundle: [`deploy/release/make-bundle.sh`](deploy/release/make-bundle.sh) and the `release`
  workflow.

## Stack
- Web: Next.js (App Router) + TypeScript + Tailwind CSS
- API: .NET 8 Web API + EF Core
- Database: PostgreSQL 16; Redis for caching and rate limits
- Documents: a local volume, or an S3-compatible store the organisation runs
- Mobile: Expo (React Native), pointed at the organisation's server on first start
- KynexBridge: Node agent that polls attendance devices on a branch network
- Reverse proxy (client kit): Caddy

> Internal note: the .NET assemblies and namespaces are still `Zayra.*`. They predate the KynexOne rebrand and are
> load-bearing — the namespace appears in every migration's `[DbContext]` attribute. Not customer visible; do not
> "tidy" them without a planned migration.

## Develop

### The stack in Docker
`docker compose up -d --build` (API on :5117, UI on :5173). Then create the organisation once:
`docker compose run --rm backend --setup-organisation` (or `scripts/ci/ci-licence.sh` plus
`e2e/bootstrap/setup-organisation.sh` for the e2e world). Builds that should accept the CI licence key need
`CI_LICENCE_KEY=true`; `AI_PROVIDER=none` keeps AI off.

The API container runs as the unprivileged `app` user (uid 1654). A `zayra-documents` volume created by an older,
root-running image is owned by root and uploads then fail with "permission denied"; fix it once with
`docker compose run --rm --no-deps --user root --entrypoint chown backend -R 1654 /app/storage`.

### Web
```
cd frontend
npm install
npm run dev
```

### API
```
cd backend-dotnet/Zayra.Api
dotnet restore
dotnet run
```
Swagger: http://localhost:5117/swagger

## Database
PostgreSQL with EF Core migrations, applied at start-up or by the installer. Schema changes follow the
[`kynexone-schema` skill](.claude/skills/kynexone-schema/SKILL.md) and the documents under `docs/schema/`.

## Product and implementation documentation
Use the [product handbook](docs/product-handbook/README.md) for product availability, administrator and user procedures, architecture, security/privacy, integration contracts, release operations and client implementation. The [full completion contract](docs/product-handbook/full-capability-closure-contract.md) covers all 36 comparative feature families. Implemented, bounded tested, enabled and production accepted are separate states. Documents describing the retired hosted (SaaS) delivery are kept in [`docs/_archive/`](docs/_archive/) for history only.

## Modules
- Command Center (Dashboard)
- Workforce (Employees, Departments, Organization)
- Time & Leave (Attendance, Leave, Overtime, Shifts)
- Payroll (Runs, Payslips, Loans & Advances)
- Talent (Recruitment, Onboarding)
- Performance (Reviews, KPIs)
- Operations (Approvals, HR Request Center, Documents, Compliance)
- Reports & Analytics
- Administration (Users, Roles, Permissions, Organisation Admin, Setup)
- KynexOne AI (advisory assistant; off unless configured and licensed)

> **Note:** The internal .NET solution and namespaces retain the `Zayra.Api`
> project name for build stability; this is not user-facing. All UI, copy, and
> product surfaces are branded **KynexOne**.
