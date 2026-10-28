using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// The workspace OWNER (<c>users.is_workspace_owner</c>, boolean NOT NULL DEFAULT false): one Admin no other Admin
    /// may act against (AccessManagementService, access_owner_protected). Only the owner transfers it; KynexOne support
    /// sets it when the owner is gone.
    /// <list type="bullet">
    ///   <item><c>ux_users__one_workspace_owner</c> (UNIQUE <c>tenant_id</c> WHERE is_workspace_owner): at most one owner
    ///     per tenant, enforced by the database. On the user's own row, so the owner is always of the same tenant —
    ///     no cross-table key to keep honest.</item>
    ///   <item>Backfill: each tenant's earliest-created active Admin (ties by id). A tenant with no active Admin gets
    ///     no owner — nobody protected — until support sets one.</item>
    /// </list>
    /// Expand-only and N-1 safe: a constant default is metadata-only on PostgreSQL 11+, and the previous release never
    /// reads the column. Down drops the designation and the protection with it; Up re-derives it.
    /// </summary>
    public partial class AddWorkspaceOwner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_workspace_owner",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "ux_users__one_workspace_owner",
                table: "users",
                column: "tenant_id",
                unique: true,
                filter: "is_workspace_owner");

            migrationBuilder.Sql(Backfill);
            migrationBuilder.Sql(Comments);
        }

        /// <summary>Each tenant without an owner: its earliest-created active Admin.</summary>
        public const string Backfill = """
            UPDATE users SET is_workspace_owner = true
            WHERE id IN (
                SELECT DISTINCT ON (u.tenant_id) u.id
                FROM users u
                JOIN user_roles ur ON ur.user_id = u.id
                JOIN roles r ON r.id = ur.role_id
                WHERE NOT u.is_deleted AND u.is_active AND u.status = 'Active'
                  AND r.normalized_name = 'ADMIN' AND r.is_active AND NOT r.is_deleted
                  AND NOT EXISTS (SELECT 1 FROM users o WHERE o.tenant_id = u.tenant_id AND o.is_workspace_owner)
                ORDER BY u.tenant_id, u.created_at_utc, u.id);
            """;

        /// <summary>Column and index comments (docs/schema: every column says what it means, every index its query).</summary>
        public const string Comments = """
            COMMENT ON COLUMN users.is_workspace_owner IS
                'This user is the workspace owner: one Admin per tenant that no other Admin may suspend, lock, delete, demote, set to no-login, or reset the password or two-step sign-in of. Only the owner transfers it (access.ownership_transferred); KynexOne support sets it when the owner is gone (platform.workspace_owner_set). No row true = no owner designated, nobody protected.';
            COMMENT ON INDEX ux_users__one_workspace_owner IS
                'At most one workspace owner per tenant (the constraint), and "who owns this workspace" (AccessManagementService owner checks and the Users list, WHERE tenant_id = $1 AND is_workspace_owner).';
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_users__one_workspace_owner",
                table: "users");

            migrationBuilder.DropColumn(
                name: "is_workspace_owner",
                table: "users");
        }
    }
}
