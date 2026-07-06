using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddGradePayScale : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "currency",
                table: "grades",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "max_salary",
                table: "grades",
                type: "numeric(14,2)",
                precision: 14,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "mid_salary",
                table: "grades",
                type: "numeric(14,2)",
                precision: 14,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "min_salary",
                table: "grades",
                type: "numeric(14,2)",
                precision: 14,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "grade_pay_scale_components",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    grade_id = table.Column<Guid>(type: "uuid", nullable: false),
                    component_code = table.Column<string>(type: "text", nullable: false),
                    component_name = table.Column<string>(type: "text", nullable: false),
                    component_type = table.Column<string>(type: "text", nullable: false),
                    calculation_type = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    percentage = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                    is_taxable = table.Column<bool>(type: "boolean", nullable: false),
                    frequency = table.Column<string>(type: "text", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_grade_pay_scale_components", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_grade_pay_scale_components_tenant_id_grade_id",
                table: "grade_pay_scale_components",
                columns: new[] { "tenant_id", "grade_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "grade_pay_scale_components");

            migrationBuilder.DropColumn(
                name: "currency",
                table: "grades");

            migrationBuilder.DropColumn(
                name: "max_salary",
                table: "grades");

            migrationBuilder.DropColumn(
                name: "mid_salary",
                table: "grades");

            migrationBuilder.DropColumn(
                name: "min_salary",
                table: "grades");
        }
    }
}
