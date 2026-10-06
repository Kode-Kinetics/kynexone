using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class RequirePrivilegedMfa : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "privileged_mfa_enforce_from_utc",
                table: "security_settings",
                type: "timestamp with time zone",
                nullable: true);

            // Mandatory MFA for privileged roles starts 14 days after THIS migration runs, i.e. after
            // the deploy that ships it — never on deploy day. PrivilegedMfaPolicy reads this row; a
            // tenant's own security_settings.privileged_mfa_enforce_from_utc overrides it. Idempotent:
            // a re-run (or a date already moved by an Owner) is left untouched.
            migrationBuilder.Sql(@"
INSERT INTO platform_config_entries (id, key, value, updated_at_utc, updated_by_platform_user_id)
VALUES (
    gen_random_uuid(),
    'auth.privileged_mfa_enforce_from_utc',
    to_char((now() AT TIME ZONE 'UTC') + interval '14 days', 'YYYY-MM-DD""T""HH24:MI:SS""Z""'),
    now(),
    NULL)
ON CONFLICT (key) DO NOTHING;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM platform_config_entries WHERE key = 'auth.privileged_mfa_enforce_from_utc';");

            migrationBuilder.DropColumn(
                name: "privileged_mfa_enforce_from_utc",
                table: "security_settings");
        }
    }
}
