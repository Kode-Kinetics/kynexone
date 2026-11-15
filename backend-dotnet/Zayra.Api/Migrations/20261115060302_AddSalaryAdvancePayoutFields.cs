using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSalaryAdvancePayoutFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "cancel_reason",
                table: "salary_advances",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "cancelled_at_utc",
                table: "salary_advances",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "paid_at_utc",
                table: "salary_advances",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "paid_by_user_id",
                table: "salary_advances",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "payment_reference",
                table: "salary_advances",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "cancel_reason",
                table: "salary_advances");

            migrationBuilder.DropColumn(
                name: "cancelled_at_utc",
                table: "salary_advances");

            migrationBuilder.DropColumn(
                name: "paid_at_utc",
                table: "salary_advances");

            migrationBuilder.DropColumn(
                name: "paid_by_user_id",
                table: "salary_advances");

            migrationBuilder.DropColumn(
                name: "payment_reference",
                table: "salary_advances");
        }
    }
}
