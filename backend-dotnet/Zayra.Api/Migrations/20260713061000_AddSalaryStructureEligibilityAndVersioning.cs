using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Zayra.Api.Data;

#nullable disable

namespace Zayra.Api.Migrations
{
    // These two attributes are what make a migration EXIST as far as EF is concerned. Without them
    // MigrationsAssembly does not discover the class, so `dotnet ef database update` exits 0 having
    // silently skipped it and /health/ready does not count it as pending. This file was written by
    // hand in 15148d0 (2026-07-13) without them and was invisible to every tool for 70 days.
    // scripts/check-migration-visibility.sh is the CI gate that now makes that impossible to repeat.
    //
    // ALREADY APPLIED IN PRODUCTION: __EFMigrationsHistory carries this id (verified 2026-09-21), so
    // restoring visibility does NOT re-run it there. That matters because, unlike its two siblings,
    // this migration uses AddColumn — plain `ALTER TABLE … ADD COLUMN` with no IF NOT EXISTS — which
    // would abort with 42701 on a second run. On a FRESH database it runs before
    // 20260816013100_RepairMigrationModelParity, whose adds are all IF NOT EXISTS and therefore
    // no-op afterwards.
    [Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof(ZayraDbContext))]
    [Migration("20260713061000_AddSalaryStructureEligibilityAndVersioning")]
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
