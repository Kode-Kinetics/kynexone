using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class S2LeaverLifecycleArtifacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "access_revoked_at_utc",
                table: "employee_offboardings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "access_revoked_by_user_id",
                table: "employee_offboardings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cancel_reason",
                table: "employee_offboardings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "cancelled_at_utc",
                table: "employee_offboardings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "cancelled_by_user_id",
                table: "employee_offboardings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "external_payment_date",
                table: "employee_final_settlements",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "external_payment_method",
                table: "employee_final_settlements",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "external_payment_recorded_by_name",
                table: "employee_final_settlements",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "external_payment_recorded_by_user_id",
                table: "employee_final_settlements",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "external_payment_reference",
                table: "employee_final_settlements",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "paid_outside_payroll",
                table: "employee_final_settlements",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "access_revoked_at_utc",
                table: "employee_offboardings");

            migrationBuilder.DropColumn(
                name: "access_revoked_by_user_id",
                table: "employee_offboardings");

            migrationBuilder.DropColumn(
                name: "cancel_reason",
                table: "employee_offboardings");

            migrationBuilder.DropColumn(
                name: "cancelled_at_utc",
                table: "employee_offboardings");

            migrationBuilder.DropColumn(
                name: "cancelled_by_user_id",
                table: "employee_offboardings");

            migrationBuilder.DropColumn(
                name: "external_payment_date",
                table: "employee_final_settlements");

            migrationBuilder.DropColumn(
                name: "external_payment_method",
                table: "employee_final_settlements");

            migrationBuilder.DropColumn(
                name: "external_payment_recorded_by_name",
                table: "employee_final_settlements");

            migrationBuilder.DropColumn(
                name: "external_payment_recorded_by_user_id",
                table: "employee_final_settlements");

            migrationBuilder.DropColumn(
                name: "external_payment_reference",
                table: "employee_final_settlements");

            migrationBuilder.DropColumn(
                name: "paid_outside_payroll",
                table: "employee_final_settlements");
        }
    }
}
