using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// The first and last day an unpaid-leave payroll impact charges. A leave that crosses a month end is written as one
    /// impact per month; payroll scales a slice for a joiner or leaver by these dates instead of guessing a slice from its
    /// day count. Nullable and not backfilled: a row without dates charges the whole request, which is exactly what every
    /// existing row does.
    /// </summary>
    public partial class AddLeavePayrollImpactSliceDates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "slice_from",
                table: "leave_payroll_impacts",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "slice_to",
                table: "leave_payroll_impacts",
                type: "date",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "slice_from",
                table: "leave_payroll_impacts");

            migrationBuilder.DropColumn(
                name: "slice_to",
                table: "leave_payroll_impacts");
        }
    }
}
