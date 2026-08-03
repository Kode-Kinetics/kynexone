using Zayra.Api.Application.CountryPack;

namespace Zayra.Api.Infrastructure.CountryPack.Uae;

// ── UAE GPSSA deduction calculator ───────────────────────────────────────────
// UAE nationals: employee 5% + employer 12.5% on basic + housing (GPSSA base).
// Expatriates: no statutory social insurance contribution in UAE.
// Source: GPSSA Federal Law 7/1999 + Cabinet Resolution 50/2022.
// Applies to both mainland and DIFC jurisdictions.
// VERIFY: rates annually against current GPSSA circulars.

public sealed class UaeDeductionCalculator : IStatutoryDeductionCalculator
{
    private readonly IStatutoryRuleReader _rules;
    public UaeDeductionCalculator(IStatutoryRuleReader rules) => _rules = rules;

    public async Task<StatutoryDeductionResult> CalculateAsync(
        StatutoryDeductionInput input, CancellationToken ct = default)
    {
        if (!IsUaeNational(input.Nationality))
            return new(0m, 0m, Array.Empty<StatutoryDeductionLine>());

        var eff = new DateOnly(input.PeriodYear, input.PeriodMonth, 1);
        decimal gpssaBase = input.Salary.GpssaBase;

        decimal empRate = await _rules.GetDecimalAsync(
            CountryCodes.UAE, Jurisdictions.UAEMainland,
            "gpssa.national_employee_rate", eff, null, ct) ?? 0.05m;   // VERIFY: 5%

        decimal erRate = await _rules.GetDecimalAsync(
            CountryCodes.UAE, Jurisdictions.UAEMainland,
            "gpssa.national_employer_rate", eff, null, ct) ?? 0.125m;  // VERIFY: 12.5%

        decimal empContrib = Math.Round(gpssaBase * empRate, 2);
        decimal erContrib  = Math.Round(gpssaBase * erRate, 2);

        var lines = new List<StatutoryDeductionLine>
        {
            new("GPSSA-EE", "GPSSA (Employee)", empContrib, 0m),
            new("GPSSA-ER", "GPSSA (Employer)", 0m, erContrib),
        };

        return new(empContrib, erContrib, lines);
    }

    private static bool IsUaeNational(string nat)
        => string.Equals(nat, CountryCodes.UAE, StringComparison.OrdinalIgnoreCase)
        || string.Equals(nat, "AE", StringComparison.OrdinalIgnoreCase)
        || string.Equals(nat, "Emirati", StringComparison.OrdinalIgnoreCase);
}

// ── UAE mainland EOSB calculator ─────────────────────────────────────────────
// Tier 1 (≤5 years):  21 calendar days basic per year.
// Tier 2 (>5 years):  30 calendar days basic per year.
// Daily rate = basic / 30.  Maximum capped at 2 years' gross salary.
// Source: UAE Labor Law Federal Decree-Law 33/2021, Art. 51.
// VERIFY: confirm current text applies to your employment category.

public sealed class UaeMainlandEndOfServiceCalculator : IEndOfServiceCalculator
{
    private readonly IStatutoryRuleReader _rules;
    public UaeMainlandEndOfServiceCalculator(IStatutoryRuleReader rules) => _rules = rules;

    public async Task<EndOfServiceResult> CalculateAsync(
        EndOfServiceInput input, CancellationToken ct = default)
    {
        _ = _rules;

        decimal basic = input.Salary.Basic;
        decimal dailyRate = basic / 30m;

        decimal serviceYears = ServiceYears(input.ServiceStartDate, input.ServiceEndDate);

        // UAE Decree-Law 33/2021 Art.51: EOSB gratuity accrues only after ONE completed
        // year of service. This eligibility floor lives in the pack (single engine) — the
        // controller no longer pre-gates on GCC minYears. (DIFC/DEWS accrues from month 1
        // and is handled by UaeDifcEndOfServiceCalculator, which has no such floor.)
        if (serviceYears < 1m)
            return await Task.FromResult(new EndOfServiceResult(
                0m, "UAE-LaborLaw-Art51",
                new List<EndOfServiceBreakdown> { new("No entitlement (< 1 year service, Art.51)", 0m) }));

        decimal tier1Years = Math.Min(serviceYears, 5m);
        decimal tier2Years = Math.Max(0m, serviceYears - 5m);

        decimal tier1 = Math.Round(tier1Years * 21m * dailyRate, 2);   // 21 days/yr
        decimal tier2 = Math.Round(tier2Years * 30m * dailyRate, 2);   // 30 days/yr
        decimal total = tier1 + tier2;

        // UAE max = 2 years gross salary; approximate as 24 months basic for simplicity
        decimal maxCap = basic * 24m;
        total = Math.Min(total, maxCap);
        total = Math.Round(total, 2);

        var bd = new List<EndOfServiceBreakdown>
        {
            new($"Tier 1 ({tier1Years:F4} yrs × 21 days × {dailyRate:F2}/day)", tier1),
            new($"Tier 2 ({tier2Years:F4} yrs × 30 days × {dailyRate:F2}/day)", tier2),
        };

        return await Task.FromResult(new EndOfServiceResult(total, "UAE-LaborLaw-Art51", bd));
    }

