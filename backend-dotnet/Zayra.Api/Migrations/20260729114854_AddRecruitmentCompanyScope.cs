using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddRecruitmentCompanyScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "offer_letters",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "job_applications",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "candidates",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_offer_letters_tenant_id_company_id",
                table: "offer_letters",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_job_applications_tenant_id_company_id",
                table: "job_applications",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_candidates_tenant_id_company_id",
                table: "candidates",
                columns: new[] { "tenant_id", "company_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_offer_letters_tenant_id_company_id",
                table: "offer_letters");

            migrationBuilder.DropIndex(
                name: "IX_job_applications_tenant_id_company_id",
                table: "job_applications");

            migrationBuilder.DropIndex(
                name: "IX_candidates_tenant_id_company_id",
                table: "candidates");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "offer_letters");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "job_applications");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "candidates");
        }
    }
}
