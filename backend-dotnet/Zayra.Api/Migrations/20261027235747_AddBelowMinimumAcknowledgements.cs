using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddBelowMinimumAcknowledgements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "below_minimum_acks_json",
                table: "leave_policies",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "eosb_below_floor_acks_json",
                table: "gcc_compliance_settings",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "below_minimum_acks_json",
                table: "leave_policies");

            migrationBuilder.DropColumn(
                name: "eosb_below_floor_acks_json",
                table: "gcc_compliance_settings");
        }
    }
}
