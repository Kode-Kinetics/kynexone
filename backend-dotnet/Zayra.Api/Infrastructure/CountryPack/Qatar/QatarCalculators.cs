using Zayra.Api.Application.CountryPack;

namespace Zayra.Api.Infrastructure.CountryPack.Qatar;

// ── Qatar GRSIA deduction calculator ─────────────────────────────────────────
// Qatar nationals: employee 7% + employer 14% on basic salary.
// Expatriates: no statutory social insurance contribution.
// Source: Qatar Law 24/2002 (GRSIA) and amendments.
// VERIFY: confirm current rates with GRSIA / HUKOOMI before use in production.

public sealed class QatarDeductionCalculator : IStatutoryDeductionCalculator
{
    private readonly IStatutoryRuleReader _rules;
    public QatarDeductionCalculator(IStatutoryRuleReader rules) => _rules = rules;

    public async Task<StatutoryDeductionResult> CalculateAsync(
        StatutoryDeductionInput input, CancellationToken ct = default)
    {
        if (!IsQatariNational(input.Nationality))
            return new(0m, 0m, Array.Empty<StatutoryDeductionLine>());

        var eff = new DateOnly(input.PeriodYear, input.PeriodMonth, 1);
        decimal basicSalary = input.Salary.Basic;  // GRSIA base = basic only

        decimal empRate = await _rules.GetDecimalAsync(
            CountryCodes.Qatar, Jurisdictions.QatarMainland,
            "grsia.national_employee_rate", eff, null, ct) ?? 0.07m;   // VERIFY: 7%

        decimal erRate = await _rules.GetDecimalAsync(
            CountryCodes.Qatar, Jurisdictions.QatarMainland,
            "grsia.national_employer_rate", eff, null, ct) ?? 0.14m;   // VERIFY: 14%

        decimal empContrib = Math.Round(basicSalary * empRate, 2);
        decimal erContrib  = Math.Round(basicSalary * erRate, 2);

        var lines = new List<StatutoryDeductionLine>
        {
            new("GRSIA-EE", "GRSIA (Employee)", empContrib, 0m),
            new("GRSIA-ER", "GRSIA (Employer)", 0m, erContrib),
        };

        return new(empContrib, erContrib, lines);
    }

    private static bool IsQatariNational(string nat)
        => string.Equals(nat, CountryCodes.Qatar, StringComparison.OrdinalIgnoreCase)
        || string.Equals(nat, "QA", StringComparison.OrdinalIgnoreCase)
        || string.Equals(nat, "Qatari", StringComparison.OrdinalIgnoreCase);
}

// ── Qatar EOSB calculator ─────────────────────────────────────────────────────
// Minimum 3 weeks (21 days) basic per year of service, pro-rated.
// Daily rate = basic / 30.
// Source: Qatar Labor Law 14/2004 Art. 54 (as amended by Law 19/2020).
// VERIFY: some categories use 7-day weeks; Art. 54 references "weeks not months".
// Implementation uses 21 calendar days per year (3 × 7) as the minimum floor.

public sealed class QatarEndOfServiceCalculator : IEndOfServiceCalculator
{
    private readonly IStatutoryRuleReader _rules;
    public QatarEndOfServiceCalculator(IStatutoryRuleReader rules) => _rules = rules;

    public async Task<EndOfServiceResult> CalculateAsync(
        EndOfServiceInput input, CancellationToken ct = default)
    {
        _ = _rules;

        decimal basic = input.Salary.Basic;
        decimal dailyRate = basic / 30m;

        int totalDays = input.ServiceEndDate.DayNumber - input.ServiceStartDate.DayNumber;
        decimal serviceYears = totalDays / 365m;

        // Qatar Law 14/2004 Art.54 (as amended by Law 19/2020): EOS gratuity requires at
        // least ONE completed year of service. This eligibility floor lives in the pack
        // (single engine) — the controller no longer pre-gates on GCC minYears.
        if (serviceYears < 1m)
            return await Task.FromResult(new EndOfServiceResult(
                0m, "Qatar-LaborLaw-14-2004-Art54",
                new List<EndOfServiceBreakdown> { new("No entitlement (< 1 year service, Art.54)", 0m) }));

        // Minimum 3 weeks = 21 days per year of service
        decimal totalDaysEntitled = Math.Round(serviceYears * 21m, 4);
        decimal total = Math.Round(totalDaysEntitled * dailyRate, 2);

        var bd = new List<EndOfServiceBreakdown>
        {
            new($"{serviceYears:F4} yrs × 21 days × {dailyRate:F2} QAR/day", total),
        };

        return await Task.FromResult(
            new EndOfServiceResult(total, "Qatar-LaborLaw-14-2004-Art54", bd));
    }
}

// ── Qatar Qatarization tracker ────────────────────────────────────────────────
// Source: Qatarization program under Qatar National Vision 2030.
// VERIFY: sector-specific targets from the Ministry of Labor.

public sealed class QatarNationalizationTracker : INationalizationTracker
{
    private readonly IStatutoryRuleReader _rules;
    public QatarNationalizationTracker(IStatutoryRuleReader rules) => _rules = rules;

    public async Task<NationalizationResult> GetStatusAsync(
        NationalizationInput input, CancellationToken ct = default)
    {
        decimal targetD = await _rules.GetDecimalAsync(
            CountryCodes.Qatar, Jurisdictions.QatarMainland,
            "qatarization.target_ratio", DateOnly.FromDateTime(DateTime.UtcNow), null, ct)
            ?? 0.20m;  // VERIFY: sector-specific Qatarization target

        double target  = (double)targetD;
        double current = input.TotalHeadcount == 0
            ? 0d
            : (double)input.NationalHeadcount / input.TotalHeadcount;

        var status = input.TotalHeadcount == 0
            ? NationalizationComplianceStatus.NotApplicable
            : current >= target
                ? NationalizationComplianceStatus.Compliant
                : current >= target * 0.75
                    ? NationalizationComplianceStatus.AtRisk
                    : NationalizationComplianceStatus.NonCompliant;

        return new(target, current, input.TotalHeadcount, input.NationalHeadcount,
            status, "Qatarization");
    }
}
