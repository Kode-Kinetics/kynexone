using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class CompanyScopedPayrollIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_salary_structures_tenant_id_code",
                table: "salary_structures");

            migrationBuilder.DropIndex(
                name: "IX_payroll_runs_tenant_id_company_id_year_month",
                table: "payroll_runs");

            migrationBuilder.DropIndex(
                name: "IX_payroll_runs_tenant_id_year_month",
                table: "payroll_runs");

            migrationBuilder.CreateIndex(
                name: "IX_salary_structures_tenant_id_company_id_code",
                table: "salary_structures",
                columns: new[] { "tenant_id", "company_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payroll_runs_tenant_id_company_id_year_month",
                table: "payroll_runs",
                columns: new[] { "tenant_id", "company_id", "year", "month" },
                unique: true,
                filter: "\"status\" != 'Voided'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_salary_structures_tenant_id_company_id_code",
                table: "salary_structures");

            migrationBuilder.DropIndex(
                name: "IX_payroll_runs_tenant_id_company_id_year_month",
                table: "payroll_runs");

            migrationBuilder.CreateIndex(
                name: "IX_salary_structures_tenant_id_code",
                table: "salary_structures",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payroll_runs_tenant_id_company_id_year_month",
                table: "payroll_runs",
                columns: new[] { "tenant_id", "company_id", "year", "month" });

            migrationBuilder.CreateIndex(
                name: "IX_payroll_runs_tenant_id_year_month",
                table: "payroll_runs",
                columns: new[] { "tenant_id", "year", "month" },
                unique: true,
                filter: "\"status\" != 'Voided'");
        }
    }
}
