using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class GlPhase2CompanyScopeAndRates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_gl_accounts_tenant_id_code",
                table: "gl_accounts");

            migrationBuilder.DropIndex(
                name: "IX_gl_account_mappings_tenant_id_driver_key",
                table: "gl_account_mappings");

            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "gl_accounts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "gl_account_mappings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "segment_cost_center_id",
                table: "gl_account_mappings",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "client_rate_definitions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rate_key = table.Column<string>(type: "text", nullable: false),
                    rate_category = table.Column<string>(type: "text", nullable: false),
                    data_type = table.Column<string>(type: "text", nullable: false),
                    unit = table.Column<string>(type: "text", nullable: false),
                    min_value = table.Column<decimal>(type: "numeric", nullable: true),
                    max_value = table.Column<decimal>(type: "numeric", nullable: true),
                    description = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_client_rate_definitions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "company_rate_policies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    rate_key = table.Column<string>(type: "text", nullable: false),
                    rate_category = table.Column<string>(type: "text", nullable: false),
                    rate_value = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    data_type = table.Column<string>(type: "text", nullable: false),
                    unit = table.Column<string>(type: "text", nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    effective_to = table.Column<DateOnly>(type: "date", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_company_rate_policies", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "company_statutory_overrides",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    country_code = table.Column<string>(type: "text", nullable: false),
                    jurisdiction = table.Column<string>(type: "text", nullable: false),
                    rule_key = table.Column<string>(type: "text", nullable: false),
                    override_value = table.Column<string>(type: "text", nullable: false),
                    data_type = table.Column<string>(type: "text", nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    effective_to = table.Column<DateOnly>(type: "date", nullable: true),
                    review_by = table.Column<DateOnly>(type: "date", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: false),
                    platform_default_at_creation = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    approval_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_company_statutory_overrides", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "gl_drivers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    key = table.Column<string>(type: "text", nullable: false),
                    label = table.Column<string>(type: "text", nullable: false),
                    category = table.Column<string>(type: "text", nullable: false),
                    posting_side = table.Column<string>(type: "text", nullable: false),
                    account_type = table.Column<string>(type: "text", nullable: false),
                    default_code = table.Column<string>(type: "text", nullable: false),
                    default_name = table.Column<string>(type: "text", nullable: false),
                    match_source = table.Column<string>(type: "text", nullable: true),
                    match_mode = table.Column<string>(type: "text", nullable: false),
                    match_component_code = table.Column<string>(type: "text", nullable: true),
                    emits_employer_expense_pair = table.Column<bool>(type: "boolean", nullable: false),
                    paired_expense_driver_key = table.Column<string>(type: "text", nullable: true),
                    is_system = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gl_drivers", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_gl_accounts_tenant_id_company_id",
                table: "gl_accounts",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_gl_accounts_tenant_id_company_id_code",
                table: "gl_accounts",
                columns: new[] { "tenant_id", "company_id", "code" });

            migrationBuilder.CreateIndex(
                name: "IX_gl_account_mappings_account_id",
                table: "gl_account_mappings",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "IX_gl_account_mappings_tenant_id_company_id",
                table: "gl_account_mappings",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_gl_account_mappings_tenant_id_company_id_driver_key",
                table: "gl_account_mappings",
                columns: new[] { "tenant_id", "company_id", "driver_key" });

            migrationBuilder.CreateIndex(
                name: "IX_client_rate_definitions_tenant_id_rate_key",
                table: "client_rate_definitions",
                columns: new[] { "tenant_id", "rate_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_company_rate_policies_tenant_id_company_id",
                table: "company_rate_policies",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_company_rate_policies_tenant_id_company_id_rate_key_status_~",
                table: "company_rate_policies",
                columns: new[] { "tenant_id", "company_id", "rate_key", "status", "effective_from" });

            migrationBuilder.CreateIndex(
                name: "IX_company_statutory_overrides_tenant_id_company_id",
                table: "company_statutory_overrides",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_company_statutory_overrides_tenant_id_company_id_country_co~",
                table: "company_statutory_overrides",
                columns: new[] { "tenant_id", "company_id", "country_code", "jurisdiction", "rule_key", "status", "effective_from" });

            migrationBuilder.CreateIndex(
                name: "IX_gl_drivers_tenant_id_company_id",
                table: "gl_drivers",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_gl_drivers_tenant_id_company_id_key",
                table: "gl_drivers",
                columns: new[] { "tenant_id", "company_id", "key" });

            // Defensive: on a clean DB there are zero orphans, but make the FK add safe against any
            // environment by removing mappings whose account_id has no matching account first.
            migrationBuilder.Sql(
                "DELETE FROM gl_account_mappings m WHERE NOT EXISTS " +
                "(SELECT 1 FROM gl_accounts a WHERE a.id = m.account_id);");

            migrationBuilder.AddForeignKey(
                name: "FK_gl_account_mappings_gl_accounts_account_id",
                table: "gl_account_mappings",
                column: "account_id",
                principalTable: "gl_accounts",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_gl_account_mappings_gl_accounts_account_id",
                table: "gl_account_mappings");

            migrationBuilder.DropTable(
                name: "client_rate_definitions");

            migrationBuilder.DropTable(
                name: "company_rate_policies");

            migrationBuilder.DropTable(
                name: "company_statutory_overrides");

            migrationBuilder.DropTable(
                name: "gl_drivers");

            migrationBuilder.DropIndex(
                name: "IX_gl_accounts_tenant_id_company_id",
                table: "gl_accounts");

            migrationBuilder.DropIndex(
                name: "IX_gl_accounts_tenant_id_company_id_code",
                table: "gl_accounts");

            migrationBuilder.DropIndex(
                name: "IX_gl_account_mappings_account_id",
                table: "gl_account_mappings");

            migrationBuilder.DropIndex(
                name: "IX_gl_account_mappings_tenant_id_company_id",
                table: "gl_account_mappings");

            migrationBuilder.DropIndex(
                name: "IX_gl_account_mappings_tenant_id_company_id_driver_key",
                table: "gl_account_mappings");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "gl_accounts");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "gl_account_mappings");

            migrationBuilder.DropColumn(
                name: "segment_cost_center_id",
                table: "gl_account_mappings");

            migrationBuilder.CreateIndex(
                name: "IX_gl_accounts_tenant_id_code",
                table: "gl_accounts",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_gl_account_mappings_tenant_id_driver_key",
                table: "gl_account_mappings",
                columns: new[] { "tenant_id", "driver_key" },
                unique: true);
        }
    }
}
