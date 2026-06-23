using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddStatusCheckConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "status",
                table: "payroll_runs",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "status",
                table: "payroll_slips",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            // Allow all statuses used in the codebase:
            // payroll_runs:  Draft, Processing, Processed, Completed, Locked, Paid, Voided, Approved, PendingFinanceReview
            // payroll_slips: Draft, Processing, Processed, Completed, Locked, Paid, Voided, Final
            migrationBuilder.Sql(@"
                ALTER TABLE payroll_runs
                  ADD CONSTRAINT chk_payroll_run_status
                  CHECK (status IN ('Draft','Processing','Processed','Completed','Locked','Paid','Voided','Approved','PendingFinanceReview'));
                ALTER TABLE payroll_slips
                  ADD CONSTRAINT chk_payroll_slip_status
                  CHECK (status IN ('Draft','Processing','Processed','Completed','Locked','Paid','Voided','Final'));
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE payroll_runs DROP CONSTRAINT IF EXISTS chk_payroll_run_status;
                ALTER TABLE payroll_slips DROP CONSTRAINT IF EXISTS chk_payroll_slip_status;
            ");

            migrationBuilder.AlterColumn<string>(
                name: "status",
                table: "payroll_runs",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(40)",
                oldMaxLength: 40);

            migrationBuilder.AlterColumn<string>(
                name: "status",
                table: "payroll_slips",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(40)",
                oldMaxLength: 40);
        }
    }
}
