using Zayra.Api.Application.CountryPack;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

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

        // ── S1/A4: three classifications, not two ────────────────────────────────────────────────
        // The local IsSaudiNational matched only SAU/SA/Saudi, so a Bahraini fell through to the
        // expat branch and received 2% occupational hazard and nothing else. Meanwhile
        // PayrollValidationEngine derives a distinct "GCC" bucket from the SAME nationality string
        // and raises a BLOCKING error demanding GOSI for it — a deadlock the customer cannot exit,
        // and the only ways out were to void the run or to override the validation and file short.
        //
        // Under the GCC Unified Insurance Extension Scheme a GCC national employed in Saudi Arabia is
        // insured under their HOME state's scheme at the HOME state's rates, collected by GOSI. Those
        // rates live in Kuwait/Oman/Bahrain packs that do not exist. So the honest behaviour is to
        // apply the home-state rates IF a tenant has configured them, and otherwise to contribute
        // NOTHING and let the validator block with a named, actionable reason — never to silently
        // apply expat treatment, which is a wrong answer dressed as a right one.
        var classification = GosiCalculationService.DeriveClassification(input.Nationality);
        bool isSaudi = classification == GosiClassifications.Saudi;
        bool isGcc   = classification == GosiClassifications.GCC;

        var lines = new List<StatutoryDeductionLine>();
        decimal empTotal = 0m, erTotal = 0m;

        if (isGcc)
        {
            var home = GosiCalculationService.DeriveGccHomeState(input.Nationality) ?? string.Empty;

            decimal? gccEmp = await _rules.GetDecimalAsync(
                CountryCodes.Saudi, Jurisdictions.KsaMainland,
                RuleKeys.GosiGccEmployeeRate(home), eff, null, ct);
            decimal? gccEr = await _rules.GetDecimalAsync(
                CountryCodes.Saudi, Jurisdictions.KsaMainland,
                RuleKeys.GosiGccEmployerRate(home), eff, null, ct);

            if (gccEmp is null || gccEr is null)
                // No lines at all. The run will be blocked by GOSI_GCC_SCHEME_NOT_CONFIGURED, which
                // names the two rule keys to seed. Blocking is the right outcome: filing a Saudi
                // payroll that under-contributes for a GCC national accrues back-contributions with a
                // monthly surcharge and costs the establishment its GOSI compliance certificate.
                return new(0m, 0m, lines);

            decimal gccEmpAmt = Math.Round(coveredWage * gccEmp.Value, 2);
            // The employer's share under the extension scheme is capped at what it would pay for a
            // Saudi; any excess over that cap is borne by the employee, not the employer.
            decimal saudiErCap = await _rules.GetDecimalAsync(
                CountryCodes.Saudi, Jurisdictions.KsaMainland,
                RuleKeys.GosiSaudiEmployerRate, eff, null, ct) ?? 0.09m;
            decimal erRateApplied = Math.Min(gccEr.Value, saudiErCap);
            decimal gccErAmt = Math.Round(coveredWage * erRateApplied, 2);
            decimal excessToEmployee = Math.Round(coveredWage * Math.Max(0m, gccEr.Value - saudiErCap), 2);

            lines.Add(new($"GOSI-GCC-{home}-EE", $"GCC Unified Scheme — {home} (Employee)", gccEmpAmt + excessToEmployee, 0m));
            lines.Add(new($"GOSI-GCC-{home}-ER", $"GCC Unified Scheme — {home} (Employer)", 0m, gccErAmt));

            decimal gccOhRate = await _rules.GetDecimalAsync(
                CountryCodes.Saudi, Jurisdictions.KsaMainland,
                RuleKeys.GosiExpOhRate, eff, null, ct) ?? 0.02m;
            decimal gccOh = Math.Round(coveredWage * gccOhRate, 2);
            lines.Add(new("GOSI-OH-ER", "Occupational Hazard (Employer)", 0m, gccOh));

            return new(gccEmpAmt + excessToEmployee, gccErAmt + gccOh, lines);
        }

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

    // S1/A4 — the local IsSaudiNational is GONE. Classification is now derived exclusively by
    // GosiCalculationService.DeriveClassification, the same function PayrollValidationEngine uses, so
    // the calculator and the validator can never again disagree about who owes GOSI.
}

