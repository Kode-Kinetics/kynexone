using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Seed;

/// <summary>
/// Seeds the default GOSI contribution rules (system-wide, TenantId = Guid.Empty)
/// if they do not already exist in the database.
///
/// Baseline rates (Saudi Arabia, effective 2016-06-01, per GOSI Regulation):
///   Saudi Annuities:           Employee 9%,    Employer 9%
///   Saudi SANED:               Employee 0.75%, Employer 0.75%
///   Occupational Hazards:      Employer 2%     (applies to all classifications)
///
/// UNIT — READ THIS BEFORE EDITING A RATE. Every <c>Rate</c> below is a decimal FRACTION of the
/// contributory wage: 9% is <c>0.09m</c>, 0.75% is <c>0.0075m</c>. It is NOT a percentage. This
/// matches <see cref="StatutoryRuleSeeder"/>, which seeds the same three rates as
/// <c>gosi.saudi_employee_rate = "0.09"</c>, <c>gosi.saudi_employer_rate = "0.09"</c> and
/// <c>gosi.saned_rate = "0.0075"</c> for the payslip and the GOSI filing to read.
/// <c>StatutoryRateUnitTests.BothStores_AgreeOnEverySeededKsaGosiRate</c> fails if the two ever
/// disagree. Until 2026-09 this store held PERCENTS (<c>9.00m</c>) and multiplied by
/// <c>Rate / 100</c>; see migration <c>GosiContributionRuleRateToFraction</c>.
///
/// GCC rates mirror the Saudi baseline but are marked as pending legal confirmation.
/// NonSaudi nationals: Occupational Hazards employer 2% only.
///
/// NOTE: Rates must be reviewed annually against current GOSI circulars. The contributory-wage
/// CAP is not seeded here at all — it is a statutory rule (gosi.covered_wage_ceiling_sar),
/// seeded by StatutoryRuleSeeder and read through KsaGosiWageBounds.
/// </summary>
public static class GosiRuleSeeder
{
    private static readonly DateOnly EffectiveFrom = new(2016, 6, 1);

    private const int GosiRateStalenessThresholdMonths = 18;

    public static async Task SeedDefaultsAsync(ZayraDbContext db, ILogger logger)
    {
        // IgnoreQueryFilters is intentional: seeder runs at startup outside any request context
        // (no IHttpContextAccessor, so the tenant filter is inactive). Must see Guid.Empty rows
        // to check whether platform defaults have already been seeded.
        var hasDefaults = await db.GosiContributionRules
            .IgnoreQueryFilters()
            .AnyAsync(r => r.TenantId == Guid.Empty);

        if (!hasDefaults)
        {
            var rules = BuildDefaultRules();
            db.GosiContributionRules.AddRange(rules);
            await db.SaveChangesAsync();
            logger.LogInformation("GOSI: seeded {Count} default contribution rules.", rules.Count);
        }

        // ── TWO STORES, ONE FACT ──────────────────────────────────────────────
        // gosi_contribution_rules (this seeder, read by the GOSI preview and the readiness report)
        // and statutory_rules (StatutoryRuleSeeder, read by the payslip and the GOSI filing) both
        // hold the KSA GOSI rates. They now use one unit — a fraction — but they are still two
        // rows, and nothing stops one being edited without the other. Compare them on every boot
        // and say so loudly; the value at stake is what a customer remits to GOSI.
        foreach (var problem in VerifyStoresAgree())
            logger.LogCritical(
                "[GOSI-RATE-STORES-DISAGREE] The two GOSI rate stores hold different values for one "
                + "statutory fact: {Problem} The GOSI preview and the readiness report read "
                + "gosi_contribution_rules; the payslip and the GOSI filing read statutory_rules. "
                + "Until they are collapsed into one table, both seeders must be changed together.",
                problem);

        // ── GOSI-RATE-AUDIT ───────────────────────────────────────────────────
        // Regardless of whether rules were just seeded, check how old the most recent
        // system-default GOSI rule effective date is. Emit a startup WARNING if stale.
        // IgnoreQueryFilters is intentional: GOSI system-default rules have TenantId == Guid.Empty
        // which is excluded by the per-tenant global filter. The seeder runs at startup outside
        // any request context (no IHttpContextAccessor), so bypassing the filter is correct here.
        var mostRecentRule = await db.GosiContributionRules
            .IgnoreQueryFilters()
            .Where(r => r.TenantId == Guid.Empty && r.CountryCode == "SA")
            .OrderByDescending(r => r.EffectiveFrom)
            .FirstOrDefaultAsync();

        if (mostRecentRule is not null)
        {
            var effectiveDate = mostRecentRule.EffectiveFrom;
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var ageMonths = (today.Year - effectiveDate.Year) * 12 + (today.Month - effectiveDate.Month);

            if (ageMonths >= GosiRateStalenessThresholdMonths)
            {
                var effectiveDateStr = effectiveDate.ToString("yyyy-MM-dd");
                logger.LogWarning(
                    "[GOSI-RATE-AUDIT] Default GOSI contribution rules effective from {Date} — older than {Threshold} months. " +
                    "Saudi GOSI rates require annual review against current GOSI circulars. " +
                    "ACTION REQUIRED: Obtain written sign-off from a Saudi-qualified payroll compliance officer " +
                    "before seeding or processing payroll for any real tenant. " +
                    "The current system default rates have NOT been independently verified against GOSI " +
                    "circulars issued after {Date}. See GosiRuleSeeder.cs for rate values.",
                    effectiveDateStr,
                    GosiRateStalenessThresholdMonths,
                    effectiveDateStr);
            }
        }
    }

