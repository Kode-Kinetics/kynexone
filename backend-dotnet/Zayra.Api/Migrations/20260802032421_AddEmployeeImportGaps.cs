using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddEmployeeImportGaps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "employee_import_gaps",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    import_batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    row_number = table.Column<int>(type: "integer", nullable: false),
                    gap_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    gap_category = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    raw_value = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    resolved_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employee_import_gaps", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_employees_tenant_work_email",
                table: "employees",
                columns: new[] { "tenant_id", "work_email" },
                filter: "work_email <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_employee_import_gaps_tenant_id_company_id",
                table: "employee_import_gaps",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_import_gaps_tenant_id_employee_id",
                table: "employee_import_gaps",
                columns: new[] { "tenant_id", "employee_id" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_import_gaps_tenant_id_gap_type_resolved_at_utc",
                table: "employee_import_gaps",
                columns: new[] { "tenant_id", "gap_type", "resolved_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_import_gaps_tenant_id_import_batch_id",
                table: "employee_import_gaps",
                columns: new[] { "tenant_id", "import_batch_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "employee_import_gaps");

            migrationBuilder.DropIndex(
                name: "ix_employees_tenant_work_email",
                table: "employees");
        }
    }
}
