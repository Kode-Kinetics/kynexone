using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddGradeBenefitDefaultsAndExceptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "classification",
                table: "benefit_plans",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Discretionary");

            migrationBuilder.AddColumn<string>(
                name: "assignment_source",
                table: "benefit_enrollments",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Manual");

            migrationBuilder.AddColumn<Guid>(
                name: "eligibility_rule_id",
                table: "benefit_enrollments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "eligibility_snapshot_json",
                table: "benefit_enrollments",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "entitlement_tier",
                table: "benefit_enrollments",
                type: "character varying(120)",
                maxLength: 120,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "exception_reason",
                table: "benefit_enrollments",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "has_exception",
                table: "benefit_enrollments",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "limit_period",
                table: "benefit_enrollments",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "maximum_benefit_amount",
                table: "benefit_enrollments",
                type: "numeric(14,2)",
                precision: 14,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "original_enrollment_id",
                table: "benefit_enrollments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "requested_benefit_amount",
                table: "benefit_enrollments",
                type: "numeric(14,2)",
                precision: 14,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "custom_criteria_note",
                table: "benefit_eligibility_rules",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "grade_match_mode",
                table: "benefit_eligibility_rules",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Exact");

            migrationBuilder.AddColumn<string>(
                name: "limit_period",
                table: "benefit_eligibility_rules",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "PerEnrollment");

            migrationBuilder.AddColumn<decimal>(
                name: "max_benefit_amount",
                table: "benefit_eligibility_rules",
                type: "numeric(14,2)",
                precision: 14,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "minimum_service_months",
                table: "benefit_eligibility_rules",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "require_probation_completed",
                table: "benefit_eligibility_rules",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "tier_name",
                table: "benefit_eligibility_rules",
                type: "character varying(120)",
                maxLength: 120,
                nullable: false,
                defaultValue: "");

            // Existing grade rules remain exact matches; only their human-readable label is populated.
            // Existing enrollments remain Manual. This migration never enrolls existing staff or creates money records.
            migrationBuilder.Sql("""
                UPDATE benefit_eligibility_rules r
                SET tier_name = LEFT(g.name, 120)
                FROM grades g
                WHERE r.tenant_id = g.tenant_id AND r.grade_id = g.id AND r.tier_name = '';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM benefit_enrollments WHERE assignment_source <> 'Manual' OR has_exception
                        OR eligibility_rule_id IS NOT NULL OR eligibility_snapshot_json <> '{}'::jsonb) THEN
                        RAISE EXCEPTION 'Benefit assignment or exception provenance exists. Preserve it before rolling back this migration.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropColumn(
                name: "classification",
                table: "benefit_plans");

            migrationBuilder.DropColumn(
                name: "assignment_source",
                table: "benefit_enrollments");

            migrationBuilder.DropColumn(
                name: "eligibility_rule_id",
                table: "benefit_enrollments");

            migrationBuilder.DropColumn(
                name: "eligibility_snapshot_json",
                table: "benefit_enrollments");

            migrationBuilder.DropColumn(
                name: "entitlement_tier",
                table: "benefit_enrollments");

            migrationBuilder.DropColumn(
                name: "exception_reason",
                table: "benefit_enrollments");

            migrationBuilder.DropColumn(
                name: "has_exception",
                table: "benefit_enrollments");

            migrationBuilder.DropColumn(
                name: "limit_period",
                table: "benefit_enrollments");

            migrationBuilder.DropColumn(
                name: "maximum_benefit_amount",
                table: "benefit_enrollments");

            migrationBuilder.DropColumn(
                name: "original_enrollment_id",
                table: "benefit_enrollments");

            migrationBuilder.DropColumn(
                name: "requested_benefit_amount",
                table: "benefit_enrollments");

            migrationBuilder.DropColumn(
                name: "custom_criteria_note",
                table: "benefit_eligibility_rules");

            migrationBuilder.DropColumn(
                name: "grade_match_mode",
                table: "benefit_eligibility_rules");

            migrationBuilder.DropColumn(
                name: "limit_period",
                table: "benefit_eligibility_rules");

            migrationBuilder.DropColumn(
                name: "max_benefit_amount",
                table: "benefit_eligibility_rules");

            migrationBuilder.DropColumn(
                name: "minimum_service_months",
                table: "benefit_eligibility_rules");

            migrationBuilder.DropColumn(
                name: "require_probation_completed",
                table: "benefit_eligibility_rules");

            migrationBuilder.DropColumn(
                name: "tier_name",
                table: "benefit_eligibility_rules");
        }
    }
}
