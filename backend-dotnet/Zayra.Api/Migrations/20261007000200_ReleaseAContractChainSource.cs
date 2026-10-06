using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// Release A R0b (owner: R4, by CTO decision on the R4 review). Expand phase, additive only:
    /// <list type="bullet">
    /// <item><c>employee_contracts.chain_source</c> (NULL = unstamped, <c>Derived</c> by the chain census / activation
    ///   stamp, <c>Recorded</c> by HR's chain confirm) — so correcting an earlier term re-derives the later Derived terms
    ///   and never touches a Recorded one.</item>
    /// <item>Three chain CHECKs (and the chain_source value set), all added <b>NOT VALID</b>: existing rows are not
    ///   scanned (no long lock), every new or changed row is checked. VALIDATE is a later migration, after the read-only
    ///   pre-check in docs/DEPLOY_ROLLBACK_RUNBOOK.md (Release A R0b) reads zero on each environment.</item>
    /// </list>
    /// The R0 literal <c>(provisional_basis IS NULL) = (renewal_number IS NOT NULL)</c> is NOT added: an unconfirmed live
    /// term legitimately has neither (it opens NeedsConfirmation instead of being guessed). The shipped half
    /// (<c>provisional_basis IS NULL OR renewal_number IS NULL</c>) stays. <c>renewed_from_counts</c> exempts a
    /// provisional (holdover, R6) successor, which carries renewed_from before Apply gives it a number.
    /// </summary>
    /// <inheritdoc />
    public partial class ReleaseAContractChainSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "chain_source",
                table: "employee_contracts",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            // The model declares the same four CHECKs (ReleaseAModelConfiguration); here they go in NOT VALID.
            migrationBuilder.Sql(AddChecksNotValidSql);
        }

        /// <summary>FROZEN: never edit — VALIDATE and any change are a new migration.</summary>
        internal const string AddChecksNotValidSql = """
            ALTER TABLE employee_contracts ADD CONSTRAINT ck_employee_contracts__chain_source
                CHECK (chain_source IS NULL OR chain_source IN ('Derived','Recorded')) NOT VALID;
            ALTER TABLE employee_contracts ADD CONSTRAINT ck_employee_contracts__chain_pair
                CHECK ((renewal_number IS NULL) = (chain_started_on IS NULL)) NOT VALID;
            ALTER TABLE employee_contracts ADD CONSTRAINT ck_employee_contracts__renewed_from_counts
                CHECK (renewed_from_contract_id IS NULL OR renewal_number >= 1 OR provisional_basis IS NOT NULL) NOT VALID;
            ALTER TABLE employee_contracts ADD CONSTRAINT ck_employee_contracts__chain_starts_by_term_start
                CHECK (chain_started_on IS NULL OR chain_started_on <= start_date) NOT VALID;
            """;

        /// <summary>Down refuses while HR-recorded chain history exists: dropping chain_source would lose which terms HR
        /// confirmed. Derived stamps can be recomputed by the census and do not block.</summary>
        internal const string GuardDownSql = """
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM employee_contracts WHERE chain_source = 'Recorded') THEN
                    RAISE EXCEPTION 'R0b Down refused: employee_contracts holds HR-recorded chain history (chain_source = Recorded). Fix forward instead.';
                END IF;
            END $$;
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(GuardDownSql);
            migrationBuilder.DropCheckConstraint(
                name: "ck_employee_contracts__chain_pair",
                table: "employee_contracts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employee_contracts__chain_source",
                table: "employee_contracts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employee_contracts__chain_starts_by_term_start",
                table: "employee_contracts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employee_contracts__renewed_from_counts",
                table: "employee_contracts");

            migrationBuilder.DropColumn(
                name: "chain_source",
                table: "employee_contracts");
        }
    }
}
