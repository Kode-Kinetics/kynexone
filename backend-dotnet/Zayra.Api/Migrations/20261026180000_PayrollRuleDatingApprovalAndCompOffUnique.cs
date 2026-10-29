using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class PayrollRuleDatingApprovalAndCompOffUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Rules become dated: every existing row keeps applying from the beginning of time, so existing tenants' pay
            // does not change.
            migrationBuilder.DropIndex(
                name: "ux_company_payroll_rules__company_rule",
                table: "company_payroll_rules");

            migrationBuilder.AddColumn<DateOnly>(
                name: "effective_from",
                table: "company_payroll_rules",
                type: "date",
                nullable: false,
                defaultValue: new DateOnly(1, 1, 1));

            migrationBuilder.CreateIndex(
                name: "ux_company_payroll_rules__company_rule_from",
                table: "company_payroll_rules",
                columns: new[] { "tenant_id", "company_id", "rule_key", "effective_from" },
                unique: true);

            migrationBuilder.CreateTable(
                name: "payroll_rule_change_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    changes_json = table.Column<string>(type: "text", nullable: false),
                    warning_codes_json = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: true),
                    requested_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    decided_by = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    decision_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payroll_rule_change_requests", x => x.id);
                    table.CheckConstraint("ck_payroll_rule_change_requests__decider_not_maker", "decided_by IS NULL OR requested_by IS NULL OR decided_by <> requested_by");
                    table.CheckConstraint("ck_payroll_rule_change_requests__status", "status IN ('Pending','Approved','Rejected')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_payroll_rule_change_requests__company_status",
                table: "payroll_rule_change_requests",
                columns: new[] { "tenant_id", "company_id", "status" });

            // RestDayWork is reserved for attendance-generated credits. Before enforcing their uniqueness, preserve
            // legacy manually entered rows under a non-reserved label so attendance cannot adopt or collide with them.
            migrationBuilder.Sql("""
                UPDATE comp_off_credits
                SET work_type = 'LegacyManualRestDayWork'
                WHERE work_type = 'RestDayWork';
                """);

            // One compensatory-day credit per employee per worked rest day/holiday (other credit types may repeat on a date).
            migrationBuilder.CreateIndex(
                name: "ux_comp_off_credits__rest_day_work",
                table: "comp_off_credits",
                columns: new[] { "tenant_id", "employee_id", "worked_date", "work_type" },
                unique: true,
                filter: "\"work_type\" = 'RestDayWork'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "ux_comp_off_credits__rest_day_work", table: "comp_off_credits");
            migrationBuilder.DropTable(name: "payroll_rule_change_requests");
            migrationBuilder.DropIndex(name: "ux_company_payroll_rules__company_rule_from", table: "company_payroll_rules");
            // Only the rows in force from the beginning of time survive the step back to one value per rule.
            migrationBuilder.Sql("DELETE FROM company_payroll_rules WHERE effective_from <> DATE '0001-01-01';");
            migrationBuilder.DropColumn(name: "effective_from", table: "company_payroll_rules");
            migrationBuilder.CreateIndex(
                name: "ux_company_payroll_rules__company_rule",
                table: "company_payroll_rules",
                columns: new[] { "tenant_id", "company_id", "rule_key" },
                unique: true);
        }
    }
}
