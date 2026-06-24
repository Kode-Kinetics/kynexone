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
            // 1. Void any remaining duplicate runs per period (idempotent — 030719 handles most).
            //    Same priority order: Locked > Processed > Approved > Completed > Draft.
            //    Never hard-deletes financial records.
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
            ");

            // 2. Ensure the partial period-uniqueness index exists (idempotent).
            //    WHERE status != 'Voided' means voided duplicates never block period re-use.
            migrationBuilder.Sql(@"
                CREATE UNIQUE INDEX IF NOT EXISTS ""IX_payroll_runs_tenant_id_year_month""
                ON payroll_runs (tenant_id, year, month)
                WHERE status != 'Voided';
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
