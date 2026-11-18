using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddOfferContractTerm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "contract_term_months",
                table: "offer_letters",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "contract_type",
                table: "offer_letters",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "employment_type",
                table: "employee_drafts",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "contract_term_months",
                table: "offer_letters");

            migrationBuilder.DropColumn(
                name: "contract_type",
                table: "offer_letters");

            migrationBuilder.DropColumn(
                name: "employment_type",
                table: "employee_drafts");
        }
    }
}
