using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddOpeningBalanceCutover : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "company_cutovers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cutover_date = table.Column<DateOnly>(type: "date", nullable: false),
                    source_system = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    notes = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_company_cutovers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "employee_eosb_opening_balances",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    employee_code = table.Column<string>(type: "text", nullable: false),
                    as_at_date = table.Column<DateOnly>(type: "date", nullable: false),
                    prior_service_start_date = table.Column<DateOnly>(type: "date", nullable: true),
                    accrued_months = table.Column<decimal>(type: "numeric(9,2)", precision: 9, scale: 2, nullable: false),
                    accrued_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    source_system = table.Column<string>(type: "text", nullable: false),
                    source_record_id = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employee_eosb_opening_balances", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "opening_balance_origins",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    employee_code = table.Column<string>(type: "text", nullable: false),
                    entity_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cutover_date = table.Column<DateOnly>(type: "date", nullable: false),
                    carried_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    source_system = table.Column<string>(type: "text", nullable: false),
                    source_record_id = table.Column<string>(type: "text", nullable: false),
                    migration_batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_opening_balance_origins", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_company_cutovers_tenant_id_company_id",
                table: "company_cutovers",
                columns: new[] { "tenant_id", "company_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_employee_eosb_opening_balances_tenant_id_company_id",
                table: "employee_eosb_opening_balances",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_eosb_opening_balances_tenant_id_company_id_as_at_d~",
                table: "employee_eosb_opening_balances",
                columns: new[] { "tenant_id", "company_id", "as_at_date" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_eosb_opening_balances_tenant_id_employee_id_as_at_~",
                table: "employee_eosb_opening_balances",
                columns: new[] { "tenant_id", "employee_id", "as_at_date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_opening_balance_origins_tenant_id_company_id",
                table: "opening_balance_origins",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_opening_balance_origins_tenant_id_company_id_cutover_date",
                table: "opening_balance_origins",
                columns: new[] { "tenant_id", "company_id", "cutover_date" });

            migrationBuilder.CreateIndex(
                name: "IX_opening_balance_origins_tenant_id_entity_type_entity_id",
                table: "opening_balance_origins",
                columns: new[] { "tenant_id", "entity_type", "entity_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_opening_balance_origins_tenant_id_migration_batch_id",
                table: "opening_balance_origins",
                columns: new[] { "tenant_id", "migration_batch_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "company_cutovers");

            migrationBuilder.DropTable(
                name: "employee_eosb_opening_balances");

            migrationBuilder.DropTable(
                name: "opening_balance_origins");
        }
    }
}
