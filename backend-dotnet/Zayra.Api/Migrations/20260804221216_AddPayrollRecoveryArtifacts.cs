using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// POD-B3 — RECOVERY / ROLLBACK of a bad payroll month. Strictly additive.
    ///
    /// WHAT IT ADDS
    ///
    ///  1. payroll_run_consumptions — the persisted WITNESS of everything a run consumed (loan/advance
    ///     balances and installments with their PRIOR values, attendance/leave/overtime impacts, payroll
    ///     adjustments). The void/reopen unwind replays these rows. It cannot be derived after the fact:
    ///     the payslip carries ONE aggregate LOAN_EMI per employee (no per-loan attribution for an
    ///     employee with two loans), the installment stamp is written only when a schedule row exists, and
    ///     attendance/leave impacts carry no run id at all.
    ///
    ///  2. payroll_validation_overrides + four attribution columns on payroll_validation_results — the
    ///     audited exit from a blocking compliance error. The override lives in its own table because
    ///     POST runs/{id}/validate DELETES and rebuilds every result row for a run, which would silently
    ///     erase a flag stored there and re-stick the run.
    ///
    ///  3. The two partial unique indexes on payroll_runs widened from run_type = 'Regular' to
    ///     run_type IN ('Regular','Replacement').
    ///
    ///  4. Idempotent GL seeds for the two recovery control accounts (1420 Employee Overpayment
    ///     Receivable, 1430 Prepaid Statutory Remittance).
    ///
    /// SAFETY FOR THE ~55 LIVE TENANTS
    ///
    ///  • Every column added is NULLABLE and every table is NEW, so there is no backfill and no rewrite of
    ///    an existing row. Runs that predate this migration simply carry no witness rows — the void then
    ///    reports consumptionWitnessed=false rather than guessing a restore, which is the honest answer.
    ///
    ///  • Widening the index filter is safe BY COUNT: no payroll_runs row anywhere has
    ///    run_type = 'Replacement' (the value did not exist until this pod), so the widened predicate
    ///    matches exactly the same rows it did before and cannot fail on a duplicate.
    ///
    ///  • The index swap uses raw SQL with IF EXISTS / IF NOT EXISTS rather than DropIndex/CreateIndex,
    ///    for the same reason 20260624000001_FixPayrollRunPartialIndex and 20260804202105 did:
    ///    payroll_runs' period indexes have been through four generations and a bare DropIndex throws on
    ///    any database whose shape drifted.
    ///
    ///  • The model-side HasFilter strings in ZayraDbContext are BYTE-IDENTICAL to the predicates below
    ///    (`!=` not `&lt;&gt;`, double-quoted column names, `IN ('Regular', 'Replacement')` with the space
    ///    after the comma exactly as EF renders it), so the next `migrations add` does not emit a spurious
    ///    drop/recreate.
    ///
    ///  • The GL seeds are NOT EXISTS-guarded, so re-running them — or overlapping with a seed-defaults
    ///    call — is a no-op. Correctness does not depend on them either: the compiled
    ///    PayrollGlCatalog.Defaults fallback already resolves "1420 - Employee Overpayment Receivable" and
    ///    "1430 - Prepaid Statutory Remittance" at posting time. The seeds only make the accounts VISIBLE
    ///    and REMAPPABLE per company in the CoA UI for tenants provisioned before this pod.
    /// </summary>
    public partial class AddPayrollRecoveryArtifacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── 1. Override attribution on the existing validation results ────────────────────────────
            migrationBuilder.AddColumn<DateTime>(
                name: "resolved_at_utc",
                table: "payroll_validation_results",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "resolved_by_name",
                table: "payroll_validation_results",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "resolved_by_user_id",
                table: "payroll_validation_results",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "resolved_reason",
                table: "payroll_validation_results",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            // ── 2. The consumption witness ────────────────────────────────────────────────────────────
            // No FK to payroll_runs — nothing in this schema uses one (PayrollSlip.RunId has none either);
            // the void/reopen unwind removes the rows explicitly once it has replayed them.
            migrationBuilder.CreateTable(
                name: "payroll_run_consumptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payroll_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    artifact_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    artifact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    prior_status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    prior_outstanding_balance = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: true),
                    prior_total_repaid = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: true),
                    prior_amount_paid = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: true),
                    prior_payroll_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payroll_run_consumptions", x => x.id);
                });

            // ── 3. The durable validation override ────────────────────────────────────────────────────
            migrationBuilder.CreateTable(
                name: "payroll_validation_overrides",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payroll_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: true),
                    code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    overridden_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    overridden_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payroll_validation_overrides", x => x.id);
                });

            // ── 4. Widen the period uniqueness to the PERIOD-OWNING run types ─────────────────────────
            // A Replacement IS the month — it takes a voided run's place — so leaving it outside these
            // indexes would let two live monthly runs exist for one period with only the API check between
            // them. Zero 'Replacement' rows exist on any tenant, so the widened predicate matches exactly
            // the rows the old one did.
            migrationBuilder.Sql(@"
                DROP INDEX IF EXISTS ""IX_payroll_runs_tenant_id_company_id_year_month"";
                CREATE UNIQUE INDEX IF NOT EXISTS ""IX_payroll_runs_tenant_id_company_id_year_month""
                  ON payroll_runs (tenant_id, company_id, year, month)
                  WHERE ""status"" != 'Voided' AND ""run_type"" IN ('Regular', 'Replacement');

                DROP INDEX IF EXISTS ""IX_payroll_runs_tenant_id_year_month"";
                CREATE UNIQUE INDEX IF NOT EXISTS ""IX_payroll_runs_tenant_id_year_month""
                  ON payroll_runs (tenant_id, year, month)
                  WHERE ""company_id"" IS NULL AND ""status"" != 'Voided' AND ""run_type"" IN ('Regular', 'Replacement');
            ");

            // ── 5. Indexes on the new tables ──────────────────────────────────────────────────────────
            // Company-scope index, added for every ICompanyScoped entity by ApplyCompanyScopeIndexes.
            migrationBuilder.CreateIndex(
                name: "IX_payroll_run_consumptions_tenant_id_company_id",
                table: "payroll_run_consumptions",
                columns: new[] { "tenant_id", "company_id" });

            // The unwind's dominant read: every witness for one run.
            migrationBuilder.CreateIndex(
                name: "IX_payroll_run_consumptions_tenant_id_payroll_run_id",
                table: "payroll_run_consumptions",
                columns: new[] { "tenant_id", "payroll_run_id" });

            // Idempotent re-Process: one witness per (run, artifact type, artifact).
            migrationBuilder.CreateIndex(
                name: "IX_payroll_run_consumptions_tenant_id_payroll_run_id_artifact_~",
                table: "payroll_run_consumptions",
                columns: new[] { "tenant_id", "payroll_run_id", "artifact_type", "artifact_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payroll_validation_overrides_tenant_id_company_id",
                table: "payroll_validation_overrides",
                columns: new[] { "tenant_id", "company_id" });

            // One override per (run, code, employee). Postgres treats NULLs as distinct, so this is exact
            // for per-employee codes and advisory for run-level ones — the endpoint re-reads by the same
            // predicate before inserting, so a run-level code is upserted rather than duplicated.
            migrationBuilder.CreateIndex(
                name: "IX_payroll_validation_overrides_tenant_id_payroll_run_id_code_~",
                table: "payroll_validation_overrides",
                columns: new[] { "tenant_id", "payroll_run_id", "code", "employee_id" },
                unique: true);

            // ── 6. Recovery control accounts (idempotent data seed) ───────────────────────────────────
            // 1420 carries net pay that LEFT THE BANK on a run that was then voided (recoverable from the
            // employee); 1430 carries statutory cash already remitted to the authority. Both are ASSETS:
            // the money is gone and the obligation that justified it is not. Without them a void of a
            // settled/remitted month could only be expressed by crediting Cash/Bank back — a claim the
            // bank statement disproves, and one that would let a replacement run pay the same money twice.
            // Mirrors 20260803231605_SeedCashBankGlDefault exactly (account → mapping → driver).
            migrationBuilder.Sql(@"
INSERT INTO gl_accounts (id, tenant_id, company_id, code, name, account_type, is_active, created_at_utc)
SELECT gen_random_uuid(), t.tenant_id, NULL, v.code, v.name, 'Asset', TRUE, now()
FROM (SELECT DISTINCT tenant_id FROM gl_accounts WHERE company_id IS NULL) t
CROSS JOIN (VALUES
    ('1420', 'Employee Overpayment Receivable'),
    ('1430', 'Prepaid Statutory Remittance')
) AS v(code, name)
WHERE NOT EXISTS (
    SELECT 1 FROM gl_accounts a
    WHERE a.tenant_id = t.tenant_id AND a.company_id IS NULL AND a.code = v.code
);");

            migrationBuilder.Sql(@"
INSERT INTO gl_account_mappings (id, tenant_id, company_id, driver_key, account_id, is_active, created_at_utc)
SELECT gen_random_uuid(), a.tenant_id, NULL, v.driver_key, a.id, TRUE, now()
FROM gl_accounts a
JOIN (VALUES
    ('1420', 'EMPLOYEE_RECEIVABLE'),
    ('1430', 'STATUTORY_PREPAID')
) AS v(code, driver_key) ON v.code = a.code
WHERE a.company_id IS NULL
  AND NOT EXISTS (
    SELECT 1 FROM gl_account_mappings m
    WHERE m.tenant_id = a.tenant_id AND m.company_id IS NULL AND m.driver_key = v.driver_key
  );");

            // Category = 'Balancing' for the same reason as CASH_BANK: component routing only ever
            // considers Earning/Deduction rows, so no payroll component can be routed here by accident —
            // these are resolved exclusively by an explicit ResolveGlAccount from the void path.
            migrationBuilder.Sql(@"
INSERT INTO gl_drivers (
    id, tenant_id, company_id, key, label, category, posting_side, account_type,
    default_code, default_name, match_source, match_mode, match_component_code,
    emits_employer_expense_pair, paired_expense_driver_key, is_system, is_active, sort_order, created_at_utc)
SELECT gen_random_uuid(), t.tenant_id, NULL, v.key, v.label, 'Balancing', 'DR', 'Asset',
    v.code, v.name, NULL, 'Any', NULL, FALSE, NULL, TRUE, TRUE, v.sort_order, now()
FROM (SELECT DISTINCT tenant_id FROM gl_drivers WHERE company_id IS NULL AND is_system = TRUE) t
CROSS JOIN (VALUES
    ('EMPLOYEE_RECEIVABLE', 'Employee Overpayment Receivable', '1420', 'Employee Overpayment Receivable', 106),
    ('STATUTORY_PREPAID',   'Prepaid Statutory Remittance',    '1430', 'Prepaid Statutory Remittance',    107)
) AS v(key, label, code, name, sort_order)
WHERE NOT EXISTS (
    SELECT 1 FROM gl_drivers d
    WHERE d.tenant_id = t.tenant_id AND d.company_id IS NULL AND d.key = v.key
);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Remove ONLY the rows this migration seeds. Mappings first (FK → gl_accounts is Restrict),
            // then the drivers, then the accounts.
            migrationBuilder.Sql("DELETE FROM gl_account_mappings WHERE company_id IS NULL AND driver_key IN ('EMPLOYEE_RECEIVABLE', 'STATUTORY_PREPAID');");
            migrationBuilder.Sql("DELETE FROM gl_drivers WHERE company_id IS NULL AND is_system = TRUE AND key IN ('EMPLOYEE_RECEIVABLE', 'STATUTORY_PREPAID');");
            migrationBuilder.Sql("DELETE FROM gl_accounts WHERE company_id IS NULL AND code IN ('1420', '1430') AND name IN ('Employee Overpayment Receivable', 'Prepaid Statutory Remittance');");

            migrationBuilder.DropTable(
                name: "payroll_run_consumptions");

            migrationBuilder.DropTable(
                name: "payroll_validation_overrides");

            // Restore the B2 shape: uniqueness scoped to Regular runs only.
            migrationBuilder.Sql(@"
                DROP INDEX IF EXISTS ""IX_payroll_runs_tenant_id_company_id_year_month"";
                CREATE UNIQUE INDEX IF NOT EXISTS ""IX_payroll_runs_tenant_id_company_id_year_month""
                  ON payroll_runs (tenant_id, company_id, year, month)
                  WHERE ""status"" != 'Voided' AND ""run_type"" = 'Regular';

                DROP INDEX IF EXISTS ""IX_payroll_runs_tenant_id_year_month"";
                CREATE UNIQUE INDEX IF NOT EXISTS ""IX_payroll_runs_tenant_id_year_month""
                  ON payroll_runs (tenant_id, year, month)
                  WHERE ""company_id"" IS NULL AND ""status"" != 'Voided' AND ""run_type"" = 'Regular';
            ");

            migrationBuilder.DropColumn(
                name: "resolved_at_utc",
                table: "payroll_validation_results");

            migrationBuilder.DropColumn(
                name: "resolved_by_name",
                table: "payroll_validation_results");

            migrationBuilder.DropColumn(
                name: "resolved_by_user_id",
                table: "payroll_validation_results");

            migrationBuilder.DropColumn(
                name: "resolved_reason",
                table: "payroll_validation_results");
        }
    }
}
