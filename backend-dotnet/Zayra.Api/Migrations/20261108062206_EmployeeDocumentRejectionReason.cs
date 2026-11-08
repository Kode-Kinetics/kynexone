using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class EmployeeDocumentRejectionReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Expand-only: one nullable column, no default, no rewrite. Every existing row is NULL (a document rejected before this
            // column existed keeps its reason in notes). ADD COLUMN takes an ACCESS EXCLUSIVE lock on employee_documents: fail fast
            // (55P03) and re-run when quiet.
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");

            migrationBuilder.AddColumn<string>(
                name: "rejection_reason",
                table: "employee_documents",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "rejection_reason",
                table: "employee_documents");
        }
    }
}
