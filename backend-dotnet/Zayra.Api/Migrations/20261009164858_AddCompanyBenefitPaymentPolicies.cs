using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyBenefitPaymentPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "source_snapshot_json",
                table: "payroll_adjustments",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "payment_policy_json",
                table: "benefit_plans",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<int>(
                name: "policy_version",
                table: "benefit_plans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "payment_policy_snapshot_json",
                table: "benefit_enrollments",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.CreateIndex(
                name: "IX_approval_requests_tenant_id_entity_name_requested_for_emplo~",
                table: "approval_requests",
                columns: new[] { "tenant_id", "entity_name", "requested_for_employee_id", "status" });

            if (ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
                migrationBuilder.Sql("""
                    CREATE OR REPLACE FUNCTION guard_additional_benefit_approval_witness() RETURNS trigger
                    LANGUAGE plpgsql AS $function$
                    BEGIN
                        IF TG_OP = 'DELETE' THEN
                            IF lower(btrim(OLD.entity_name)) IN ('benefitadditionalgrant', 'benefitclaim') THEN
                                RAISE EXCEPTION 'Benefit approval evidence cannot be deleted; withdraw a pending request instead.' USING ERRCODE = '23514';
                            END IF;
                            RETURN OLD;
                        END IF;
                        IF lower(btrim(OLD.entity_name)) IN ('benefitadditionalgrant', 'benefitclaim')
                           OR lower(btrim(NEW.entity_name)) IN ('benefitadditionalgrant', 'benefitclaim') THEN
                            IF ROW(NEW.tenant_id, NEW.company_id, NEW.entity_name, NEW.entity_id, NEW.workflow_id,
                                   NEW.requested_for_employee_id, NEW.requested_by_user_id, NEW.payload, NEW.payload_sha256)
                               IS DISTINCT FROM
                               ROW(OLD.tenant_id, OLD.company_id, OLD.entity_name, OLD.entity_id, OLD.workflow_id,
                                   OLD.requested_for_employee_id, OLD.requested_by_user_id, OLD.payload, OLD.payload_sha256) THEN
                                RAISE EXCEPTION 'Submitted benefit terms, subject and workflow are immutable; withdraw and submit a new request.' USING ERRCODE = '23514';
                            END IF;
                        END IF;
                        RETURN NEW;
                    END;
                    $function$;

                    CREATE FUNCTION guard_benefit_enrollment_payment_policy() RETURNS trigger
                    LANGUAGE plpgsql AS $function$
                    BEGIN
                        IF NEW.payment_policy_snapshot_json IS DISTINCT FROM OLD.payment_policy_snapshot_json THEN
                            RAISE EXCEPTION 'Agreed benefit payment policy is immutable; create a dated successor.' USING ERRCODE = '23514';
                        END IF;
                        RETURN NEW;
                    END;
                    $function$;
                    CREATE TRIGGER trg_benefit_enrollments__payment_policy
                    BEFORE UPDATE ON benefit_enrollments FOR EACH ROW
                    EXECUTE FUNCTION guard_benefit_enrollment_payment_policy();

                    CREATE FUNCTION guard_benefit_payroll_authority() RETURNS trigger
                    LANGUAGE plpgsql AS $function$
                    BEGIN
                        IF TG_OP = 'DELETE' THEN
                            IF OLD.source_type IN ('BenefitRecurring', 'BenefitClaim') THEN
                                RAISE EXCEPTION 'Benefit payment authority cannot be deleted.' USING ERRCODE = '23514';
                            END IF;
                            RETURN OLD;
                        END IF;
                        IF OLD.source_type IN ('BenefitRecurring', 'BenefitClaim')
                           OR NEW.source_type IN ('BenefitRecurring', 'BenefitClaim') THEN
                            IF ROW(NEW.tenant_id, NEW.employee_id, NEW.source_type, NEW.source_id,
                                   NEW.source_snapshot_json, NEW.amount, NEW.adjustment_type, NEW.reason)
                               IS DISTINCT FROM
                               ROW(OLD.tenant_id, OLD.employee_id, OLD.source_type, OLD.source_id,
                                   OLD.source_snapshot_json, OLD.amount, OLD.adjustment_type, OLD.reason) THEN
                                RAISE EXCEPTION 'Benefit payment authority is immutable; preserve its source and history.' USING ERRCODE = '23514';
                            END IF;
                            IF NEW.payroll_run_id IS DISTINCT FROM OLD.payroll_run_id AND NOT EXISTS (
                                SELECT 1 FROM payroll_runs old_run JOIN payroll_runs new_run
                                  ON new_run.id = NEW.payroll_run_id AND new_run.tenant_id = OLD.tenant_id
                                  AND new_run.company_id = old_run.company_id
                                WHERE old_run.id = OLD.payroll_run_id AND old_run.tenant_id = OLD.tenant_id
                                  AND old_run.status = 'Voided' AND NEW.status = 'Approved'
                            ) THEN
                                RAISE EXCEPTION 'Only voided benefit payments can be assigned to a replacement payroll in the same company.' USING ERRCODE = '23514';
                            END IF;
                        END IF;
                        RETURN NEW;
                    END;
                    $function$;
                    CREATE TRIGGER trg_payroll_adjustments__benefit_authority
                    BEFORE UPDATE OR DELETE ON payroll_adjustments FOR EACH ROW
                    EXECUTE FUNCTION guard_benefit_payroll_authority();
                    """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
                migrationBuilder.Sql("""
                    DO $guard$
                    BEGIN
                        IF EXISTS (SELECT 1 FROM benefit_plans WHERE policy_version > 0)
                           OR EXISTS (SELECT 1 FROM benefit_enrollments WHERE payment_policy_snapshot_json <> '{}'::jsonb)
                           OR EXISTS (SELECT 1 FROM approval_requests WHERE lower(btrim(entity_name)) = 'benefitclaim')
                           OR EXISTS (SELECT 1 FROM payroll_adjustments WHERE source_type IN ('BenefitRecurring', 'BenefitClaim')) THEN
                            RAISE EXCEPTION 'Cannot roll back used benefit policies or payment evidence. Deploy a forward correction.';
                        END IF;
                    END;
                    $guard$;
                    DROP TRIGGER trg_benefit_enrollments__payment_policy ON benefit_enrollments;
                    DROP FUNCTION guard_benefit_enrollment_payment_policy();
                    DROP TRIGGER trg_payroll_adjustments__benefit_authority ON payroll_adjustments;
                    DROP FUNCTION guard_benefit_payroll_authority();
                    CREATE OR REPLACE FUNCTION guard_additional_benefit_approval_witness() RETURNS trigger
                    LANGUAGE plpgsql AS $function$
                    BEGIN
                        IF TG_OP = 'DELETE' THEN
                            IF lower(btrim(OLD.entity_name)) = 'benefitadditionalgrant' THEN
                                RAISE EXCEPTION 'Additional benefit approval evidence cannot be deleted; withdraw a pending request instead.' USING ERRCODE = '23514';
                            END IF;
                            RETURN OLD;
                        END IF;
                        IF lower(btrim(OLD.entity_name)) = 'benefitadditionalgrant'
                           OR lower(btrim(NEW.entity_name)) = 'benefitadditionalgrant' THEN
                            IF ROW(NEW.tenant_id, NEW.company_id, NEW.entity_name, NEW.entity_id, NEW.workflow_id,
                                   NEW.requested_for_employee_id, NEW.requested_by_user_id, NEW.payload, NEW.payload_sha256)
                               IS DISTINCT FROM
                               ROW(OLD.tenant_id, OLD.company_id, OLD.entity_name, OLD.entity_id, OLD.workflow_id,
                                   OLD.requested_for_employee_id, OLD.requested_by_user_id, OLD.payload, OLD.payload_sha256) THEN
                                RAISE EXCEPTION 'Submitted additional benefit terms, subject and workflow are immutable; withdraw and submit a new request.' USING ERRCODE = '23514';
                            END IF;
                        END IF;
                        RETURN NEW;
                    END;
                    $function$;
                    """);

            migrationBuilder.DropIndex(
                name: "IX_approval_requests_tenant_id_entity_name_requested_for_emplo~",
                table: "approval_requests");

            migrationBuilder.DropColumn(
                name: "source_snapshot_json",
                table: "payroll_adjustments");

            migrationBuilder.DropColumn(
                name: "payment_policy_json",
                table: "benefit_plans");

            migrationBuilder.DropColumn(
                name: "policy_version",
                table: "benefit_plans");

            migrationBuilder.DropColumn(
                name: "payment_policy_snapshot_json",
                table: "benefit_enrollments");
        }
    }
}