    /// <summary>
    /// THE bridge between the product's two GOSI rate stores. Each <c>(Branch, Payer)</c> of
    /// <c>gosi_contribution_rules</c> names the <c>StatutoryRule</c> key that holds the SAME
    /// statutory fact for the payslip and the GOSI filing (<c>StatutoryRuleSeeder</c>, read by
    /// <c>KsaDeductionCalculator</c> through <c>RuleKeys</c>).
    ///
    /// <para>Two stores for one fact is the defect; until they are collapsed this map is what makes
    /// their disagreement detectable. <see cref="VerifyStoresAgree"/> walks it at boot and
    /// <c>StatutoryRateUnitTests.BothStores_AgreeOnEverySeededKsaGosiRate</c> walks it in the
    /// suite, so a rate changed in one seeder and not the other fails loudly instead of producing
    /// two different remittances from two screens.</para>
    /// </summary>
    public static readonly IReadOnlyDictionary<(string Branch, string Payer), string> StatutoryRuleKeyFor =
        new Dictionary<(string, string), string>
        {
            [(GosiBranches.Annuities, GosiPayers.Employee)]           = RuleKeys.GosiSaudiEmployeeRate,
            [(GosiBranches.Annuities, GosiPayers.Employer)]           = RuleKeys.GosiSaudiEmployerRate,
            [(GosiBranches.SANED, GosiPayers.Employee)]               = RuleKeys.GosiSanedRate,
            [(GosiBranches.SANED, GosiPayers.Employer)]               = RuleKeys.GosiSanedRate,
            [(GosiBranches.OccupationalHazards, GosiPayers.Employer)] = RuleKeys.GosiExpOhRate,
        };

