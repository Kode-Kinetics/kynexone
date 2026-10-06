using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatformMfaRecoveryCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "mfa_recovery_code_hashes",
                table: "platform_users",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            // TOTP replay protection: the last accepted time-step per principal.
            migrationBuilder.AddColumn<long>(
                name: "mfa_last_totp_step",
                table: "platform_users",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "mfa_last_totp_step",
                table: "users",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "mfa_last_totp_step",
                table: "users");

            migrationBuilder.DropColumn(
                name: "mfa_last_totp_step",
                table: "platform_users");

            migrationBuilder.DropColumn(
                name: "mfa_recovery_code_hashes",
                table: "platform_users");
        }
    }
}
