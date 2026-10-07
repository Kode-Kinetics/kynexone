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

- **Release A foundation (`20261007000100_ReleaseAEntitlementsAndRenewals`).** Additive only: two new tables
  (`employee_entitlements`, `contract_renewal_cases`), nullable or defaulted columns on `pay_components`,
  `grade_entitlements`, `employee_contracts`, `employee_salary_structures`, `approval_requests` and `employee_loans`,
  and four triggers. Every Release A surface is behind the per-tenant `release_a` opt-in flag, which is **off** unless
  the platform enables it, so rolling the *app* back is safe for every tenant that never had it on. For a tenant that
  did, switch the flag off first (`PUT /api/platform/tenants/{id}/features/release_a {"isEnabled": false}`), then roll
  back the image. `Down()` refuses while any Release A data exists (a frozen package, a renewal case, a skipped
  benefit, a stamped contract chain, a salary basis or Qiwa confirmation, an approval payload, a loan consent, or a
  grade cell using a Release A value type or criterion) — take a Neon branch and fix forward instead. Order: image →
  flag → schema `Down`.
- **Release A R2 dependants soft delete (`20261008000200_ReleaseAR2DependantsSoftDelete`).** Expand-only: adds
  `employee_dependents.is_deleted` (default false), `deleted_at_utc` and `deleted_by`. Roll it back before R0's migration.
  Its `Down()` refuses (`R2_DEPENDANTS_SOFT_DELETED`) while any removed dependant exists, because dropping the column would
  make every removed dependant covered again. Restore or purge those rows deliberately (with HR sign-off), or fix forward.

- **Release A R0b (`20261007000200_ReleaseAContractChainSource`).** Additive: `employee_contracts.chain_source` (nullable)
  and four CHECKs added **NOT VALID** (`chain_source`, `chain_pair`, `renewed_from_counts`, `chain_starts_by_term_start`):
  no table scan at deploy, every new or changed row is checked. **Before the later VALIDATE migration**, run this
  read-only pre-check on each environment; every count must be 0 (a non-zero row is fixed through chain confirm, never by
  hand):
  ```sql
  SELECT tenant_id,
         count(*) FILTER (WHERE NOT (chain_source IS NULL OR chain_source IN ('Derived','Recorded')))            AS bad_chain_source,
         count(*) FILTER (WHERE NOT ((renewal_number IS NULL) = (chain_started_on IS NULL)))                    AS bad_chain_pair,
         count(*) FILTER (WHERE NOT (renewed_from_contract_id IS NULL OR renewal_number >= 1
                                     OR provisional_basis IS NOT NULL))                                         AS bad_renewed_from,
         count(*) FILTER (WHERE NOT (chain_started_on IS NULL OR chain_started_on <= start_date))               AS bad_chain_start
  FROM employee_contracts GROUP BY tenant_id
  HAVING count(*) FILTER (WHERE NOT ((renewal_number IS NULL) = (chain_started_on IS NULL))) > 0
      OR count(*) FILTER (WHERE NOT (renewed_from_contract_id IS NULL OR renewal_number >= 1 OR provisional_basis IS NOT NULL)) > 0
      OR count(*) FILTER (WHERE NOT (chain_started_on IS NULL OR chain_started_on <= start_date)) > 0
      OR count(*) FILTER (WHERE NOT (chain_source IS NULL OR chain_source IN ('Derived','Recorded'))) > 0;
  ```
  Rollback: `Down()` refuses while any term carries HR-recorded history (`chain_source = 'Recorded'`); otherwise it drops
  the four CHECKs and the column. Order: image → schema `Down` (R0b before R0).
  Re-applying R0b after a Down marks every term that still carries a stamped chain (`renewal_number` and
  `chain_started_on` set, `chain_source` dropped with the column) as `Derived` again — they can only have come from
  the census, because recorded history blocks the Down.

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

## Stored full IBANs — post-deploy diagnostic (read-only)

Before the pilot-sensitive-leaks release, an `INVALID_IBAN` payroll validation finding wrote the whole
IBAN into `payroll_validation_results.message`, and the migration import wrote legacy history values into
`employee_histories.old_value` / `new_value` unmasked. New rows carry only the last 4 characters
(`IBAN ***1234 is invalid: …`). **Existing rows are not rewritten by the release.** A run's findings are
replaced the next time it is validated or processed; locked runs keep theirs. Count what is left with
these queries (SELECT only), then decide on a reviewed clean-up:

