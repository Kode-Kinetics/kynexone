using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// Overtime stops being held as rounded decimal hours at the payroll boundary.
    ///
    /// <para><c>overtime_payroll_impacts.hours</c> and <c>overtime_calculations.approved_hours</c>
    /// were <c>numeric(8,2)</c>, written as <c>Math.Round(ApprovedMinutes / 60m, 2)</c> and then
    /// multiplied by the hourly rate by the payroll run. 50 approved minutes were stored as 0.83 h
    /// and paid as 0.83 h — SAR 134.88 instead of SAR 135.42 on the canonical KSA package, on every
    /// overtime line whose minutes are not a whole multiple of 0.6 of a minute, always downward.</para>
    ///
    /// <para>Both columns become <c>integer</c> minutes, the unit the request, the attendance record
    /// and <c>attendance_payroll_impacts.minutes</c> already use. The hours value is now derived in
    /// the model, so the API contract is unchanged and there is one stored unit for one quantity.</para>
    ///
    /// <para><b>Backfill.</b> The true minute count is recovered from
    /// <c>overtime_requests.approved_minutes</c>, which is the value the rounded hours were derived
    /// FROM and is still intact — so existing rows are restored exactly, not approximately. Only
    /// rows whose request has gone (or was never approved) fall back to <c>round(hours * 60)</c>.</para>
    ///
    /// <para><b>What this does NOT correct.</b> The <c>amount</c> already stored on historical
    /// impacts and calculations was computed from the rounded hours and is left alone: it is the
    /// figure the module displayed at the time, and rewriting money on approved records is not a
    /// migration's job. The payroll run recomputes overtime pay from the QUANTITY, not from
    /// <c>amount</c>, so any period re-run after this migration pays the corrected figure; periods
    /// already locked and paid keep their (short) amounts until an operator issues an adjustment.</para>
    /// </summary>
    public partial class OvertimeQuantityInMinutes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "minutes",
                table: "overtime_payroll_impacts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "approved_minutes",
                table: "overtime_calculations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Exact recovery: the request still holds the minutes the hours copy was rounded from.
            migrationBuilder.Sql(@"
                UPDATE overtime_payroll_impacts i
                   SET minutes = r.approved_minutes
                  FROM overtime_requests r
                 WHERE r.id = i.overtime_request_id
                   AND r.tenant_id = i.tenant_id
                   AND r.approved_minutes > 0;

                UPDATE overtime_calculations c
                   SET approved_minutes = r.approved_minutes
                  FROM overtime_requests r
                 WHERE r.id = c.overtime_request_id
                   AND r.tenant_id = c.tenant_id
                   AND r.approved_minutes > 0;
            ");

            // Fallback for orphans: the best available reconstruction, to the nearest minute.
            migrationBuilder.Sql(@"
                UPDATE overtime_payroll_impacts
                   SET minutes = ROUND(hours * 60)::int
                 WHERE minutes = 0 AND hours > 0;

                UPDATE overtime_calculations
                   SET approved_minutes = ROUND(approved_hours * 60)::int
                 WHERE approved_minutes = 0 AND approved_hours > 0;
            ");

            migrationBuilder.DropColumn(
                name: "hours",
                table: "overtime_payroll_impacts");

            migrationBuilder.DropColumn(
                name: "approved_hours",
                table: "overtime_calculations");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "hours",
                table: "overtime_payroll_impacts",
                type: "numeric(8,2)",
                precision: 8,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "approved_hours",
                table: "overtime_calculations",
                type: "numeric(8,2)",
                precision: 8,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            // Lossy by construction — that loss is the defect this migration removed.
            migrationBuilder.Sql(@"
                UPDATE overtime_payroll_impacts SET hours = ROUND(minutes / 60.0, 2);
                UPDATE overtime_calculations    SET approved_hours = ROUND(approved_minutes / 60.0, 2);
            ");

            migrationBuilder.DropColumn(
                name: "minutes",
                table: "overtime_payroll_impacts");

            migrationBuilder.DropColumn(
                name: "approved_minutes",
                table: "overtime_calculations");
        }
    }
}