// ── KSA EOSB calculator ───────────────────────────────────────────────────────
// S1/A1 — the award is computed on the LAST WAGE, not on basic salary.
//   Art. 84 M/51: the award is "calculated on the basis of the last wage".
//   Art. 2  M/51: "Wage" = "the basic wage plus all other due increments" — housing
//                 allowance is not arguable, and Saudi labour courts apply it consistently.
// The statutory FLOOR is therefore basic + housing, and it is NOT configurable: a tenant
// whose pay-component catalog flags fewer components than statute still gets basic + housing.
// Configuration can only ever raise the base (EndOfServiceInput.ConfiguredEosbWage).
// Tier 1 (years ≤ 5): ½ month wage per year of service.
// Tier 2 (years > 5): 1 month wage per year, applied to the entire tenure.
// Termination (employer-initiated / end-of-contract / retirement): full award from
//   the first day of service (Art.84) — no minimum-service gate.
// Resignation reduction per KSA Labor Law Art. 85 (tenure-band scale).
// Dismissal for grave fault per KSA Labor Law Art. 80: FULL forfeiture (nil award).
// Source: KSA Labor Law Royal Decree M/51 2005 + amendments (Art.80/84/85).

public sealed class KsaEndOfServiceCalculator : IEndOfServiceCalculator
{
    private readonly IStatutoryRuleReader _rules;
    public KsaEndOfServiceCalculator(IStatutoryRuleReader rules) => _rules = rules;

