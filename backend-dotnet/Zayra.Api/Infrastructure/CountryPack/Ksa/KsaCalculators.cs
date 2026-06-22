using Zayra.Api.Application.CountryPack;

namespace Zayra.Api.Infrastructure.CountryPack.Ksa;

// ── KSA GOSI deduction calculator ────────────────────────────────────────────
// Saudi nationals: Annuities + SANED + Occupational Hazard (employer only) on covered wage.
//   Employee: 9% Annuities + 0.75% SANED = 9.75%
//   Employer: 9% Annuities + 0.75% SANED + 2% OH = 11.75%
// Non-Saudi: Occupational Hazard employer contribution only (2%).
// Sources: GOSI Regulation 2016 (Royal Decree M/33); rates VERIFY annually.

public sealed class KsaDeductionCalculator : IStatutoryDeductionCalculator
{
    private readonly IStatutoryRuleReader _rules;
    public KsaDeductionCalculator(IStatutoryRuleReader rules) => _rules = rules;

    public async Task<StatutoryDeductionResult> CalculateAsync(
        StatutoryDeductionInput input, CancellationToken ct = default)
    {
        var eff = new DateOnly(input.PeriodYear, input.PeriodMonth, 1);

        // Covered wage = basic + housing, capped at statutory ceiling
        decimal coveredWage = input.Salary.GosiCoveredWage;
        decimal ceiling = await _rules.GetDecimalAsync(
            CountryCodes.Saudi, Jurisdictions.KsaMainland,
            RuleKeys.GosiCoveredWageCeilingSar, eff, null, ct) ?? 45_000m;  // VERIFY: SAR 45,000 as of 2024
        coveredWage = Math.Min(coveredWage, ceiling);

        bool isSaudi = IsSaudiNational(input.Nationality);
        var lines = new List<StatutoryDeductionLine>();
        decimal empTotal = 0m, erTotal = 0m;

        if (isSaudi)
        {
            decimal empAnnuity = await _rules.GetDecimalAsync(
                CountryCodes.Saudi, Jurisdictions.KsaMainland,
                RuleKeys.GosiSaudiEmployeeRate, eff, null, ct) ?? 0.09m;    // VERIFY: 9%

            decimal erAnnuity = await _rules.GetDecimalAsync(
                CountryCodes.Saudi, Jurisdictions.KsaMainland,
                RuleKeys.GosiSaudiEmployerRate, eff, null, ct) ?? 0.09m;    // VERIFY: 9%

            decimal sanedRate = await _rules.GetDecimalAsync(
                CountryCodes.Saudi, Jurisdictions.KsaMainland,
                RuleKeys.GosiSanedRate, eff, null, ct) ?? 0.0075m;          // VERIFY: 0.75% each side

            decimal annuityEmp = Math.Round(coveredWage * empAnnuity, 2);
            decimal annuityEr  = Math.Round(coveredWage * erAnnuity, 2);
            decimal sanedEmp   = Math.Round(coveredWage * sanedRate, 2);
            decimal sanedEr    = Math.Round(coveredWage * sanedRate, 2);

            decimal ohRate = await _rules.GetDecimalAsync(
                CountryCodes.Saudi, Jurisdictions.KsaMainland,
                RuleKeys.GosiExpOhRate, eff, null, ct) ?? 0.02m;            // VERIFY: 2% OH (employer pays for all employees)

            decimal ohEr = Math.Round(coveredWage * ohRate, 2);

            lines.Add(new("GOSI-ANN-EE", "GOSI Annuities (Employee)",    annuityEmp, 0m));
            lines.Add(new("GOSI-ANN-ER", "GOSI Annuities (Employer)",    0m, annuityEr));
            lines.Add(new("GOSI-SANED-EE", "SANED (Employee)",           sanedEmp, 0m));
            lines.Add(new("GOSI-SANED-ER", "SANED (Employer)",           0m, sanedEr));
            lines.Add(new("GOSI-OH-ER", "Occupational Hazard (Employer)", 0m, ohEr));

            empTotal = annuityEmp + sanedEmp;
            erTotal  = annuityEr  + sanedEr + ohEr;
        }
        else
        {
            decimal ohRate = await _rules.GetDecimalAsync(
                CountryCodes.Saudi, Jurisdictions.KsaMainland,
                RuleKeys.GosiExpOhRate, eff, null, ct) ?? 0.02m;            // VERIFY: 2% employer only

            decimal oh = Math.Round(coveredWage * ohRate, 2);
            lines.Add(new("GOSI-OH-ER", "Occupational Hazard (Employer)", 0m, oh));
            erTotal = oh;
        }

        return new(empTotal, erTotal, lines);
    }

    private static bool IsSaudiNational(string nationality)
        => string.Equals(nationality, CountryCodes.Saudi, StringComparison.OrdinalIgnoreCase)
        || string.Equals(nationality, "SA", StringComparison.OrdinalIgnoreCase)
        || string.Equals(nationality, "Saudi", StringComparison.OrdinalIgnoreCase);
}

// ── KSA EOSB calculator ───────────────────────────────────────────────────────
// Tiered on basic salary only.
// Tier 1 (years ≤ 5): ½ month basic per year of service.
// Tier 2 (years > 5): 1 month basic per year, applied to the entire tenure.
// Resignation discount per KSA Labor Law Art. 84.
// Source: KSA Labor Law Royal Decree M/51 2005 + amendments.

public sealed class KsaEndOfServiceCalculator : IEndOfServiceCalculator
{
    private readonly IStatutoryRuleReader _rules;
    public KsaEndOfServiceCalculator(IStatutoryRuleReader rules) => _rules = rules;

