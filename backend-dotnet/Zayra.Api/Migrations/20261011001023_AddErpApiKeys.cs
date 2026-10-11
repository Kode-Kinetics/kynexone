using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddErpApiKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "erp_api_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    prefix = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    key_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    scopes = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ip_allow_list = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    last_used_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_used_ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    revoked_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    revoked_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    revoked_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_erp_api_keys", x => x.id);
                    table.CheckConstraint("ck_erp_api_keys__expiry_after_creation", "expires_at_utc > created_at_utc");
                    table.CheckConstraint("ck_erp_api_keys__scopes_present", "scopes <> ''");
                });

            migrationBuilder.CreateIndex(
                name: "IX_erp_api_keys_key_hash",
                table: "erp_api_keys",
                column: "key_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_erp_api_keys_tenant_id_created_at_utc",
                table: "erp_api_keys",
                columns: new[] { "tenant_id", "created_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "erp_api_keys");
        }
    }
}
