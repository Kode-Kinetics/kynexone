using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddEstablishmentNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Expand only: two nullable columns, no default, no backfill, nothing for old code to trip over.
            migrationBuilder.AddColumn<string>(
                name: "establishment_note",
                table: "departments",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "note",
                table: "department_staffing_budgets",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "establishment_note",
                table: "departments");

            migrationBuilder.DropColumn(
                name: "note",
                table: "department_staffing_budgets");
        }
    }
}