    public async Task<EndOfServiceResult> CalculateAsync(
        EndOfServiceInput input, CancellationToken ct = default)
    {
        _ = _rules; // rates not needed for EOSB formula; ceiling could be added later

        decimal basic = input.Salary.Basic;
        (int fullMonths, int remDays) = ServicePeriod(input.ServiceStartDate, input.ServiceEndDate);
        decimal serviceYears = fullMonths / 12m + remDays / 365m;

        decimal tier1Years = Math.Min(serviceYears, 5m);
        decimal tier2Years = Math.Max(0m, serviceYears - 5m);

        decimal tier1 = Math.Round(tier1Years * 0.5m * basic, 2);
        decimal tier2 = Math.Round(tier2Years * 1.0m * basic, 2);
        decimal totalBeforeDiscount = tier1 + tier2;

        decimal total = ApplyResignationDiscount(totalBeforeDiscount, serviceYears, input.TerminationReason);
        total = Math.Round(total, 2);

        var bd = new List<EndOfServiceBreakdown>
        {
            new($"Tier 1 ({tier1Years:F4} yrs × ½ month)", tier1),
            new($"Tier 2 ({tier2Years:F4} yrs × 1 month)", tier2),
        };
        if (total != totalBeforeDiscount)
            bd.Add(new("Resignation discount", total - totalBeforeDiscount));

        return await Task.FromResult(new EndOfServiceResult(total, "KSA-LaborLaw-Art84", bd));
    }

    // Returns (fullMonths, remainingDays) excluding the termination month's excess days
    private static (int fullMonths, int remDays) ServicePeriod(DateOnly start, DateOnly end)
    {
        int months = (end.Year - start.Year) * 12 + (end.Month - start.Month);
        int days   = end.Day - start.Day;
        if (days < 0)
        {
            months--;
            days += DateTime.DaysInMonth(end.Year, end.Month == 1 ? 12 : end.Month - 1);
        }
        return (months, days);
    }

    private static decimal ApplyResignationDiscount(decimal total, decimal years, string reason)
    {
        if (!string.Equals(reason, "Resignation", StringComparison.OrdinalIgnoreCase)) return total;
        if (years < 2m)  return 0m;
        if (years < 5m)  return Math.Round(total / 3m, 2);
        if (years < 10m) return Math.Round(total * 2m / 3m, 2);
        return total;
    }
}

// ── KSA Nitaqat nationalization tracker ───────────────────────────────────────
// Nitaqat classifies establishments into Platinum/Green/Yellow/Red bands
// based on Saudi employee percentage vs. target ratio.
// Source: HRSD Nitaqat program.  Target ratios vary by sector; the seeded
// value is a directional placeholder. VERIFY: obtain sector-specific targets.

public sealed class KsaNationalizationTracker : INationalizationTracker
{
    private readonly IStatutoryRuleReader _rules;
    public KsaNationalizationTracker(IStatutoryRuleReader rules) => _rules = rules;

    public async Task<NationalizationResult> GetStatusAsync(
        NationalizationInput input, CancellationToken ct = default)
    {
        decimal targetRatioD = await _rules.GetDecimalAsync(
            CountryCodes.Saudi, Jurisdictions.KsaMainland,
            RuleKeys.NitaqatTargetRatio, DateOnly.FromDateTime(DateTime.UtcNow), null, ct) ?? 0.35m; // VERIFY: sector-specific

        double target  = (double)targetRatioD;
        double current = input.TotalHeadcount == 0
            ? 0d
            : (double)input.NationalHeadcount / input.TotalHeadcount;

        var status = (current, target) switch
        {
            _ when input.TotalHeadcount == 0 => NationalizationComplianceStatus.NotApplicable,
            _ when current >= target * 1.1   => NationalizationComplianceStatus.Compliant,   // Platinum/Green
            _ when current >= target         => NationalizationComplianceStatus.Compliant,
            _ when current >= target * 0.8   => NationalizationComplianceStatus.AtRisk,       // Yellow
            _                                => NationalizationComplianceStatus.NonCompliant,  // Red
        };

        return new(target, current, input.TotalHeadcount, input.NationalHeadcount, status, "Nitaqat");
    }
}

internal static class RuleKeys
{
    // KSA GOSI
    public const string GosiCoveredWageCeilingSar = "gosi.covered_wage_ceiling_sar";
    public const string GosiSaudiEmployeeRate     = "gosi.saudi_employee_rate";
    public const string GosiSaudiEmployerRate     = "gosi.saudi_employer_rate";
    public const string GosiSanedRate             = "gosi.saned_rate";
    public const string GosiExpOhRate             = "gosi.expat_occupational_hazard_rate";
    // KSA Nationalization
    public const string NitaqatTargetRatio        = "nitaqat.default_target_ratio";
    // UAE GPSSA
    public const string GpssaNationalEmployeeRate = "gpssa.national_employee_rate";
    public const string GpssaNationalEmployerRate = "gpssa.national_employer_rate";
    // UAE Emiratisation
    public const string EmiratiTargetRatio        = "emiratisation.target_ratio";
    // UAE DEWS
    public const string DewsTier1Rate             = "dews.tier1_monthly_rate";
    public const string DewsTier2Rate             = "dews.tier2_monthly_rate";
    // Qatar GRSIA
    public const string GrsiaNationalEmployeeRate = "grsia.national_employee_rate";
    public const string GrsiaNationalEmployerRate = "grsia.national_employer_rate";
    // Qatar nationalization
    public const string QatarizationTargetRatio   = "qatarization.target_ratio";
}
