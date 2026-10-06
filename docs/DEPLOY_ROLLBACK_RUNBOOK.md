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

   **Shutdown drain.** On SIGTERM the old instance answers `/health/ready` with
   `503 {"status":"draining"}` straight away (no database call), keeps serving for
   `Shutdown__ReadinessDrainSeconds` (default 0 — set it to about 5 only once two or more instances
   sit behind a balancer; on today's single Render instance with a disk it would only add downtime),
   then stops accepting and gives in-flight requests up to `Shutdown__TimeoutSeconds` (default 30).
   The drain delay runs inside that timeout, not on top of it, and is capped to leave in-flight
   requests at least 10s of it, so the worst case is about 30s;
   Render's default `maxShutdownDelaySeconds` is 30. `/health/live` is unchanged. See `ShutdownDrain.cs`.

   **Rolling back a migration.** Running a migration's `Down` (`dotnet ef database update <previous>`)
   is itself a contract-phase change: do it only after the code has been rolled back to a release that
   no longer uses what `Down` removes, and take a Neon branch first.

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

> **This covers the backend only.** The frontend deploys by different rules — Vercel auto-deploys
> every push to `main` while this pipeline waits for an approval — so a backend rollback can leave
> the UI ahead of the API. Before rolling anything back, run
> `./scripts/check_deployment_parity.py` to see which tier is ahead and by how much, and read
> [`RELEASE_CANDIDATE_AND_PARITY.md`](RELEASE_CANDIDATE_AND_PARITY.md) for the two-tier procedure
> and the split-brain remedy (roll the Vercel alias, do not rebuild).

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

- **Grade loan limits (`20261006000100_AddGradeLoanLimits`, `20261006000200_AddGradeNameArAndLoanOffering`).** Rolling the *app* back to
  a release before grade limits leaves the columns in place but **stops enforcing them**: loan types with
  "Limit this loan type by grade" on, and companies that switched a loan type off, accept requests on policy
  rules alone until the release is restored. Take a Neon branch before rolling back, and list what is
  affected with `SELECT id, code FROM loan_types WHERE grade_limited` and
  `SELECT company_id, loan_type_id FROM loan_policies WHERE is_active AND NOT is_offered`. Both
  `Down()` migrations refuse to run while those rows exist.

- **Before deploying `20261006000200_AddGradeNameArAndLoanOffering` (owner runs this; agents never touch production).** The
  migration adds `ck_loan_types__interest_free` as `NOT VALID`: legacy rows survive, but any edit to an
  interest-bearing loan type is refused from then on. List them first, read-only:

  ```sql
  SELECT lt.tenant_id, lt.id, lt.code, lt.name_en, lt.is_interest_free, lt.interest_rate,
         count(el.id) FILTER (WHERE el.status IN ('Pending','Approved'))          AS pending_or_approved_loans,
         count(el.id) FILTER (WHERE el.status IN ('Active','Overdue'))            AS disbursed_open_loans
  FROM loan_types lt
  LEFT JOIN employee_loans el ON el.loan_type_id = lt.id AND NOT el.is_deleted
  WHERE NOT lt.is_deleted AND (NOT lt.is_interest_free OR lt.interest_rate <> 0)
  GROUP BY lt.tenant_id, lt.id, lt.code, lt.name_en, lt.is_interest_free, lt.interest_rate
  ORDER BY lt.tenant_id, lt.code;
  ```

  Zero rows is expected (the API has refused interest since before this release). Once any rows are cleaned,
  `ALTER TABLE loan_types VALIDATE CONSTRAINT ck_loan_types__interest_free;` makes the rule cover history too.

### 3. Re-verify before restoring traffic
- `/health/ready` must read `ready` with `pendingMigrations: 0`.
- Never promote an image whose migration has not been applied — the `/health/ready` gate (and the
  optional `preDeployCommand`) enforce this automatically, but confirm manually after any manual deploy.

## Pre-deploy checklist — KSA WPS pilot

1. **Nationality audit.** Run the read-only query in the next section and hand the list to payroll
   (past payslips of those employees carried no employee GOSI; nothing is recomputed automatically).
2. **Bank-file settings per legal entity:** MOL establishment ID (as shown in Qiwa), the 16-digit ANB
   main account, organisation name and three address lines, company name, narrative, batch type; the
   10-digit national unified number if ANB auto-WPS is on. If a GCC WPS agent ID is also set it must equal
   the MOL establishment ID, or the export is refused.
3. **Pay-redirection guard.** A pending approval-gated change to IBAN, beneficiary details, account
   number or routing code blocks the bank export for that employee. A CSV import never writes bank
   details for an EXISTING employee (they go to approval); a NEW employee's imported bank details are
   flagged in the import warnings for verification before the first payroll.
3a. **Beneficiary BIC.** One resolver serves pre-lock, export and the SIF check: the approved
   `Employee.WpsBankDetails.bicCode`, else the payroll profile's `BankRoutingCode` (case-insensitive). An
   ANB-to-ANB credit needs a 16-digit ANB account number with BIC `ARNBSARI` in either place.
4. **Cash / cheque employees are supported at go-live** — there is no "no cash/cheque" condition. Set
   payment method `Cash` or `Cheque` on the payroll profile. The flow:
   - warned before Lock (`PAID_OUTSIDE_BANK_FILE`, stronger `…_WITH_IBAN` when a valid IBAN is on file) and
     acknowledged by count at Approve; the acknowledged list is sealed into the approval;
   - if the list changes after approval, Lock sends the run back for approval (re-validating alone does not
     make it lockable); otherwise Lock freezes the methods and the batch reads them from there;
   - the bank batch settles only its own employees; each cash/cheque wage is recorded per employee
     ("Record payment outside the bank file") — serialized per batch, once per employee — and can be
     reversed with a reason (not by the employee, not after Reconciled) and recorded again;
   - Salaries Payable (2100) is clear, and the batch can reach Reconciled, only after the bank batch is
     settled and every outside payment is recorded;
   - a run locked before this release falls back to the live profile, but batch creation first lists the
     cash/cheque employees and requires `expectedOutsideBankCount`.
   Cash wages count against Mudad WPS compliance.
5. **Two payroll users with `payroll.export`** per legal entity: the person who generates the bank/WPS
   file or uploads the evidence cannot mark the batch Accepted.
6. Leave `QIWA_USE_LIVE_ADAPTER` unset (Qiwa data check only).

### Final settlements and the Art. 92/93 cap

A final-settlement run recovers an outstanding loan or advance through the ordinary `LOAN_EMI` /
`ADVANCE_EMI` lines, which are debt-type. Recovering a large balance from one final wage can therefore
exceed half of that wage and raise `DEDUCTIONS_EXCEED_HALF_WAGE`, blocking Approve and Lock. Either
reschedule the recovery (leave the remainder as a receivable) and re-process, or have an approver who is
neither the run's preparer nor the leaver override it citing a labour court / commission decision or
other lawful written basis, with its reference (`documentReference`). The bank export honours that
override. No legal conclusion about when set-off is permitted is built into the product.

## Saudi nationality normaliser — pre/post-deploy diagnostic (read-only)

GOSI used to recognise only `SA`, `SAU`, `Saudi`, `Saudi Arabia`, `Saudi Arabian`, compared without
trimming. The shared normaliser (`Infrastructure/Compliance/SaudiNationality.cs`) also accepts `KSA`,
`SaudiArabia`, `Kingdom of Saudi Arabia` and the Arabic `سعودي` / `سعودى` / `سعودية` / `السعودية` /
`المملكة العربية السعودية`, and trims. Employees recorded with one of
the newly recognised values were classified as expatriates: **their past payslips carried no employee GOSI.**
From the next processed run they are Saudi. **Do not recompute or edit filed/locked payslips;** take the
list to payroll and the GOSI portal for a reviewed correction.

Run on the production database (SELECT only) to count and list those employees:

```sql
-- Employees whose nationality is newly classified as Saudi (was NonSaudi before this release).
SELECT tenant_id, company_id, id AS employee_id, employee_code, status, nationality
FROM employees
WHERE NOT is_deleted
  AND (lower(btrim(nationality)) IN ('ksa', 'saudiarabia', 'kingdom of saudi arabia',
                                     'سعودي', 'سعودى', 'سعودية', 'السعودية', 'المملكة العربية السعودية')
       OR (nationality <> btrim(nationality)
           AND lower(btrim(nationality)) IN ('sa', 'sau', 'saudi', 'saudi arabia', 'saudi arabian')))
ORDER BY tenant_id, company_id, employee_code;

-- Count only, per tenant.
SELECT tenant_id, count(*) AS newly_saudi
FROM employees
WHERE NOT is_deleted
  AND (lower(btrim(nationality)) IN ('ksa', 'saudiarabia', 'kingdom of saudi arabia',
                                     'سعودي', 'سعودى', 'سعودية', 'السعودية', 'المملكة العربية السعودية')
       OR (nationality <> btrim(nationality)
           AND lower(btrim(nationality)) IN ('sa', 'sau', 'saudi', 'saudi arabia', 'saudi arabian')))
GROUP BY tenant_id;
```

The second branch catches previously recognised spellings stored with surrounding spaces (the old
comparison did not trim). Zero rows means no past payslip was affected.

## Invariants
- **Schema leads code.** Migrations apply in `migrate-backend` before the deploy hook fires.
- **Single trigger.** `autoDeploy: false`; the CI hook is the only deploy path.
- **Fail-closed.** Missing `PROD_DATABASE_URL` or `RENDER_DEPLOY_HOOK_URL` fails the pipeline loudly.
- **Traffic gate.** `/health/ready` = 503 while migrations are pending, on every plan.
