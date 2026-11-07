using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class PayrollEarningDefinitionTerms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Expand-only: one nullable column, no default and no rewrite. ADD COLUMN takes an ACCESS EXCLUSIVE lock on payroll_earnings, which
            // payroll reads queue behind: fail fast (55P03) and re-run when quiet. Every existing line is NULL, and only a customer-defined cash
            // allowance line (ALW_*) ever carries a value, which Process writes; a Processed run whose allowance lines are NULL is simply
            // processed again before it can be approved (none exist where the cash allowances first ship).
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");

            migrationBuilder.AddColumn<string>(
                name: "definition_terms",
                table: "payroll_earnings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "definition_terms",
                table: "payroll_earnings");
        }
    }
}