    public async Task<EndOfServiceResult> CalculateAsync(
        EndOfServiceInput input, CancellationToken ct = default)
    {
        var eff = input.ServiceEndDate;
        var notices = new List<string>();

        // ── A1: the Art. 84 "last wage" base ──────────────────────────────────────────────────────
        // Statutory floor = basic + housing (Art. 2 "basic wage plus all other due increments").
        // Never configurable — a tenant cannot contract out of the Labour Law.
        decimal statutoryFloor = input.Salary.Basic + input.Salary.HousingAllowance;

        // [COUNSEL] Transport: a FIXED monthly transport allowance is due irrespective of expenditure,
        // so on my reading of Art. 2 it is an "increment" and part of the wage — hence the default is
        // to include it. It is the genuinely arguable component (a reimbursive travel float is not a
        // wage increment), so it is a rule with an effective date, not a literal, and its inclusion is
        // announced on the settlement rather than applied silently.
        bool includeTransport = await Flag(RuleKeys.EosbIncludeTransport, true, eff, ct);
        // [COUNSEL] "Other allowances" is a COMPOSITE of food + mobile + other in this data model. It
        // mixes regular cash increments with reimbursive items, so it cannot be assumed to be wage and
        // defaults OUT. A tenant whose "other" is genuinely a regular cash allowance turns it on — or,
        // better, models it as its own EosbIncluded pay component, which raises the base via
        // ConfiguredEosbWage without this blunt switch.
        bool includeOther = await Flag(RuleKeys.EosbIncludeOtherAllowances, false, eff, ct);

        decimal statutoryBase = statutoryFloor
            + (includeTransport ? input.Salary.TransportAllowance : 0m)
            + (includeOther ? input.Salary.OtherAllowances : 0m);

        // Tenant configuration can only RAISE the base above statute, never lower it.
        decimal wage = Math.Max(statutoryBase, input.ConfiguredEosbWage);

        if (includeTransport && input.Salary.TransportAllowance > 0m)
            notices.Add($"[COUNSEL-KSA] The transport allowance ({input.Salary.TransportAllowance:N2}) is INCLUDED in the " +
                        "Art. 84 last-wage base by default (Art. 2: \"the basic wage plus all other due increments\"). " +
                        "Housing is settled law and is always included; transport is the arguable component. If your " +
                        "transport allowance is reimbursive rather than a fixed monthly entitlement, set the statutory " +
                        $"rule '{RuleKeys.EosbIncludeTransport}' to false for SAU/KSA-mainland and recompute.");
        if (!includeOther && input.Salary.OtherAllowances > 0m)
            notices.Add($"[COUNSEL-KSA] Other allowances ({input.Salary.OtherAllowances:N2}) are EXCLUDED from the Art. 84 " +
                        "last-wage base, because this field composites food/mobile/other and may contain reimbursive " +
                        "items. Any of them that is a regular, non-discretionary cash increment IS part of the wage under " +
                        $"Art. 2 — model it as its own EOSB-included pay component, or set '{RuleKeys.EosbIncludeOtherAllowances}' " +
                        "to true. Under-stating the base is the direction that gets litigated.");

        // ── A8: unpaid leave and the service period ───────────────────────────────────────────────
        (int fullMonths, int remDays) = ServicePeriod(input.ServiceStartDate, input.ServiceEndDate);
        decimal serviceYears = fullMonths / 12m + remDays / 365m;
        decimal grossServiceYears = serviceYears;

        // [CONF] KSA turns on "continuous service" rather than an express exclusion like UAE Art. 51,
        // so the exclusion defaults OFF here and is a rule, not a literal. Turning it on is the
        // employer-favourable direction, which is exactly why it must be a conscious decision.
        bool excludeUnpaid = await Flag(RuleKeys.EosbExcludeUnpaidLeave, false, eff, ct);
        if (input.UnpaidLeaveDays > 0)
        {
            if (excludeUnpaid)
            {
                serviceYears = Math.Max(0m, serviceYears - input.UnpaidLeaveDays / 365m);
                notices.Add($"[CONF-KSA] {input.UnpaidLeaveDays} day(s) of unpaid leave were EXCLUDED from the service " +
                            $"period ({grossServiceYears:F4} yrs → {serviceYears:F4} yrs) because the statutory rule " +
                            $"'{RuleKeys.EosbExcludeUnpaidLeave}' is set. Unlike UAE Art. 51 this is not express KSA " +
                            "statute — it rests on the \"continuous service\" reading. Confirm with counsel.");
            }
            else
            {
                notices.Add($"[CONF-KSA] {input.UnpaidLeaveDays} day(s) of unpaid leave are INCLUDED in the service period " +
                            "for gratuity. KSA has no express exclusion (contrast UAE Art. 51), so the award — and the " +
                            "EOSB provision on the balance sheet — is measured on elapsed calendar service. Set " +
                            $"'{RuleKeys.EosbExcludeUnpaidLeave}' to true if counsel advises the \"continuous service\" reading.");
            }
        }

        // ── S1: the AWARD RATES are read, not hard-coded ──────────────────────────────────────────
        // This calculator used to open with `_ = _rules; // rates not needed for EOSB formula` and
        // multiply by literal 0.5 / 1.0 month. Meanwhile the Saudi compliance-config screen let a
        // customer edit "Rate: Years 1–5 (days/yr)" and "Rate: Years 5+ (days/yr)" — and nothing read
        // them, while the EosbEnabled toggle next to them worked. The rates are expressed in DAYS PER
        // YEAR on a /30 day-rate, which is exactly the Art. 84 half-month / full-month scale:
        //   15 days/yr for the first five years, 30 days/yr thereafter.
        // Statutory FLOORS. A company may enhance above them (common, and lawful — see
        // StatutoryRateGuard's `eosb.enhancement_days_per_year`); it may not go under them.
        decimal tier1Days = await Days(RuleKeys.EosbTier1DaysPerYear, 15m, eff, ct);
        decimal tier2Days = await Days(RuleKeys.EosbTier2DaysPerYear, 30m, eff, ct);
        (tier1Days, tier2Days) = ApplyPolicyRates(input.Policy, tier1Days, tier2Days, notices);

        // ── S1: the MINIMUM-SERVICE GATE ──────────────────────────────────────────────────────────
        // Deliberately NOT applied as a nil in KSA, and that is the whole point of implementing it.
        // Art. 84 grants the award from the FIRST DAY of service on an employer-initiated termination,
        // end of contract or retirement — there is no lawful minimum-service threshold to configure.
        // The only KSA tenure threshold is Art. 85's RESIGNATION scale, which is a different mechanism
        // (a reduction, not a gate) and is applied below, untouched. Honouring a configured 1-year
        // minimum would deny a lawful entitlement, so it is refused and named.
        if (input.Policy?.MinServiceYears is int minYears && minYears > 0 && serviceYears < minYears)
            notices.Add($"[CERT-KSA] This company is configured with a {minYears}-year EOSB minimum-service " +
                        $"requirement and this leaver has {serviceYears:F2} years, but the award has NOT been " +
                        "withheld. KSA Labour Law Art. 84 grants the end-of-service award from the first day of " +
                        "service — pro-rated, with no minimum-service gate — on an employer-initiated termination, " +
                        "end of contract or retirement. Withholding it would be an unlawful denial. The only KSA " +
                        "tenure threshold is the Art. 85 RESIGNATION scale (nil under 2 years, ⅓ to 5, ⅔ to 10), " +
                        "which is a reduction rather than a gate and is applied separately. Clear the " +
                        "minimum-service field for SA, or record it as a non-statutory internal policy.");

        decimal tier1Years = Math.Min(serviceYears, 5m);
        decimal tier2Years = Math.Max(0m, serviceYears - 5m);

        decimal tier1 = Math.Round(tier1Years * (tier1Days / 30m) * wage, 2);
        decimal tier2 = Math.Round(tier2Years * (tier2Days / 30m) * wage, 2);
        decimal totalBeforeDiscount = tier1 + tier2;

        decimal total = ApplyReasonAdjustment(totalBeforeDiscount, serviceYears, input.TerminationReason);
        total = Math.Round(total, 2);

        var bd = new List<EndOfServiceBreakdown>
        {
            new($"Tier 1 ({tier1Years:F4} yrs × {tier1Days:0.##} days/yr × {wage:N2} last wage ÷ 30)", tier1),
            new($"Tier 2 ({tier2Years:F4} yrs × {tier2Days:0.##} days/yr × {wage:N2} last wage ÷ 30)", tier2),
        };
        if (total != totalBeforeDiscount)
        {
            var adjustmentLabel = IsDismissalForCause(input.TerminationReason)
                ? "Art.80 forfeiture (dismissal for grave fault)"
                : "Art.85 resignation reduction";
            bd.Add(new(adjustmentLabel, total - totalBeforeDiscount));
        }

        return new EndOfServiceResult(total, "KSA-LaborLaw-Art84", bd) { Notices = notices, AppliedWageBase = wage };

        async Task<bool> Flag(string key, bool fallback, DateOnly on, CancellationToken token)
            => await StatutoryFlag.ReadAsync(_rules, CountryCodes.Saudi, Jurisdictions.KsaMainland, key, on, fallback, token);

        async Task<decimal> Days(string key, decimal fallback, DateOnly on, CancellationToken token)
            => await _rules.GetDecimalAsync(CountryCodes.Saudi, Jurisdictions.KsaMainland, key, on, null, token) ?? fallback;
    }

