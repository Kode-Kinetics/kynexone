using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class OfferLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "artifact_id",
                table: "offer_letters",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "candidate_id",
                table: "offer_letters",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "communication_outcome",
                table: "offer_letters",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "frozen_at_utc",
                table: "offer_letters",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "previous_revision_id",
                table: "offer_letters",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "recipient_email",
                table: "offer_letters",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "revision_number",
                table: "offer_letters",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "series_id",
                table: "offer_letters",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "superseded_at_utc",
                table: "offer_letters",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "version",
                table: "offer_letters",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<Guid>(
                name: "current_issued_offer_id",
                table: "job_applications",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "offer_reservation_id",
                table: "job_applications",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "offer_reservation_offer_id",
                table: "job_applications",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "offer_reservation_state",
                table: "job_applications",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "offer_reservation_version",
                table: "job_applications",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "version",
                table: "job_applications",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_offer_letters_tenant_id_id",
                table: "offer_letters",
                columns: new[] { "tenant_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_job_applications_tenant_id_id",
                table: "job_applications",
                columns: new[] { "tenant_id", "id" });

            migrationBuilder.CreateTable(
                name: "offer_artifacts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    offer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    storage_key = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    provenance = table.Column<string>(type: "text", nullable: false),
                    renderer_version = table.Column<string>(type: "text", nullable: false),
                    source_offer_version = table.Column<long>(type: "bigint", nullable: false),
                    pending_offer_version = table.Column<long>(type: "bigint", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    finalized_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    deleted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_offer_artifacts", x => x.id);
                    table.UniqueConstraint("AK_offer_artifacts_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "FK_offer_artifacts_companies_tenant_id_company_id",
                        columns: x => new { x.tenant_id, x.company_id },
                        principalTable: "companies",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_offer_artifacts_job_applications_tenant_id_application_id",
                        columns: x => new { x.tenant_id, x.application_id },
                        principalTable: "job_applications",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_offer_artifacts_offer_letters_tenant_id_offer_id",
                        columns: x => new { x.tenant_id, x.offer_id },
                        principalTable: "offer_letters",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "offer_lifecycle_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    offer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_name = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    data_json = table.Column<string>(type: "text", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    occurred_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    recorded_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_offer_lifecycle_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_offer_lifecycle_events_companies_tenant_id_company_id",
                        columns: x => new { x.tenant_id, x.company_id },
                        principalTable: "companies",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_offer_lifecycle_events_job_applications_tenant_id_applicati~",
                        columns: x => new { x.tenant_id, x.application_id },
                        principalTable: "job_applications",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_offer_lifecycle_events_offer_letters_tenant_id_offer_id",
                        columns: x => new { x.tenant_id, x.offer_id },
                        principalTable: "offer_letters",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "offer_series",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legacy_root_offer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    next_revision_number = table.Column<int>(type: "integer", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_offer_series", x => x.id);
                    table.UniqueConstraint("AK_offer_series_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "FK_offer_series_companies_tenant_id_company_id",
                        columns: x => new { x.tenant_id, x.company_id },
                        principalTable: "companies",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_offer_series_job_applications_tenant_id_application_id",
                        columns: x => new { x.tenant_id, x.application_id },
                        principalTable: "job_applications",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_offer_series_offer_letters_tenant_id_legacy_root_offer_id",
                        columns: x => new { x.tenant_id, x.legacy_root_offer_id },
                        principalTable: "offer_letters",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_offer_letters_tenant_id_artifact_id",
                table: "offer_letters",
                columns: new[] { "tenant_id", "artifact_id" });

            migrationBuilder.CreateIndex(
                name: "IX_offer_letters_tenant_id_company_id_application_id_id",
                table: "offer_letters",
                columns: new[] { "tenant_id", "company_id", "application_id", "id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_offer_letters_tenant_id_company_id_id",
                table: "offer_letters",
                columns: new[] { "tenant_id", "company_id", "id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_offer_letters_tenant_id_previous_revision_id",
                table: "offer_letters",
                columns: new[] { "tenant_id", "previous_revision_id" });

            migrationBuilder.CreateIndex(
                name: "IX_offer_letters_tenant_id_series_id_revision_number",
                table: "offer_letters",
                columns: new[] { "tenant_id", "series_id", "revision_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_job_applications_tenant_id_company_id_id",
                table: "job_applications",
                columns: new[] { "tenant_id", "company_id", "id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_job_applications_tenant_id_current_issued_offer_id",
                table: "job_applications",
                columns: new[] { "tenant_id", "current_issued_offer_id" });

            migrationBuilder.CreateIndex(
                name: "IX_job_applications_tenant_id_offer_reservation_offer_id",
                table: "job_applications",
                columns: new[] { "tenant_id", "offer_reservation_offer_id" });

            migrationBuilder.CreateIndex(
                name: "IX_offer_artifacts_storage_key",
                table: "offer_artifacts",
                column: "storage_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_offer_artifacts_tenant_id_application_id",
                table: "offer_artifacts",
                columns: new[] { "tenant_id", "application_id" });

            migrationBuilder.CreateIndex(
                name: "IX_offer_artifacts_tenant_id_company_id",
                table: "offer_artifacts",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_offer_artifacts_tenant_id_company_id_id",
                table: "offer_artifacts",
                columns: new[] { "tenant_id", "company_id", "id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_offer_artifacts_tenant_id_company_id_offer_id_id",
                table: "offer_artifacts",
                columns: new[] { "tenant_id", "company_id", "offer_id", "id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_offer_artifacts_tenant_id_offer_id",
                table: "offer_artifacts",
                columns: new[] { "tenant_id", "offer_id" });

            migrationBuilder.CreateIndex(
                name: "IX_offer_lifecycle_events_tenant_id_application_id",
                table: "offer_lifecycle_events",
                columns: new[] { "tenant_id", "application_id" });

            migrationBuilder.CreateIndex(
                name: "IX_offer_lifecycle_events_tenant_id_company_id",
                table: "offer_lifecycle_events",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_offer_lifecycle_events_tenant_id_company_id_recorded_at_utc~",
                table: "offer_lifecycle_events",
                columns: new[] { "tenant_id", "company_id", "recorded_at_utc", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_offer_lifecycle_events_tenant_id_offer_id_idempotency_key",
                table: "offer_lifecycle_events",
                columns: new[] { "tenant_id", "offer_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_offer_series_tenant_id_application_id",
                table: "offer_series",
                columns: new[] { "tenant_id", "application_id" });

            migrationBuilder.CreateIndex(
                name: "IX_offer_series_tenant_id_company_id",
                table: "offer_series",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_offer_series_tenant_id_company_id_application_id_id",
                table: "offer_series",
                columns: new[] { "tenant_id", "company_id", "application_id", "id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_offer_series_tenant_id_company_id_id",
                table: "offer_series",
                columns: new[] { "tenant_id", "company_id", "id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_offer_series_tenant_id_legacy_root_offer_id",
                table: "offer_series",
                columns: new[] { "tenant_id", "legacy_root_offer_id" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_job_applications_offer_letters_tenant_id_current_issued_off~",
                table: "job_applications",
                columns: new[] { "tenant_id", "current_issued_offer_id" },
                principalTable: "offer_letters",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_job_applications_offer_letters_tenant_id_offer_reservation_~",
                table: "job_applications",
                columns: new[] { "tenant_id", "offer_reservation_offer_id" },
                principalTable: "offer_letters",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_offer_letters_offer_artifacts_tenant_id_artifact_id",
                table: "offer_letters",
                columns: new[] { "tenant_id", "artifact_id" },
                principalTable: "offer_artifacts",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_offer_letters_offer_letters_tenant_id_previous_revision_id",
                table: "offer_letters",
                columns: new[] { "tenant_id", "previous_revision_id" },
                principalTable: "offer_letters",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_offer_letters_offer_series_tenant_id_series_id",
                table: "offer_letters",
                columns: new[] { "tenant_id", "series_id" },
                principalTable: "offer_series",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

            // Nullable legacy CompanyId must not become an EF required alternate key. These
            // manually maintained FKs target nullable-preserving unique indexes instead.
            migrationBuilder.Sql("""
                ALTER TABLE offer_series ADD CONSTRAINT fk_offer_series_scoped_application
                  FOREIGN KEY (tenant_id,company_id,application_id) REFERENCES job_applications(tenant_id,company_id,id) ON DELETE RESTRICT;
                ALTER TABLE offer_series ADD CONSTRAINT fk_offer_series_scoped_legacy_root
                  FOREIGN KEY (tenant_id,company_id,application_id,legacy_root_offer_id) REFERENCES offer_letters(tenant_id,company_id,application_id,id) ON DELETE RESTRICT;
                ALTER TABLE offer_letters ADD CONSTRAINT fk_offer_revision_scoped_series
                  FOREIGN KEY (tenant_id,company_id,application_id,series_id) REFERENCES offer_series(tenant_id,company_id,application_id,id) ON DELETE RESTRICT;
                ALTER TABLE offer_letters ADD CONSTRAINT fk_offer_revision_scoped_previous
                  FOREIGN KEY (tenant_id,company_id,application_id,previous_revision_id) REFERENCES offer_letters(tenant_id,company_id,application_id,id) ON DELETE RESTRICT;
                ALTER TABLE offer_artifacts ADD CONSTRAINT fk_offer_artifact_scoped_parent
                  FOREIGN KEY (tenant_id,company_id,application_id,offer_id) REFERENCES offer_letters(tenant_id,company_id,application_id,id) ON DELETE RESTRICT;
                ALTER TABLE offer_lifecycle_events ADD CONSTRAINT fk_offer_event_scoped_parent
                  FOREIGN KEY (tenant_id,company_id,application_id,offer_id) REFERENCES offer_letters(tenant_id,company_id,application_id,id) ON DELETE RESTRICT;
                ALTER TABLE offer_letters ADD CONSTRAINT fk_offer_original_scoped_artifact
                  FOREIGN KEY (tenant_id,company_id,id,artifact_id) REFERENCES offer_artifacts(tenant_id,company_id,offer_id,id) ON DELETE RESTRICT;
                ALTER TABLE job_applications ADD CONSTRAINT fk_offer_current_scoped_application
                  FOREIGN KEY (tenant_id,company_id,id,current_issued_offer_id) REFERENCES offer_letters(tenant_id,company_id,application_id,id) ON DELETE RESTRICT;
                ALTER TABLE job_applications ADD CONSTRAINT fk_offer_reserved_scoped_application
                  FOREIGN KEY (tenant_id,company_id,id,offer_reservation_offer_id) REFERENCES offer_letters(tenant_id,company_id,application_id,id) ON DELETE RESTRICT;
                ALTER TABLE offer_letters ADD CONSTRAINT ck_offer_managed_identity CHECK
                  (version > 0 AND (series_id IS NULL OR (company_id IS NOT NULL AND revision_number IS NOT NULL AND revision_number > 0))
                   AND (artifact_id IS NULL OR (company_id IS NOT NULL AND series_id IS NOT NULL))
                   AND (frozen_at_utc IS NULL OR artifact_id IS NOT NULL));
                ALTER TABLE job_applications ADD CONSTRAINT ck_offer_reservation_shape CHECK
                  (version > 0 AND (current_issued_offer_id IS NULL OR company_id IS NOT NULL) AND
                   ((offer_reservation_id IS NULL AND offer_reservation_offer_id IS NULL AND offer_reservation_state IS NULL AND offer_reservation_version IS NULL)
                    OR (company_id IS NOT NULL AND offer_reservation_id IS NOT NULL AND offer_reservation_offer_id IS NOT NULL
                        AND offer_reservation_state IS NOT NULL AND offer_reservation_state IN ('Queued','Sending','Unknown')
                        AND offer_reservation_version IS NOT NULL AND offer_reservation_version > 0)));
                CREATE FUNCTION enforce_offer_lifecycle_immutability() RETURNS trigger LANGUAGE plpgsql AS $guard$
                BEGIN
                  IF current_setting('app.offer_lifecycle_erasure', true) = OLD.tenant_id::text
                     AND EXISTS (SELECT 1 FROM tenants WHERE id=OLD.tenant_id AND NOT is_active AND position('__deleted_' in slug)>0) THEN
                    IF TG_OP='DELETE' THEN RETURN OLD; ELSE RETURN NEW; END IF;
                  END IF;
                  IF TG_TABLE_NAME='offer_lifecycle_events' THEN
                    RAISE EXCEPTION 'offer_event_immutable: lifecycle evidence is append-only';
                  END IF;
                  IF TG_OP='DELETE' THEN
                    RAISE EXCEPTION 'offer_immutable: only authorized tenant erasure removes offer history';
                  END IF;
                  IF TG_TABLE_NAME='offer_artifacts' THEN
                    IF (to_jsonb(OLD) - ARRAY['state','finalized_at_utc','deleted_at_utc','version']) IS DISTINCT FROM
                       (to_jsonb(NEW) - ARRAY['state','finalized_at_utc','deleted_at_utc','version'])
                       OR (OLD.state='Finalized' AND (NEW.state IS DISTINCT FROM OLD.state OR NEW.finalized_at_utc IS DISTINCT FROM OLD.finalized_at_utc)) THEN
                      RAISE EXCEPTION 'offer_immutable: original artifact identity and finalized provenance are immutable';
                    END IF;
                  ELSIF OLD.frozen_at_utc IS NOT NULL THEN
                    IF (to_jsonb(OLD) - ARRAY['status','version','sent_at_utc','accepted_at_utc','declined_at_utc','decline_reason','superseded_at_utc','communication_outcome']) IS DISTINCT FROM
                       (to_jsonb(NEW) - ARRAY['status','version','sent_at_utc','accepted_at_utc','declined_at_utc','decline_reason','superseded_at_utc','communication_outcome']) THEN
                      RAISE EXCEPTION 'offer_immutable: frozen terms and recipient identity require a new revision';
                    END IF;
                  END IF;
                  RETURN NEW;
                END;
                $guard$;
                CREATE TRIGGER offer_terms_immutable BEFORE UPDATE OR DELETE ON offer_letters
                  FOR EACH ROW EXECUTE FUNCTION enforce_offer_lifecycle_immutability();
                CREATE TRIGGER offer_artifact_immutable BEFORE UPDATE OR DELETE ON offer_artifacts
                  FOR EACH ROW EXECUTE FUNCTION enforce_offer_lifecycle_immutability();
                CREATE TRIGGER offer_event_immutable BEFORE UPDATE OR DELETE ON offer_lifecycle_events
                  FOR EACH ROW EXECUTE FUNCTION enforce_offer_lifecycle_immutability();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $guard$ BEGIN
                  IF EXISTS (SELECT 1 FROM offer_artifacts) OR EXISTS (SELECT 1 FROM offer_lifecycle_events)
                     OR EXISTS (SELECT 1 FROM offer_series) THEN
                    RAISE EXCEPTION 'offer_lifecycle_history_exists: rollback would destroy original-file obligations and revision evidence';
                  END IF;
                END $guard$;
                DROP TRIGGER offer_terms_immutable ON offer_letters;
                DROP TRIGGER offer_artifact_immutable ON offer_artifacts;
                DROP TRIGGER offer_event_immutable ON offer_lifecycle_events;
                DROP FUNCTION enforce_offer_lifecycle_immutability();
                ALTER TABLE job_applications DROP CONSTRAINT fk_offer_current_scoped_application,
                  DROP CONSTRAINT fk_offer_reserved_scoped_application, DROP CONSTRAINT ck_offer_reservation_shape;
                ALTER TABLE offer_letters DROP CONSTRAINT fk_offer_revision_scoped_series,
                  DROP CONSTRAINT fk_offer_revision_scoped_previous, DROP CONSTRAINT fk_offer_original_scoped_artifact,
                  DROP CONSTRAINT ck_offer_managed_identity;
                ALTER TABLE offer_series DROP CONSTRAINT fk_offer_series_scoped_application,
                  DROP CONSTRAINT fk_offer_series_scoped_legacy_root;
                ALTER TABLE offer_artifacts DROP CONSTRAINT fk_offer_artifact_scoped_parent;
                ALTER TABLE offer_lifecycle_events DROP CONSTRAINT fk_offer_event_scoped_parent;
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_job_applications_offer_letters_tenant_id_current_issued_off~",
                table: "job_applications");

            migrationBuilder.DropForeignKey(
                name: "FK_job_applications_offer_letters_tenant_id_offer_reservation_~",
                table: "job_applications");

            migrationBuilder.DropForeignKey(
                name: "FK_offer_letters_offer_artifacts_tenant_id_artifact_id",
                table: "offer_letters");

            migrationBuilder.DropForeignKey(
                name: "FK_offer_letters_offer_letters_tenant_id_previous_revision_id",
                table: "offer_letters");

            migrationBuilder.DropForeignKey(
                name: "FK_offer_letters_offer_series_tenant_id_series_id",
                table: "offer_letters");

            migrationBuilder.DropTable(
                name: "offer_artifacts");

            migrationBuilder.DropTable(
                name: "offer_lifecycle_events");

            migrationBuilder.DropTable(
                name: "offer_series");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_offer_letters_tenant_id_id",
                table: "offer_letters");

            migrationBuilder.DropIndex(
                name: "IX_offer_letters_tenant_id_artifact_id",
                table: "offer_letters");

            migrationBuilder.DropIndex(
                name: "IX_offer_letters_tenant_id_company_id_application_id_id",
                table: "offer_letters");

            migrationBuilder.DropIndex(
                name: "IX_offer_letters_tenant_id_company_id_id",
                table: "offer_letters");

            migrationBuilder.DropIndex(
                name: "IX_offer_letters_tenant_id_previous_revision_id",
                table: "offer_letters");

            migrationBuilder.DropIndex(
                name: "IX_offer_letters_tenant_id_series_id_revision_number",
                table: "offer_letters");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_job_applications_tenant_id_id",
                table: "job_applications");

            migrationBuilder.DropIndex(
                name: "IX_job_applications_tenant_id_company_id_id",
                table: "job_applications");

            migrationBuilder.DropIndex(
                name: "IX_job_applications_tenant_id_current_issued_offer_id",
                table: "job_applications");

            migrationBuilder.DropIndex(
                name: "IX_job_applications_tenant_id_offer_reservation_offer_id",
                table: "job_applications");

            migrationBuilder.DropColumn(
                name: "artifact_id",
                table: "offer_letters");

            migrationBuilder.DropColumn(
                name: "candidate_id",
                table: "offer_letters");

            migrationBuilder.DropColumn(
                name: "communication_outcome",
                table: "offer_letters");

            migrationBuilder.DropColumn(
                name: "frozen_at_utc",
                table: "offer_letters");

            migrationBuilder.DropColumn(
                name: "previous_revision_id",
                table: "offer_letters");

            migrationBuilder.DropColumn(
                name: "recipient_email",
                table: "offer_letters");

            migrationBuilder.DropColumn(
                name: "revision_number",
                table: "offer_letters");

            migrationBuilder.DropColumn(
                name: "series_id",
                table: "offer_letters");

            migrationBuilder.DropColumn(
                name: "superseded_at_utc",
                table: "offer_letters");

            migrationBuilder.DropColumn(
                name: "version",
                table: "offer_letters");

            migrationBuilder.DropColumn(
                name: "current_issued_offer_id",
                table: "job_applications");

            migrationBuilder.DropColumn(
                name: "offer_reservation_id",
                table: "job_applications");

            migrationBuilder.DropColumn(
                name: "offer_reservation_offer_id",
                table: "job_applications");

            migrationBuilder.DropColumn(
                name: "offer_reservation_state",
                table: "job_applications");

            migrationBuilder.DropColumn(
                name: "offer_reservation_version",
                table: "job_applications");

            migrationBuilder.DropColumn(
                name: "version",
                table: "job_applications");
        }
    }
}
