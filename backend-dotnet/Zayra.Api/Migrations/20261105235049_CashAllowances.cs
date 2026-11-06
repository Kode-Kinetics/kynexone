using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class CashAllowances : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Expand-only: nullable columns, CHECKs and one new table. ADD COLUMN takes an ACCESS EXCLUSIVE lock that would block pay-component
            // and salary-change reads behind it: fail fast (55P03) and re-run when quiet. Nothing changes for a tenant until a customer adds a
            // cash allowance. (Wave D folded in: the Finance-confirmation and end-of-service category columns on pay_components, and the
            // consent reference on salary_change_requests. This migration has never been applied anywhere, so it is amended rather than followed.)
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");

            migrationBuilder.AddColumn<string>(
                name: "allowances_json",
                table: "salary_change_requests",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "eosb_exclusion_reason",
                table: "pay_components",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reduction_consent_reference",
                table: "salary_change_requests",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "eosb_exclusion_category",
                table: "pay_components",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "finance_confirmed_at_utc",
                table: "pay_components",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "finance_confirmed_by_user_id",
                table: "pay_components",
                type: "uuid",
                nullable: true);

            // Allowances that exist before Finance confirmation did are treated as confirmed (nothing is live; there is no confirming person to name).
            // None exist where this migration is first applied; it keeps a database that was built from the earlier shape of this change consistent.
            migrationBuilder.Sql(
                "UPDATE pay_components SET finance_confirmed_at_utc = COALESCE(updated_at_utc, created_at_utc) " +
                "WHERE code LIKE 'ALW!_%' ESCAPE '!' AND company_id IS NULL AND component_type = 'Earning' AND calc_method = 'GradePackage';");

            migrationBuilder.AddCheckConstraint(
                name: "ck_salary_change_requests__reduction_consent_reference",
                table: "salary_change_requests",
                sql: "reduction_consent_reference IS NULL OR length(trim(reduction_consent_reference)) > 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_pay_components__eosb_exclusion_category",
                table: "pay_components",
                sql: "eosb_exclusion_category IS NULL OR (eosb_exclusion_category IN ('Reimbursement','Discretionary','NonRecurring','Other') AND NOT eosb_included)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_pay_components__eosb_exclusion_other_note",
                table: "pay_components",
                sql: "eosb_exclusion_category <> 'Other' OR (eosb_exclusion_reason IS NOT NULL AND length(trim(eosb_exclusion_reason)) > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_pay_components__finance_confirmation",
                table: "pay_components",
                sql: "finance_confirmed_by_user_id IS NULL OR finance_confirmed_at_utc IS NOT NULL");

            migrationBuilder.CreateTable(
                name: "employee_salary_allowances",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    salary_structure_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    basis = table.Column<string>(type: "character varying(14)", maxLength: 14, nullable: false),
                    rate = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: true),
                    amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employee_salary_allowances", x => x.id);
                    table.CheckConstraint("ck_employee_salary_allowances__amount_positive", "amount > 0");
                    table.CheckConstraint("ck_employee_salary_allowances__basis", "basis IN ('Amount','PercentOfBasic')");
                    table.CheckConstraint("ck_employee_salary_allowances__rate_pair", "(basis = 'PercentOfBasic') = (rate IS NOT NULL)");
                    table.CheckConstraint("ck_employee_salary_allowances__rate_range", "rate IS NULL OR (rate > 0 AND rate <= 1)");
                    table.ForeignKey(
                        name: "FK_employee_salary_allowances_employee_salary_structures_tenan~",
                        columns: x => new { x.tenant_id, x.salary_structure_id },
                        principalTable: "employee_salary_structures",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_employee_salary_allowances_tenant_id_salary_structure_id_co~",
                table: "employee_salary_allowances",
                columns: new[] { "tenant_id", "salary_structure_id", "code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "employee_salary_allowances");

            migrationBuilder.DropCheckConstraint(name: "ck_salary_change_requests__reduction_consent_reference", table: "salary_change_requests");
            migrationBuilder.DropCheckConstraint(name: "ck_pay_components__eosb_exclusion_category", table: "pay_components");
            migrationBuilder.DropCheckConstraint(name: "ck_pay_components__eosb_exclusion_other_note", table: "pay_components");
            migrationBuilder.DropCheckConstraint(name: "ck_pay_components__finance_confirmation", table: "pay_components");

            migrationBuilder.DropColumn(
                name: "reduction_consent_reference",
                table: "salary_change_requests");

            migrationBuilder.DropColumn(
                name: "eosb_exclusion_category",
                table: "pay_components");

            migrationBuilder.DropColumn(
                name: "finance_confirmed_at_utc",
                table: "pay_components");

            migrationBuilder.DropColumn(
                name: "finance_confirmed_by_user_id",
                table: "pay_components");

            migrationBuilder.DropColumn(
                name: "allowances_json",
                table: "salary_change_requests");

            migrationBuilder.DropColumn(
                name: "eosb_exclusion_reason",
                table: "pay_components");
        }
    }
}
