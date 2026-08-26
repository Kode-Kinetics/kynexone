using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class PersistKeyRingAndEmployeePublicIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "public_id",
                table: "employees",
                type: "uuid",
                nullable: true);

            // Backfill every existing employee with a distinct, non-guessable public identity.
            // A Guid.Empty default would collide as soon as a tenant had two employees.
            migrationBuilder.Sql("""
                UPDATE employees
                SET public_id = gen_random_uuid()
                WHERE public_id IS NULL;
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "public_id",
                table: "employees",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            // Reconcile only finance rows that carry the proven integer bridge. Never infer an
            // employee from a name or overwrite unresolved GUID-only compliance/onboarding data.
            migrationBuilder.Sql("""
                UPDATE employee_loans AS item
                SET employee_id = employee.public_id
                FROM employees AS employee
                WHERE item.tenant_id = employee.tenant_id
                  AND item.employee_int_id = employee.id
                  AND item.employee_id IS DISTINCT FROM employee.public_id;

                UPDATE salary_advances AS item
                SET employee_id = employee.public_id
                FROM employees AS employee
                WHERE item.tenant_id = employee.tenant_id
                  AND item.employee_int_id = employee.id
                  AND item.employee_id IS DISTINCT FROM employee.public_id;

                UPDATE employee_bonuses AS item
                SET employee_id = employee.public_id
                FROM employees AS employee
                WHERE item.tenant_id = employee.tenant_id
                  AND item.employee_int_id = employee.id
                  AND item.employee_id IS DISTINCT FROM employee.public_id;
                """);

            migrationBuilder.CreateTable(
                name: "DataProtectionKeys",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    friendly_name = table.Column<string>(type: "text", nullable: true),
                    xml = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataProtectionKeys", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_salary_advances_tenant_id_employee_int_id_status",
                table: "salary_advances",
                columns: new[] { "tenant_id", "employee_int_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_employees_tenant_public_id",
                table: "employees",
                columns: new[] { "tenant_id", "public_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_employee_loans_tenant_id_employee_int_id_status",
                table: "employee_loans",
                columns: new[] { "tenant_id", "employee_int_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_bonuses_tenant_id_employee_int_id_status",
                table: "employee_bonuses",
                columns: new[] { "tenant_id", "employee_int_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DataProtectionKeys");

            migrationBuilder.DropIndex(
                name: "IX_salary_advances_tenant_id_employee_int_id_status",
                table: "salary_advances");

            migrationBuilder.DropIndex(
                name: "ux_employees_tenant_public_id",
                table: "employees");

            migrationBuilder.DropIndex(
                name: "IX_employee_loans_tenant_id_employee_int_id_status",
                table: "employee_loans");

            migrationBuilder.DropIndex(
                name: "IX_employee_bonuses_tenant_id_employee_int_id_status",
                table: "employee_bonuses");

            migrationBuilder.DropColumn(
                name: "public_id",
                table: "employees");
        }
    }
}
