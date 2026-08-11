using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddFinalSettlementPersistenceAndExportScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "settles_final_settlements",
                table: "payroll_runs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "employee_final_settlements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    employee_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    employee_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    offboarding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_working_day = table.Column<DateOnly>(type: "date", nullable: false),
                    service_start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    settlement_due_date = table.Column<DateOnly>(type: "date", nullable: false),
                    termination_reason = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    confirmed_termination_reason = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    service_years = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: false),
                    currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    eosb_result_json = table.Column<string>(type: "json", nullable: false),
                    inputs_snapshot_json = table.Column<string>(type: "json", nullable: false),
                    eosb_calculation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    gratuity_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    leave_encashment_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    leave_encashment_days = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    notice_pay_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    other_dues_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    notice_shortfall_deduction = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    other_deductions_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    planned_loan_recovery = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    planned_advance_recovery = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    planned_receivable_recovery = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    gross_payable = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    total_deductions = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    net_payable = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    unpaid_wages_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    wages_paid_by_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    wages_paid_through_date = table.Column<DateOnly>(type: "date", nullable: true),
                    wages_acknowledged_unpaid = table.Column<bool>(type: "boolean", nullable: false),
                    wages_acknowledgement_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    wage_base_delta_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    wage_base_acknowledged_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    wage_base_acknowledged_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    wage_base_acknowledged_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    submitted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    submitted_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    submitted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    approved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    approved_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    cancelled_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cancelled_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    cancelled_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    cancel_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    gl_posted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    gl_period = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: true),
                    payroll_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payment_batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    paid_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    residual_debt_reclassed = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    residual_debt_unbooked = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    warnings_json = table.Column<string>(type: "json", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employee_final_settlements", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "final_settlement_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    settlement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    component_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    component_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    line_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    source = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    source_entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    narrative = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_final_settlement_lines", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_gl_journal_exports_tenant_id_company_id",
                table: "gl_journal_exports",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "ix_employee_final_settlements_live_offboarding",
                table: "employee_final_settlements",
                columns: new[] { "tenant_id", "offboarding_id" },
                unique: true,
                filter: "status <> 'Cancelled'");

            migrationBuilder.CreateIndex(
                name: "IX_employee_final_settlements_tenant_id_company_id",
                table: "employee_final_settlements",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_final_settlements_tenant_id_employee_id_status",
                table: "employee_final_settlements",
                columns: new[] { "tenant_id", "employee_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_final_settlements_tenant_id_payroll_run_id",
                table: "employee_final_settlements",
                columns: new[] { "tenant_id", "payroll_run_id" });

            migrationBuilder.CreateIndex(
                name: "IX_final_settlement_lines_tenant_id_settlement_id",
                table: "final_settlement_lines",
                columns: new[] { "tenant_id", "settlement_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "employee_final_settlements");

            migrationBuilder.DropTable(
                name: "final_settlement_lines");

            migrationBuilder.DropIndex(
                name: "IX_gl_journal_exports_tenant_id_company_id",
                table: "gl_journal_exports");

            migrationBuilder.DropColumn(
                name: "settles_final_settlements",
                table: "payroll_runs");
        }
    }
}
