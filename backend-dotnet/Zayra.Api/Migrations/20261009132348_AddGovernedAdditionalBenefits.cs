using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddGovernedAdditionalBenefits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "approval_request_id",
                table: "benefit_enrollments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "grant_reason",
                table: "benefit_enrollments",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "review_date",
                table: "benefit_enrollments",
                type: "date",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_benefit_enrollments_tenant_id_approval_request_id",
                table: "benefit_enrollments",
                columns: new[] { "tenant_id", "approval_request_id" },
                unique: true,
                filter: "approval_request_id IS NOT NULL");

            // The shared approval row is the submitted witness. Keep its terms and subject immutable
            // even for SQL writers; normal decision/version/current-step routing updates remain legal.
            if (ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
                migrationBuilder.Sql("""
                    CREATE FUNCTION guard_additional_benefit_approval_witness() RETURNS trigger
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
                    CREATE TRIGGER trg_approval_requests__additional_benefit_witness
                    BEFORE UPDATE OR DELETE ON approval_requests
                    FOR EACH ROW EXECUTE FUNCTION guard_additional_benefit_approval_witness();
                    """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
                migrationBuilder.Sql("""
                    DO $guard$
                    BEGIN
                        IF EXISTS (SELECT 1 FROM approval_requests WHERE lower(btrim(entity_name)) = 'benefitadditionalgrant')
                           OR EXISTS (SELECT 1 FROM benefit_enrollments WHERE approval_request_id IS NOT NULL
                               OR grant_reason IS NOT NULL OR review_date IS NOT NULL OR assignment_source = 'IndividualAdditional') THEN
                            RAISE EXCEPTION 'Cannot roll back governed additional benefits after requests or grants exist. Preserve the approval evidence and deploy a forward correction.';
                        END IF;
                    END;
                    $guard$;
                    DROP TRIGGER trg_approval_requests__additional_benefit_witness ON approval_requests;
                    DROP FUNCTION guard_additional_benefit_approval_witness();
                    """);

            migrationBuilder.DropIndex(
                name: "IX_benefit_enrollments_tenant_id_approval_request_id",
                table: "benefit_enrollments");

            migrationBuilder.DropColumn(
                name: "approval_request_id",
                table: "benefit_enrollments");

            migrationBuilder.DropColumn(
                name: "grant_reason",
                table: "benefit_enrollments");

            migrationBuilder.DropColumn(
                name: "review_date",
                table: "benefit_enrollments");
        }
    }
}
