using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddGovernmentCredentialsAndExchanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "government_credentials",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    body = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    environment = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    ciphertext = table.Column<byte[]>(type: "bytea", nullable: false),
                    dek_version = table.Column<int>(type: "integer", nullable: false),
                    credential_version = table.Column<int>(type: "integer", nullable: false),
                    client_identifier = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    base_url_host = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    token_url_host = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_used_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    rotated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    rotated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_government_credentials", x => x.id);
                    table.CheckConstraint("ck_government_credentials__body", "body IN ('qiwa','gosi','muqeem','takamol','bank_anb')");
                    table.CheckConstraint("ck_government_credentials__ciphertext", "octet_length(ciphertext) >= 30 AND get_byte(ciphertext, 0) IN (1, 2)");
                    table.CheckConstraint("ck_government_credentials__environment", "environment IN ('sandbox','production')");
                    table.CheckConstraint("ck_government_credentials__kind", "kind IN ('oauth_client','api_key','private_key','certificate')");
                    table.CheckConstraint("ck_government_credentials__rotation", "(rotated_at_utc IS NULL) = (credential_version = 1)");
                    table.CheckConstraint("ck_government_credentials__version", "credential_version >= 1 AND dek_version BETWEEN 1 AND 250");
                    table.ForeignKey(
                        name: "FK_government_credentials_companies_company_id",
                        column: x => x.company_id,
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_government_credentials_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "government_exchanges",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    body = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    operation = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    channel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    environment = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    subject_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    subject_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    request_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    response_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    artifact_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    masked_preview = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    http_status = table.Column<int>(type: "integer", nullable: true),
                    error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    external_reference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    artifact_secondary_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    account_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    batch_reference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    total_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    item_count = table.Column<int>(type: "integer", nullable: true),
                    state_version = table.Column<int>(type: "integer", nullable: false),
                    resolved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resolved_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    resolution = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    resolution_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    last_polled_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    poll_count = table.Column<int>(type: "integer", nullable: false),
                    adapter = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    is_live = table.Column<bool>(type: "boolean", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approval_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_government_exchanges", x => x.id);
                    table.CheckConstraint("ck_government_exchanges__body", "body IN ('qiwa','gosi','muqeem','takamol','bank_anb')");
                    table.CheckConstraint("ck_government_exchanges__channel", "channel IN ('file_out','file_in','manual','api')");
                    table.CheckConstraint("ck_government_exchanges__counts", "state_version >= 1 AND poll_count >= 0 AND (item_count IS NULL OR item_count >= 0)");
                    table.CheckConstraint("ck_government_exchanges__environment", "environment IS NULL OR environment IN ('sandbox','production')");
                    table.CheckConstraint("ck_government_exchanges__hashes", "(request_sha256 IS NULL OR request_sha256 ~ '^[0-9a-f]{64}$') AND (response_sha256 IS NULL OR response_sha256 ~ '^[0-9a-f]{64}$') AND (artifact_sha256 IS NULL OR artifact_sha256 ~ '^[0-9a-f]{64}$') AND (artifact_secondary_sha256 IS NULL OR artifact_secondary_sha256 ~ '^[0-9a-f]{64}$')");
                    table.CheckConstraint("ck_government_exchanges__intent", "state <> 'SubmitIntended' OR (artifact_sha256 IS NOT NULL AND subject_id IS NOT NULL AND company_id IS NOT NULL AND account_fingerprint IS NOT NULL AND batch_reference IS NOT NULL)");
                    table.CheckConstraint("ck_government_exchanges__live_channel", "NOT is_live OR channel = 'api'");
                    table.CheckConstraint("ck_government_exchanges__live_states", "state NOT IN ('Accepted','Initiated','Validated','InProgress','Completed','Rejected') OR (is_live AND channel = 'api')");
                    table.CheckConstraint("ck_government_exchanges__resolution", "(resolution IS NULL AND resolved_by_user_id IS NULL AND resolved_at_utc IS NULL) OR (resolution IN ('bank_has_it','bank_does_not') AND resolved_by_user_id IS NOT NULL AND resolved_at_utc IS NOT NULL AND resolution_reason IS NOT NULL)");
                    table.CheckConstraint("ck_government_exchanges__second_person", "resolved_by_user_id IS NULL OR resolved_by_user_id IS DISTINCT FROM actor_user_id");
                    table.CheckConstraint("ck_government_exchanges__state", "state IN ('Attempted','Recorded','Simulated','Refused','Failed','SubmitIntended','Accepted','RejectedStructure','Unknown','ReleasedAfterUnknown','Initiated','Validated','InProgress','Completed','Rejected')");
                    table.ForeignKey(
                        name: "FK_government_exchanges_companies_company_id",
                        column: x => x.company_id,
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_government_exchanges_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_government_credentials_company_id",
                table: "government_credentials",
                column: "company_id");

            migrationBuilder.CreateIndex(
                name: "IX_government_credentials_tenant_id_company_id",
                table: "government_credentials",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "ux_government_credentials__company_body_env",
                table: "government_credentials",
                columns: new[] { "tenant_id", "company_id", "body", "environment" },
                unique: true,
                filter: "company_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_government_credentials__tenant_body_env",
                table: "government_credentials",
                columns: new[] { "tenant_id", "body", "environment" },
                unique: true,
                filter: "company_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_government_exchanges__tenant_subject",
                table: "government_exchanges",
                columns: new[] { "tenant_id", "body", "subject_type", "subject_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_government_exchanges_company_id",
                table: "government_exchanges",
                column: "company_id");

            migrationBuilder.CreateIndex(
                name: "IX_government_exchanges_tenant_id_company_id",
                table: "government_exchanges",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "ux_government_exchanges__bank_reference",
                table: "government_exchanges",
                columns: new[] { "tenant_id", "body", "external_reference" },
                unique: true,
                filter: "external_reference IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_government_exchanges__open_batch",
                table: "government_exchanges",
                columns: new[] { "tenant_id", "body", "account_fingerprint", "batch_reference" },
                unique: true,
                filter: "account_fingerprint IS NOT NULL AND batch_reference IS NOT NULL AND state IN ('SubmitIntended','Unknown','Accepted','Initiated','Validated','InProgress','Completed','Rejected')");

            migrationBuilder.CreateIndex(
                name: "ux_government_exchanges__tenant_body_operation_key",
                table: "government_exchanges",
                columns: new[] { "tenant_id", "body", "operation", "idempotency_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "government_credentials");

            migrationBuilder.DropTable(
                name: "government_exchanges");
        }
    }
}
