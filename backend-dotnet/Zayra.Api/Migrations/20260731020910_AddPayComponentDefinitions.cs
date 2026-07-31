using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPayComponentDefinitions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pay_components",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name_en = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    name_ar = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    component_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    calc_method = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    structure_field = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    value = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    formula_expression = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    provider_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    is_taxable = table.Column<bool>(type: "boolean", nullable: false),
                    gosi_subject = table.Column<bool>(type: "boolean", nullable: false),
                    wps_included = table.Column<bool>(type: "boolean", nullable: false),
                    eosb_included = table.Column<bool>(type: "boolean", nullable: false),
                    gl_driver_key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    emit_when_zero = table.Column<bool>(type: "boolean", nullable: false),
                    is_family = table.Column<bool>(type: "boolean", nullable: false),
                    display_order = table.Column<int>(type: "integer", nullable: false),
                    is_system = table.Column<bool>(type: "boolean", nullable: false),
                    is_statutory = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pay_components", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_pay_components_tenant_id_company_id",
                table: "pay_components",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_pay_components_tenant_id_company_id_code_component_type",
                table: "pay_components",
                columns: new[] { "tenant_id", "company_id", "code", "component_type" });

            migrationBuilder.CreateIndex(
                name: "IX_pay_components_tenant_id_company_id_is_active_is_deleted",
                table: "pay_components",
                columns: new[] { "tenant_id", "company_id", "is_active", "is_deleted" });

            // Real per-scope UNIQUE with PG15 NULLS NOT DISTINCT semantics (Npgsql 8 cannot express it
            // fluently — same idiom as ux_gl_drivers_scope_key in RekeyGlUniquesForCompanyScope). Without
            // NULLS NOT DISTINCT, two tenant-default rows (company_id IS NULL) with the same code+type would
            // NOT collide, producing duplicate defaults and non-deterministic component resolution. Neon is
            // PG15+. component_type is in the key so the "ADJ" earning family and "ADJ" deduction family can
            // coexist as distinct tenant defaults.
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX IF NOT EXISTS ux_pay_components_scope_code_type " +
                "ON pay_components (tenant_id, company_id, code, component_type) NULLS NOT DISTINCT;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ux_pay_components_scope_code_type;");
            migrationBuilder.DropTable(
                name: "pay_components");
        }
    }
}
