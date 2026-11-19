using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddBenefitUsageRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "benefit_usage_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    entitlement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    benefit_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    period_start = table.Column<DateOnly>(type: "date", nullable: false),
                    period_end = table.Column<DateOnly>(type: "date", nullable: false),
                    usage_date = table.Column<DateOnly>(type: "date", nullable: false),
                    dependant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    units = table.Column<short>(type: "smallint", nullable: true),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    academic_year = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    provider_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    evidence_reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    status = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    decided_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    decision_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_benefit_usage_requests", x => x.id);
                    table.CheckConstraint("ck_benefit_usage_requests__currency_iff_amount", "(amount IS NULL) = (currency IS NULL)");
                    table.CheckConstraint("ck_benefit_usage_requests__decided", "(status = 'Pending') = (decided_by_user_id IS NULL) AND (decided_by_user_id IS NULL) = (decided_at_utc IS NULL)");
                    table.CheckConstraint("ck_benefit_usage_requests__measure", "(units IS NULL) <> (amount IS NULL) AND (units IS NULL OR units > 0) AND (amount IS NULL OR amount > 0)");
                    table.CheckConstraint("ck_benefit_usage_requests__period", "period_end >= period_start");
                    table.CheckConstraint("ck_benefit_usage_requests__reason", "status NOT IN ('Rejected','Cancelled') OR (decision_reason IS NOT NULL AND length(trim(decision_reason)) > 0)");
                    table.CheckConstraint("ck_benefit_usage_requests__status", "status IN ('Pending','Approved','Rejected','Cancelled')");
                    table.ForeignKey(
                        name: "FK_benefit_usage_requests_companies_company_id",
                        column: x => x.company_id,
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_benefit_usage_requests_employee_dependents_dependant_id",
                        column: x => x.dependant_id,
                        principalTable: "employee_dependents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_benefit_usage_requests_employee_entitlements_tenant_id_enti~",
                        columns: x => new { x.tenant_id, x.entitlement_id },
                        principalTable: "employee_entitlements",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_benefit_usage_requests_employees_employee_id",
                        column: x => x.employee_id,
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_benefit_usage_requests__company_status",
                table: "benefit_usage_requests",
                columns: new[] { "tenant_id", "company_id", "status", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_benefit_usage_requests__employee_benefit_period",
                table: "benefit_usage_requests",
                columns: new[] { "tenant_id", "employee_id", "benefit_code", "period_start" });

            migrationBuilder.CreateIndex(
                name: "IX_benefit_usage_requests_company_id",
                table: "benefit_usage_requests",
                column: "company_id");

            migrationBuilder.CreateIndex(
                name: "IX_benefit_usage_requests_dependant_id",
                table: "benefit_usage_requests",
                column: "dependant_id");

            migrationBuilder.CreateIndex(
                name: "IX_benefit_usage_requests_employee_id",
                table: "benefit_usage_requests",
                column: "employee_id");

            migrationBuilder.CreateIndex(
                name: "IX_benefit_usage_requests_tenant_id_company_id",
                table: "benefit_usage_requests",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_benefit_usage_requests_tenant_id_entitlement_id",
                table: "benefit_usage_requests",
                columns: new[] { "tenant_id", "entitlement_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "benefit_usage_requests");
        }
    }
}
