using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class Phase1BCompanyScopeFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "wps_file_batches",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "work_permit_records",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "visa_records",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "account_type",
                table: "tenants",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "shift_assignments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "salary_advances",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "payslips",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "payroll_slips",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "payroll_deductions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "passport_records",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "overtime_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "leave_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "leave_balance_transactions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "employee_loans",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "employee_documents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "employee_contracts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "employee_bonuses",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "audit_logs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "attendance_regularization_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "attendance_records",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "admin_audit_logs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "company_compliance_profiles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    country_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    jurisdiction = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    compliance_pack = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    effective_to = table.Column<DateOnly>(type: "date", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    required_fields_json = table.Column<string>(type: "text", nullable: false),
                    notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_company_compliance_profiles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "company_tax_policies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    country_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    effective_to = table.Column<DateOnly>(type: "date", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    income_tax_rate_percent = table.Column<decimal>(type: "numeric(8,4)", precision: 8, scale: 4, nullable: true),
                    applies_to_bonus = table.Column<bool>(type: "boolean", nullable: false),
                    tax_configuration_json = table.Column<string>(type: "text", nullable: false),
                    notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_company_tax_policies", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_wps_file_batches_tenant_id_company_id",
                table: "wps_file_batches",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_work_permit_records_tenant_id_company_id",
                table: "work_permit_records",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_visa_records_tenant_id_company_id",
                table: "visa_records",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_shift_assignments_tenant_id_company_id",
                table: "shift_assignments",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_salary_advances_tenant_id_company_id",
                table: "salary_advances",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_public_holiday_calendars_tenant_id_company_id",
                table: "public_holiday_calendars",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_payslips_tenant_id_company_id",
                table: "payslips",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_payroll_slips_tenant_id_company_id",
                table: "payroll_slips",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_payroll_runs_tenant_id_company_id",
                table: "payroll_runs",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_payroll_deductions_tenant_id_company_id",
                table: "payroll_deductions",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_passport_records_tenant_id_company_id",
                table: "passport_records",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_overtime_requests_tenant_id_company_id",
                table: "overtime_requests",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_leave_requests_tenant_id_company_id",
                table: "leave_requests",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_leave_requests_tenant_id_company_id_status",
                table: "leave_requests",
                columns: new[] { "tenant_id", "company_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_leave_policy_eligibilities_tenant_id_company_id",
                table: "leave_policy_eligibilities",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_leave_policies_tenant_id_company_id",
                table: "leave_policies",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_leave_balance_transactions_tenant_id_company_id",
                table: "leave_balance_transactions",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_employees_tenant_id_company_id",
                table: "employees",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_loans_tenant_id_company_id",
                table: "employee_loans",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_id_rules_tenant_id_company_id",
                table: "employee_id_rules",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_documents_tenant_id_company_id",
                table: "employee_documents",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_contracts_tenant_id_company_id",
                table: "employee_contracts",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_bonuses_tenant_id_company_id",
                table: "employee_bonuses",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_tenant_id_company_id",
                table: "audit_logs",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_attendance_regularization_requests_tenant_id_company_id",
                table: "attendance_regularization_requests",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_attendance_records_tenant_id_company_id",
                table: "attendance_records",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_attendance_records_tenant_id_company_id_work_date",
                table: "attendance_records",
                columns: new[] { "tenant_id", "company_id", "work_date" });

            migrationBuilder.CreateIndex(
                name: "IX_admin_audit_logs_tenant_id_company_id",
                table: "admin_audit_logs",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_company_compliance_profiles_tenant_id_company_id",
                table: "company_compliance_profiles",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_company_compliance_profiles_tenant_id_company_id_status_eff~",
                table: "company_compliance_profiles",
                columns: new[] { "tenant_id", "company_id", "status", "effective_from" });

            migrationBuilder.CreateIndex(
                name: "IX_company_tax_policies_tenant_id_company_id",
                table: "company_tax_policies",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_company_tax_policies_tenant_id_company_id_status_effective_~",
                table: "company_tax_policies",
                columns: new[] { "tenant_id", "company_id", "status", "effective_from" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "company_compliance_profiles");

            migrationBuilder.DropTable(
                name: "company_tax_policies");

            migrationBuilder.DropIndex(
                name: "IX_wps_file_batches_tenant_id_company_id",
                table: "wps_file_batches");

            migrationBuilder.DropIndex(
                name: "IX_work_permit_records_tenant_id_company_id",
                table: "work_permit_records");

            migrationBuilder.DropIndex(
                name: "IX_visa_records_tenant_id_company_id",
                table: "visa_records");

            migrationBuilder.DropIndex(
                name: "IX_shift_assignments_tenant_id_company_id",
                table: "shift_assignments");

            migrationBuilder.DropIndex(
                name: "IX_salary_advances_tenant_id_company_id",
                table: "salary_advances");

            migrationBuilder.DropIndex(
                name: "IX_public_holiday_calendars_tenant_id_company_id",
                table: "public_holiday_calendars");

            migrationBuilder.DropIndex(
                name: "IX_payslips_tenant_id_company_id",
                table: "payslips");

            migrationBuilder.DropIndex(
                name: "IX_payroll_slips_tenant_id_company_id",
                table: "payroll_slips");

            migrationBuilder.DropIndex(
                name: "IX_payroll_runs_tenant_id_company_id",
                table: "payroll_runs");

            migrationBuilder.DropIndex(
                name: "IX_payroll_deductions_tenant_id_company_id",
                table: "payroll_deductions");

            migrationBuilder.DropIndex(
                name: "IX_passport_records_tenant_id_company_id",
                table: "passport_records");

            migrationBuilder.DropIndex(
                name: "IX_overtime_requests_tenant_id_company_id",
                table: "overtime_requests");

            migrationBuilder.DropIndex(
                name: "IX_leave_requests_tenant_id_company_id",
                table: "leave_requests");

            migrationBuilder.DropIndex(
                name: "IX_leave_requests_tenant_id_company_id_status",
                table: "leave_requests");

            migrationBuilder.DropIndex(
                name: "IX_leave_policy_eligibilities_tenant_id_company_id",
                table: "leave_policy_eligibilities");

            migrationBuilder.DropIndex(
                name: "IX_leave_policies_tenant_id_company_id",
                table: "leave_policies");

            migrationBuilder.DropIndex(
                name: "IX_leave_balance_transactions_tenant_id_company_id",
                table: "leave_balance_transactions");

            migrationBuilder.DropIndex(
                name: "IX_employees_tenant_id_company_id",
                table: "employees");

            migrationBuilder.DropIndex(
                name: "IX_employee_loans_tenant_id_company_id",
                table: "employee_loans");

            migrationBuilder.DropIndex(
                name: "IX_employee_id_rules_tenant_id_company_id",
                table: "employee_id_rules");

            migrationBuilder.DropIndex(
                name: "IX_employee_documents_tenant_id_company_id",
                table: "employee_documents");

            migrationBuilder.DropIndex(
                name: "IX_employee_contracts_tenant_id_company_id",
                table: "employee_contracts");

            migrationBuilder.DropIndex(
                name: "IX_employee_bonuses_tenant_id_company_id",
                table: "employee_bonuses");

            migrationBuilder.DropIndex(
                name: "IX_audit_logs_tenant_id_company_id",
                table: "audit_logs");

            migrationBuilder.DropIndex(
                name: "IX_attendance_regularization_requests_tenant_id_company_id",
                table: "attendance_regularization_requests");

            migrationBuilder.DropIndex(
                name: "IX_attendance_records_tenant_id_company_id",
                table: "attendance_records");

            migrationBuilder.DropIndex(
                name: "IX_attendance_records_tenant_id_company_id_work_date",
                table: "attendance_records");

            migrationBuilder.DropIndex(
                name: "IX_admin_audit_logs_tenant_id_company_id",
                table: "admin_audit_logs");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "wps_file_batches");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "work_permit_records");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "visa_records");

            migrationBuilder.DropColumn(
                name: "account_type",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "shift_assignments");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "salary_advances");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "payslips");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "payroll_slips");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "payroll_deductions");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "passport_records");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "overtime_requests");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "leave_requests");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "leave_balance_transactions");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "employee_loans");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "employee_documents");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "employee_contracts");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "employee_bonuses");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "audit_logs");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "attendance_regularization_requests");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "attendance_records");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "admin_audit_logs");
        }
    }
}
