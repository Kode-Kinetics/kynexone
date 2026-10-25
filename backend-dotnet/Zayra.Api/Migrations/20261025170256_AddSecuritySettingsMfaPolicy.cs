using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSecuritySettingsMfaPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "mfa_policy",
                table: "security_settings",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "PrivilegedRoles");

            // Existing rows: a company that required two-step for everyone stays on Everyone; every other row
            // takes the column default (PrivilegedRoles), which is today's behaviour. Nobody lands on Optional.
            migrationBuilder.Sql("UPDATE security_settings SET mfa_policy = CASE WHEN mfa_required THEN 'Everyone' ELSE 'PrivilegedRoles' END;");

            migrationBuilder.AddCheckConstraint(
                name: "ck_security_settings__mfa_policy",
                table: "security_settings",
                sql: "mfa_policy IN ('Optional','PrivilegedRoles','Everyone')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_security_settings__mfa_policy_required",
                table: "security_settings",
                sql: "NOT (mfa_required AND mfa_policy = 'Optional')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_security_settings__mfa_policy",
                table: "security_settings");

            migrationBuilder.DropCheckConstraint(
                name: "ck_security_settings__mfa_policy_required",
                table: "security_settings");

            migrationBuilder.DropColumn(
                name: "mfa_policy",
                table: "security_settings");
        }
    }
}
