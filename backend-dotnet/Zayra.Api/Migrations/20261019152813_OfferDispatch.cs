using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class OfferDispatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "offer_dispatches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    offer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    artifact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    request_fingerprint = table.Column<string>(type: "text", nullable: false),
                    subject = table.Column<string>(type: "text", nullable: false),
                    message = table.Column<string>(type: "text", nullable: false),
                    recipient_email = table.Column<string>(type: "text", nullable: false),
                    recipient_name = table.Column<string>(type: "text", nullable: false),
                    initiator_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    initiator_name = table.Column<string>(type: "text", nullable: false),
                    resend_reason = table.Column<string>(type: "text", nullable: true),
                    from_address = table.Column<string>(type: "text", nullable: true),
                    from_name = table.Column<string>(type: "text", nullable: true),
                    sender_source = table.Column<string>(type: "text", nullable: false),
                    sender_fingerprint = table.Column<string>(type: "text", nullable: false),
                    outcome = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    reservation_version = table.Column<long>(type: "bigint", nullable: false),
                    lease_token = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_until_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    transport_started_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    transport_completed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    finalized_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_offer_dispatches", x => x.id);
                    table.UniqueConstraint("AK_offer_dispatches_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "FK_offer_dispatches_companies_tenant_id_company_id",
                        columns: x => new { x.tenant_id, x.company_id },
                        principalTable: "companies",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_offer_dispatches_job_applications_tenant_id_application_id",
                        columns: x => new { x.tenant_id, x.application_id },
                        principalTable: "job_applications",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_offer_dispatches_offer_artifacts_tenant_id_artifact_id",
                        columns: x => new { x.tenant_id, x.artifact_id },
                        principalTable: "offer_artifacts",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_offer_dispatches_offer_letters_tenant_id_offer_id",
                        columns: x => new { x.tenant_id, x.offer_id },
                        principalTable: "offer_letters",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "offer_dispatch_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    offer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    dispatch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lease_token = table.Column<Guid>(type: "uuid", nullable: false),
                    stage = table.Column<string>(type: "text", nullable: false),
                    outcome = table.Column<string>(type: "text", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    occurred_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_offer_dispatch_attempts", x => x.id);
                    table.ForeignKey(
                        name: "FK_offer_dispatch_attempts_companies_tenant_id_company_id",
                        columns: x => new { x.tenant_id, x.company_id },
                        principalTable: "companies",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_offer_dispatch_attempts_offer_dispatches_tenant_id_dispatch~",
                        columns: x => new { x.tenant_id, x.dispatch_id },
                        principalTable: "offer_dispatches",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_offer_dispatch_attempts_tenant_id_company_id",
                table: "offer_dispatch_attempts",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_offer_dispatch_attempts_tenant_id_dispatch_id_lease_token_s~",
                table: "offer_dispatch_attempts",
                columns: new[] { "tenant_id", "dispatch_id", "lease_token", "stage" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_offer_dispatch_attempts_tenant_id_dispatch_id_occurred_at_u~",
                table: "offer_dispatch_attempts",
                columns: new[] { "tenant_id", "dispatch_id", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_offer_dispatches_outcome_lease_until_utc_created_at_utc",
                table: "offer_dispatches",
                columns: new[] { "outcome", "lease_until_utc", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_offer_dispatches_tenant_id_application_id",
                table: "offer_dispatches",
                columns: new[] { "tenant_id", "application_id" });

            migrationBuilder.CreateIndex(
                name: "IX_offer_dispatches_tenant_id_artifact_id",
                table: "offer_dispatches",
                columns: new[] { "tenant_id", "artifact_id" });

            migrationBuilder.CreateIndex(
                name: "IX_offer_dispatches_tenant_id_company_id",
                table: "offer_dispatches",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_offer_dispatches_tenant_id_company_id_application_id_offer_~",
                table: "offer_dispatches",
                columns: new[] { "tenant_id", "company_id", "application_id", "offer_id", "id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_offer_dispatches_tenant_id_offer_id_idempotency_key",
                table: "offer_dispatches",
                columns: new[] { "tenant_id", "offer_id", "idempotency_key" },
                unique: true);

            migrationBuilder.Sql("""
                UPDATE offer_letters SET communication_outcome='External' WHERE communication_outcome='ExternalRecorded';
                ALTER TABLE offer_dispatches ADD CONSTRAINT fk_offer_dispatch_scoped_application
                  FOREIGN KEY (tenant_id,company_id,application_id) REFERENCES job_applications(tenant_id,company_id,id) ON DELETE RESTRICT;
                ALTER TABLE offer_dispatches ADD CONSTRAINT fk_offer_dispatch_scoped_offer
                  FOREIGN KEY (tenant_id,company_id,application_id,offer_id) REFERENCES offer_letters(tenant_id,company_id,application_id,id) ON DELETE RESTRICT;
                ALTER TABLE offer_dispatches ADD CONSTRAINT fk_offer_dispatch_scoped_artifact
                  FOREIGN KEY (tenant_id,company_id,offer_id,artifact_id) REFERENCES offer_artifacts(tenant_id,company_id,offer_id,id) ON DELETE RESTRICT;
                ALTER TABLE offer_dispatch_attempts ADD CONSTRAINT fk_offer_attempt_scoped_dispatch
                  FOREIGN KEY (tenant_id,company_id,application_id,offer_id,dispatch_id) REFERENCES offer_dispatches(tenant_id,company_id,application_id,offer_id,id) ON DELETE RESTRICT;
                ALTER TABLE offer_dispatches ADD CONSTRAINT ck_offer_dispatch_state CHECK
                  (version>0 AND reservation_version>0 AND outcome IN ('Queued','Sending','Cancelled','AcceptedByRelay','Captured','NotConfigured','Failed','Unknown','External')
                   AND (outcome<>'Sending' OR (lease_token IS NOT NULL AND lease_until_utc IS NOT NULL)));
                ALTER TABLE offer_dispatch_attempts ADD CONSTRAINT ck_offer_attempt_stage CHECK
                  (stage IN ('Claimed','TransportStarted','Result','LateResult','LeaseExpired')
                   AND outcome IN ('Sending','AcceptedByRelay','Captured','NotConfigured','Failed','Unknown'));
                CREATE FUNCTION enforce_offer_dispatch_immutability() RETURNS trigger LANGUAGE plpgsql AS $guard$
                BEGIN
                  IF current_setting('app.offer_lifecycle_erasure', true)=OLD.tenant_id::text
                     AND EXISTS(SELECT 1 FROM tenants WHERE id=OLD.tenant_id AND NOT is_active AND position('__deleted_' in slug)>0) THEN
                    IF TG_OP='DELETE' THEN RETURN OLD; ELSE RETURN NEW; END IF;
                  END IF;
                  IF TG_TABLE_NAME='offer_dispatch_attempts' OR TG_OP='DELETE' THEN
                    RAISE EXCEPTION 'offer_dispatch_evidence_immutable: only authorized tenant erasure may remove evidence';
                  END IF;
                  IF (to_jsonb(OLD)-ARRAY['outcome','version','lease_token','lease_until_utc','transport_started_at_utc','transport_completed_at_utc','finalized_at_utc']) IS DISTINCT FROM
                     (to_jsonb(NEW)-ARRAY['outcome','version','lease_token','lease_until_utc','transport_started_at_utc','transport_completed_at_utc','finalized_at_utc'])
                     OR (OLD.lease_token IS NOT NULL AND NEW.lease_token IS DISTINCT FROM OLD.lease_token)
                     OR (OLD.transport_started_at_utc IS NOT NULL AND NEW.transport_started_at_utc IS DISTINCT FROM OLD.transport_started_at_utc)
                     OR (OLD.transport_completed_at_utc IS NOT NULL AND NEW.transport_completed_at_utc IS DISTINCT FROM OLD.transport_completed_at_utc)
                     OR (OLD.finalized_at_utc IS NOT NULL AND NEW.finalized_at_utc IS DISTINCT FROM OLD.finalized_at_utc) THEN
                    RAISE EXCEPTION 'offer_dispatch_immutable: reviewed request and observed transport identity cannot be rewritten';
                  END IF;
                  RETURN NEW;
                END;
                $guard$;
                CREATE TRIGGER offer_dispatch_immutable BEFORE UPDATE OR DELETE ON offer_dispatches
                  FOR EACH ROW EXECUTE FUNCTION enforce_offer_dispatch_immutability();
                CREATE TRIGGER offer_dispatch_attempt_immutable BEFORE UPDATE OR DELETE ON offer_dispatch_attempts
                  FOR EACH ROW EXECUTE FUNCTION enforce_offer_dispatch_immutability();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $guard$ BEGIN
                  IF EXISTS(SELECT 1 FROM offer_dispatches) OR EXISTS(SELECT 1 FROM offer_dispatch_attempts) THEN
                    RAISE EXCEPTION 'offer_dispatch_history_exists: rollback would destroy delivery evidence';
                  END IF;
                END $guard$;
                DROP TRIGGER offer_dispatch_attempt_immutable ON offer_dispatch_attempts;
                DROP TRIGGER offer_dispatch_immutable ON offer_dispatches;
                DROP FUNCTION enforce_offer_dispatch_immutability();
                """);
            migrationBuilder.DropTable(
                name: "offer_dispatch_attempts");

            migrationBuilder.DropTable(
                name: "offer_dispatches");
        }
    }
}