    /// <summary>
    /// Applies the tenant's configured days-per-year rates as an ENHANCEMENT only. A configured rate
    /// above the statutory floor is honoured — enhancing end-of-service above the Art. 84 minimum is
    /// lawful and common. A configured rate BELOW the floor is refused and named, because the floor
    /// is the law and a settings screen is not a way around it.
    /// </summary>
    internal static (decimal Tier1, decimal Tier2) ApplyPolicyRates(
        EosbPolicyOverride? policy, decimal floorTier1, decimal floorTier2, List<string> notices)
    {
        if (policy is null) return (floorTier1, floorTier2);

        var t1 = Resolve(policy.Tier1DaysPerYear, floorTier1, "Years 1–5");
        var t2 = Resolve(policy.Tier2DaysPerYear, floorTier2, "Years 5+");
        return (t1, t2);

        decimal Resolve(decimal? configured, decimal floor, string label)
        {
            if (configured is not decimal c || c <= 0m) return floor;   // not configured
            if (c >= floor)
            {
                if (c > floor)
                    notices.Add($"[POLICY-KSA] The configured '{label}' end-of-service rate of {c:0.##} days/year " +
                                $"is an ENHANCEMENT above the Art. 84 statutory floor of {floor:0.##} days/year and " +
                                "has been applied. This is a contractual benefit, not a statutory one.");
                return c;
            }
            notices.Add($"[CERT-KSA] The configured '{label}' end-of-service rate of {c:0.##} days/year is BELOW the " +
                        $"KSA Art. 84 statutory floor of {floor:0.##} days/year, so it has been IGNORED and the floor " +
                        "applied. Art. 84 awards half a month's wage per year for the first five years and a full " +
                        "month per year thereafter; that is a minimum, not a default. Correct the configuration.");
            return floor;
        }
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

    // Applies the reason-driven adjustment to the full two-tier award (Art.84 base):
    //   • Art.80 dismissal for grave fault → nil (full forfeiture).
    //   • Art.85 resignation → tenure-band reduction (nil < 2yr; ⅓ for 2–5yr inclusive;
    //     ⅔ for >5 and <10yr; full ≥ 10yr).
    //   • All other reasons (Termination / EndOfContract / Retirement / …) → full award.
    private static decimal ApplyReasonAdjustment(decimal total, decimal years, string reason)
    {
        // Art.80: summary dismissal for grave fault forfeits the entire gratuity.
        if (IsDismissalForCause(reason)) return 0m;

        // Art.85: resignation reduction scale. Non-resignation reasons keep the full award.
        if (!string.Equals(reason, "Resignation", StringComparison.OrdinalIgnoreCase)) return total;
        if (years < 2m)  return 0m;                          // < 2 yrs → nil
        if (years <= 5m) return Math.Round(total / 3m, 2);   // 2–5 yrs inclusive → ⅓ (Art.85)
        if (years < 10m) return Math.Round(total * 2m / 3m, 2); // > 5 and < 10 yrs → ⅔
        return total;                                        // ≥ 10 yrs → full
    }

    private static bool IsDismissalForCause(string reason)
        => string.Equals(reason, "Article80", StringComparison.OrdinalIgnoreCase)
        || string.Equals(reason, "DismissalForCause", StringComparison.OrdinalIgnoreCase);
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
    // S1/A4 — GCC Unified Insurance Extension Scheme. A GCC national employed in KSA is insured under
    // their HOME state's scheme at the HOME state's rates, collected by GOSI. There is no single "GCC
    // rate", so these keys are per home state: gosi.gcc.BH.employee_rate, gosi.gcc.KW.employer_rate, …
    // Nothing is seeded — the rates belong to Kuwait/Oman/Bahrain/Qatar/UAE packs that do not exist yet.
    public static string GosiGccEmployeeRate(string homeIso2) => $"gosi.gcc.{homeIso2.ToUpperInvariant()}.employee_rate";
    public static string GosiGccEmployerRate(string homeIso2) => $"gosi.gcc.{homeIso2.ToUpperInvariant()}.employer_rate";
    // S1/A1, A8 — KSA EOSB wage base and service period. See KsaEndOfServiceCalculator.
    public const string EosbIncludeTransport       = "eosb.include_transport";        // [COUNSEL] default TRUE (Art.2 increment)
    public const string EosbIncludeOtherAllowances = "eosb.include_other_allowances"; // [COUNSEL] default FALSE (composite field)
    public const string EosbExcludeUnpaidLeave     = "eosb.exclude_unpaid_leave";     // [CONF] default FALSE for KSA
    // S1 — Art.84 award scale, expressed in DAYS PER YEAR on a /30 day-rate. Statutory FLOORS.
    public const string EosbTier1DaysPerYear       = "eosb.tier1_days_per_year";      // 15 = ½ month/yr, first 5 years
    public const string EosbTier2DaysPerYear       = "eosb.tier2_days_per_year";      // 30 = 1 month/yr thereafter
    // KSA Nationalization
    public const string NitaqatTargetRatio        = "nitaqat.default_target_ratio";
    // KSA OT / LOP — FLAG FOR SAUDI COMPLIANCE SIGN-OFF before any production filing.
    // OT multiplier: KSA Labor Law Art.107 mandates 1.5× for regular OT hours.
    // Weekend/holiday multipliers may differ — encode separately if required.
    // LOP day-rate: basic ÷ 30 is a common KSA practice but court precedent varies.
    // GOSI treatment of OT and LOP effect on covered wage — flagged separately in Process.
    public const string OtStandardMultiplier          = "ot.standard_multiplier";           // FLAG: 1.5 per Art.107 — VERIFY
    public const string OtStandardMonthlyHours        = "ot.standard_monthly_hours";        // FLAG: 240 (30d × 8h) — depends on contract
    public const string LopMonthlyDayDivisor          = "lop.monthly_day_divisor";          // FLAG: 30 — common KSA practice, VERIFY
    public const string LopStandardWorkMinutesPerDay  = "lop.standard_work_minutes_per_day"; // FLAG: 480 (8h) — depends on contract
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
