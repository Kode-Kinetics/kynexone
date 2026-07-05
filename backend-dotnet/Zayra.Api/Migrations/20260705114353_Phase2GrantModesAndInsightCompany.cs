using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class Phase2GrantModesAndInsightCompany : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "grant_mode",
                table: "user_entity_accesses",
                type: "text",
                nullable: false,
                defaultValue: "SelectedCompanies");

            // Least-privilege / behavior-preserving backfill for pre-existing grants:
            //   company_id set  → SelectedCompanies (that one company only — least privilege)
            //   company_id null → AllCurrentAndFutureCompanies (a null-company grant has
            //                     always meant dynamic group access; freezing it to
            //                     AllCurrent would silently change behavior)
            migrationBuilder.Sql(
                "UPDATE user_entity_accesses SET grant_mode = 'AllCurrentAndFutureCompanies' WHERE company_id IS NULL;");
            migrationBuilder.Sql(
                "UPDATE user_entity_accesses SET grant_mode = 'SelectedCompanies' WHERE company_id IS NOT NULL;");

            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "ai_insights",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ai_insights_tenant_id_company_id",
                table: "ai_insights",
                columns: new[] { "tenant_id", "company_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ai_insights_tenant_id_company_id",
                table: "ai_insights");

            migrationBuilder.DropColumn(
                name: "grant_mode",
                table: "user_entity_accesses");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "ai_insights");
        }
    }
}