```sql
-- Validation findings whose message still holds an IBAN-shaped value (2 letters, 2 digits, 11-30 alphanumerics).
SELECT tenant_id, code, count(*) AS rows_with_iban
FROM payroll_validation_results
WHERE message ~ '\m[A-Z]{2}[0-9]{2}[A-Z0-9]{11,30}\M'
GROUP BY tenant_id, code
ORDER BY tenant_id, code;

-- Employee history values that still hold an IBAN-shaped value.
SELECT tenant_id, field_name, count(*) AS rows_with_iban
FROM employee_histories
WHERE old_value ~ '\m[A-Z]{2}[0-9]{2}[A-Z0-9]{11,30}\M'
   OR new_value ~ '\m[A-Z]{2}[0-9]{2}[A-Z0-9]{11,30}\M'
GROUP BY tenant_id, field_name
ORDER BY tenant_id, field_name;

-- Migration batches that still hold the raw package (written before the masked copy, policy "masked-v1").
-- The second pattern catches 10-digit Saudi national IDs / iqama numbers.
SELECT tenant_id, package_type, count(*) AS batches_with_raw_identifiers
FROM migration_import_batches
WHERE package_type = 'MigrationPackage'
  AND (payload_json ->> 'policy') IS DISTINCT FROM 'masked-v1'
  AND (payload_json::text ~ '\m[A-Z]{2}[0-9]{2}[A-Z0-9]{11,30}\M'
       OR payload_json::text ~ '(^|[^0-9])[12][0-9]{9}([^0-9]|$)')
GROUP BY tenant_id, package_type
ORDER BY tenant_id;
```

The migration batch's `payload_json` now keeps the checksum, the row count per section and a masked copy of
each section. Nothing reads it back to run an import: Resume takes the package again from the caller and
matches it on the checksum, so an old raw payload can be cleared without breaking a resume.

Zero rows means nothing is left to clean up. The pattern is deliberately broad, so review what it finds
before acting on it. `PayrollIbanMaskingPostgresTests` runs the first and third queries, so keep them in sync.

## Migration import — who used it to create roles or grant access (read-only exposure check)

Before this fix the migration import (`POST /api/migrations/preview|commit|{id}/resume`) accepted `roles` and
`users` sections from any caller the controller admits — Admin, HR Manager, and through `employees.bulk_import`
also HR Officer and HR Director — although the Access screen requires `security.manage`. An HR Manager could
create roles and give any account, their own included, the Admin role. Those sections wrote **no per-entity
audit row**; the evidence is the batch ledger (`migration_import_batches.payload_json` keeps the whole package and
`created_by` the committer) and the `migration.import_completed` audit row.

**Detection only — run it, read it, do not "fix" from it.** Review each hit with the tenant owner; removing a role or
an account is a decision for the Access screen, with its own audit. Do **not** run it against production without
the owner's say-so. SELECT only:

