using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class UniquePayrollRunPerPeriod : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // One payroll run per (tenant, year, month) — period uniqueness independent of company.
            // A null company_id falls back to the tenant's first active company during Process(),
            // so two runs for the same period (one with company_id, one without) would compute
            // the same payroll and produce a double-payment. The DB constraint enforces
            // period-level uniqueness.
            //
            // NOTE: Remove any duplicate runs before applying this migration.
            // Dedup SQL (keep the run with data, delete the rest):
            //   DELETE FROM payroll_runs
            //   WHERE id IN (
            //     SELECT id FROM (
            //       SELECT id,
            //              ROW_NUMBER() OVER (PARTITION BY tenant_id, year, month
            //                                ORDER BY employee_count DESC, created_at_utc ASC) AS rn
            //       FROM payroll_runs
            //     ) t WHERE rn > 1
            //   );
            migrationBuilder.Sql(@"
                CREATE UNIQUE INDEX ""IX_payroll_runs_tenant_id_year_month""
                ON payroll_runs (tenant_id, year, month);
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_payroll_runs_tenant_id_year_month"";");
        }
    }
}
