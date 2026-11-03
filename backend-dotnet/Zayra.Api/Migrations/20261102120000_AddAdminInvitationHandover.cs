using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminInvitationHandover : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Expand only: a NOT NULL boolean defaulting to false, so every existing tenant stays strict and old code is unaffected.
            migrationBuilder.AddColumn<bool>(
                name: "allow_admin_invitation_handover",
                table: "security_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "allow_admin_invitation_handover",
                table: "security_settings");
        }
    }
}
