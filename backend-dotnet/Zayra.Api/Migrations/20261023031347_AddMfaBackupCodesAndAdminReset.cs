using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMfaBackupCodesAndAdminReset : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ADD COLUMN / ADD CONSTRAINT on users take an ACCESS EXCLUSIVE lock, and a DDL queued behind a long reader
            // blocks every sign-in. Fail fast instead (55P03) and re-run when quiet. Expand-only: a nullable column every
            // existing row leaves NULL, a CHECK every existing row satisfies, and a widened reason vocabulary.
            migrationBuilder.Sql(LockTimeoutSql);

            migrationBuilder.DropCheckConstraint(
                name: "ck_mfa_trusted_devices__revoked_reason",
                table: "mfa_trusted_devices");

            migrationBuilder.AddColumn<string>(
                name: "mfa_recovery_code_hashes",
                table: "users",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_users__mfa_recovery_code_hashes_need_factor",
                table: "users",
                sql: "mfa_recovery_code_hashes IS NULL OR mfa_secret_encrypted IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_mfa_trusted_devices__revoked_reason",
                table: "mfa_trusted_devices",
                sql: "revoked_reason IS NULL OR revoked_reason IN ('user_revoked', 'user_revoked_all', 'password_changed', 'password_reset', 'mfa_disabled', 'mfa_recovery_disabled', 'mfa_resetup', 'user_deactivated', 'user_deleted', 'policy_shortened', 'mfa_admin_reset')");
        }

        /// <summary>Transaction-scoped lock wait ceiling for this migration's DDL (declared here so the destructive-migration gate can read it).</summary>
        public const string LockTimeoutSql = "SET LOCAL lock_timeout = '5s';";

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(LockTimeoutSql);

            // The earlier vocabulary has no admin reset; its nearest reason is the privileged recovery disable. Without
            // this a rollback after any reset fails on the restored CHECK. Backup codes are dropped with the column
            // (the earlier release cannot use them; affected users simply have none and keep their app factor).
            migrationBuilder.Sql(
                "UPDATE mfa_trusted_devices SET revoked_reason = 'mfa_recovery_disabled' WHERE revoked_reason = 'mfa_admin_reset';");

            migrationBuilder.DropCheckConstraint(
                name: "ck_users__mfa_recovery_code_hashes_need_factor",
                table: "users");

            migrationBuilder.DropCheckConstraint(
                name: "ck_mfa_trusted_devices__revoked_reason",
                table: "mfa_trusted_devices");

            migrationBuilder.DropColumn(
                name: "mfa_recovery_code_hashes",
                table: "users");

            migrationBuilder.AddCheckConstraint(
                name: "ck_mfa_trusted_devices__revoked_reason",
                table: "mfa_trusted_devices",
                sql: "revoked_reason IS NULL OR revoked_reason IN ('user_revoked', 'user_revoked_all', 'password_changed', 'password_reset', 'mfa_disabled', 'mfa_recovery_disabled', 'mfa_resetup', 'user_deactivated', 'user_deleted', 'policy_shortened')");
        }
    }
}
