using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddStatutoryTotalsToSlipAndRun : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "employee_statutory_total",
                table: "payroll_slips",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "employer_statutory_total",
                table: "payroll_slips",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "total_employer_statutory_cost",
                table: "payroll_runs",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "employee_statutory_total",
                table: "payroll_slips");

            migrationBuilder.DropColumn(
                name: "employer_statutory_total",
                table: "payroll_slips");

            migrationBuilder.DropColumn(
                name: "total_employer_statutory_cost",
                table: "payroll_runs");
        }
    }
}
