using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class SetupRowsTurnOffAndRestore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "pre_removal_status",
                table: "payslip_templates",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_active",
                table: "gcc_compliance_settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "turned_off_at_utc",
                table: "gcc_compliance_settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "turned_off_by",
                table: "gcc_compliance_settings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pre_removal_status",
                table: "company_compliance_profiles",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "removal_set_effective_to",
                table: "company_compliance_profiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "pre_removal_status",
                table: "payslip_templates");

            migrationBuilder.DropColumn(
                name: "is_active",
                table: "gcc_compliance_settings");

            migrationBuilder.DropColumn(
                name: "turned_off_at_utc",
                table: "gcc_compliance_settings");

            migrationBuilder.DropColumn(
                name: "turned_off_by",
                table: "gcc_compliance_settings");

            migrationBuilder.DropColumn(
                name: "pre_removal_status",
                table: "company_compliance_profiles");

            migrationBuilder.DropColumn(
                name: "removal_set_effective_to",
                table: "company_compliance_profiles");
        }
    }
}
