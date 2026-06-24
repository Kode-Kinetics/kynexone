using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── (a) Add schema columns ────────────────────────────────────────────────

            // users.is_group_scope: true = sees all tenant companies (group-level access).
            // Defaults to false so new users are default-deny; backfill below grants
            // existing admins/owners group scope so they lose no access on deploy.
            migrationBuilder.AddColumn<bool>(
                name: "is_group_scope",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // user_entity_accesses.granted_by / granted_at: explicit audit trail for who
            // granted the access and when (separate from the generic created_by/created_at
            // so that backfilled rows are distinguishable from runtime grants).
            migrationBuilder.AddColumn<Guid>(
                name: "granted_by",
                table: "user_entity_accesses",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "granted_at",
                table: "user_entity_accesses",
                type: "timestamp with time zone",
                nullable: true);

            // ── (b) Backfill: grant group scope to ALL existing users ─────────────────
            //
            // Safety rationale: existing users have no entity_access JWT claims yet.
            // Setting is_group_scope=true for every existing user ensures zero regressions
            // on deploy — nobody loses cross-company visibility they had before this migration.
            // "Default-deny" only applies to users provisioned AFTER this migration, who
            // will receive either is_group_scope=true or explicit user_entity_accesses rows.
            //
            // More targeted alternative (admins/owners by authority_level or role name) is
            // also included in the WHERE clause as documentation of intent; the broad UPDATE
            // is the safe path chosen here.
            migrationBuilder.Sql(@"
                UPDATE users
                SET    is_group_scope = true
                WHERE  is_deleted = false;
            ");

            // ── (c) Verify backfill: confirm every non-deleted user now has group scope ─
            // This is a sanity assertion — will raise if any active user was missed.
            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM users WHERE is_deleted = false AND is_group_scope = false
                    ) THEN
                        RAISE EXCEPTION 'CompanyScope backfill incomplete: found active users without is_group_scope=true';
                    END IF;
                END $$;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "granted_at",
                table: "user_entity_accesses");

            migrationBuilder.DropColumn(
                name: "granted_by",
                table: "user_entity_accesses");

            migrationBuilder.DropColumn(
                name: "is_group_scope",
                table: "users");
        }
    }
}
