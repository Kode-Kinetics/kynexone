using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddGradeLoanLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Slice L1 — grade-based loan limits. Additive only: every new column is nullable or defaulted,
            // so the previous release keeps running against this schema (N-1). btree_gist is needed by the
            // no-overlap EXCLUDE below; it is a trusted extension (PG 13+), creatable by the database owner.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS btree_gist;");

            migrationBuilder.AddColumn<string>(
                name: "entitlement_class",
                table: "pay_components",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<string>(
                name: "statutory_floor",
                table: "pay_components",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<string>(
                name: "entitlement_component_code",
                table: "loan_types",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "grade_limited",
                table: "loan_types",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "grade_entitlement_id",
                table: "employee_loans",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "grade_id_at_request",
                table: "employee_loans",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "grade_outstanding_cap",
                table: "employee_loans",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "grade_per_loan_cap",
                table: "employee_loans",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_grades_tenant_id_id",
                table: "grades",
                columns: new[] { "tenant_id", "id" });

            migrationBuilder.CreateTable(
                name: "grade_entitlements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    company_key = table.Column<Guid>(type: "uuid", nullable: false, computedColumnSql: "COALESCE(company_id, '00000000-0000-0000-0000-000000000000')", stored: true),
                    grade_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pay_component_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    entitlement_class = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    eligible = table.Column<bool>(type: "boolean", nullable: false),
                    value_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    rate = table.Column<decimal>(type: "numeric(9,4)", precision: 9, scale: 4, nullable: true),
                    max_outstanding_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    source_rule = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    effective_to = table.Column<DateOnly>(type: "date", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_grade_entitlements", x => x.id);
                    table.UniqueConstraint("AK_grade_entitlements_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_grade_entitlements__dates", "effective_to IS NULL OR effective_to >= effective_from");
                    table.CheckConstraint("ck_grade_entitlements__entitlement_class", "entitlement_class IN ('QiwaWage','Contractual','Facility')");
                    table.CheckConstraint("ck_grade_entitlements__ineligible_has_no_values", "eligible OR (amount IS NULL AND rate IS NULL AND max_outstanding_amount IS NULL)");
                    table.CheckConstraint("ck_grade_entitlements__outstanding_is_facility", "max_outstanding_amount IS NULL OR (entitlement_class = 'Facility' AND max_outstanding_amount >= 0)");
                    table.CheckConstraint("ck_grade_entitlements__value_shape", "(value_type = 'Amount' AND amount IS NOT NULL AND amount >= 0 AND rate IS NULL) OR (value_type IN ('MultipleOfBasic','MultipleOfGross') AND rate IS NOT NULL AND rate > 0 AND amount IS NULL) OR (value_type = 'EligibilityOnly' AND amount IS NULL AND rate IS NULL)");
                    table.CheckConstraint("ck_grade_entitlements__value_type", "value_type IN ('Amount','MultipleOfBasic','MultipleOfGross','EligibilityOnly')");
                    table.ForeignKey(
                        name: "FK_grade_entitlements_companies_company_id",
                        column: x => x.company_id,
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_grade_entitlements_grades_tenant_id_grade_id",
                        columns: x => new { x.tenant_id, x.grade_id },
                        principalTable: "grades",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_pay_components__entitlement_class",
                table: "pay_components",
                sql: "entitlement_class IN ('None','QiwaWage','Contractual','Facility')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_pay_components__statutory_floor",
                table: "pay_components",
                sql: "statutory_floor IN ('None','Housing','Transport','Medical','Art40')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_loan_types__grade_limited_has_component",
                table: "loan_types",
                sql: "NOT grade_limited OR entitlement_component_code IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_employee_loans_tenant_id_grade_entitlement_id",
                table: "employee_loans",
                columns: new[] { "tenant_id", "grade_entitlement_id" });

            migrationBuilder.CreateIndex(
                name: "IX_grade_entitlements_company_id",
                table: "grade_entitlements",
                column: "company_id");

            migrationBuilder.CreateIndex(
                name: "IX_grade_entitlements_tenant_id_company_id",
                table: "grade_entitlements",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_grade_entitlements_tenant_id_grade_id_pay_component_code_ef~",
                table: "grade_entitlements",
                columns: new[] { "tenant_id", "grade_id", "pay_component_code", "effective_from" },
                descending: new[] { false, false, false, true });

            migrationBuilder.AddForeignKey(
                name: "FK_employee_loans_grade_entitlements_tenant_id_grade_entitleme~",
                table: "employee_loans",
                columns: new[] { "tenant_id", "grade_entitlement_id" },
                principalTable: "grade_entitlements",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

            // EF cannot model EXCLUDE: one cell per (tenant, company scope, grade, component) per day, enforced
            // by the database. Frozen copy of GradeEntitlementSql.AddExclusion, which the Postgres test fixture
            // applies; GradeLoanLimitMigrationPostgresTests proves this migration produces that constraint.
            migrationBuilder.Sql(
                "ALTER TABLE grade_entitlements ADD CONSTRAINT ex_grade_entitlements__no_overlap EXCLUDE USING gist (" +
                "tenant_id WITH =, company_key WITH =, grade_id WITH =, pay_component_code WITH =, " +
                "daterange(effective_from, COALESCE(effective_to, 'infinity'::date), '[]') WITH &&);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Never roll back over evidence: a loan decided under a grade cell, or a published grid, is history.
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM grade_entitlements)
                        OR EXISTS (SELECT 1 FROM employee_loans WHERE grade_entitlement_id IS NOT NULL OR grade_id_at_request IS NOT NULL)
                        OR EXISTS (SELECT 1 FROM loan_types WHERE grade_limited)
                        OR EXISTS (SELECT 1 FROM pay_components WHERE entitlement_class <> 'None' OR statutory_floor <> 'None') THEN
                        RAISE EXCEPTION 'Grade loan limits or entitlement classifications exist. Preserve them and use a forward corrective migration.';
                    END IF;
                END $$;
                """);
            // btree_gist is deliberately left installed: dropping an extension is not this migration's call.
            migrationBuilder.DropForeignKey(
                name: "FK_employee_loans_grade_entitlements_tenant_id_grade_entitleme~",
                table: "employee_loans");

            migrationBuilder.DropTable(
                name: "grade_entitlements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_pay_components__entitlement_class",
                table: "pay_components");

            migrationBuilder.DropCheckConstraint(
                name: "ck_pay_components__statutory_floor",
                table: "pay_components");

            migrationBuilder.DropCheckConstraint(
                name: "ck_loan_types__grade_limited_has_component",
                table: "loan_types");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_grades_tenant_id_id",
                table: "grades");

            migrationBuilder.DropIndex(
                name: "IX_employee_loans_tenant_id_grade_entitlement_id",
                table: "employee_loans");

            migrationBuilder.DropColumn(
                name: "entitlement_class",
                table: "pay_components");

            migrationBuilder.DropColumn(
                name: "statutory_floor",
                table: "pay_components");

            migrationBuilder.DropColumn(
                name: "entitlement_component_code",
                table: "loan_types");

            migrationBuilder.DropColumn(
                name: "grade_limited",
                table: "loan_types");

            migrationBuilder.DropColumn(
                name: "grade_entitlement_id",
                table: "employee_loans");

            migrationBuilder.DropColumn(
                name: "grade_id_at_request",
                table: "employee_loans");

            migrationBuilder.DropColumn(
                name: "grade_outstanding_cap",
                table: "employee_loans");

            migrationBuilder.DropColumn(
                name: "grade_per_loan_cap",
                table: "employee_loans");
        }
    }
}
