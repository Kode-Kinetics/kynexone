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
            // Selfie attendance v2 — expand phase, additive only: two new tables and nothing on any existing table, so the
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
                    sha256 = table.Column<string>(type: "character(64)", nullable: false),
                    content_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    byte_size = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    used_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    used_by_raw_event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    purge_state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "Active"),
                    purged_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attendance_evidence", x => x.id);
                    table.CheckConstraint("ck_attendance_evidence__byte_size", "byte_size > 0");
                    table.CheckConstraint("ck_attendance_evidence__expiry", "expires_at_utc > created_at_utc");
                    table.CheckConstraint("ck_attendance_evidence__purge_state", "purge_state IN ('Active','Purged')");
                    table.CheckConstraint("ck_attendance_evidence__purged_pair", "(purge_state = 'Purged') = (purged_at_utc IS NOT NULL)");
                    table.CheckConstraint("ck_attendance_evidence__used_pair", "(used_at_utc IS NULL) = (used_by_raw_event_id IS NULL)");
                    table.ForeignKey(
                        name: "FK_attendance_evidence_attendance_raw_events_used_by_raw_event~",
                        column: x => x.used_by_raw_event_id,
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
                columns: new[] { "purge_state", "created_at_utc" },
                filter: "purge_state = 'Active'");

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
                'One selfie an employee uploaded for an attendance punch: the envelope (owner, SHA-256, single-use evidence id, expiry, the punch that used it) of a re-encoded EXIF-free JPEG in document storage. No face matching. Blob purged 90 days after the punch''s payroll month locks, at work date + 120 days without a lock, or 24 hours after an unused upload; the row and its sha256 are kept. @tier:E @owner:HR @retention:E';
            COMMENT ON TABLE biometric_consents IS
                'An employee''s consent to selfie attendance per policy version, with the channel it was given on and when it was withdrawn. Without open consent the employee punches without a selfie. @tier:E @owner:HR @retention:employment+statutory';
            COMMENT ON INDEX ix_attendance_evidence__employee_created IS
                'Upload rate limit: COUNT(*) of one employee''s attendance_evidence rows with created_at_utc in the last hour (SelfieAttendanceController.StoreAsync).';
            COMMENT ON INDEX ix_attendance_evidence__purge_due IS
                'Selfie purge: Active rows old enough to be due, oldest first (SelfieEvidencePurgeScheduler.EnqueueDueAsync, SelfieEvidencePurger.FindDueAsync). Partial on purge_state = ''Active'' so purged rows never bloat it.';
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
