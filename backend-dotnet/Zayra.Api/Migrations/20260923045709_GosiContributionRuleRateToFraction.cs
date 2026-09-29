using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// Moves <c>gosi_contribution_rules.rate</c> from a PERCENT to a decimal FRACTION, so that the
    /// product holds one statutory fact in one unit.
    ///
    /// <para><b>The defect.</b> <c>GosiCalculationService</c> computed <c>wage × rate / 100</c>
    /// against a column seeded <c>9.00</c>, while <c>statutory_rules.rule_value</c> held the same
    /// GOSI rate as <c>'0.09'</c> and <c>KsaDeductionCalculator</c> computed
    /// <c>coveredWage × rate</c>. Each store was internally consistent, so no test could see it;
    /// a rate entered in the other store's unit was a 100× remittance error.</para>
    ///
    /// <para><b>The conversion is not a guess.</b> A GOSI/GCC social-insurance branch rate written
    /// as a PERCENT is never below 0.5 — annuities are 9–11, SANED 0.75–1, occupational hazards 2,
    /// and the platform seeder wrote exactly 9.00 / 0.75 / 2.00. The same rate written as a
    /// FRACTION is never above 0.30 (the largest in the region is GRSIA employer at 0.14).
    /// A threshold of 0.5 therefore sits in a gap with no legitimate value on either side.</para>
    ///
    /// <para><b>Rows below 0.5 are left alone, deliberately.</b> Those are the rows that were
    /// already written as fractions — the exact defect
    /// <c>StatutoryRateStoreTests.EverySeededGosiRate_IsExpressedInPercentNotAsAFraction</c> was
    /// written to catch (a since-deleted demo seeder wrote <c>0.10m</c> meaning "10%"). Under the
    /// old percent reading they contributed about a hundredth of what was intended; under the new
    /// fraction reading they contribute what was written. Dividing them again would make a wrong
    /// number wronger.</para>
    ///
    /// <para><b>It refuses rather than guesses.</b> If any row is left outside the admissible
    /// fraction band [0, 0.30] the migration aborts and names the offending rows, because a GOSI
    /// rate that cannot be read confidently must block, not be assumed. Fix the row and re-run.</para>
    /// </summary>
    public partial class GosiContributionRuleRateToFraction : Migration
    {
        /// <summary>The ceiling for a contribution rate expressed as a fraction.
        /// Mirrors <c>StatutoryValueUnits.MaxContributionRateFraction</c>.</summary>
        private const string MaxFraction = "0.30";

        /// <summary>Values at or above this were written as percents. See the class remarks.</summary>
        private const string PercentThreshold = "0.5";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // numeric(9,6) is the rate type TARGET_SCHEMA §2.E specifies. It is widened BEFORE the
            // division so that 0.75 → 0.0075 keeps every digit; numeric(7,4) would have rounded it.
            migrationBuilder.AlterColumn<decimal>(
                name: "rate",
                table: "gosi_contribution_rules",
                type: "numeric(9,6)",
                precision: 9,
                scale: 6,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(7,4)",
                oldPrecision: 7,
                oldScale: 4);

            migrationBuilder.Sql($@"
UPDATE gosi_contribution_rules
   SET rate = rate / 100
 WHERE rate >= {PercentThreshold};");

            migrationBuilder.Sql($@"
DO $$
DECLARE offending text;
BEGIN
    SELECT string_agg(
               format('%s (classification=%s branch=%s payer=%s rate=%s)',
                      id, classification, branch, payer, rate), ', ')
      INTO offending
      FROM gosi_contribution_rules
     WHERE rate < 0 OR rate > {MaxFraction};

    IF offending IS NOT NULL THEN
        RAISE EXCEPTION
            'GosiContributionRuleRateToFraction: % gosi_contribution_rules row(s) hold a rate outside the admissible fraction band [0, {MaxFraction}] after conversion: %. A GOSI rate is a decimal FRACTION of the contributory wage (9%% is 0.09). These rows cannot be read confidently, so the migration refuses rather than guessing. Correct each row by hand and re-run.',
            (SELECT count(*) FROM gosi_contribution_rules WHERE rate < 0 OR rate > {MaxFraction}),
            offending;
    END IF;
END $$;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Exact inverse of the Up conversion: every row Up divided ended at or above 0.005.
            // Rows that were ALREADY fractions before Up (and were therefore left untouched) are
            // multiplied here too and will not return to their pre-Up value — there is no
            // information left to distinguish them, and they were wrong in either unit. Review
            // gosi_contribution_rules by hand after a rollback.
            migrationBuilder.Sql(@"
UPDATE gosi_contribution_rules
   SET rate = rate * 100
 WHERE rate >= 0.005;");

            migrationBuilder.AlterColumn<decimal>(
                name: "rate",
                table: "gosi_contribution_rules",
                type: "numeric(7,4)",
                precision: 7,
                scale: 4,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(9,6)",
                oldPrecision: 9,
                oldScale: 6);
        }
    }
}
