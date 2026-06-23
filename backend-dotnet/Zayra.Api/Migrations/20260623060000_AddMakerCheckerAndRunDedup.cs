using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMakerCheckerAndRunDedup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Dedup payroll_runs before the unique index can be applied.
            //    Keep the run with the most employee data (highest employee_count);
            //    if tied, keep the earlier one (created_at_utc ASC).
            //    The UniquePayrollRunPerPeriod migration created the raw SQL index;
            //    this dedup ensures no duplicate rows exist before that or this migration runs.
            migrationBuilder.Sql(@"
                DELETE FROM payroll_runs
                WHERE id IN (
                    SELECT id FROM (
                        SELECT id,
                               ROW_NUMBER() OVER (
                                   PARTITION BY tenant_id, year, month
                                   ORDER BY employee_count DESC, created_at_utc ASC
                               ) AS rn
                        FROM payroll_runs
                    ) t WHERE rn > 1
                );
            ");

            // 2. Ensure the period-uniqueness index exists (idempotent — safe if already created
            //    by the UniquePayrollRunPerPeriod migration in the same deploy batch).
            migrationBuilder.Sql(@"
                CREATE UNIQUE INDEX IF NOT EXISTS ""IX_payroll_runs_tenant_id_year_month""
                ON payroll_runs (tenant_id, year, month);
            ");

            // 3. Add processed_by_user_id to track the user who called /process —
            //    enforces maker-checker: ProcessedByUserId != ApprovingUserId.
            migrationBuilder.AddColumn<Guid>(
                name: "processed_by_user_id",
                table: "payroll_runs",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "processed_by_user_id",
                table: "payroll_runs");

            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_payroll_runs_tenant_id_year_month"";");
        }
    }
}
