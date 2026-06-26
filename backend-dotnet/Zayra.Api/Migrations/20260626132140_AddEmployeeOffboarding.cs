using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddEmployeeOffboarding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "employee_offboardings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    employee_name = table.Column<string>(type: "text", nullable: false),
                    employee_code = table.Column<string>(type: "text", nullable: false),
                    department = table.Column<string>(type: "text", nullable: false),
                    designation = table.Column<string>(type: "text", nullable: false),
                    separation_type = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    notice_date = table.Column<DateOnly>(type: "date", nullable: false),
                    notice_period_days = table.Column<int>(type: "integer", nullable: false),
                    last_working_day = table.Column<DateOnly>(type: "date", nullable: false),
                    rehire_eligible = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    exit_interview_status = table.Column<string>(type: "text", nullable: false),
                    exit_interview_date = table.Column<DateOnly>(type: "date", nullable: true),
                    exit_reason_category = table.Column<string>(type: "text", nullable: false),
                    exit_interview_rating = table.Column<int>(type: "integer", nullable: false),
                    exit_interview_notes = table.Column<string>(type: "text", nullable: false),
                    assets_returned = table.Column<bool>(type: "boolean", nullable: false),
                    access_revoked = table.Column<bool>(type: "boolean", nullable: false),
                    knowledge_handover = table.Column<bool>(type: "boolean", nullable: false),
                    final_settlement_done = table.Column<bool>(type: "boolean", nullable: false),
                    backfill_requisition_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employee_offboardings", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_employee_offboardings_tenant_id_employee_id",
                table: "employee_offboardings",
                columns: new[] { "tenant_id", "employee_id" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_offboardings_tenant_id_status",
                table: "employee_offboardings",
                columns: new[] { "tenant_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "employee_offboardings");
        }
    }
}
