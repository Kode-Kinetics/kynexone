using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddInstallationLicences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "installation_licences",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    licence_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    kid = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    payload = table.Column<string>(type: "text", nullable: false),
                    signature = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    licensee = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    imported_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    imported_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_installation_licences", x => x.id);
                    table.CheckConstraint("ck_installation_licences__status", "status IN ('Active','Superseded','Rejected')");
                    table.ForeignKey(
                        name: "FK_installation_licences_users_imported_by_user_id",
                        column: x => x.imported_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_installation_licences_imported_at_utc",
                table: "installation_licences",
                column: "imported_at_utc");

            migrationBuilder.CreateIndex(
                name: "IX_installation_licences_imported_by_user_id",
                table: "installation_licences",
                column: "imported_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_installation_licences_licence_id",
                table: "installation_licences",
                column: "licence_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_installation_licences__one_active",
                table: "installation_licences",
                column: "status",
                unique: true,
                filter: "status = 'Active'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "installation_licences");
        }
    }
}