```sql
-- 1. Every committed (non-dry-run) migration package that carried a roles or users section, who committed it,
--    and whether that person holds security.manage TODAY (the gate's requirement, which was not checked then).
WITH access_batches AS (
    SELECT b.tenant_id, b.id AS batch_id, b.external_batch_id, b.status, b.created_by,
           b.created_at_utc, b.completed_at_utc,
           (coalesce(b.payload_json::jsonb -> 'Sections', b.payload_json::jsonb -> 'sections')) ? 'roles' AS had_roles,
           (coalesce(b.payload_json::jsonb -> 'Sections', b.payload_json::jsonb -> 'sections')) ? 'users' AS had_users,
           lower(coalesce(coalesce(b.payload_json::jsonb -> 'Sections', b.payload_json::jsonb -> 'sections') ->> 'roles', '')) AS roles_csv,
           lower(coalesce(coalesce(b.payload_json::jsonb -> 'Sections', b.payload_json::jsonb -> 'sections') ->> 'users', '')) AS users_csv
    FROM migration_import_batches b
    WHERE b.package_type = 'MigrationPackage' AND NOT b.dry_run AND b.status <> 'Previewed'
      AND (coalesce(b.payload_json::jsonb -> 'Sections', b.payload_json::jsonb -> 'sections')) ?| array['roles', 'users'])
SELECT ab.tenant_id, ab.batch_id, ab.external_batch_id, ab.status, ab.completed_at_utc,
       ab.had_roles, ab.had_users, committer.email AS committed_by,
       EXISTS (SELECT 1 FROM user_roles ur
               JOIN role_permissions rp ON rp.role_id = ur.role_id
               JOIN permissions p ON p.id = rp.permission_id
               WHERE ur.user_id = ab.created_by AND p.permission_key = 'security.manage') AS committer_holds_security_manage_now
FROM access_batches ab
LEFT JOIN users committer ON committer.id = ab.created_by
ORDER BY ab.completed_at_utc DESC NULLS FIRST;

-- 2. The accounts those packages named (first CSV column = Email), with the roles they hold NOW.
--    privileged = holds Admin or any role carrying security.manage.
WITH access_batches AS (
    SELECT b.tenant_id, b.id AS batch_id, b.created_by,
           lower(coalesce(coalesce(b.payload_json::jsonb -> 'Sections', b.payload_json::jsonb -> 'sections') ->> 'users', '')) AS users_csv
    FROM migration_import_batches b
    WHERE b.package_type = 'MigrationPackage' AND NOT b.dry_run AND b.status <> 'Previewed'
      AND (coalesce(b.payload_json::jsonb -> 'Sections', b.payload_json::jsonb -> 'sections')) ? 'users')
SELECT ab.tenant_id, ab.batch_id, u.id AS user_id, u.email, u.status, u.is_active, u.is_group_scope,
       u.id = ab.created_by AS committer_changed_own_account,
       string_agg(DISTINCT r.name, ', ') AS roles_now,
       coalesce(bool_or(r.normalized_name = 'ADMIN' OR p.permission_key = 'security.manage'), false) AS privileged
FROM access_batches ab
JOIN users u ON u.tenant_id = ab.tenant_id AND NOT u.is_deleted
 AND ab.users_csv ~ ('(^|\n)"?' || regexp_replace(lower(u.email), '([.+*?^$()\[\]{}|\\-])', '\\\1', 'g') || '"?,')
LEFT JOIN user_roles ur ON ur.user_id = u.id
LEFT JOIN roles r ON r.id = ur.role_id AND NOT r.is_deleted
LEFT JOIN role_permissions rp ON rp.role_id = r.id
LEFT JOIN permissions p ON p.id = rp.permission_id
GROUP BY ab.tenant_id, ab.batch_id, ab.created_by, u.id, u.email, u.status, u.is_active, u.is_group_scope
ORDER BY privileged DESC, ab.tenant_id, u.email;

-- 3. The roles those packages named (first CSV column = Name), as they stand NOW.
WITH access_batches AS (
    SELECT b.tenant_id, b.id AS batch_id,
           lower(coalesce(coalesce(b.payload_json::jsonb -> 'Sections', b.payload_json::jsonb -> 'sections') ->> 'roles', '')) AS roles_csv
    FROM migration_import_batches b
    WHERE b.package_type = 'MigrationPackage' AND NOT b.dry_run AND b.status <> 'Previewed'
      AND (coalesce(b.payload_json::jsonb -> 'Sections', b.payload_json::jsonb -> 'sections')) ? 'roles')
SELECT ab.tenant_id, ab.batch_id, r.id AS role_id, r.name, r.is_system, r.is_active, r.created_at_utc,
       (SELECT count(*) FROM user_roles ur WHERE ur.role_id = r.id) AS members_now,
       (SELECT string_agg(p.permission_key, ', ' ORDER BY p.permission_key) FROM role_permissions rp
          JOIN permissions p ON p.id = rp.permission_id WHERE rp.role_id = r.id) AS permissions_now
FROM access_batches ab
JOIN roles r ON (r.tenant_id = ab.tenant_id OR r.tenant_id IS NULL) AND NOT r.is_deleted
 AND ab.roles_csv ~ ('(^|\n)"?' || regexp_replace(lower(r.name), '([.+*?^$()\[\]{}|\\-])', '\\\1', 'g') || '"?,')
ORDER BY ab.tenant_id, r.name;
```

Zero rows from query 1 means the sections were never committed. From this release on, every role and user the
import writes also gets its own `access.role_created|role_updated|user_created|user_updated|roles_assigned` audit
row with `"source":"migration_import"` and the batch id in its metadata.

## Invariants
- **Schema leads code.** Migrations apply in `migrate-backend` before the deploy hook fires.
- **Single trigger.** `autoDeploy: false`; the CI hook is the only deploy path.
- **Fail-closed.** Missing `PROD_DATABASE_URL` or `RENDER_DEPLOY_HOOK_URL` fails the pipeline loudly.
- **Traffic gate.** `/health/ready` = 503 while migrations are pending, on every plan.
