using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    public partial class AddSalaryStructureEligibilityAndVersioning : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "eligible_designation_ids_json",
                table: "salary_structures",
                type: "json",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "eligible_grade_ids_json",
                table: "salary_structures",
                type: "json",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<decimal>(
                name: "max_basic_salary",
                table: "salary_structures",
                type: "numeric(14,2)",
                precision: 14,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "max_gross_salary",
                table: "salary_structures",
                type: "numeric(14,2)",
                precision: 14,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "min_basic_salary",
                table: "salary_structures",
                type: "numeric(14,2)",
                precision: 14,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "min_gross_salary",
                table: "salary_structures",
                type: "numeric(14,2)",
                precision: 14,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<Guid>(
                name: "previous_version_id",
                table: "salary_structures",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "version_number",
                table: "salary_structures",
                type: "integer",
                nullable: false,
                defaultValue: 1);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "eligible_designation_ids_json", table: "salary_structures");
            migrationBuilder.DropColumn(name: "eligible_grade_ids_json", table: "salary_structures");
            migrationBuilder.DropColumn(name: "max_basic_salary", table: "salary_structures");
            migrationBuilder.DropColumn(name: "max_gross_salary", table: "salary_structures");
            migrationBuilder.DropColumn(name: "min_basic_salary", table: "salary_structures");
            migrationBuilder.DropColumn(name: "min_gross_salary", table: "salary_structures");
            migrationBuilder.DropColumn(name: "previous_version_id", table: "salary_structures");
            migrationBuilder.DropColumn(name: "version_number", table: "salary_structures");
        }
    }
}
