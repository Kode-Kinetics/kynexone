using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddEmployeeGccIdExpiryScalars : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "civil_id_expiry_date",
                table: "employees",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "emirates_id_expiry_date",
                table: "employees",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "iqama_expiry_date",
                table: "employees",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "qid_expiry_date",
                table: "employees",
                type: "date",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "civil_id_expiry_date",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "emirates_id_expiry_date",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "iqama_expiry_date",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "qid_expiry_date",
                table: "employees");
        }
    }
}
