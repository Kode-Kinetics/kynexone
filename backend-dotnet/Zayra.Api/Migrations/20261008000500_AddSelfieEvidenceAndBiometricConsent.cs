using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSelfieEvidenceAndBiometricConsent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Selfie attendance v2 — expand phase, additive only: two new tables (attendance_evidence carries the review-3
            // server-failure waiver columns failed_reason / waiver_*) and nothing on any existing table, so the
            // previous release runs unchanged against this schema (N-1). No data is written. The whole migration runs in
            // one transaction (PostgreSQL DDL is transactional), so a failure leaves nothing behind and it re-runs clean;
            // the COMMENT statements at the end are idempotent by nature.
            migrationBuilder.CreateTable(
                name: "attendance_evidence",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    storage_key = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    sha256 = table.Column<string>(type: "character(64)", nullable: true),
                    content_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    byte_size = table.Column<int>(type: "integer", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    used_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    used_by_raw_event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    purge_state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "Pending"),
                    purged_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    failed_reason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    waiver_consumed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    waiver_raw_event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    waiver_cancelled_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attendance_evidence", x => x.id);
                    table.CheckConstraint("ck_attendance_evidence__active_payload", "purge_state <> 'Active' OR (sha256 IS NOT NULL AND byte_size IS NOT NULL)");
                    table.CheckConstraint("ck_attendance_evidence__byte_size", "byte_size IS NULL OR byte_size > 0");
                    table.CheckConstraint("ck_attendance_evidence__expiry", "expires_at_utc > created_at_utc");
                    table.CheckConstraint("ck_attendance_evidence__failed_never_active", "failed_reason IS NULL OR (purge_state <> 'Active' AND used_at_utc IS NULL)");
                    table.CheckConstraint("ck_attendance_evidence__failed_reason", "failed_reason IS NULL OR failed_reason IN ('Busy','Storage')");
                    table.CheckConstraint("ck_attendance_evidence__purge_state", "purge_state IN ('Pending','Active','Purged')");
                    table.CheckConstraint("ck_attendance_evidence__purged_pair", "(purge_state = 'Purged') = (purged_at_utc IS NOT NULL)");
                    table.CheckConstraint("ck_attendance_evidence__used_pair", "(used_at_utc IS NULL) = (used_by_raw_event_id IS NULL)");
                    table.CheckConstraint("ck_attendance_evidence__used_was_active", "used_at_utc IS NULL OR purge_state <> 'Pending'");
                    table.CheckConstraint("ck_attendance_evidence__waiver_needs_failure", "failed_reason IS NOT NULL OR (waiver_consumed_at_utc IS NULL AND waiver_cancelled_at_utc IS NULL)");
                    table.CheckConstraint("ck_attendance_evidence__waiver_once", "waiver_consumed_at_utc IS NULL OR waiver_cancelled_at_utc IS NULL");
                    table.CheckConstraint("ck_attendance_evidence__waiver_pair", "(waiver_consumed_at_utc IS NULL) = (waiver_raw_event_id IS NULL)");
                    table.ForeignKey(
                        name: "FK_attendance_evidence_attendance_raw_events_used_by_raw_event~",
                        column: x => x.used_by_raw_event_id,
                        principalTable: "attendance_raw_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_attendance_evidence_attendance_raw_events_waiver_raw_event_~",
                        column: x => x.waiver_raw_event_id,
                        principalTable: "attendance_raw_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_attendance_evidence_employees_employee_id",
                        column: x => x.employee_id,
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "biometric_consents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    policy_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    given_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    withdrawn_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    channel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_biometric_consents", x => x.id);
                    table.CheckConstraint("ck_biometric_consents__channel", "channel IN ('Mobile','Web')");
                    table.CheckConstraint("ck_biometric_consents__policy_version", "policy_version <> ''");
                    table.CheckConstraint("ck_biometric_consents__withdrawn_after_given", "withdrawn_at_utc IS NULL OR withdrawn_at_utc >= given_at_utc");
                    table.ForeignKey(
                        name: "FK_biometric_consents_employees_employee_id",
                        column: x => x.employee_id,
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_attendance_evidence__employee_created",
                table: "attendance_evidence",
                columns: new[] { "tenant_id", "employee_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_attendance_evidence__purge_due",
                table: "attendance_evidence",
                columns: new[] { "tenant_id", "created_at_utc" },
                filter: "purge_state IN ('Pending','Active')")
                .Annotation("Npgsql:IndexInclude", new[] { "used_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_attendance_evidence__used_purge_due",
                table: "attendance_evidence",
                columns: new[] { "tenant_id", "used_at_utc" },
                filter: "purge_state = 'Active' AND used_at_utc IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_attendance_evidence__waived_punches",
                table: "attendance_evidence",
                columns: new[] { "tenant_id", "waiver_consumed_at_utc" },
                filter: "waiver_consumed_at_utc IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_attendance_evidence__waiver_raw_event",
                table: "attendance_evidence",
                column: "waiver_raw_event_id",
                unique: true,
                filter: "waiver_raw_event_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_attendance_evidence_employee_id",
                table: "attendance_evidence",
                column: "employee_id");

            migrationBuilder.CreateIndex(
                name: "ux_attendance_evidence__used_by_raw_event",
                table: "attendance_evidence",
                column: "used_by_raw_event_id",
                unique: true,
                filter: "used_by_raw_event_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_biometric_consents__employee_history",
                table: "biometric_consents",
                columns: new[] { "tenant_id", "employee_id", "given_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_biometric_consents_employee_id",
                table: "biometric_consents",
                column: "employee_id");

            migrationBuilder.CreateIndex(
                name: "ux_biometric_consents__one_open_per_employee",
                table: "biometric_consents",
                columns: new[] { "tenant_id", "employee_id" },
                unique: true,
                filter: "withdrawn_at_utc IS NULL");

            // What EF cannot model: the table @tier/@owner/@retention tags and, for every index, the query it serves.
            migrationBuilder.Sql(Comments);
        }

        /// <summary>Table and index comments (docs/schema: every table names its owner and retention, every index its query).</summary>
        public const string Comments = """
            COMMENT ON TABLE attendance_evidence IS
                'One selfie an employee uploaded for an attendance punch: the envelope (owner, SHA-256, single-use evidence id, expiry, the punch that used it) of a re-encoded EXIF-free JPEG in document storage. Inserted Pending before the upload, Active once the file is stored; one upload in flight per employee (a Pending row younger than 60 s refuses another); an attempt refused for bad input deletes its row, and a busy attempt is kept Purged (no file) with failed_reason, so only attempts that reached storage count toward the hourly limit. A server failure (Busy, Storage) with no other attempt in flight is a waiver: failed_reason set; one punch may use it within 10 minutes (waiver_consumed_at_utc + waiver_raw_event_id, under the per-employee advisory lock), at most 2 per employee per tenant-local day; a later successful upload cancels it (waiver_cancelled_at_utc). No face matching. Blob purged 90 days after the punch''s payroll month locks, at work date + 120 days without a lock, 24 hours after an unused upload (at once when the employee has no open consent), 1 hour after an unfinished one, at once for unused selfies when consent is withdrawn, and before tenant erasure; the purge runs every 15 minutes; the row and its sha256 are kept. @tier:E @owner:HR @retention:E';
            COMMENT ON TABLE biometric_consents IS
                'An employee''s consent to selfie attendance per policy version, with the channel it was given on and when it was withdrawn. Without open consent the employee punches without a selfie. @tier:E @owner:HR @retention:employment+statutory';
            COMMENT ON INDEX ix_attendance_evidence__employee_created IS
                'One employee''s attempts by time, under the per-employee advisory lock: the upload rate limit (COUNT(*) of attempts that reached storage in the last hour, busy ones excluded) and the one-upload-in-flight check (a Pending row younger than 60 s) in AttendanceEvidenceController.ReserveAttemptAsync; the open server-failure waiver of the last 10 minutes and the waivers used today (bounded by created_at_utc) in SelfieWaivers.';
            COMMENT ON INDEX ix_attendance_evidence__waived_punches IS
                'HR''s waived-punches report (GET /api/attendance/selfie/waived-punches): one tenant''s used server-failure waivers in a date range, newest first. Partial: only used waivers.';
            COMMENT ON INDEX ux_attendance_evidence__waiver_raw_event IS
                'One waiver per punch, and punch -> waiver lookup by attendance_raw_events.id; backs the RESTRICT FK. Partial: rows without a used waiver carry NULL.';
            COMMENT ON INDEX ix_attendance_evidence__purge_due IS
                'Selfie purge: SELECT DISTINCT tenant_id over rows not yet purged that can be due (SelfieEvidencePurgeScheduler.EnqueueDueAsync), and one tenant''s Pending rows past an hour and unused rows past 24 hours, oldest first (SelfieEvidencePurger.FindDueItemsAsync, queries a and b). Partial on purge_state IN (Pending, Active) so purged rows never bloat it; INCLUDE used_at_utc answers the due predicate from the index.';
            COMMENT ON INDEX ix_attendance_evidence__used_purge_due IS
                'Selfie purge, used selfies: one tenant''s Active rows a punch used, ordered by used_at_utc, due by the 120-day fallback (excluding months locked less than 90 days ago, whose rows wait for lock + 90) or inside one payroll month locked 90+ days ago (SelfieEvidencePurger.FindDueItemsAsync, queries c1 and c2), each with its own limit and returning only due rows, so none starves another. Partial on Active and used.';
            COMMENT ON INDEX ux_attendance_evidence__used_by_raw_event IS
                'One selfie per punch, and punch -> selfie lookup by attendance_raw_events.id. Partial: unused rows carry NULL.';
            COMMENT ON INDEX "IX_attendance_evidence_employee_id" IS
                'EF foreign-key index on employee_id: serves the ON DELETE RESTRICT check from employees.';
            COMMENT ON INDEX ux_biometric_consents__one_open_per_employee IS
                'Is there open consent for this employee (upload, punch, ESS read), and at most one open row per employee. Partial on withdrawn_at_utc IS NULL.';
            COMMENT ON INDEX ix_biometric_consents__employee_history IS
                'One employee''s consent history, newest first (audit and data-subject requests).';
            COMMENT ON INDEX "IX_biometric_consents_employee_id" IS
                'EF foreign-key index on employee_id: serves the ON DELETE RESTRICT check from employees.';
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Never roll back over evidence: a consent record or a selfie envelope is part of the audit trail.
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM attendance_evidence) OR EXISTS (SELECT 1 FROM biometric_consents) THEN
                        RAISE EXCEPTION 'Selfie evidence or biometric consent rows exist. Preserve them and use a forward corrective migration.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropTable(
                name: "attendance_evidence");

            migrationBuilder.DropTable(
                name: "biometric_consents");
        }
    }
}
