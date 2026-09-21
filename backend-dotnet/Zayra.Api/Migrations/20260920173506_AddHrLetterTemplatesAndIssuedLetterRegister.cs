using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddHrLetterTemplatesAndIssuedLetterRegister : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "addressee_name",
                table: "employee_document_requests",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "decided_at_utc",
                table: "employee_document_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "decided_by_user_id",
                table: "employee_document_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "decision_note",
                table: "employee_document_requests",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "hr_request_id",
                table: "employee_document_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "issued_letter_id",
                table: "employee_document_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "language",
                table: "employee_document_requests",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "letter_type",
                table: "employee_document_requests",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "hr_letter_templates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    letter_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name_en = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    name_ar = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    language = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    title_en = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    title_ar = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    body_en = table.Column<string>(type: "text", nullable: false),
                    body_ar = table.Column<string>(type: "text", nullable: false),
                    closing_en = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    closing_ar = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    is_system_default = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hr_letter_templates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "issued_letters",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    employee_code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    employee_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    letter_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    reference_number = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    sequence_number = table.Column<int>(type: "integer", nullable: false),
                    sequence_year = table.Column<int>(type: "integer", nullable: false),
                    template_id = table.Column<Guid>(type: "uuid", nullable: true),
                    language = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    purpose = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    addressee_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    issued_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    issued_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    issued_by_title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    issued_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    file_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    file_size_bytes = table.Column<int>(type: "integer", nullable: false),
                    merged_values_json = table.Column<string>(type: "jsonb", nullable: false),
                    rendered_content_json = table.Column<string>(type: "jsonb", nullable: false),
                    document_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    hr_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_issued_letters", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_employee_document_requests_tenant_status_created",
                table: "employee_document_requests",
                columns: new[] { "tenant_id", "status", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_hr_letter_templates_tenant_id_company_id",
                table: "hr_letter_templates",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "ux_hr_letter_templates_scope_type",
                table: "hr_letter_templates",
                columns: new[] { "tenant_id", "company_id", "letter_type" },
                unique: true,
                filter: "is_deleted = false");

            migrationBuilder.CreateIndex(
                name: "ix_issued_letters_tenant_employee_issued",
                table: "issued_letters",
                columns: new[] { "tenant_id", "employee_id", "issued_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_issued_letters_tenant_id_company_id",
                table: "issued_letters",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "ux_issued_letters_series_ordinal",
                table: "issued_letters",
                columns: new[] { "tenant_id", "letter_type", "sequence_year", "sequence_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_issued_letters_tenant_reference",
                table: "issued_letters",
                columns: new[] { "tenant_id", "reference_number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "hr_letter_templates");

            migrationBuilder.DropTable(
                name: "issued_letters");

            migrationBuilder.DropIndex(
                name: "ix_employee_document_requests_tenant_status_created",
                table: "employee_document_requests");

            migrationBuilder.DropColumn(
                name: "addressee_name",
                table: "employee_document_requests");

            migrationBuilder.DropColumn(
                name: "decided_at_utc",
                table: "employee_document_requests");

            migrationBuilder.DropColumn(
                name: "decided_by_user_id",
                table: "employee_document_requests");

            migrationBuilder.DropColumn(
                name: "decision_note",
                table: "employee_document_requests");

            migrationBuilder.DropColumn(
                name: "hr_request_id",
                table: "employee_document_requests");

            migrationBuilder.DropColumn(
                name: "issued_letter_id",
                table: "employee_document_requests");

            migrationBuilder.DropColumn(
                name: "language",
                table: "employee_document_requests");

            migrationBuilder.DropColumn(
                name: "letter_type",
                table: "employee_document_requests");
        }
    }
}
