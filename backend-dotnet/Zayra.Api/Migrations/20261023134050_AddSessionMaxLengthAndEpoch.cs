using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// The hard session limit (security_settings.session_max_hours, refresh_tokens.session_started_at_utc) and the
    /// session epoch (users.session_epoch, refresh_tokens.session_epoch). Expand-only, and nobody is signed out by the
    /// deploy: every tenant gets the 12-hour default, every existing refresh token's session starts at its own
    /// created_at_utc (the last rotation, so an active session keeps at least 12 more hours), and every epoch starts at
    /// 0 on both sides. The column defaults also keep an earlier release writing refresh tokens correctly during a
    /// rollback of the code alone.
    /// </summary>
    public partial class AddSessionMaxLengthAndEpoch : Migration
    {
        /// <summary>Transaction-scoped lock wait ceiling for this migration's DDL (declared here so the destructive-migration gate can read it).</summary>
        public const string LockTimeoutSql = "SET LOCAL lock_timeout = '5s';";

        /// <summary>
        /// Writing the epoch at all means "raise it": the stored value becomes exactly one above the row it replaces,
        /// whatever value was written. The application raises it in the save hook from the value it read; two
        /// concurrent raises from the same read would otherwise both write N+1 (the second one then changing nothing),
        /// and a session issued between them at N+1 would survive the second credential change. A BEFORE UPDATE row
        /// trigger sees the row as it stands after any concurrent writer committed, so each raise lands one above that.
        /// It fires only when the UPDATE names session_epoch; EF names a column only when it changed.
        /// </summary>
        public const string EpochTriggerSql = """
            CREATE OR REPLACE FUNCTION fn_users__session_epoch_increments() RETURNS trigger
            LANGUAGE plpgsql AS $$
            BEGIN
                NEW.session_epoch := OLD.session_epoch + 1;
                RETURN NEW;
            END;
            $$;
            CREATE TRIGGER trg_users__session_epoch_increments
                BEFORE UPDATE OF session_epoch ON users
                FOR EACH ROW EXECUTE FUNCTION fn_users__session_epoch_increments();
            """;

        public const string DropEpochTriggerSql = """
            DROP TRIGGER IF EXISTS trg_users__session_epoch_increments ON users;
            DROP FUNCTION IF EXISTS fn_users__session_epoch_increments();
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ADD COLUMN / ADD CONSTRAINT on users and refresh_tokens take an ACCESS EXCLUSIVE lock, and DDL queued
            // behind a long reader blocks every sign-in. Fail fast instead (55P03) and re-run when quiet.
            migrationBuilder.Sql(LockTimeoutSql);

            migrationBuilder.AddColumn<long>(
                name: "session_epoch",
                table: "users",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "session_max_hours",
                table: "security_settings",
                type: "integer",
                nullable: false,
                defaultValue: 12);

            migrationBuilder.AddColumn<long>(
                name: "session_epoch",
                table: "refresh_tokens",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTime>(
                name: "session_started_at_utc",
                table: "refresh_tokens",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            // Backfill: an existing session is measured from its latest rotation, so the deploy itself ends none.
            migrationBuilder.Sql("UPDATE refresh_tokens SET session_started_at_utc = created_at_utc;");

            migrationBuilder.AddCheckConstraint(
                name: "ck_security_settings__session_max_hours",
                table: "security_settings",
                sql: "session_max_hours BETWEEN 1 AND 168");

            migrationBuilder.Sql(EpochTriggerSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(LockTimeoutSql);

            migrationBuilder.Sql(DropEpochTriggerSql);

            migrationBuilder.DropCheckConstraint(
                name: "ck_security_settings__session_max_hours",
                table: "security_settings");

            migrationBuilder.DropColumn(
                name: "session_epoch",
                table: "users");

            migrationBuilder.DropColumn(
                name: "session_max_hours",
                table: "security_settings");

            migrationBuilder.DropColumn(
                name: "session_epoch",
                table: "refresh_tokens");

            migrationBuilder.DropColumn(
                name: "session_started_at_utc",
                table: "refresh_tokens");
        }
    }
}
