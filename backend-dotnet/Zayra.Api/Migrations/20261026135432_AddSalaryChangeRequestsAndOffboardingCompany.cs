using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSalaryChangeRequestsAndOffboardingCompany : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "employee_offboardings",
                type: "uuid",
                nullable: true);

            // Existing separations take their company from the employee they belong to (the employee's CURRENT company, so a
            // later transfer re-homes the history; a hard-deleted employee, or one with no company, leaves the row NULL).
            //
            // OPS NOTE. A separation with a NULL company is visible to group-scope users only (operational company filter), so a
            // company administrator will not see it after this migration. That is fail-closed, not data loss. The count is logged
            // below (RAISE NOTICE, visible in the migration output) per tenant. To fix a row, set employee_offboardings.company_id
            // to its employee's company; do not delete it.
            migrationBuilder.Sql(@"UPDATE employee_offboardings o SET company_id = e.company_id
FROM employees e WHERE o.employee_id = e.id AND o.tenant_id = e.tenant_id AND o.company_id IS NULL AND e.company_id IS NOT NULL;");
            migrationBuilder.Sql(@"DO $$
DECLARE r record;
BEGIN
  FOR r IN SELECT tenant_id, count(*) AS n FROM employee_offboardings WHERE company_id IS NULL GROUP BY tenant_id LOOP
    RAISE NOTICE 'employee_offboardings backfill: tenant % has % separation(s) with no company (visible to group scope only)', r.tenant_id, r.n;
  END LOOP;
END $$;");

            migrationBuilder.CreateTable(
                name: "salary_change_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    employee_code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    employee_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    salary_structure_id = table.Column<Guid>(type: "uuid", nullable: false),
                    basic_salary = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    housing_allowance = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    transport_allowance = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    food_allowance = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    mobile_allowance = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    other_allowance = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    fixed_deduction = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    effective_date = table.Column<DateOnly>(type: "date", nullable: false),
                    currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    previous_basic_salary = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    requested_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    requested_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    decided_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    decided_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    decision_note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    applied_assignment_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_salary_change_requests", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_employee_offboardings_tenant_id_company_id",
                table: "employee_offboardings",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_salary_change_requests_tenant_id_batch_id",
                table: "salary_change_requests",
                columns: new[] { "tenant_id", "batch_id" });

            migrationBuilder.CreateIndex(
                name: "IX_salary_change_requests_tenant_id_company_id",
                table: "salary_change_requests",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_salary_change_requests_tenant_id_employee_id_effective_date",
                table: "salary_change_requests",
                columns: new[] { "tenant_id", "employee_id", "effective_date" });

            migrationBuilder.CreateIndex(
                name: "IX_salary_change_requests_tenant_id_status_requested_at_utc",
                table: "salary_change_requests",
                columns: new[] { "tenant_id", "status", "requested_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "salary_change_requests");

            migrationBuilder.DropIndex(
                name: "IX_employee_offboardings_tenant_id_company_id",
                table: "employee_offboardings");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "employee_offboardings");
        }
    }
}
