using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class FixPayrollRunPartialIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Replace the plain unique index with a partial index that excludes Voided runs.
            // Without the WHERE clause a voided run permanently blocks re-running the same period.
            migrationBuilder.Sql(@"
                DROP INDEX IF EXISTS ""IX_payroll_runs_tenant_id_year_month"";
                CREATE UNIQUE INDEX IF NOT EXISTS ""IX_payroll_runs_tenant_id_year_month""
                  ON payroll_runs (tenant_id, year, month)
                  WHERE status != 'Voided';
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DROP INDEX IF EXISTS ""IX_payroll_runs_tenant_id_year_month"";
                CREATE UNIQUE INDEX IF NOT EXISTS ""IX_payroll_runs_tenant_id_year_month""
                  ON payroll_runs (tenant_id, year, month);
            ");
        }
    }
}
