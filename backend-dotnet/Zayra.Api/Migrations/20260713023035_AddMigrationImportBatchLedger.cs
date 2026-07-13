using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMigrationImportBatchLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "privacy_status",
                table: "employees",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "redacted_at_utc",
                table: "employees",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "retention_until_utc",
                table: "employees",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "status",
                table: "employee_change_requests",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "sensitive_fields",
                table: "employee_change_requests",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<string>(
                name: "rejection_reason",
                table: "employee_change_requests",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "entry_hash",
                table: "audit_logs",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "hash_algorithm",
                table: "audit_logs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "previous_hash",
                table: "audit_logs",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "migration_import_batches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_batch_id = table.Column<string>(type: "text", nullable: true),
                    package_checksum = table.Column<string>(type: "text", nullable: false),
                    package_type = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false, defaultValue: "OrganizationStructure"),
                    status = table.Column<string>(type: "text", nullable: false),
                    dry_run = table.Column<bool>(type: "boolean", nullable: false),
                    current_section = table.Column<string>(type: "text", nullable: false),
                    payload_json = table.Column<string>(type: "json", nullable: false),
                    received_rows = table.Column<int>(type: "integer", nullable: false),
                    created_rows = table.Column<int>(type: "integer", nullable: false),
                    updated_rows = table.Column<int>(type: "integer", nullable: false),
                    skipped_rows = table.Column<int>(type: "integer", nullable: false),
                    error_rows = table.Column<int>(type: "integer", nullable: false),
                    reconciliation_json = table.Column<string>(type: "json", nullable: false),
                    error_json = table.Column<string>(type: "json", nullable: false),
                    result_json = table.Column<string>(type: "json", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    started_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_migration_import_batches", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_tenant_id_entry_hash",
                table: "audit_logs",
                columns: new[] { "tenant_id", "entry_hash" });

            migrationBuilder.CreateIndex(
                name: "IX_migration_import_batches_tenant_id_external_batch_id",
                table: "migration_import_batches",
                columns: new[] { "tenant_id", "external_batch_id" },
                unique: true,
                filter: "external_batch_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_migration_import_batches_tenant_id_package_checksum",
                table: "migration_import_batches",
                columns: new[] { "tenant_id", "package_checksum" });

            migrationBuilder.CreateIndex(
                name: "IX_migration_import_batches_tenant_id_status_created_at_utc",
                table: "migration_import_batches",
                columns: new[] { "tenant_id", "status", "created_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "migration_import_batches");

            migrationBuilder.DropIndex(
                name: "IX_audit_logs_tenant_id_entry_hash",
                table: "audit_logs");

            migrationBuilder.DropColumn(
                name: "privacy_status",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "redacted_at_utc",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "retention_until_utc",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "rejection_reason",
                table: "employee_change_requests");

            migrationBuilder.DropColumn(
                name: "entry_hash",
                table: "audit_logs");

            migrationBuilder.DropColumn(
                name: "hash_algorithm",
                table: "audit_logs");

            migrationBuilder.DropColumn(
                name: "previous_hash",
                table: "audit_logs");

            migrationBuilder.AlterColumn<string>(
                name: "status",
                table: "employee_change_requests",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(80)",
                oldMaxLength: 80);

            migrationBuilder.AlterColumn<string>(
                name: "sensitive_fields",
                table: "employee_change_requests",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(1000)",
                oldMaxLength: 1000);
        }
    }
}
