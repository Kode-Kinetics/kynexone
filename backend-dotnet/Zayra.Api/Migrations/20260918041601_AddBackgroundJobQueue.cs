using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddBackgroundJobQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "background_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    key_retention = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    progress_total = table.Column<int>(type: "integer", nullable: true),
                    progress_completed = table.Column<int>(type: "integer", nullable: false),
                    progress_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    max_attempts = table.Column<int>(type: "integer", nullable: false),
                    run_after_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    lease_owner = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    lease_token = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    heartbeat_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    cancel_requested_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    result_json = table.Column<string>(type: "jsonb", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    started_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_background_jobs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "background_job_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    result_json = table.Column<string>(type: "jsonb", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_background_job_items", x => x.id);
                    table.ForeignKey(
                        name: "FK_background_job_items_background_jobs_job_id",
                        column: x => x.job_id,
                        principalTable: "background_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_background_job_items_tenant_job",
                table: "background_job_items",
                columns: new[] { "tenant_id", "job_id" });

            migrationBuilder.CreateIndex(
                name: "ux_background_job_items_job_item",
                table: "background_job_items",
                columns: new[] { "job_id", "item_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_background_jobs_status_lease",
                table: "background_jobs",
                columns: new[] { "status", "lease_expires_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_background_jobs_status_run_after",
                table: "background_jobs",
                columns: new[] { "status", "run_after_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_background_jobs_tenant_created",
                table: "background_jobs",
                columns: new[] { "tenant_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_background_jobs_active_key",
                table: "background_jobs",
                columns: new[] { "tenant_id", "job_type", "idempotency_key" },
                unique: true,
                filter: "status IN ('Queued','Running')");

            migrationBuilder.CreateIndex(
                name: "ux_background_jobs_retained_key",
                table: "background_jobs",
                columns: new[] { "tenant_id", "job_type", "idempotency_key" },
                unique: true,
                filter: "key_retention = 'Forever' AND status IN ('Queued','Running','Succeeded')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "background_job_items");

            migrationBuilder.DropTable(
                name: "background_jobs");
        }
    }
}
