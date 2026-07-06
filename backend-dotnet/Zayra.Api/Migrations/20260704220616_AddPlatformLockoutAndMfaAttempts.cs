using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatformLockoutAndMfaAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "failed_login_count",
                table: "platform_users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "lockout_end_utc",
                table: "platform_users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "failed_attempts",
                table: "mfa_challenge_tokens",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "failed_login_count",
                table: "platform_users");

            migrationBuilder.DropColumn(
                name: "lockout_end_utc",
                table: "platform_users");

            migrationBuilder.DropColumn(
                name: "failed_attempts",
                table: "mfa_challenge_tokens");
        }
    }
}
