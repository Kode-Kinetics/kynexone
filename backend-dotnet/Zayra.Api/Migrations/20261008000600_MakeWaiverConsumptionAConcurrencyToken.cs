using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// Selfie hardening (follow-up to 20261008000500, which has shipped and is not edited):
    /// <list type="bullet">
    ///   <item><c>attendance_evidence.waiver_consumed_at_utc</c> becomes an EF concurrency token (model-only, no DDL): the
    ///     database backstop for "one waiver, one punch".</item>
    ///   <item><c>ck_attendance_evidence__failed_reason</c> also accepts the non-waivable reasons <c>Timeout</c>,
    ///     <c>Aborted</c> and <c>DeniedFailure</c> (an attempt closed as failed that never waives anything), and
    ///     <c>ck_attendance_evidence__waiver_needs_failure</c> now lets only a WAIVABLE failure (<c>Busy</c>,
    ///     <c>Storage</c>) carry a waiver.</item>
    /// </list>
    /// Expand-safe: every existing row (failed_reason NULL, Busy or Storage; waivers only on Busy/Storage rows) satisfies
    /// both new CHECKs, and the previous release never writes the new values. The migration runs in one transaction, so a
    /// failure leaves the old constraints in place and it re-runs clean. Down restores the 000500 constraints (it fails
    /// if rows with the new reasons exist — clear or relabel them first). Ordered after 000500; the peer's is 000700.
    /// </summary>
    public partial class MakeWaiverConsumptionAConcurrencyToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_attendance_evidence__failed_reason",
                table: "attendance_evidence");

            migrationBuilder.DropCheckConstraint(
                name: "ck_attendance_evidence__waiver_needs_failure",
                table: "attendance_evidence");

            migrationBuilder.AddCheckConstraint(
                name: "ck_attendance_evidence__failed_reason",
                table: "attendance_evidence",
                sql: "failed_reason IS NULL OR failed_reason IN ('Busy','Storage','Timeout','Aborted','DeniedFailure')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_attendance_evidence__waiver_needs_failure",
                table: "attendance_evidence",
                sql: "(failed_reason IS NOT NULL AND failed_reason IN ('Busy','Storage')) OR (waiver_consumed_at_utc IS NULL AND waiver_cancelled_at_utc IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_attendance_evidence__failed_reason",
                table: "attendance_evidence");

            migrationBuilder.DropCheckConstraint(
                name: "ck_attendance_evidence__waiver_needs_failure",
                table: "attendance_evidence");

            migrationBuilder.AddCheckConstraint(
                name: "ck_attendance_evidence__failed_reason",
                table: "attendance_evidence",
                sql: "failed_reason IS NULL OR failed_reason IN ('Busy','Storage')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_attendance_evidence__waiver_needs_failure",
                table: "attendance_evidence",
                sql: "failed_reason IS NOT NULL OR (waiver_consumed_at_utc IS NULL AND waiver_cancelled_at_utc IS NULL)");
        }
    }
}
