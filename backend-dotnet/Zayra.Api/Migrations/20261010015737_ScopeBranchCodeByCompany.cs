using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class ScopeBranchCodeByCompany : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_branches_tenant_id_code",
                table: "branches");

            migrationBuilder.CreateIndex(
                name: "IX_branches_tenant_id_company_id_code",
                table: "branches",
                columns: new[] { "tenant_id", "company_id", "code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_branches_tenant_id_company_id_code",
                table: "branches");

            migrationBuilder.CreateIndex(
                name: "IX_branches_tenant_id_code",
                table: "branches",
                columns: new[] { "tenant_id", "code" },
                unique: true);
        }
    }
}
