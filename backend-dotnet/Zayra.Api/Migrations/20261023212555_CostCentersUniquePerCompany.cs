using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// Addendum B, owner decision 2026-10-23: cost centres are per company like departments, designations and grades.
    /// Code uniqueness moves from (tenant_id, code) to (tenant_id, company_id, code) NULLS NOT DISTINCT. Expand only —
    /// no data moves (every existing code is already tenant-unique, so it is company-unique too). Down refuses
    /// (unique violation) once two companies share a code; restore the pre-deploy database branch instead.
    /// </summary>
    public partial class CostCentersUniquePerCompany : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_cost_centers_tenant_id_code",
                table: "cost_centers");

            migrationBuilder.CreateIndex(
                name: "IX_cost_centers_tenant_id_company_id_code",
                table: "cost_centers",
                columns: new[] { "tenant_id", "company_id", "code" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_cost_centers_tenant_id_company_id_code",
                table: "cost_centers");

            migrationBuilder.CreateIndex(
                name: "IX_cost_centers_tenant_id_code",
                table: "cost_centers",
                columns: new[] { "tenant_id", "code" },
                unique: true);
        }
    }
}
