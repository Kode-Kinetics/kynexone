# KynexOne

**One Platform for Every Workforce Operation**

AI-Powered HRM and Workforce Operating System.

Developed by [Kode Kinetics](https://www.kodekinetics.com/). Visit the company website for business enquiries; use this repository for technical documentation and development.

## Stack
- Frontend: Next.js (App Router) + TypeScript + Tailwind CSS — deployed on Vercel
- Enterprise API: .NET 8 Web API + EF Core — deployed on Render
- Database: PostgreSQL (Neon)
- Document storage: S3-compatible object storage (Backblaze B2)

> Internal note: the .NET assemblies and namespaces are still `Zayra.*`, and the Render hostname
> is still `zayra-ai-workforce.onrender.com`. Both predate the KynexOne rebrand and are load-bearing
> — the namespace appears in 713 files including every migration's `[DbContext]` attribute, and the
> hostname is fixed at Render service creation and cannot follow a rename. Neither is customer
> visible; do not "tidy" them without a planned migration.

## Run Frontend
cd frontend
npm install
npm run dev

## Run .NET API
cd backend-dotnet/Zayra.Api
dotnet restore
dotnet run
Swagger: http://localhost:5117/swagger or printed launch URL

## Run the stack in Docker
`docker compose up -d --build` (API on :5117, UI on :5173). The API container runs as the
unprivileged `app` user (uid 1654), not root. A `zayra-documents` volume created by an older,
root-running image is owned by root, and document uploads then fail with "permission denied".
Fix it once, either by dropping the local volumes (`docker compose down -v` — deletes local data)
or by keeping them and handing the directory over:
`docker compose run --rm --no-deps --user root --entrypoint chown backend -R 1654 /app/storage`.

## Run Node AI Service
cd backend-node
cp .env.example .env
npm install
npm run dev

## Database
The enterprise API uses PostgreSQL with EF Core migrations. The legacy MySQL instruction is superseded. Follow [deployment and rollback](docs/DEPLOY_ROLLBACK_RUNBOOK.md) and [release parity](docs/RELEASE_CANDIDATE_AND_PARITY.md); migrations and the expected migration manifest must agree before a backend release serves traffic.

## Product and implementation documentation
Use the [product handbook](docs/product-handbook/README.md) for product availability, administrator and user procedures, architecture, security/privacy, integration contracts, release operations and client implementation. The [full completion contract](docs/product-handbook/full-capability-closure-contract.md) covers all 36 comparative feature families. Implemented, bounded tested, enabled and production accepted are separate states.

## Modules
- Command Center (Dashboard)
- Workforce (Employees, Departments, Organization)
- Time & Leave (Attendance, Leave, Overtime, Shifts)
- Payroll (Runs, Payslips, Loans & Advances)
- Talent (Recruitment, Onboarding)
- Performance (Reviews, KPIs)
- Operations (Approvals, HR Request Center, Documents, Compliance)
- Reports & Analytics
- Administration (Users, Roles, Permissions, Tenant Admin, Setup)
- KynexOne AI (advisory assistant)

> **Note:** The internal .NET solution and namespaces retain the `Zayra.Api`
> project name for build stability; this is not user-facing. All UI, copy, and
> product surfaces are branded **KynexOne**.
