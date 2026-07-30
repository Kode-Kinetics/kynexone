using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyIdToGccComplianceSetting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_gcc_compliance_settings_tenant_id_country_code",
                table: "gcc_compliance_settings");

            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "gcc_compliance_settings",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_gcc_compliance_settings_tenant_id_company_id",
                table: "gcc_compliance_settings",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_gcc_compliance_settings_tenant_id_company_id_country_code",
                table: "gcc_compliance_settings",
                columns: new[] { "tenant_id", "company_id", "country_code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_gcc_compliance_settings_tenant_id_company_id",
                table: "gcc_compliance_settings");

            migrationBuilder.DropIndex(
                name: "IX_gcc_compliance_settings_tenant_id_company_id_country_code",
                table: "gcc_compliance_settings");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "gcc_compliance_settings");

            migrationBuilder.CreateIndex(
                name: "IX_gcc_compliance_settings_tenant_id_country_code",
                table: "gcc_compliance_settings",
                columns: new[] { "tenant_id", "country_code" },
                unique: true);
        }
    }
}
