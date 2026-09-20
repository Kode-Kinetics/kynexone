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

        // ── S1/A10: the GPSSA contribution account salary has a FLOOR and a CEILING ───────────────
        // This calculator applied 5%/12.5% to the whole of basic + housing with no bound read at all,
        // in contrast to KsaDeductionCalculator two directories away, which has always applied the
        // 45,000 GOSI ceiling. An Emirati executive on AED 120,000 basic + housing had 5% deducted
        // from the entire amount — an OVER-deduction from the employee's net pay, which under Art. 25
        // of Decree-Law 33/2021 is an unlawful deduction. That is the rarer and worse direction of
        // error: the employee is out of pocket this month, not in thirty years.
        //
        // [COUNSEL] on the exact figures — the widely published Law 7/1999 private-sector bounds are
        // AED 1,000 and AED 50,000, and Decree-Law 57/2023 carries its own. They are effective-dated
        // rules, not literals, so a circular can be back-dated when counsel supplies it. A rule set to
        // zero or absent means "no bound", which preserves the pre-S1 behaviour for a tenant the
        // seeder has not reached.
        decimal gpssaBase = input.Salary.GpssaBase;
        decimal floor = await _rules.GetDecimalAsync(
            CountryCodes.UAE, Jurisdictions.UAEMainland,
            "gpssa.contribution_salary_min", eff, null, ct) ?? 0m;
        decimal cap = await _rules.GetDecimalAsync(
            CountryCodes.UAE, Jurisdictions.UAEMainland,
            "gpssa.contribution_salary_max", eff, null, ct) ?? 0m;
        if (floor > 0m && gpssaBase < floor) gpssaBase = floor;
        if (cap   > 0m && gpssaBase > cap)   gpssaBase = cap;

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
        // S1/A1 — UAE gratuity IS basic-salary-only. Art. 51 is explicit, and this is the OPPOSITE of
        // the KSA rule fixed in the same stream: a client who insists UAE gratuity should include
        // allowances is wrong. Reading Salary.Basic (rather than a pre-collapsed scalar) is what makes
        // the pack immune to a tenant flagging HOUSING into the EOSB component catalog for KSA's sake.
        decimal basic = input.Salary.Basic;
        decimal dailyRate = basic / 30m;

        var notices = new List<string>();
        decimal serviceYears = ServiceYears(input.ServiceStartDate, input.ServiceEndDate);

        // ── S1/A8: unpaid leave is EXCLUDED from the service period ───────────────────────────────
        // Decree-Law 33/2021 Art. 51 says so expressly. [CERT]. It is still expressed as an
        // effective-dated rule rather than a literal so a jurisdiction change can be back-dated, but
        // the default is the statute and turning it OFF over-states both the award and the provision.
        if (input.UnpaidLeaveDays > 0)
        {
            bool exclude = await StatutoryFlag.ReadAsync(
                _rules, CountryCodes.UAE, Jurisdictions.UAEMainland,
                "eosb.exclude_unpaid_leave", input.ServiceEndDate, true, ct);
            if (exclude)
            {
                var before = serviceYears;
                serviceYears = Math.Max(0m, serviceYears - input.UnpaidLeaveDays / 365m);
                notices.Add($"[CERT-UAE] {input.UnpaidLeaveDays} day(s) of unpaid leave were EXCLUDED from the " +
                            $"service period ({before:F4} yrs → {serviceYears:F4} yrs) per Decree-Law 33/2021 " +
                            "Art. 51, which excludes unpaid leave from the service period for gratuity expressly.");
            }
            else
            {
                notices.Add($"[CERT-UAE] {input.UnpaidLeaveDays} day(s) of unpaid leave are being INCLUDED in the " +
                            "service period because the rule 'eosb.exclude_unpaid_leave' has been turned off for " +
                            "ARE/UAE-mainland. Decree-Law 33/2021 Art. 51 excludes them expressly — this over-states " +
                            "the gratuity and the EOSB provision. Confirm with counsel or restore the default.");
            }
        }

        // UAE Decree-Law 33/2021 Art.51: EOSB gratuity accrues only after ONE completed
        // year of service. This eligibility floor lives in the pack (single engine) — the
        // controller no longer pre-gates on GCC minYears. (DIFC/DEWS accrues from month 1
        // and is handled by UaeDifcEndOfServiceCalculator, which has no such floor.)
        // S1 — the configured minimum-service years, honoured only where lawful. Art. 51's own gate is
        // ONE completed year; a company that configures a higher minimum would be denying a statutory
        // entitlement to anyone between the two, so the excess is refused and named rather than applied.
        if (input.Policy?.MinServiceYears is int minYears && minYears > 1 && serviceYears >= 1m && serviceYears < minYears)
            notices.Add($"[CERT-UAE] This company is configured with a {minYears}-year EOSB minimum-service " +
                        $"requirement and this leaver has {serviceYears:F2} years, but the award has NOT been " +
                        "withheld. UAE Decree-Law 33/2021 Art. 51 sets the gate at ONE completed year and a company " +
                        "cannot raise it by configuration. Correct the setting or record it as a non-statutory " +
                        "internal policy.");

        if (serviceYears < 1m)
            return new EndOfServiceResult(
                0m, "UAE-LaborLaw-Art51",
                new List<EndOfServiceBreakdown> { new("No entitlement (< 1 year service, Art.51)", 0m) })
            { Notices = notices, AppliedWageBase = basic };

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

        return new EndOfServiceResult(total, "UAE-LaborLaw-Art51", bd)
        { Notices = notices, AppliedWageBase = basic };
    }

    internal static decimal ServiceYears(DateOnly start, DateOnly end)
    {
        int totalDays = end.DayNumber - start.DayNumber;
        return totalDays / 365m;
    }
}

