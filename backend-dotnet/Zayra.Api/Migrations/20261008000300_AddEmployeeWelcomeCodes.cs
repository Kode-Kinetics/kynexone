using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddEmployeeWelcomeCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "welcome_code_expires_at_utc",
                table: "employee_user_accounts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "welcome_code_failed_attempts",
                table: "employee_user_accounts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "welcome_code_hash",
                table: "employee_user_accounts",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "welcome_code_issued_at_utc",
                table: "employee_user_accounts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "welcome_code_issued_by",
                table: "employee_user_accounts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "welcome_code_redeemed_at_utc",
                table: "employee_user_accounts",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "welcome_code_expires_at_utc",
                table: "employee_user_accounts");

            migrationBuilder.DropColumn(
                name: "welcome_code_failed_attempts",
                table: "employee_user_accounts");

            migrationBuilder.DropColumn(
                name: "welcome_code_hash",
                table: "employee_user_accounts");

            migrationBuilder.DropColumn(
                name: "welcome_code_issued_at_utc",
                table: "employee_user_accounts");

            migrationBuilder.DropColumn(
                name: "welcome_code_issued_by",
                table: "employee_user_accounts");

            migrationBuilder.DropColumn(
                name: "welcome_code_redeemed_at_utc",
                table: "employee_user_accounts");
        }
    }
}
