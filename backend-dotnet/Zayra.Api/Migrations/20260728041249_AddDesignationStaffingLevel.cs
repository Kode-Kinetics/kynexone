using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddDesignationStaffingLevel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "staffing_level_id",
                table: "designations",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_designations_tenant_id_staffing_level_id",
                table: "designations",
                columns: new[] { "tenant_id", "staffing_level_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_designations_tenant_id_staffing_level_id",
                table: "designations");

            migrationBuilder.DropColumn(
                name: "staffing_level_id",
                table: "designations");
        }
    }
}
