using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddLiveSeparationUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_employee_offboardings_live_per_employee",
                table: "employee_offboardings",
                columns: new[] { "tenant_id", "employee_id" },
                unique: true,
                filter: "status NOT IN ('Cancelled', 'Completed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_employee_offboardings_live_per_employee",
                table: "employee_offboardings");
        }
    }
}