// ── UAE DIFC end-of-service calculator (post-DEWS) ───────────────────────────
// S1/A9 — THIS CALCULATOR USED TO PRODUCE A NUMBER THE EMPLOYER MUST NOT PAY.
//
// Since 1 February 2020 a DIFC employer does NOT accrue a termination gratuity for post-cut-over
// service. It remits a MONTHLY contribution (5.83% of basic for the first five years of service,
// 8.33% thereafter) to DEWS or another Qualifying Scheme, and the TRUSTEE — not the employer —
// pays the employee their vested account value, including investment return, on termination.
//
// The previous implementation computed months × basic × rate across the WHOLE service period and
// returned it as EndOfServiceResult.TotalGratuity, the same field the mainland calculator uses for
// a payable cash lump sum and which the final-settlement pipeline turns into a payable line. A DIFC
// customer paying that figure pays it ON TOP of the trustee's payout — roughly AED 90,000 twice for
// a 6-year employee on AED 20,000 basic. The old code comment was accurate; the plumbing was not.
//
// What this now returns as the PAYABLE gratuity is the GRANDFATHERED portion only: service up to
// 31 January 2020, under the pre-DEWS DIFC Law 4/2005 scale (21 days' basic per year for the first
// five years, 30 days thereafter), measured on the basic wage. Post-cut-over service is reported in
// the breakdown at ZERO and named in a notice, so the settlement screen states the trustee position
// instead of inventing a cash figure.
//
// Source: DIFC Employment Law 2/2019 as amended by 4/2020, Schedule 1; DIFC Law 4/2005 Art. 62 for
// the grandfathered portion. [CERT] on the mechanism.
// STILL MISSING and OUT OF SCOPE for this stream: the monthly employer DEWS contribution line in
// the payroll run, the remittance file, and arrears tracking. DEWS is a monthly employer
// contribution and belongs in the statutory-deduction path, not in IEndOfServiceCalculator.

public sealed class UaeDifcEndOfServiceCalculator : IEndOfServiceCalculator
{
    /// <summary>1 February 2020 — the date DIFC replaced the termination gratuity with DEWS.</summary>
    internal static readonly DateOnly DewsCutOver = new(2020, 2, 1);

    private readonly IStatutoryRuleReader _rules;
    public UaeDifcEndOfServiceCalculator(IStatutoryRuleReader rules) => _rules = rules;

