using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Zayra.Api.Data;

#nullable disable

namespace Zayra.Api.Migrations
{
    [Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof(ZayraDbContext))]
    [Migration("20260713040000_AddEnterpriseIdentityBoundary")]
    public partial class AddEnterpriseIdentityBoundary : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "external_id",
                table: "users",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "identity_provider",
                table: "users",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "Local");

            migrationBuilder.AddColumn<DateTime>(
                name: "last_provisioned_at_utc",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "provisioning_source",
                table: "users",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "Local");

            migrationBuilder.CreateTable(
                name: "tenant_identity_provider_settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    saml_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    oidc_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    scim_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    enforce_sso_login = table.Column<bool>(type: "boolean", nullable: false),
                    scim_dry_run = table.Column<bool>(type: "boolean", nullable: false),
                    allowed_domains_csv = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    saml_entity_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    saml_sso_url = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    saml_certificate_thumbprint = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    oidc_authority = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    oidc_client_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    oidc_client_secret_configured = table.Column<bool>(type: "boolean", nullable: false),
                    scim_token_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    scim_token_rotated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table => { table.PrimaryKey("pk_tenant_identity_provider_settings", x => x.id); });

            migrationBuilder.CreateTable(
                name: "enterprise_identity_provisioning_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    protocol = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    action = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    external_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    employee_id = table.Column<int>(type: "integer", nullable: true),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    details_json = table.Column<string>(type: "json", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table => { table.PrimaryKey("pk_enterprise_identity_provisioning_events", x => x.id); });

            migrationBuilder.CreateIndex(name: "ix_tenant_identity_provider_settings_tenant_id", table: "tenant_identity_provider_settings", column: "tenant_id", unique: true);
            migrationBuilder.CreateIndex(name: "ix_tenant_identity_provider_settings_scim_token_hash", table: "tenant_identity_provider_settings", column: "scim_token_hash");
            migrationBuilder.CreateIndex(name: "ix_enterprise_identity_provisioning_events_tenant_id_external_id", table: "enterprise_identity_provisioning_events", columns: new[] { "tenant_id", "external_id" });
            migrationBuilder.CreateIndex(name: "ix_enterprise_identity_provisioning_events_tenant_id_action_created_at_utc", table: "enterprise_identity_provisioning_events", columns: new[] { "tenant_id", "action", "created_at_utc" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "enterprise_identity_provisioning_events");
            migrationBuilder.DropTable(name: "tenant_identity_provider_settings");
            migrationBuilder.DropColumn(name: "external_id", table: "users");
            migrationBuilder.DropColumn(name: "identity_provider", table: "users");
            migrationBuilder.DropColumn(name: "last_provisioned_at_utc", table: "users");
            migrationBuilder.DropColumn(name: "provisioning_source", table: "users");
        }
    }
}
