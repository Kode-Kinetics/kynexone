using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddShiftPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "shift_policies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    gender_shift_rules_json = table.Column<string>(type: "text", nullable: false),
                    voluntary_shift_codes_json = table.Column<string>(type: "text", nullable: false),
                    weekend_demand_json = table.Column<string>(type: "text", nullable: false),
                    holiday_demand_json = table.Column<string>(type: "text", nullable: false),
                    min_rest_hours = table.Column<int>(type: "integer", nullable: false),
                    max_consecutive_days = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shift_policies", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_shift_policies_tenant_id",
                table: "shift_policies",
                column: "tenant_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "shift_policies");
        }
    }
}
