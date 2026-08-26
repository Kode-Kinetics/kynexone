using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class DropGlJournalExportCompanyScopeIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_gl_journal_exports_tenant_id_company_id",
                table: "gl_journal_exports");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_gl_journal_exports_tenant_id_company_id",
                table: "gl_journal_exports",
                columns: new[] { "tenant_id", "company_id" });
        }
    }
}
