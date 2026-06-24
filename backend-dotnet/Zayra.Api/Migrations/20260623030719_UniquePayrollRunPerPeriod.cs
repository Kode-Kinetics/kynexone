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
            // Void duplicate runs per period before creating the unique index.
            // Priority: Locked > Processed > Approved > Completed > Draft — never hard-deletes.
            // Uses a partial index (WHERE status != 'Voided') so voided duplicates don't conflict.
            migrationBuilder.Sql(@"
                UPDATE payroll_runs
                SET status = 'Voided'
                WHERE id IN (
                    SELECT id FROM (
                        SELECT id,
                               ROW_NUMBER() OVER (
                                   PARTITION BY tenant_id, year, month
                                   ORDER BY
                                       CASE status
                                           WHEN 'Locked'    THEN 1
                                           WHEN 'Processed' THEN 2
                                           WHEN 'Approved'  THEN 3
                                           WHEN 'Completed' THEN 4
                                           WHEN 'Draft'     THEN 5
                                           ELSE 6
                                       END ASC,
                                       employee_count DESC,
                                       created_at_utc ASC
                               ) AS rn
                        FROM payroll_runs
                        WHERE status != 'Voided'
                    ) t WHERE rn > 1
                );

                CREATE UNIQUE INDEX IF NOT EXISTS ""IX_payroll_runs_tenant_id_year_month""
                ON payroll_runs (tenant_id, year, month)
                WHERE status != 'Voided';
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_payroll_runs_tenant_id_year_month"";");
        }
    }
}
