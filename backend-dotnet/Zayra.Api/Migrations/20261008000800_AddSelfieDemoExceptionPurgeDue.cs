using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// The selfie DEMO EXCEPTION's retention marker (owner decision 2026-10-08; <c>SelfieDemoExceptionOptions</c>):
    /// <list type="bullet">
    ///   <item><c>attendance_evidence.purge_due_at_utc</c> (timestamptz, NULL): stamped at upload ONLY for a selfie taken
    ///     under the exception, at capture + the exception's retention days. The purge deletes the blob then, used or not,
    ///     overriding the payroll-lock and 120-day rules — and still after the exception expires, which is why it is
    ///     stored on the row and not derived from configuration.</item>
    ///   <item><c>ck_attendance_evidence__purge_due_after_capture</c>: the stamp is after the capture.</item>
    ///   <item><c>ix_attendance_evidence__purge_due_override</c> (<c>tenant_id, purge_due_at_utc</c>, partial on stamped,
    ///     unpurged rows): the purge's query d and the scheduler's tenant scan.</item>
    /// </list>
    /// Expand-only and N-1 safe: a nullable column with no default is metadata-only on PostgreSQL 11+ (no rewrite), every
    /// existing row is NULL (passes the CHECK), and the previous release never reads or writes it. One transaction, so a
    /// failure leaves nothing behind and it re-runs clean. Down refuses while a stamped selfie is still unpurged (dropping
    /// the stamp would drop the promise to delete it); purge them first. Ordered after 000600; 000700 is the peer's (#210).
    /// </summary>
    public partial class AddSelfieDemoExceptionPurgeDue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "purge_due_at_utc",
                table: "attendance_evidence",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_attendance_evidence__purge_due_override",
                table: "attendance_evidence",
                columns: new[] { "tenant_id", "purge_due_at_utc" },
                filter: "purge_due_at_utc IS NOT NULL AND purge_state IN ('Pending','Active')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_attendance_evidence__purge_due_after_capture",
                table: "attendance_evidence",
                sql: "purge_due_at_utc IS NULL OR purge_due_at_utc > created_at_utc");

            // What EF cannot model: the query the index serves, and what the column means.
            migrationBuilder.Sql(Comments);
        }

        /// <summary>Column and index comments (docs/schema: every index names its query).</summary>
        public const string Comments = """
            COMMENT ON COLUMN attendance_evidence.purge_due_at_utc IS
                'Set at upload only for a selfie taken under the time-boxed selfie demo exception (SelfieDemoException config): the blob is deleted at this instant (capture + retention days), used or not, overriding the payroll-lock and 120-day rules, and still after the exception expires. NULL for every other selfie.';
            COMMENT ON INDEX ix_attendance_evidence__purge_due_override IS
                'Selfie purge, demo-exception selfies: one tenant''s unpurged rows whose stamped purge_due_at_utc has passed, oldest stamp first (SelfieEvidencePurger.FindDueItemsAsync, query d, its own limit), and the scheduler''s DISTINCT tenant scan (SelfieEvidenceRetention.MaybeDue). Partial on stamped, unpurged rows.';
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM attendance_evidence WHERE purge_due_at_utc IS NOT NULL AND purge_state <> 'Purged') THEN
                        RAISE EXCEPTION 'Selfies taken under the demo exception are still stored. Let the purge delete them (or purge them) before rolling back: dropping purge_due_at_utc would drop the promise to delete them.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropIndex(
                name: "ix_attendance_evidence__purge_due_override",
                table: "attendance_evidence");

            migrationBuilder.DropCheckConstraint(
                name: "ck_attendance_evidence__purge_due_after_capture",
                table: "attendance_evidence");

            migrationBuilder.DropColumn(
                name: "purge_due_at_utc",
                table: "attendance_evidence");
        }
    }
}
