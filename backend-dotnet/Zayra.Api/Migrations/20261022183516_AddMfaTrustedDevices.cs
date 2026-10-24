using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMfaTrustedDevices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ADD COLUMN / ADD CONSTRAINT on security_settings take an ACCESS EXCLUSIVE lock, and a DDL waiting on a lock
            // blocks every later sign-in that reads the tenant's settings. Fail fast instead (55P03); re-run when quiet.
            // Transaction-scoped: EF runs each migration in one transaction. Expand-only: a new table, a defaulted column
            // and a CHECK every existing row (default 30) satisfies.
            migrationBuilder.Sql(LockTimeoutSql);

            migrationBuilder.AddColumn<int>(
                name: "mfa_trusted_device_days",
                table: "security_settings",
                type: "integer",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.CreateTable(
                name: "mfa_trusted_devices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_used_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    user_agent = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ip_address = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    revoked_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    revoked_reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mfa_trusted_devices", x => x.id);
                    table.CheckConstraint("ck_mfa_trusted_devices__lifetime", "expires_at_utc > created_at_utc");
                    table.CheckConstraint("ck_mfa_trusted_devices__revoked_pair", "(revoked_at_utc IS NULL) = (revoked_reason IS NULL)");
                    table.CheckConstraint("ck_mfa_trusted_devices__revoked_reason", "revoked_reason IS NULL OR revoked_reason IN ('user_revoked', 'user_revoked_all', 'password_changed', 'password_reset', 'mfa_disabled', 'mfa_recovery_disabled', 'mfa_resetup', 'user_deactivated', 'user_deleted', 'policy_shortened')");
                    table.ForeignKey(
                        name: "FK_mfa_trusted_devices_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_mfa_trusted_devices_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_security_settings__mfa_trusted_device_days",
                table: "security_settings",
                sql: "mfa_trusted_device_days BETWEEN 0 AND 90");

            migrationBuilder.CreateIndex(
                name: "ix_mfa_trusted_devices__tenant_user_active",
                table: "mfa_trusted_devices",
                columns: new[] { "tenant_id", "user_id" },
                filter: "revoked_at_utc IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_mfa_trusted_devices_user_id",
                table: "mfa_trusted_devices",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_mfa_trusted_devices__token_hash",
                table: "mfa_trusted_devices",
                column: "token_hash",
                unique: true);
        }

        /// <summary>Transaction-scoped lock wait ceiling for this migration's DDL.</summary>
        public const string LockTimeoutSql = "SET LOCAL lock_timeout = '5s';";

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mfa_trusted_devices");

            migrationBuilder.DropCheckConstraint(
                name: "ck_security_settings__mfa_trusted_device_days",
                table: "security_settings");

            migrationBuilder.DropColumn(
                name: "mfa_trusted_device_days",
                table: "security_settings");
        }
    }
}
