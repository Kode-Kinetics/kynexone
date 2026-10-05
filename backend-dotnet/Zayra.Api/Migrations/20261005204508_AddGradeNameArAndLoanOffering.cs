using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddGradeNameArAndLoanOffering : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Slice L1 — additive only. is_offered defaults TRUE so every existing policy keeps offering its loan
            // type; created_by_offering_switch / copied_from_policy_id let the per-company switch undo itself;
            // name_ar is optional (readers fall back to name).
            migrationBuilder.AddColumn<Guid>(
                name: "copied_from_policy_id",
                table: "loan_policies",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "created_by_offering_switch",
                table: "loan_policies",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "is_offered",
                table: "loan_policies",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "name_ar",
                table: "grades",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            // Qard: an employer loan is principal only (Civil Transactions Law Art. 385). NOT VALID enforces every
            // new and updated loan_types row without failing on legacy interest-bearing rows; those are listed by
            // the read-only pre-deploy query in docs/DEPLOY_ROLLBACK_RUNBOOK.md and validated once cleaned.
            migrationBuilder.Sql(
                "ALTER TABLE loan_types ADD CONSTRAINT ck_loan_types__interest_free " +
                "CHECK (is_interest_free AND interest_rate = 0) NOT VALID;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dropping is_offered would silently re-offer every switched-off loan type; refuse over that evidence.
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM loan_policies WHERE NOT is_offered) OR EXISTS (SELECT 1 FROM grades WHERE name_ar IS NOT NULL) THEN
                        RAISE EXCEPTION 'Company loan offerings or Arabic grade names exist. Preserve them and use a forward corrective migration.';
                    END IF;
                END $$;
                """);
            migrationBuilder.Sql("ALTER TABLE loan_types DROP CONSTRAINT IF EXISTS ck_loan_types__interest_free;");
            migrationBuilder.DropColumn(
                name: "copied_from_policy_id",
                table: "loan_policies");

            migrationBuilder.DropColumn(
                name: "created_by_offering_switch",
                table: "loan_policies");

            migrationBuilder.DropColumn(
                name: "is_offered",
                table: "loan_policies");

            migrationBuilder.DropColumn(
                name: "name_ar",
                table: "grades");
        }
    }
}