    /// <summary>
    /// Compares the Saudi platform defaults of THIS store against the KSA GOSI rows
    /// <see cref="StatutoryRuleSeeder"/> seeds, rate for rate, in the one unit both now use.
    /// Returns a human-readable disagreement per mismatched branch, or an empty list.
    ///
    /// <para>Both sides are compiled constants, so this is pure in-memory arithmetic: no query, no
    /// cost, and it cannot be skipped by a tenant whose rows have not been seeded yet.</para>
    /// </summary>
    public static IReadOnlyList<string> VerifyStoresAgree()
    {
        var statutory = StatutoryRuleSeeder.BuildRules()
            .Where(r => r.CountryCode == CountryCodes.Saudi && r.Jurisdiction == Jurisdictions.KsaMainland)
            .GroupBy(r => r.RuleKey)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.EffectiveFrom).First().RuleValue);

        var problems = new List<string>();

        foreach (var rule in BuildDefaultRules()
                     .Where(r => r.Classification == GosiClassifications.Saudi))
        {
            if (!StatutoryRuleKeyFor.TryGetValue((rule.Branch, rule.Payer), out var key))
            {
                problems.Add($"{rule.Branch}/{rule.Payer} has no StatutoryRule key in "
                           + "GosiRuleSeeder.StatutoryRuleKeyFor, so the two GOSI rate stores cannot "
                           + "be compared for it.");
                continue;
            }

            if (!statutory.TryGetValue(key, out var raw))
            {
                problems.Add($"{rule.Branch}/{rule.Payer} maps to StatutoryRule key '{key}', which "
                           + "StatutoryRuleSeeder does not seed for KSA mainland.");
                continue;
            }

            if (!decimal.TryParse(raw, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var other))
            {
                problems.Add($"StatutoryRule '{key}' holds '{raw}', which is not a decimal.");
                continue;
            }

            if (other != rule.Rate)
                problems.Add($"{rule.Branch}/{rule.Payer}: gosi_contribution_rules seeds "
                           + $"{rule.Rate.ToString(System.Globalization.CultureInfo.InvariantCulture)} "
                           + $"but StatutoryRule '{key}' seeds "
                           + $"{other.ToString(System.Globalization.CultureInfo.InvariantCulture)}. "
                           + "Both are fractions of the contributory wage; one of the two seeders is stale.");
        }

        return problems;
    }

    private static List<GosiContributionRule> BuildDefaultRules()
    {
        var rules = new List<GosiContributionRule>();

        // ── Saudi nationals ────────────────────────────────────────────────────
        rules.Add(Rule(GosiClassifications.Saudi, GosiBranches.Annuities, GosiPayers.Employee, 0.09m,
            "GOSI Regulation 2016 — Saudi Annuities employee contribution"));
        rules.Add(Rule(GosiClassifications.Saudi, GosiBranches.Annuities, GosiPayers.Employer, 0.09m,
            "GOSI Regulation 2016 — Saudi Annuities employer contribution"));

        rules.Add(Rule(GosiClassifications.Saudi, GosiBranches.SANED, GosiPayers.Employee, 0.0075m,
            "GOSI Regulation 2016 — SANED employee contribution"));
        rules.Add(Rule(GosiClassifications.Saudi, GosiBranches.SANED, GosiPayers.Employer, 0.0075m,
            "GOSI Regulation 2016 — SANED employer contribution"));

        rules.Add(Rule(GosiClassifications.Saudi, GosiBranches.OccupationalHazards, GosiPayers.Employer, 0.02m,
            "GOSI Regulation 2016 — Occupational Hazards employer contribution"));

        // ── GCC nationals (pending bilateral treaty confirmation) ──────────────
        var gccNote = "GCC bilateral treaty baseline — PENDING LEGAL CONFIRMATION per applicable bilateral agreement. " +
                      "Rates mirror Saudi baseline until legally confirmed.";

        rules.Add(Rule(GosiClassifications.GCC, GosiBranches.Annuities, GosiPayers.Employee, 0.09m,
            gccNote, notes: "Pending legal confirmation"));
        rules.Add(Rule(GosiClassifications.GCC, GosiBranches.Annuities, GosiPayers.Employer, 0.09m,
            gccNote, notes: "Pending legal confirmation"));

        rules.Add(Rule(GosiClassifications.GCC, GosiBranches.SANED, GosiPayers.Employee, 0.0075m,
            gccNote, notes: "Pending legal confirmation"));
        rules.Add(Rule(GosiClassifications.GCC, GosiBranches.SANED, GosiPayers.Employer, 0.0075m,
            gccNote, notes: "Pending legal confirmation"));

        rules.Add(Rule(GosiClassifications.GCC, GosiBranches.OccupationalHazards, GosiPayers.Employer, 0.02m,
            gccNote, notes: "Pending legal confirmation"));

        // ── Non-Saudi nationals (Occupational Hazards employer only) ──────────
        rules.Add(Rule(GosiClassifications.NonSaudi, GosiBranches.OccupationalHazards, GosiPayers.Employer, 0.02m,
            "GOSI Regulation 2016 — Occupational Hazards employer contribution (all employees including expats)"));

        return rules;
    }

    // MinContributoryWage / MaxContributoryWage are deliberately NOT set, and populating them would
    // be the wrong fix. They are a second store for a statutory value; the contributory-wage bounds
    // live in the effective-dated statutory rules engine under gosi.covered_wage_ceiling_sar and are
    // read through KsaGosiWageBounds by every surface — payslip, GOSI preview and readiness report
    // alike. Nothing reads these two columns any more. See GosiCalculationService.
    private static GosiContributionRule Rule(
        string  classification,
        string  branch,
        string  payer,
        decimal rate,
        string  sourceReference,
        string? notes = null) =>
        new()
        {
            Id              = Guid.NewGuid(),
            TenantId        = Guid.Empty,
            CountryCode     = "SA",
            Classification  = classification,
            Branch          = branch,
            Payer           = payer,
            Rate            = rate,
            EffectiveFrom   = EffectiveFrom,
            EffectiveTo     = null,
            IsActive        = true,
            SourceReference = sourceReference,
            Notes           = notes,
            CreatedAtUtc    = DateTime.UtcNow,
        };
}
