using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTimesheets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "timesheet_day_reconciliations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    timesheet_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    employee_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    work_date = table.Column<DateOnly>(type: "date", nullable: false),
                    logged_minutes = table.Column<int>(type: "integer", nullable: false),
                    attendance_minutes = table.Column<int>(type: "integer", nullable: true),
                    variance_minutes = table.Column<int>(type: "integer", nullable: true),
                    attendance_status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    is_over_allocated = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_timesheet_day_reconciliations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "timesheets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    employee_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    period_start = table.Column<DateOnly>(type: "date", nullable: false),
                    period_end = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    total_minutes = table.Column<int>(type: "integer", nullable: false),
                    approval_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    submitted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    submitted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    decision_comments = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_timesheets", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "timesheet_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    timesheet_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    work_date = table.Column<DateOnly>(type: "date", nullable: false),
                    cost_center_id = table.Column<Guid>(type: "uuid", nullable: true),
                    minutes = table.Column<int>(type: "integer", nullable: false),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_timesheet_entries", x => x.id);
                    table.ForeignKey(
                        name: "FK_timesheet_entries_timesheets_timesheet_id",
                        column: x => x.timesheet_id,
                        principalTable: "timesheets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_timesheet_day_reconciliations_tenant_date_employee",
                table: "timesheet_day_reconciliations",
                columns: new[] { "tenant_id", "work_date", "employee_id" });

            migrationBuilder.CreateIndex(
                name: "IX_timesheet_day_reconciliations_tenant_id_company_id",
                table: "timesheet_day_reconciliations",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "ux_timesheet_day_reconciliations_sheet_date",
                table: "timesheet_day_reconciliations",
                columns: new[] { "tenant_id", "timesheet_id", "work_date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_timesheet_entries_tenant_cost_centre_date",
                table: "timesheet_entries",
                columns: new[] { "tenant_id", "cost_center_id", "work_date" });

            migrationBuilder.CreateIndex(
                name: "IX_timesheet_entries_tenant_id_company_id",
                table: "timesheet_entries",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "ix_timesheet_entries_tenant_sheet_date",
                table: "timesheet_entries",
                columns: new[] { "tenant_id", "timesheet_id", "work_date" });

            migrationBuilder.CreateIndex(
                name: "IX_timesheet_entries_timesheet_id",
                table: "timesheet_entries",
                column: "timesheet_id");

            migrationBuilder.CreateIndex(
                name: "ix_timesheets_tenant_approval_request",
                table: "timesheets",
                columns: new[] { "tenant_id", "approval_request_id" });

            migrationBuilder.CreateIndex(
                name: "IX_timesheets_tenant_id_company_id",
                table: "timesheets",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "ix_timesheets_tenant_status_period",
                table: "timesheets",
                columns: new[] { "tenant_id", "status", "period_start" });

            migrationBuilder.CreateIndex(
                name: "ux_timesheets_tenant_employee_period",
                table: "timesheets",
                columns: new[] { "tenant_id", "employee_id", "period_start" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "timesheet_day_reconciliations");

            migrationBuilder.DropTable(
                name: "timesheet_entries");

            migrationBuilder.DropTable(
                name: "timesheets");
        }
    }
}
