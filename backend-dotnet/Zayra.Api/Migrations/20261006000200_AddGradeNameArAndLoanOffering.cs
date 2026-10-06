using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddGradeNameArAndLoanOffering : Migration
    {
        // Frozen history: never edit these, even if LoanTypeSql changes — write a new migration instead.
        internal const string AddInterestFreeCheckSql =
            "ALTER TABLE loan_types ADD CONSTRAINT ck_loan_types__interest_free CHECK (is_interest_free AND interest_rate = 0) NOT VALID;";
        internal const string DropInterestFreeCheckSql = "ALTER TABLE loan_types DROP CONSTRAINT IF EXISTS ck_loan_types__interest_free;";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Slice L1 — additive only. is_offered defaults TRUE so every existing policy keeps offering its loan
            // type; created_by_offering_switch / copied_from_policy_id let the per-company switch undo itself, with
            // a tenant-composite FK and two CHECKs keeping those markers honest; name_ar is optional (readers fall
            // back to name).
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

            migrationBuilder.AddUniqueConstraint(
                name: "AK_loan_policies_tenant_id_id",
                table: "loan_policies",
                columns: new[] { "tenant_id", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_loan_policies_tenant_id_copied_from_policy_id",
                table: "loan_policies",
                columns: new[] { "tenant_id", "copied_from_policy_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_loan_policies__copied_from_only_on_switch",
                table: "loan_policies",
                sql: "copied_from_policy_id IS NULL OR created_by_offering_switch");

            migrationBuilder.AddCheckConstraint(
                name: "ck_loan_policies__switch_stub_not_offered",
                table: "loan_policies",
                sql: "NOT created_by_offering_switch OR NOT is_offered");

            migrationBuilder.AddForeignKey(
                name: "FK_loan_policies_loan_policies_tenant_id_copied_from_policy_id",
                table: "loan_policies",
                columns: new[] { "tenant_id", "copied_from_policy_id" },
                principalTable: "loan_policies",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

            // Qard: employer loans are principal only. FROZEN copy of LoanTypeSql.AddInterestFreeCheck (which the
            // Postgres test fixture applies); GradeLoanLimitMigrationPostgresTests fails if the two diverge.
            migrationBuilder.Sql(AddInterestFreeCheckSql);
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
            migrationBuilder.Sql(DropInterestFreeCheckSql);
            migrationBuilder.DropForeignKey(
                name: "FK_loan_policies_loan_policies_tenant_id_copied_from_policy_id",
                table: "loan_policies");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_loan_policies_tenant_id_id",
                table: "loan_policies");

            migrationBuilder.DropIndex(
                name: "IX_loan_policies_tenant_id_copied_from_policy_id",
                table: "loan_policies");

            migrationBuilder.DropCheckConstraint(
                name: "ck_loan_policies__copied_from_only_on_switch",
                table: "loan_policies");

            migrationBuilder.DropCheckConstraint(
                name: "ck_loan_policies__switch_stub_not_offered",
                table: "loan_policies");

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
