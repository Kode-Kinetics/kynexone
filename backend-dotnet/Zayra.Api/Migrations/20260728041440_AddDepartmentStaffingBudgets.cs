using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddDepartmentStaffingBudgets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing tenant_hr_configs rows must land on the documented default ("Enforced" —
            // deploy-safe because zero budget rows exist at migration time, so nothing blocks).
            // The guard additionally treats null/"" defensively as Enforced.
            migrationBuilder.AddColumn<string>(
                name: "establishment_enforcement_mode",
                table: "tenant_hr_configs",
                type: "text",
                nullable: false,
                defaultValue: "Enforced");

            migrationBuilder.CreateTable(
                name: "department_staffing_budgets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    staffing_level_id = table.Column<Guid>(type: "uuid", nullable: false),
                    budgeted_headcount = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_department_staffing_budgets", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_employees_occupancy",
                table: "employees",
                columns: new[] { "tenant_id", "department_id", "designation_id" },
                filter: "NOT is_deleted AND status IN ('Active','Offboarded','Suspended')");

            migrationBuilder.CreateIndex(
                name: "IX_department_staffing_budgets_tenant_id_department_id",
                table: "department_staffing_budgets",
                columns: new[] { "tenant_id", "department_id" });

            migrationBuilder.CreateIndex(
                name: "IX_department_staffing_budgets_tenant_id_department_id_staffin~",
                table: "department_staffing_budgets",
                columns: new[] { "tenant_id", "department_id", "staffing_level_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "department_staffing_budgets");

            migrationBuilder.DropIndex(
                name: "ix_employees_occupancy",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "establishment_enforcement_mode",
                table: "tenant_hr_configs");
        }
    }
}
