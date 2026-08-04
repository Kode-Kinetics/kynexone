using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// POD-B2 — payroll run TYPING (Regular | OffCycle | Supplementary | Correction), the pay BASIS, the
    /// parent-run link, an optional GL posting period, and the audited include/exclude population selector.
    ///
    /// SAFETY FOR THE ~55 LIVE TENANTS
    ///
    ///  • Backfill is implicit and total: `AddColumn(nullable: false, defaultValue: "Regular")` makes
    ///    Postgres write 'Regular' into every existing payroll_runs row in the same statement. Likewise
    ///    includes_recurring_pay defaults to true. So every run that exists today becomes exactly what it
    ///    already was — the tenant's monthly, full-recurring run — and no behaviour changes for it.
    ///
    ///  • NO dedupe pass is needed (unlike 20260623030719_UniquePayrollRunPerPeriod, which had to collapse
    ///    duplicates before it could add its unique index). The existing unique index already guarantees
    ///    at most one non-voided run per (tenant, company, year, month), so backfilling them all to
    ///    'Regular' cannot violate the narrowed index this migration creates.
    ///
    ///  • Statement ORDER is load-bearing: run_type must EXIST before any index whose predicate names it.
    ///
    ///  • The index swaps use raw SQL with IF EXISTS / IF NOT EXISTS rather than DropIndex/CreateIndex.
    ///    payroll_runs' period indexes have been through three generations (20260623030719 →
    ///    20260624000001_FixPayrollRunPartialIndex → 20260712235953_CompanyScopedPayrollIndexes), so a
    ///    bare DropIndex throws on any database whose shape drifted. This is the same idiom
    ///    FixPayrollRunPartialIndex used, for the same reason.
    ///
    ///  • The model-side HasFilter strings in ZayraDbContext are BYTE-IDENTICAL to the predicates below
    ///    (`!=` not `&lt;&gt;`, double-quoted column names), so the next `migrations add` does not emit a
    ///    spurious drop/recreate. Same discipline for HasDefaultValue vs defaultValue.
    /// </summary>
    public partial class AddPayrollRunTypeAndSelection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── 1. Columns + implicit backfill ────────────────────────────────────────────────────
            // Every pre-B2 row becomes Regular / full-recurring-basis: what it already was.
            migrationBuilder.AddColumn<string>(
                name: "run_type",
                table: "payroll_runs",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Regular");

            migrationBuilder.AddColumn<bool>(
                name: "includes_recurring_pay",
                table: "payroll_runs",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<Guid>(
                name: "parent_run_id",
                table: "payroll_runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "gl_posting_period",
                table: "payroll_runs",
                type: "character varying(7)",
                maxLength: 7,
                nullable: true);

            // ── 2. Re-key the period uniqueness to REGULAR runs only ──────────────────────────────
            // Must come AFTER step 1: both predicates reference run_type.
            //
            // Index A — company-scoped. Narrowed from "one non-voided run per (tenant, company, period)"
            // to "one non-voided REGULAR run", so OffCycle/Supplementary/Correction runs may coexist.
            //
            // Index B — the NULL-COMPANY companion, and the reason this migration is not just a filter
            // change. Postgres unique indexes treat NULL as DISTINCT, so index A constrains nothing at all
            // for rows with company_id IS NULL — and both seeders (Infrastructure/Seed/AuthSeeder and
            // DemoDataSeeder) create exactly such rows. The index that used to cover them,
            // IX_payroll_runs_tenant_id_year_month, was dropped by 20260712235953. Without B, a seeded
            // tenant-wide Regular run does not block a second Regular run for a company in the same
            // period — two competing monthly runs, i.e. the month accrued twice. B restores that coverage,
            // scoped to unscoped Regular rows.
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

            // ── 3. Restore an UNFILTERED period index ─────────────────────────────────────────────
            // After step 2 the only index on (tenant, company, year, month) is filtered to Regular runs,
            // so it cannot serve a non-Regular lookup — ListRuns, PayrollOverview, PayrollReadiness,
            // Reconciliation's prior-run probe, and B2's sibling-run scans would all seq-scan. Declared
            // in the model with a DISTINCT property ORDER so EF does not collapse it into the unique
            // index (an identical property set returns the SAME index builder); the order here is also
            // the better prefix, since those queries filter tenant+period first and company second.
            migrationBuilder.CreateIndex(
                name: "IX_payroll_runs_period_lookup",
                table: "payroll_runs",
                columns: new[] { "tenant_id", "year", "month", "company_id" });

            // Parent-run lookups (a run's corrections; the void path's orphaned-children report).
            migrationBuilder.CreateIndex(
                name: "IX_payroll_runs_tenant_id_parent_run_id",
                table: "payroll_runs",
                columns: new[] { "tenant_id", "parent_run_id" });

            // ── 4. The population selector ────────────────────────────────────────────────────────
            // No FK to payroll_runs — nothing in this schema uses one (PayrollSlip.RunId has none
            // either); DeleteRun removes the rows explicitly.
            migrationBuilder.CreateTable(
                name: "payroll_run_employee_selections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payroll_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payroll_run_employee_selections", x => x.id);
                });

            // Company-scope index, added for every ICompanyScoped entity by ApplyCompanyScopeIndexes.
            migrationBuilder.CreateIndex(
                name: "IX_payroll_run_employee_selections_tenant_id_company_id",
                table: "payroll_run_employee_selections",
                columns: new[] { "tenant_id", "company_id" });

            // Upsert key: re-posting the same employee flips Mode/Reason instead of duplicating.
            migrationBuilder.CreateIndex(
                name: "IX_payroll_run_employee_selections_tenant_id_payroll_run_id_em~",
                table: "payroll_run_employee_selections",
                columns: new[] { "tenant_id", "payroll_run_id", "employee_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "payroll_run_employee_selections");

            migrationBuilder.DropIndex(
                name: "IX_payroll_runs_period_lookup",
                table: "payroll_runs");

            migrationBuilder.DropIndex(
                name: "IX_payroll_runs_tenant_id_parent_run_id",
                table: "payroll_runs");

            // Restore the pre-B2 shape: the company-scoped partial unique index WITHOUT the run_type
            // clause, and no null-company companion (matching 20260712235953's end state). Dropped before
            // the columns, because the current predicates reference run_type.
            migrationBuilder.Sql(@"
                DROP INDEX IF EXISTS ""IX_payroll_runs_tenant_id_year_month"";
                DROP INDEX IF EXISTS ""IX_payroll_runs_tenant_id_company_id_year_month"";
                CREATE UNIQUE INDEX IF NOT EXISTS ""IX_payroll_runs_tenant_id_company_id_year_month""
                  ON payroll_runs (tenant_id, company_id, year, month)
                  WHERE ""status"" != 'Voided';
            ");

            migrationBuilder.DropColumn(
                name: "gl_posting_period",
                table: "payroll_runs");

            migrationBuilder.DropColumn(
                name: "includes_recurring_pay",
                table: "payroll_runs");

            migrationBuilder.DropColumn(
                name: "parent_run_id",
                table: "payroll_runs");

            migrationBuilder.DropColumn(
                name: "run_type",
                table: "payroll_runs");
        }
    }
}
