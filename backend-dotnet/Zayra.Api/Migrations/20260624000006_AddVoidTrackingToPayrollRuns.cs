using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddVoidTrackingToPayrollRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // PayrollRun.cs added VoidReason/VoidedAt/VoidedByUserId/VoidedByName in commit 044dbba
            // but no migration was created. EF Core's SELECT includes all mapped properties →
            // PostgreSQL 42703 "column void_reason does not exist" → 500 on every runs query.
            // All four columns are nullable: populated only when Status == "Voided".
            migrationBuilder.AddColumn<string>(
                name: "void_reason",
                table: "payroll_runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "voided_at_utc",
                table: "payroll_runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "voided_by_user_id",
                table: "payroll_runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "voided_by_name",
                table: "payroll_runs",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "void_reason",
                table: "payroll_runs");

            migrationBuilder.DropColumn(
                name: "voided_at_utc",
                table: "payroll_runs");

            migrationBuilder.DropColumn(
                name: "voided_by_user_id",
                table: "payroll_runs");

            migrationBuilder.DropColumn(
                name: "voided_by_name",
                table: "payroll_runs");
        }
    }
}
