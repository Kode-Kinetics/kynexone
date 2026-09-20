using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddReportScheduleFailureVisibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "consecutive_failure_count",
                table: "report_schedules",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "last_failure_at_utc",
                table: "report_schedules",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_failure_reason",
                table: "report_schedules",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "owner_invalidated_at_utc",
                table: "report_schedules",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_report_schedules_tenant_failures",
                table: "report_schedules",
                columns: new[] { "tenant_id", "consecutive_failure_count" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_report_schedules_tenant_failures",
                table: "report_schedules");

            migrationBuilder.DropColumn(
                name: "consecutive_failure_count",
                table: "report_schedules");

            migrationBuilder.DropColumn(
                name: "last_failure_at_utc",
                table: "report_schedules");

            migrationBuilder.DropColumn(
                name: "last_failure_reason",
                table: "report_schedules");

            migrationBuilder.DropColumn(
                name: "owner_invalidated_at_utc",
                table: "report_schedules");
        }
    }
}