    public async Task<EndOfServiceResult> CalculateAsync(
        EndOfServiceInput input, CancellationToken ct = default)
    {
        var eff = new DateOnly(input.ServiceEndDate.Year, input.ServiceEndDate.Month, 1);
        var notices = new List<string>();
        var bd = new List<EndOfServiceBreakdown>();

        decimal tier1Rate = await _rules.GetDecimalAsync(
            CountryCodes.UAE, Jurisdictions.Difc, "dews.tier1_monthly_rate", eff, null, ct)
            ?? 0.0583m;  // VERIFY: 5.83% per DIFC Law 2/2019 Schedule 1

        decimal tier2Rate = await _rules.GetDecimalAsync(
            CountryCodes.UAE, Jurisdictions.Difc, "dews.tier2_monthly_rate", eff, null, ct)
            ?? 0.0833m;  // VERIFY: 8.33% per DIFC Law 2/2019 Schedule 1

        // DIFC gratuity, like UAE mainland, is on BASIC only.
        decimal basic = input.Salary.Basic;
        var cutOver = await CutOverAsync(eff, ct);

        // ── The grandfathered, EMPLOYER-PAYABLE portion: service before the cut-over ──────────────
        decimal grandfathered = 0m;
        if (input.ServiceStartDate < cutOver)
        {
            var preEnd = input.ServiceEndDate < cutOver ? input.ServiceEndDate : cutOver;
            decimal preYears = UaeMainlandEndOfServiceCalculator.ServiceYears(input.ServiceStartDate, preEnd);
            decimal dailyRate = basic / 30m;
            decimal t1 = Math.Round(Math.Min(preYears, 5m) * 21m * dailyRate, 2);
            decimal t2 = Math.Round(Math.Max(0m, preYears - 5m) * 30m * dailyRate, 2);
            grandfathered = Math.Round(t1 + t2, 2);
            bd.Add(new($"Grandfathered pre-{cutOver:yyyy-MM-dd} service ({preYears:F4} yrs, 21/30 days × {dailyRate:F2}/day)", grandfathered));
            notices.Add($"[CERT-DIFC] {preYears:F4} year(s) of service fall BEFORE the {cutOver:d MMMM yyyy} DEWS " +
                        "cut-over and remain an employer-paid lump-sum gratuity under the pre-DEWS DIFC law. " +
                        "[COUNSEL] confirm the split methodology and the basic-wage reference date for the " +
                        "grandfathered portion — this calculation uses the LAST basic wage, not the wage frozen " +
                        "at the cut-over date, which is the conservative (employee-favourable) reading.");
        }

        // ── The post-cut-over portion: a TRUSTEE obligation. Never an employer payable. ───────────
        if (input.ServiceEndDate > cutOver)
        {
            var postStart = input.ServiceStartDate > cutOver ? input.ServiceStartDate : cutOver;
            decimal postYears = UaeMainlandEndOfServiceCalculator.ServiceYears(postStart, input.ServiceEndDate);
            // Informational only: what the employer SHOULD have remitted monthly. Tiering is on TOTAL
            // service length, not on post-cut-over length, because the 5-year step is a service test.
            decimal totalYearsAtStart = UaeMainlandEndOfServiceCalculator.ServiceYears(input.ServiceStartDate, postStart);
            int postMonths = (int)Math.Floor(postYears * 12m);
            int monthsToFiveYears = Math.Max(0, 60 - (int)Math.Floor(totalYearsAtStart * 12m));
            int tier1Months = Math.Min(postMonths, monthsToFiveYears);
            int tier2Months = Math.Max(0, postMonths - tier1Months);
            decimal accrued = Math.Round(tier1Months * basic * tier1Rate, 2)
                            + Math.Round(tier2Months * basic * tier2Rate, 2);

            bd.Add(new($"DEWS-scheme service from {(postStart > cutOver ? postStart : cutOver):yyyy-MM-dd} " +
                       $"({tier1Months} mo × {tier1Rate:P2} + {tier2Months} mo × {tier2Rate:P2} ≈ {accrued:N2}) — " +
                       "PAID BY THE TRUSTEE, NOT BY THE EMPLOYER", 0m));
            notices.Add($"[CERT-DIFC] DO NOT PAY the DEWS portion as cash. {postYears:F4} year(s) of service fall " +
                        $"after the {cutOver:d MMMM yyyy} cut-over and are covered by the employee's DEWS (or other " +
                        "Qualifying Scheme) account, which the TRUSTEE pays out — including investment return — on " +
                        $"termination. The nominal employer contribution over that period was approximately " +
                        $"{accrued:N2}; the employee's actual entitlement is their vested account value, which this " +
                        "product does not hold. It is carried at ZERO in this settlement deliberately: paying it here " +
                        "would pay the employee twice. Reconcile against the trustee's statement.");
            notices.Add("[GAP-DIFC] This product does not yet model the employer's MONTHLY DEWS remittance " +
                        "obligation, the remittance file, or arrears. That obligation is real, enforceable and " +
                        "falls due by the payroll date each month; DIFCA fines for late or missed contributions. " +
                        "Track it outside the product until the monthly contribution line exists.");
        }

        return new EndOfServiceResult(grandfathered, "DIFC-EmploymentLaw-DEWS-2020-cutover", bd)
        { Notices = notices, AppliedWageBase = basic };

        async Task<DateOnly> CutOverAsync(DateOnly on, CancellationToken token)
        {
            // Effective-dated so the cut-over is data, not a compiled constant, but it defaults to the
            // statutory 1 Feb 2020 and nothing seeds an override.
            var raw = await _rules.GetStringAsync(CountryCodes.UAE, Jurisdictions.Difc, "dews.cutover_date", on, null, token);
            return DateOnly.TryParse(raw, out var d) ? d : DewsCutOver;
        }
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