    internal static decimal ServiceYears(DateOnly start, DateOnly end)
    {
        int totalDays = end.DayNumber - start.DayNumber;
        return totalDays / 365m;
    }
}

// ── UAE DIFC DEWS (end-of-service) calculator ────────────────────────────────
// DIFC Employment Law (Law 2/2019 + 4/2020): employer makes monthly contributions
// to the DEWS (DIFC Employee Workplace Savings) scheme instead of paying a lump
// sum on termination.
// Tier 1 (≤5 yrs): 5.83% of monthly basic.
// Tier 2 (>5 yrs): 8.33% of monthly basic.
// This calc returns the TOTAL ACCRUED contribution for the full service period
// (months × applicable monthly rate × basic) rather than a termination lump sum.
// Source: DIFC Law 2/2019, Schedule 1.  VERIFY: current DEWS rates and any amendments.

public sealed class UaeDifcEndOfServiceCalculator : IEndOfServiceCalculator
{
    private readonly IStatutoryRuleReader _rules;
    public UaeDifcEndOfServiceCalculator(IStatutoryRuleReader rules) => _rules = rules;

    public async Task<EndOfServiceResult> CalculateAsync(
        EndOfServiceInput input, CancellationToken ct = default)
    {
        var eff = new DateOnly(input.ServiceEndDate.Year, input.ServiceEndDate.Month, 1);

        decimal tier1Rate = await _rules.GetDecimalAsync(
            CountryCodes.UAE, Jurisdictions.Difc, "dews.tier1_monthly_rate", eff, null, ct)
            ?? 0.0583m;  // VERIFY: 5.83% per DIFC Law 2/2019 Schedule 1

        decimal tier2Rate = await _rules.GetDecimalAsync(
            CountryCodes.UAE, Jurisdictions.Difc, "dews.tier2_monthly_rate", eff, null, ct)
            ?? 0.0833m;  // VERIFY: 8.33% per DIFC Law 2/2019 Schedule 1

        decimal basic = input.Salary.Basic;
        decimal serviceYears = UaeMainlandEndOfServiceCalculator.ServiceYears(
            input.ServiceStartDate, input.ServiceEndDate);

        int totalMonths = (int)Math.Floor(serviceYears * 12m);
        int tier1Months = Math.Min(totalMonths, 60); // first 5 years = 60 months
        int tier2Months = Math.Max(0, totalMonths - 60);

        decimal tier1Accrual = Math.Round(tier1Months * basic * tier1Rate, 2);
        decimal tier2Accrual = Math.Round(tier2Months * basic * tier2Rate, 2);
        decimal total = tier1Accrual + tier2Accrual;

        var bd = new List<EndOfServiceBreakdown>
        {
            new($"DEWS Tier 1 ({tier1Months} months × {tier1Rate:P2})", tier1Accrual),
            new($"DEWS Tier 2 ({tier2Months} months × {tier2Rate:P2})", tier2Accrual),
        };

        return new(total, "DEWS-monthly-contribution", bd);
    }
}

// ── UAE Emiratisation + Nafis nationalization tracker ────────────────────────
// Target ratio seeded as a directional placeholder.
// Source: Nafis program (Cabinet Resolution 71/2022).
// VERIFY: sector and size-specific Emiratisation targets from MOHRE/Nafis.

public sealed class UaeNationalizationTracker : INationalizationTracker
{
    private readonly IStatutoryRuleReader _rules;
    public UaeNationalizationTracker(IStatutoryRuleReader rules) => _rules = rules;

    public async Task<NationalizationResult> GetStatusAsync(
        NationalizationInput input, CancellationToken ct = default)
    {
        decimal targetD = await _rules.GetDecimalAsync(
            CountryCodes.UAE, Jurisdictions.UAEMainland,
            "emiratisation.target_ratio", DateOnly.FromDateTime(DateTime.UtcNow), null, ct)
            ?? 0.10m;  // VERIFY: sector-specific Emiratisation target

        double target  = (double)targetD;
        double current = input.TotalHeadcount == 0
            ? 0d
            : (double)input.NationalHeadcount / input.TotalHeadcount;

        var status = input.TotalHeadcount == 0
            ? NationalizationComplianceStatus.NotApplicable
            : current >= target
                ? NationalizationComplianceStatus.Compliant
                : current >= target * 0.8
                    ? NationalizationComplianceStatus.AtRisk
                    : NationalizationComplianceStatus.NonCompliant;

        return new(target, current, input.TotalHeadcount, input.NationalHeadcount, status,
            "Emiratisation+Nafis");
    }
}
