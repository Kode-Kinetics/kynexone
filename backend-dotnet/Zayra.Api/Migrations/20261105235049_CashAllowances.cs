using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class CashAllowances : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Expand-only: two nullable columns and one new table. ADD COLUMN takes an ACCESS EXCLUSIVE lock that would block pay-component
            // and salary-change reads behind it: fail fast (55P03) and re-run when quiet. Nothing is backfilled and nothing changes for a
            // tenant until a customer adds a cash allowance.
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");

            migrationBuilder.AddColumn<string>(
                name: "allowances_json",
                table: "salary_change_requests",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "eosb_exclusion_reason",
                table: "pay_components",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "employee_salary_allowances",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    salary_structure_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    basis = table.Column<string>(type: "character varying(14)", maxLength: 14, nullable: false),
                    rate = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: true),
                    amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employee_salary_allowances", x => x.id);
                    table.CheckConstraint("ck_employee_salary_allowances__amount_positive", "amount > 0");
                    table.CheckConstraint("ck_employee_salary_allowances__basis", "basis IN ('Amount','PercentOfBasic')");
                    table.CheckConstraint("ck_employee_salary_allowances__rate_pair", "(basis = 'PercentOfBasic') = (rate IS NOT NULL)");
                    table.CheckConstraint("ck_employee_salary_allowances__rate_range", "rate IS NULL OR (rate > 0 AND rate <= 1)");
                    table.ForeignKey(
                        name: "FK_employee_salary_allowances_employee_salary_structures_tenan~",
                        columns: x => new { x.tenant_id, x.salary_structure_id },
                        principalTable: "employee_salary_structures",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_employee_salary_allowances_tenant_id_salary_structure_id_co~",
                table: "employee_salary_allowances",
                columns: new[] { "tenant_id", "salary_structure_id", "code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "employee_salary_allowances");

            migrationBuilder.DropColumn(
                name: "allowances_json",
                table: "salary_change_requests");

            migrationBuilder.DropColumn(
                name: "eosb_exclusion_reason",
                table: "pay_components");
        }
    }
}
