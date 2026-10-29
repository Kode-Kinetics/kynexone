using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyPayrollRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "company_payroll_rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_key = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    rule_value = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    acknowledged_by = table.Column<Guid>(type: "uuid", nullable: true),
                    acknowledged_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    acknowledgement_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_company_payroll_rules", x => x.id);
                    table.CheckConstraint("ck_company_payroll_rules__ack_pair", "(acknowledged_by IS NULL) = (acknowledged_at_utc IS NULL)");
                });

            migrationBuilder.CreateIndex(
                name: "ux_company_payroll_rules__company_rule",
                table: "company_payroll_rules",
                columns: new[] { "tenant_id", "company_id", "rule_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "company_payroll_rules");
        }
    }
}
