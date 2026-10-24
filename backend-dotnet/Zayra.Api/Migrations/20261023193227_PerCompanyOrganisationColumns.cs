using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// Addendum B (B1), step 1 — expand only. Departments, designations and grades become per company (owner decision
    /// 2026-10-23: companies in a group are separate legal entities). Adds <c>company_id</c> (composite FK
    /// (tenant_id, company_id) → companies, RESTRICT) to the three tables, moves code uniqueness from (tenant, code) to
    /// (tenant, company_id, code) NULLS NOT DISTINCT (two unassigned legacy rows still cannot share a code), and adds
    /// <c>cost_centers.name_ar</c>. The data backfill is the next migration, BackfillOrganisationPerCompany.
    /// Down refuses (unique violation on the restored (tenant, code) index) once a per-company copy exists — restore
    /// the pre-deploy database branch instead.
    /// </summary>
    public partial class PerCompanyOrganisationColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_grades_tenant_id_code",
                table: "grades");

            migrationBuilder.DropIndex(
                name: "IX_designations_tenant_id_code",
                table: "designations");

            migrationBuilder.DropIndex(
                name: "IX_departments_tenant_id_code",
                table: "departments");

            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "grades",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "designations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "departments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "name_ar",
                table: "cost_centers",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_grades_tenant_id_company_id",
                table: "grades",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_grades_tenant_id_company_id_code",
                table: "grades",
                columns: new[] { "tenant_id", "company_id", "code" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_designations_tenant_id_company_id",
                table: "designations",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_designations_tenant_id_company_id_code",
                table: "designations",
                columns: new[] { "tenant_id", "company_id", "code" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_departments_tenant_id_company_id",
                table: "departments",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_departments_tenant_id_company_id_code",
                table: "departments",
                columns: new[] { "tenant_id", "company_id", "code" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.AddForeignKey(
                name: "FK_departments_companies_tenant_id_company_id",
                table: "departments",
                columns: new[] { "tenant_id", "company_id" },
                principalTable: "companies",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_designations_companies_tenant_id_company_id",
                table: "designations",
                columns: new[] { "tenant_id", "company_id" },
                principalTable: "companies",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_grades_companies_tenant_id_company_id",
                table: "grades",
                columns: new[] { "tenant_id", "company_id" },
                principalTable: "companies",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_departments_companies_tenant_id_company_id",
                table: "departments");

            migrationBuilder.DropForeignKey(
                name: "FK_designations_companies_tenant_id_company_id",
                table: "designations");

            migrationBuilder.DropForeignKey(
                name: "FK_grades_companies_tenant_id_company_id",
                table: "grades");

            migrationBuilder.DropIndex(
                name: "IX_grades_tenant_id_company_id",
                table: "grades");

            migrationBuilder.DropIndex(
                name: "IX_grades_tenant_id_company_id_code",
                table: "grades");

            migrationBuilder.DropIndex(
                name: "IX_designations_tenant_id_company_id",
                table: "designations");

            migrationBuilder.DropIndex(
                name: "IX_designations_tenant_id_company_id_code",
                table: "designations");

            migrationBuilder.DropIndex(
                name: "IX_departments_tenant_id_company_id",
                table: "departments");

            migrationBuilder.DropIndex(
                name: "IX_departments_tenant_id_company_id_code",
                table: "departments");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "grades");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "designations");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "departments");

            migrationBuilder.DropColumn(
                name: "name_ar",
                table: "cost_centers");

            migrationBuilder.CreateIndex(
                name: "IX_grades_tenant_id_code",
                table: "grades",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_designations_tenant_id_code",
                table: "designations",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_departments_tenant_id_code",
                table: "departments",
                columns: new[] { "tenant_id", "code" },
                unique: true);
        }
    }
}
