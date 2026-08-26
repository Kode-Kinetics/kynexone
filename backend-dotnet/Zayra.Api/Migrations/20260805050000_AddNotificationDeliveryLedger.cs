using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// POD-D5 — NOTIFICATION DELIVERY LEDGER. Strictly ADDITIVE: one new table, nothing else.
    /// No column is added to, removed from, or rewritten on any existing table, and there is no
    /// backfill — safe to apply to all ~55 live tenants with zero data movement.
    ///
    /// WHY A TABLE WAS UNAVOIDABLE
    ///
    ///   • Notification.Status is the READ state (Unread/Read) — it is rendered by the admin bell
    ///     (NotificationsController.Recent) and by frontend/src/api/notifications.ts, so it cannot
    ///     be repurposed to carry delivery state.
    ///   • Notification.Channel is a SINGLE value, but one notification fans out to several
    ///     channels: delivery state is one-to-many by nature.
    ///   • Writing outcomes into admin_audit_logs (NewValuesJson) was considered purely to avoid a
    ///     migration and REJECTED: idempotency needs a DB-enforced unique key, and an audit row has
    ///     none. Without ux_notification_deliveries_tenant_dedupe every retry is racy and a
    ///     concurrent re-entry can send the same SMS twice.
    ///
    /// THE INDEXES ARE LOAD-BEARING
    ///
    ///   • ux (tenant_id, dedupe_key) — this IS the exactly-once guarantee. The dedupe key is a
    ///     SHA-256 of pure business identity (tenant, event code, entity, recipient, channel,
    ///     rendered content) with NO GUID in it, so a re-Lock / retried POST / double-click computes
    ///     the same key and the database refuses it. Check-then-act alone would not be safe.
    ///   • PARTIAL ix (outcome, next_attempt_at_utc) WHERE outcome IN ('queued','sending') — the
    ///     worker's drain query. This table grows by employees × channels on every payroll run
    ///     across every tenant, and terminal rows are the overwhelming majority; a full index would
    ///     not stay cheap.
    ///   • ix (tenant_id, created_at_utc) and ix (tenant_id, outcome, channel) — the admin
    ///     visibility endpoints (GET /api/notifications/deliveries and /deliveries/summary).
    ///
    /// PRIVACY / RETENTION
    ///
    ///   destination_masked is the only contact ever returned by the API. destination_raw is
    ///   populated ONLY for an external address with no directory subject to re-resolve from, and
    ///   NotificationDeliveryWorker clears it the moment the row reaches a terminal state — so the
    ///   PII window is the queue lifetime, not forever. error_message is scrubbed of phone numbers
    ///   and email addresses before it is persisted (providers echo the destination back inside
    ///   error text). RECOMMENDED RETENTION: purge terminal rows older than 180 days; there is no
    ///   purge job in this migration and the lead should schedule one before the table matures.
    ///
    /// CONCURRENCY NOTE FOR THE LEAD — READ THIS
    ///
    ///   This migration and its Designer were HAND-WRITTEN. `dotnet ef migrations add` was NOT run,
    ///   because two other pods were editing the same working tree and the tool regenerates
    ///   ZayraDbContextModelSnapshot.cs wholesale over their in-flight state. The snapshot edit that
    ///   accompanies this migration is a single inserted block for Zayra.Api.Models.NotificationDelivery
    ///   and touches no other line. Please reconcile the snapshot against the other pods' migrations
    ///   before merging.
    /// </summary>
    public partial class AddNotificationDeliveryLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "notification_deliveries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    notification_id = table.Column<Guid>(type: "uuid", nullable: true),
                    employee_notification_id = table.Column<Guid>(type: "uuid", nullable: true),
                    event_code = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    entity_name = table.Column<string>(type: "text", nullable: false),
                    entity_id = table.Column<string>(type: "text", nullable: true),
                    channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    audience_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    employee_id = table.Column<int>(type: "integer", nullable: true),
                    destination_masked = table.Column<string>(type: "text", nullable: false),
                    destination_raw = table.Column<string>(type: "text", nullable: false),
                    subject = table.Column<string>(type: "text", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    provider_name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    provider_reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    error_code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    error_message = table.Column<string>(type: "text", nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    max_attempts = table.Column<int>(type: "integer", nullable: false),
                    first_attempt_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_attempt_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    next_attempt_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    dedupe_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    lease_owner = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    lease_version = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_deliveries", x => x.id);
                });

            // THE exactly-once guarantee. Not an optimisation.
            migrationBuilder.CreateIndex(
                name: "ix_notification_deliveries_tenant_id_dedupe_key",
                table: "notification_deliveries",
                columns: new[] { "tenant_id", "dedupe_key" },
                unique: true);

            // Worker drain queue — partial so terminal rows never enter the index.
            migrationBuilder.CreateIndex(
                name: "ix_notification_deliveries_outcome_next_attempt_at_utc",
                table: "notification_deliveries",
                columns: new[] { "outcome", "next_attempt_at_utc" },
                filter: "outcome IN ('queued','sending')");

            migrationBuilder.CreateIndex(
                name: "ix_notification_deliveries_tenant_id_created_at_utc",
                table: "notification_deliveries",
                columns: new[] { "tenant_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_notification_deliveries_tenant_id_outcome_channel",
                table: "notification_deliveries",
                columns: new[] { "tenant_id", "outcome", "channel" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dropping the ledger loses delivery history but no business data — nothing else
            // references it and no existing table was modified on the way up.
            migrationBuilder.DropTable(name: "notification_deliveries");
        }
    }
}
