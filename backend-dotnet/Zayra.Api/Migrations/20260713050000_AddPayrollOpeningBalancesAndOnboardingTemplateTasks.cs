using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Zayra.Api.Data;

#nullable disable

namespace Zayra.Api.Migrations
{
    [Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof(ZayraDbContext))]
    [Migration("20260713050000_AddPayrollOpeningBalancesAndOnboardingTemplateTasks")]
    public partial class AddPayrollOpeningBalancesAndOnboardingTemplateTasks : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "payroll_opening_balances",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    employee_code = table.Column<string>(type: "text", nullable: false),
                    year = table.Column<int>(type: "integer", nullable: false),
                    balance_type = table.Column<string>(type: "text", nullable: false),
                    component_code = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    source_system = table.Column<string>(type: "text", nullable: false),
                    source_record_id = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table => table.PrimaryKey("PK_payroll_opening_balances", x => x.id));

            migrationBuilder.CreateTable(
                name: "onboarding_checklist_template_tasks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    checklist_id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_title = table.Column<string>(type: "text", nullable: false),
                    task_description = table.Column<string>(type: "text", nullable: false),
                    category = table.Column<string>(type: "text", nullable: false),
                    assigned_to_name = table.Column<string>(type: "text", nullable: false),
                    assigned_to_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    due_offset_days = table.Column<int>(type: "integer", nullable: false),
                    order_index = table.Column<int>(type: "integer", nullable: false),
                    is_mandatory = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table => table.PrimaryKey("PK_onboarding_checklist_template_tasks", x => x.id));

            migrationBuilder.CreateIndex(
                name: "IX_payroll_opening_balances_tenant_id_employee_id_year_balance_type_component_code",
                table: "payroll_opening_balances",
                columns: new[] { "tenant_id", "employee_id", "year", "balance_type", "component_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payroll_opening_balances_tenant_id_company_id_year",
                table: "payroll_opening_balances",
                columns: new[] { "tenant_id", "company_id", "year" });

            migrationBuilder.CreateIndex(
                name: "IX_onboarding_checklist_template_tasks_tenant_id_checklist_id_order_index",
                table: "onboarding_checklist_template_tasks",
                columns: new[] { "tenant_id", "checklist_id", "order_index" });

            migrationBuilder.CreateIndex(
                name: "IX_onboarding_checklist_template_tasks_tenant_id_checklist_id_task_title",
                table: "onboarding_checklist_template_tasks",
                columns: new[] { "tenant_id", "checklist_id", "task_title" },
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "onboarding_checklist_template_tasks");
            migrationBuilder.DropTable(name: "payroll_opening_balances");
        }
    }
}
