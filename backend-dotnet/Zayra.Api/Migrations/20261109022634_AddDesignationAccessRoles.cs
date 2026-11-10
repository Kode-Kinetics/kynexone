using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddDesignationAccessRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Expand-only: one new table and one nullable column with no default (no rewrite; every existing role stays 'given by hand').
            // The new foreign key from user_roles to designations takes a brief lock on both: fail fast (55P03) and re-run when quiet.
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");

            migrationBuilder.AddColumn<Guid>(
                name: "granted_by_designation_id",
                table: "user_roles",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "designation_access_roles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    designation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_designation_access_roles", x => x.id);
                    table.ForeignKey(
                        name: "FK_designation_access_roles_designations_designation_id",
                        column: x => x.designation_id,
                        principalTable: "designations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_designation_access_roles_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_user_roles_granted_by_designation_id",
                table: "user_roles",
                column: "granted_by_designation_id",
                filter: "granted_by_designation_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_designation_access_roles_designation_id",
                table: "designation_access_roles",
                column: "designation_id");

            migrationBuilder.CreateIndex(
                name: "IX_designation_access_roles_role_id",
                table: "designation_access_roles",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "IX_designation_access_roles_tenant_id_designation_id_role_id",
                table: "designation_access_roles",
                columns: new[] { "tenant_id", "designation_id", "role_id" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_user_roles_designations_granted_by_designation_id",
                table: "user_roles",
                column: "granted_by_designation_id",
                principalTable: "designations",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_user_roles_designations_granted_by_designation_id",
                table: "user_roles");

            migrationBuilder.DropTable(
                name: "designation_access_roles");

            migrationBuilder.DropIndex(
                name: "IX_user_roles_granted_by_designation_id",
                table: "user_roles");

            migrationBuilder.DropColumn(
                name: "granted_by_designation_id",
                table: "user_roles");
        }
    }
}
