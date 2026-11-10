using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class BenefitDefinition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Expand-only (feature benefit_definition_v1, default OFF): four nullable columns, no default and no rewrite, CHECKs no existing row violates
            // (every new column is NULL on every row written before), one FK to the legacy plan and a partial unique index. Nothing changes for a tenant until
            // the feature is switched on. ADD COLUMN takes an ACCESS EXCLUSIVE lock that pay-component reads queue behind: fail fast (55P03) and re-run when quiet.
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");

            migrationBuilder.AddColumn<string>(
                name: "benefit_category",
                table: "pay_components",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "policy_class",
                table: "pay_components",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "requires_enrolment",
                table: "pay_components",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "source_benefit_plan_id",
                table: "pay_components",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_pay_components_source_benefit_plan_id",
                table: "pay_components",
                column: "source_benefit_plan_id");

            migrationBuilder.CreateIndex(
                name: "IX_pay_components_tenant_id_source_benefit_plan_id",
                table: "pay_components",
                columns: new[] { "tenant_id", "source_benefit_plan_id" },
                unique: true,
                filter: "source_benefit_plan_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_pay_components__benefit_category_not_blank",
                table: "pay_components",
                sql: "benefit_category IS NULL OR length(trim(benefit_category)) > 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_pay_components__benefit_fields_custom_only",
                table: "pay_components",
                sql: "(benefit_category IS NULL AND policy_class IS NULL AND requires_enrolment IS NULL AND source_benefit_plan_id IS NULL) OR code LIKE 'BEN!_%' ESCAPE '!' OR code LIKE 'ALW!_%' ESCAPE '!'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_pay_components__policy_class",
                table: "pay_components",
                sql: "policy_class IS NULL OR policy_class IN ('Contractual','Discretionary')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_pay_components__requires_enrolment_unsupported",
                table: "pay_components",
                sql: "requires_enrolment IS NULL OR requires_enrolment = false");

            migrationBuilder.AddForeignKey(
                name: "FK_pay_components_benefit_plans_source_benefit_plan_id",
                table: "pay_components",
                column: "source_benefit_plan_id",
                principalTable: "benefit_plans",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_pay_components_benefit_plans_source_benefit_plan_id",
                table: "pay_components");

            migrationBuilder.DropIndex(
                name: "IX_pay_components_source_benefit_plan_id",
                table: "pay_components");

            migrationBuilder.DropIndex(
                name: "IX_pay_components_tenant_id_source_benefit_plan_id",
                table: "pay_components");

            migrationBuilder.DropCheckConstraint(
                name: "ck_pay_components__benefit_category_not_blank",
                table: "pay_components");

            migrationBuilder.DropCheckConstraint(
                name: "ck_pay_components__benefit_fields_custom_only",
                table: "pay_components");

            migrationBuilder.DropCheckConstraint(
                name: "ck_pay_components__policy_class",
                table: "pay_components");

            migrationBuilder.DropCheckConstraint(
                name: "ck_pay_components__requires_enrolment_unsupported",
                table: "pay_components");

            migrationBuilder.DropColumn(
                name: "benefit_category",
                table: "pay_components");

            migrationBuilder.DropColumn(
                name: "policy_class",
                table: "pay_components");

            migrationBuilder.DropColumn(
                name: "requires_enrolment",
                table: "pay_components");

            migrationBuilder.DropColumn(
                name: "source_benefit_plan_id",
                table: "pay_components");
        }
    }
}
