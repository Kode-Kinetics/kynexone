using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddEmployeeDuplicateDetection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "duplicate_of_employee_id",
                table: "employees",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_employees_dup_civil_id",
                table: "employees",
                columns: new[] { "tenant_id", "civil_id" },
                filter: "civil_id <> ''");

            migrationBuilder.CreateIndex(
                name: "ix_employees_dup_dob",
                table: "employees",
                columns: new[] { "tenant_id", "date_of_birth" });

            migrationBuilder.CreateIndex(
                name: "ix_employees_dup_emirates_id",
                table: "employees",
                columns: new[] { "tenant_id", "emirates_id" },
                filter: "emirates_id <> ''");

            migrationBuilder.CreateIndex(
                name: "ix_employees_dup_id_number",
                table: "employees",
                columns: new[] { "tenant_id", "id_number" },
                filter: "id_number <> ''");

            migrationBuilder.CreateIndex(
                name: "ix_employees_dup_iqama",
                table: "employees",
                columns: new[] { "tenant_id", "iqama_number" },
                filter: "iqama_number <> ''");

            migrationBuilder.CreateIndex(
                name: "ix_employees_dup_passport",
                table: "employees",
                columns: new[] { "tenant_id", "passport_number" },
                filter: "passport_number <> ''");

            migrationBuilder.CreateIndex(
                name: "ix_employees_dup_qid",
                table: "employees",
                columns: new[] { "tenant_id", "qid" },
                filter: "qid <> ''");

            migrationBuilder.CreateIndex(
                name: "ix_employees_duplicate_of",
                table: "employees",
                columns: new[] { "tenant_id", "duplicate_of_employee_id" },
                filter: "duplicate_of_employee_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_employees_dup_civil_id",
                table: "employees");

            migrationBuilder.DropIndex(
                name: "ix_employees_dup_dob",
                table: "employees");

            migrationBuilder.DropIndex(
                name: "ix_employees_dup_emirates_id",
                table: "employees");

            migrationBuilder.DropIndex(
                name: "ix_employees_dup_id_number",
                table: "employees");

            migrationBuilder.DropIndex(
                name: "ix_employees_dup_iqama",
                table: "employees");

            migrationBuilder.DropIndex(
                name: "ix_employees_dup_passport",
                table: "employees");

            migrationBuilder.DropIndex(
                name: "ix_employees_dup_qid",
                table: "employees");

            migrationBuilder.DropIndex(
                name: "ix_employees_duplicate_of",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "duplicate_of_employee_id",
                table: "employees");
        }
    }
}
