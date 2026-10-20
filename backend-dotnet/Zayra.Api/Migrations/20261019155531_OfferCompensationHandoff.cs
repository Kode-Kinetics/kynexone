using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class OfferCompensationHandoff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(name: "offer_compensation_placement_company_id", table: "employee_drafts", type: "uuid", nullable: true);
            migrationBuilder.CreateIndex(name: "IX_employee_drafts_tenant_id_offer_compensation_placement_comp~", table: "employee_drafts",
                columns: new[] { "tenant_id", "offer_compensation_placement_company_id" });
            migrationBuilder.AddForeignKey(name: "FK_employee_drafts_companies_tenant_id_offer_compensation_plac~", table: "employee_drafts",
                columns: new[] { "tenant_id", "offer_compensation_placement_company_id" }, principalTable: "companies",
                principalColumns: new[] { "tenant_id", "id" }, onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddColumn<Guid>(
                name: "accepted_offer_company_id",
                table: "employee_drafts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "accepted_offer_compensation_json",
                table: "employee_drafts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "accepted_offer_id",
                table: "employee_drafts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "offer_compensation_confirmed_at_utc",
                table: "employee_drafts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "offer_compensation_confirmed_by_user_id",
                table: "employee_drafts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "offer_compensation_confirmed_fingerprint",
                table: "employee_drafts",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "offer_compensation_dispositions_json",
                table: "employee_drafts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "offer_compensation_version",
                table: "employee_drafts",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateIndex(
                name: "IX_employee_drafts_tenant_id_accepted_offer_company_id",
                table: "employee_drafts",
                columns: new[] { "tenant_id", "accepted_offer_company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_drafts_tenant_id_accepted_offer_id",
                table: "employee_drafts",
                columns: new[] { "tenant_id", "accepted_offer_id" });

            migrationBuilder.AddForeignKey(
                name: "FK_employee_drafts_companies_tenant_id_accepted_offer_company_~",
                table: "employee_drafts",
                columns: new[] { "tenant_id", "accepted_offer_company_id" },
                principalTable: "companies",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);
            // Nullable legacy source companies cannot be alternate keys in EF. PostgreSQL's
            // scoped parent FK and explicit NULL-equality insert guard protect that boundary.
            migrationBuilder.Sql("""
                ALTER TABLE employee_drafts ADD CONSTRAINT fk_draft_accepted_offer_company
                  FOREIGN KEY (tenant_id, accepted_offer_company_id, accepted_offer_id)
                  REFERENCES offer_letters (tenant_id, company_id, id) ON DELETE RESTRICT;
                CREATE FUNCTION guard_employee_offer_handoff() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF TG_OP <> 'INSERT' THEN
                    IF current_setting('app.offer_lifecycle_erasure', true) = OLD.tenant_id::text
                      AND EXISTS (SELECT 1 FROM tenants WHERE id = OLD.tenant_id AND NOT is_active AND slug LIKE '%__deleted_%') THEN
                      IF TG_OP = 'DELETE' THEN RETURN OLD; ELSE RETURN NEW; END IF;
                    END IF;
                    IF TG_OP = 'DELETE' THEN
                      IF OLD.accepted_offer_id IS NOT NULL THEN RAISE EXCEPTION 'offer_handoff_immutable: accepted draft history is retained'; END IF;
                      RETURN OLD;
                    END IF;
                    IF ROW(OLD.tenant_id, OLD.accepted_offer_id, OLD.accepted_offer_company_id, OLD.accepted_offer_compensation_json)
                      IS DISTINCT FROM ROW(NEW.tenant_id, NEW.accepted_offer_id, NEW.accepted_offer_company_id, NEW.accepted_offer_compensation_json) THEN
                      RAISE EXCEPTION 'offer_handoff_immutable: accepted source cannot be rewritten';
                    END IF;
                    IF OLD.offer_compensation_placement_company_id IS DISTINCT FROM NEW.offer_compensation_placement_company_id THEN
                      IF OLD.offer_compensation_placement_company_id IS NOT NULL OR NEW.offer_compensation_placement_company_id IS NULL
                        OR NEW.accepted_offer_id IS NULL OR NEW.accepted_offer_company_id IS NOT NULL
                        OR NEW.accepted_offer_compensation_json IS NULL
                        OR NEW.offer_compensation_confirmed_at_utc IS NULL OR NEW.offer_compensation_confirmed_by_user_id IS NULL
                        OR NEW.offer_compensation_confirmed_fingerprint IS NULL OR NEW.offer_compensation_dispositions_json IS NULL
                        OR (NEW.accepted_offer_compensation_json::jsonb->>'schemaVersion') IS DISTINCT FROM '1'
                        OR (NEW.accepted_offer_compensation_json::jsonb->>'policySource') IS DISTINCT FROM 'LegacyTenantFallback'
                        OR jsonb_array_length(NEW.accepted_offer_compensation_json::jsonb->'components') IS DISTINCT FROM 4
                        OR (SELECT count(DISTINCT r->>'code') FROM jsonb_array_elements(NEW.accepted_offer_compensation_json::jsonb->'components') r
                          WHERE r->>'kind'='Cash' AND r->>'frequency'='Monthly' AND r->>'mappingSource'='LegacyFourField'
                            AND r->>'structureField' = CASE r->>'code' WHEN 'BASIC' THEN 'BasicSalary' WHEN 'HOUSING' THEN 'HousingAllowance'
                              WHEN 'TRANSPORT' THEN 'TransportAllowance' WHEN 'OTHER_ALLOWANCES' THEN 'OtherAllowance' END) <> 4 THEN
                        RAISE EXCEPTION 'offer_handoff_immutable: legacy placement can only be bound once during explicit confirmation';
                      END IF;
                    END IF;
                    RETURN NEW;
                  END IF;
                  IF NEW.offer_compensation_placement_company_id IS NOT NULL THEN
                    RAISE EXCEPTION 'offer_handoff_immutable: placement must be confirmed after acceptance';
                  END IF;
                  IF NEW.accepted_offer_id IS NULL THEN
                    IF NEW.accepted_offer_company_id IS NOT NULL OR NEW.accepted_offer_compensation_json IS NOT NULL THEN
                      RAISE EXCEPTION 'offer_handoff_source_invalid: a source offer is required';
                    END IF;
                  ELSIF NOT EXISTS (SELECT 1 FROM offer_letters o WHERE o.id = NEW.accepted_offer_id AND o.tenant_id = NEW.tenant_id
                    AND o.company_id IS NOT DISTINCT FROM NEW.accepted_offer_company_id AND o.status = 'Accepted'
                    AND o.compensation_snapshot_json IS NOT DISTINCT FROM NEW.accepted_offer_compensation_json) THEN
                    RAISE EXCEPTION 'offer_handoff_source_invalid: source must equal the accepted scoped offer';
                  END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER employee_offer_handoff_guard BEFORE INSERT OR UPDATE OR DELETE ON employee_drafts
                  FOR EACH ROW EXECUTE FUNCTION guard_employee_offer_handoff();
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_employee_drafts_offer_letters_tenant_id_accepted_offer_id",
                table: "employee_drafts",
                columns: new[] { "tenant_id", "accepted_offer_id" },
                principalTable: "offer_letters",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                  IF EXISTS (SELECT 1 FROM employee_drafts WHERE accepted_offer_id IS NOT NULL
                    OR accepted_offer_compensation_json IS NOT NULL OR offer_compensation_dispositions_json IS NOT NULL
                    OR offer_compensation_placement_company_id IS NOT NULL) THEN
                    RAISE EXCEPTION 'offer_handoff_retained: cannot downgrade while accepted compensation obligations remain';
                  END IF;
                END $$;
                DROP TRIGGER employee_offer_handoff_guard ON employee_drafts;
                DROP FUNCTION guard_employee_offer_handoff();
                ALTER TABLE employee_drafts DROP CONSTRAINT fk_draft_accepted_offer_company;
                """);
            migrationBuilder.DropForeignKey(name: "FK_employee_drafts_companies_tenant_id_offer_compensation_plac~", table: "employee_drafts");
            migrationBuilder.DropIndex(name: "IX_employee_drafts_tenant_id_offer_compensation_placement_comp~", table: "employee_drafts");
            migrationBuilder.DropColumn(name: "offer_compensation_placement_company_id", table: "employee_drafts");
            migrationBuilder.DropForeignKey(
                name: "FK_employee_drafts_companies_tenant_id_accepted_offer_company_~",
                table: "employee_drafts");

            migrationBuilder.DropForeignKey(
                name: "FK_employee_drafts_offer_letters_tenant_id_accepted_offer_id",
                table: "employee_drafts");

            migrationBuilder.DropIndex(
                name: "IX_employee_drafts_tenant_id_accepted_offer_company_id",
                table: "employee_drafts");

            migrationBuilder.DropIndex(
                name: "IX_employee_drafts_tenant_id_accepted_offer_id",
                table: "employee_drafts");

            migrationBuilder.DropColumn(
                name: "accepted_offer_company_id",
                table: "employee_drafts");

            migrationBuilder.DropColumn(
                name: "accepted_offer_compensation_json",
                table: "employee_drafts");

            migrationBuilder.DropColumn(
                name: "accepted_offer_id",
                table: "employee_drafts");

            migrationBuilder.DropColumn(
                name: "offer_compensation_confirmed_at_utc",
                table: "employee_drafts");

            migrationBuilder.DropColumn(
                name: "offer_compensation_confirmed_by_user_id",
                table: "employee_drafts");

            migrationBuilder.DropColumn(
                name: "offer_compensation_confirmed_fingerprint",
                table: "employee_drafts");

            migrationBuilder.DropColumn(
                name: "offer_compensation_dispositions_json",
                table: "employee_drafts");

            migrationBuilder.DropColumn(
                name: "offer_compensation_version",
                table: "employee_drafts");
        }
    }
}
