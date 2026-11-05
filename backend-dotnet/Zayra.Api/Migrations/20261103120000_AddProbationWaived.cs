using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddProbationWaived : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Expand only: a NOT NULL boolean with a constant default, so old code that never writes it keeps working
            // and every existing employee and draft reads as "has a probation" exactly as before.
            migrationBuilder.AddColumn<bool>(
                name: "probation_waived",
                table: "employees",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "probation_waived",
                table: "employee_drafts",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "probation_waived",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "probation_waived",
                table: "employee_drafts");
        }
    }
}
