using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Zayra.Api.Data;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    [Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof(ZayraDbContext))]
    [Migration("20260713031500_AddBenefitsCompensationFoundation")]
    public partial class AddBenefitsCompensationFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "benefit_plans",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    plan_type = table.Column<string>(type: "text", nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    effective_to = table.Column<DateOnly>(type: "date", nullable: true),
                    requires_enrollment = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table => table.PrimaryKey("PK_benefit_plans", x => x.id));

            migrationBuilder.CreateTable(
                name: "benefit_eligibility_rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    benefit_plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    grade_id = table.Column<Guid>(type: "uuid", nullable: true),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    effective_to = table.Column<DateOnly>(type: "date", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table => table.PrimaryKey("PK_benefit_eligibility_rules", x => x.id));

            migrationBuilder.CreateTable(
                name: "benefit_enrollments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    benefit_plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    employee_name = table.Column<string>(type: "text", nullable: false),
                    coverage_tier = table.Column<string>(type: "text", nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    effective_to = table.Column<DateOnly>(type: "date", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table => table.PrimaryKey("PK_benefit_enrollments", x => x.id));

            migrationBuilder.CreateTable(
                name: "benefit_contributions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    benefit_enrollment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    benefit_plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    employee_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    employer_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    frequency = table.Column<string>(type: "text", nullable: false),
                    payroll_component_code = table.Column<string>(type: "text", nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    effective_to = table.Column<DateOnly>(type: "date", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table => table.PrimaryKey("PK_benefit_contributions", x => x.id));

            migrationBuilder.CreateTable(
                name: "benefit_payroll_deduction_links",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    benefit_enrollment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    benefit_contribution_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payroll_deduction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payroll_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    linked_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table => table.PrimaryKey("PK_benefit_payroll_deduction_links", x => x.id));

            migrationBuilder.CreateIndex(name: "IX_benefit_plans_tenant_id_company_id_code", table: "benefit_plans", columns: new[] { "tenant_id", "company_id", "code" }, unique: true);
            migrationBuilder.CreateIndex(name: "IX_benefit_plans_tenant_id_company_id_is_active", table: "benefit_plans", columns: new[] { "tenant_id", "company_id", "is_active" });
            migrationBuilder.CreateIndex(name: "IX_benefit_eligibility_rules_tenant_id_benefit_plan_id_company_id_grade_id_is_active", table: "benefit_eligibility_rules", columns: new[] { "tenant_id", "benefit_plan_id", "company_id", "grade_id", "is_active" });
            migrationBuilder.CreateIndex(name: "IX_benefit_enrollments_tenant_id_benefit_plan_id_employee_id_status", table: "benefit_enrollments", columns: new[] { "tenant_id", "benefit_plan_id", "employee_id", "status" });
            migrationBuilder.CreateIndex(name: "IX_benefit_enrollments_tenant_id_employee_id_effective_from", table: "benefit_enrollments", columns: new[] { "tenant_id", "employee_id", "effective_from" });
            migrationBuilder.CreateIndex(name: "IX_benefit_contributions_tenant_id_benefit_enrollment_id_is_active", table: "benefit_contributions", columns: new[] { "tenant_id", "benefit_enrollment_id", "is_active" });
            migrationBuilder.CreateIndex(name: "IX_benefit_contributions_tenant_id_employee_id_effective_from", table: "benefit_contributions", columns: new[] { "tenant_id", "employee_id", "effective_from" });
            migrationBuilder.CreateIndex(name: "IX_benefit_payroll_deduction_links_tenant_id_benefit_enrollment_id_payroll_run_id", table: "benefit_payroll_deduction_links", columns: new[] { "tenant_id", "benefit_enrollment_id", "payroll_run_id" });
            migrationBuilder.CreateIndex(name: "IX_benefit_payroll_deduction_links_tenant_id_payroll_deduction_id", table: "benefit_payroll_deduction_links", columns: new[] { "tenant_id", "payroll_deduction_id" }, unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "benefit_payroll_deduction_links");
            migrationBuilder.DropTable(name: "benefit_contributions");
            migrationBuilder.DropTable(name: "benefit_enrollments");
            migrationBuilder.DropTable(name: "benefit_eligibility_rules");
            migrationBuilder.DropTable(name: "benefit_plans");
        }
    }
}
