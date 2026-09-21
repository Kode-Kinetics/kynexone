using Zayra.Api.Application.CountryPack;

namespace Zayra.Api.Infrastructure.CountryPack.Qatar;

// ── Qatar GRSIA deduction calculator ─────────────────────────────────────────
// Qatar nationals: employee 7% + employer 14%.
// Expatriates: no statutory social insurance contribution.
// S1/A11 — Source is Social Insurance Law No. 1 of 2022 (in force January 2023), NOT Law 24/2002,
// which this header cited and which was superseded. The rates were right; the contribution-salary
// BASE was wrong (basic only, where Law 1/2022 uses basic + social + housing) and there was neither
// a floor nor a cap. VERIFY the floor and cap, and the treatment of in-kind housing, with GRSIA.

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

        // ── S1/A11: the contribution salary under Law 1/2022 ─────────────────────────────────────
        // The 7%/14% rates were right; the BASE and the citation were wrong. Social Insurance Law
        // No. 1 of 2022 (in force January 2023, replacing Law 24/2002, which this file's header still
        // cited) defines the contribution salary for Qatari nationals as basic salary plus the social
        // allowance plus the housing allowance — so a flat basic-only base under-contributes for
        // essentially every Qatari national, all of whom have a housing allowance. [CONF] on the base
        // composition; [COUNSEL] on in-kind housing and on the current floor and cap.
        //
        // Effective-dated rather than switched: pre-2023 periods stay on Law 24/2002's basic-only
        // base, so re-running an old period still reproduces what was filed at the time. A SOCIAL
        // allowance has no field in this data model — the breakdown carries basic/housing/transport/
        // other — so only housing is added. A tenant that pays a social allowance must model it as
        // housing or as its own component; noted as an open gap rather than silently approximated.
        bool useLaw2022Base = await StatutoryFlag.ReadAsync(
            _rules, CountryCodes.Qatar, Jurisdictions.QatarMainland,
            "grsia.include_housing_in_contribution_salary", eff, eff >= new DateOnly(2023, 1, 1), ct);
        decimal contributionSalary = useLaw2022Base
            ? input.Salary.Basic + input.Salary.HousingAllowance
            : input.Salary.Basic;

        // [COUNSEL] statutory floor and cap. Absent / zero means "no bound", i.e. pre-S1 behaviour.
        decimal floor = await _rules.GetDecimalAsync(
            CountryCodes.Qatar, Jurisdictions.QatarMainland,
            "grsia.contribution_salary_min", eff, null, ct) ?? 0m;
        decimal cap = await _rules.GetDecimalAsync(
            CountryCodes.Qatar, Jurisdictions.QatarMainland,
            "grsia.contribution_salary_max", eff, null, ct) ?? 0m;
        if (floor > 0m && contributionSalary < floor) contributionSalary = floor;
        if (cap   > 0m && contributionSalary > cap)   contributionSalary = cap;

        decimal empRate = await _rules.GetDecimalAsync(
            CountryCodes.Qatar, Jurisdictions.QatarMainland,
            "grsia.national_employee_rate", eff, null, ct) ?? 0.07m;   // 7% — correct and current

        decimal erRate = await _rules.GetDecimalAsync(
            CountryCodes.Qatar, Jurisdictions.QatarMainland,
            "grsia.national_employer_rate", eff, null, ct) ?? 0.14m;   // 14% — correct and current

        decimal empContrib = Math.Round(contributionSalary * empRate, 2);
        decimal erContrib  = Math.Round(contributionSalary * erRate, 2);

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
        // S1/A1 — Qatar Art. 54 measures the award on the BASIC wage, so this pack reads Salary.Basic
        // and is deliberately unaffected by the KSA Art. 84 last-wage fix in the same stream.
        decimal basic = input.Salary.Basic;
        decimal dailyRate = basic / 30m;

        var notices = new List<string>();
        int totalDays = input.ServiceEndDate.DayNumber - input.ServiceStartDate.DayNumber;
        decimal serviceYears = totalDays / 365m;

        // ── S1/A8: unpaid leave and the service period ────────────────────────────────────────────
        // [CONF] Qatar turns on "continuous service" with no express exclusion like UAE Art. 51, so the
        // exclusion defaults OFF and is a rule, not a literal. Including the days over-states the award
        // and the provision; excluding them without counsel under-pays. Either way it is now VISIBLE.
        if (input.UnpaidLeaveDays > 0)
        {
            bool exclude = await StatutoryFlag.ReadAsync(
                _rules, CountryCodes.Qatar, Jurisdictions.QatarMainland,
                "eosb.exclude_unpaid_leave", input.ServiceEndDate, false, ct);
            if (exclude)
            {
                var before = serviceYears;
                serviceYears = Math.Max(0m, serviceYears - input.UnpaidLeaveDays / 365m);
                notices.Add($"[CONF-QAT] {input.UnpaidLeaveDays} day(s) of unpaid leave were EXCLUDED from the service " +
                            $"period ({before:F4} yrs → {serviceYears:F4} yrs) because 'eosb.exclude_unpaid_leave' is " +
                            "set for QAT/QAT-mainland. Qatar has no express exclusion — confirm with counsel.");
            }
            else
            {
                notices.Add($"[CONF-QAT] {input.UnpaidLeaveDays} day(s) of unpaid leave are INCLUDED in the service " +
                            "period for gratuity. Qatar has no express exclusion (contrast UAE Art. 51), so the award " +
                            "is measured on elapsed calendar service and the EOSB provision is correspondingly higher. " +
                            "Set 'eosb.exclude_unpaid_leave' if counsel advises the \"continuous service\" reading.");
            }
        }

        // Qatar Law 14/2004 Art.54 (as amended by Law 19/2020): EOS gratuity requires at
        // least ONE completed year of service. This eligibility floor lives in the pack
        // (single engine) — the controller no longer pre-gates on GCC minYears.
        // S1 — the configured minimum-service years. Art. 54's gate is ONE completed year; a higher
        // configured minimum would deny a statutory entitlement, so it is refused and named.
        if (input.Policy?.MinServiceYears is int minYears && minYears > 1 && serviceYears >= 1m && serviceYears < minYears)
            notices.Add($"[CERT-QAT] This company is configured with a {minYears}-year EOSB minimum-service " +
                        $"requirement and this leaver has {serviceYears:F2} years, but the award has NOT been " +
                        "withheld. Qatar Law 14/2004 Art. 54 (as amended by Law 19/2020) sets the gate at ONE " +
                        "completed year and it cannot be raised by configuration.");

        if (serviceYears < 1m)
            return new EndOfServiceResult(
                0m, "Qatar-LaborLaw-14-2004-Art54",
                new List<EndOfServiceBreakdown> { new("No entitlement (< 1 year service, Art.54)", 0m) })
            { Notices = notices, AppliedWageBase = basic };

        // Minimum 3 weeks = 21 days per year of service
        decimal totalDaysEntitled = Math.Round(serviceYears * 21m, 4);
        decimal total = Math.Round(totalDaysEntitled * dailyRate, 2);

        var bd = new List<EndOfServiceBreakdown>
        {
            new($"{serviceYears:F4} yrs × 21 days × {dailyRate:F2} QAR/day", total),
        };

        return new EndOfServiceResult(total, "Qatar-LaborLaw-14-2004-Art54", bd)
        { Notices = notices, AppliedWageBase = basic };
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
