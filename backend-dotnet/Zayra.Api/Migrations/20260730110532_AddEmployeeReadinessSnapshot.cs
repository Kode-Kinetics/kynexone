using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddEmployeeReadinessSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "activation_blockers_count",
                table: "employees",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "readiness_evaluated_at_utc",
                table: "employees",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "readiness_state",
                table: "employees",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Blocked");

            migrationBuilder.CreateIndex(
                name: "IX_employees_tenant_id_readiness_state",
                table: "employees",
                columns: new[] { "tenant_id", "readiness_state" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_employees_tenant_id_readiness_state",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "activation_blockers_count",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "readiness_evaluated_at_utc",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "readiness_state",
                table: "employees");
        }
    }
}
