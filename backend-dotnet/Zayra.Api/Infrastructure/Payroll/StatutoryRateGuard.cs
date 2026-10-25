using Zayra.Api.Application.CountryPack;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// Central compliance boundary for the rates surface. Owned server-side so the free-CRUD /
/// bounded-override split can never drift from the statutory seeders.
///
/// Two responsibilities:
///  1. <see cref="IsStatutoryKey"/> — is a rate key statutory / Saudization / floored, and therefore
///     NOT eligible for Surface-A free CRUD? Prefix-based, case-folded, sourced from the real
///     StatutoryRuleSeeder / GosiContributionRule key families (gosi, saned, gpssa, dews, grsia,
///     wps, sif, nitaqat, emiratisation, qatarization, ot, lop, eosb).
///  2. <see cref="DefaultClientRateDefinitions"/> — the seeded ALLOW-list of client-configurable
///     (non-statutory) rate keys with typed bounds. Anything not in this list is not free-CRUD.
/// </summary>
public static class StatutoryRateGuard
{
    // Statutory / Saudization / statutory-floor prefixes. Anything under these is bounded-override
    // only (Surface B) — never Surface-A free CRUD. eosb/ot/lop are statutory FLOORS in the GCC:
    // a client may enhance ABOVE the floor via a dedicated allow-listed key, but the raw statutory
    // parameter itself is bounded.
    private static readonly string[] StatutoryPrefixes =
    {
        "gosi.", "saned.", "gpssa.", "dews.", "grsia.",
        "wps.", "sif.", "mudad.",
        "nitaqat.", "emiratisation.", "qatarization.", "saudization.", "omanization.", "bahrainization.",
        "ot.", "lop.", "eosb.",
        // KSA Art.98/109/117 — the Ramadan working-hours baseline, the Art.109 annual-leave tier and
        // the Art.117 sick-leave pay scale. Bounded-override only: these are statutory FLOORS in the
        // same sense as eosb/ot/lop, so a tenant may enhance above them through its own leave policy
        // but may not edit the statutory parameter itself through Surface-A free CRUD.
        "workhours.", "leave.",
    };

    /// <summary>
    /// Client-configurable keys that live under a statutory prefix because they ENHANCE that floor
    /// (extra EOSB days on top of the statutory minimum). They are allow-listed below; without this
    /// exemption the prefix guard refused them and the allow-list entry was unwritable.
    /// </summary>
    private static readonly HashSet<string> EnhancementKeysUnderStatutoryPrefix = new(StringComparer.Ordinal)
    {
        "eosb.enhancement_days_per_year",
    };

    public static bool IsStatutoryKey(string? rateKey)
    {
        if (string.IsNullOrWhiteSpace(rateKey)) return false;
        var k = rateKey.Trim().ToLowerInvariant();
        if (EnhancementKeysUnderStatutoryPrefix.Contains(k)) return false;
        return StatutoryPrefixes.Any(p => k.StartsWith(p, StringComparison.Ordinal));
    }

    /// <summary>Upper ceiling for a client income-tax rate (percent). Income tax is legitimately
    /// client config (not a statutory <c>gosi.</c>/<c>saned.</c> key), so it is not free-CRUD
    /// bounded via the prefix families above — but it still needs its own sanity floor/ceiling
    /// and the GCC zero-PIT compliance boundary. This is the single write-path check for the
    /// <c>tax.</c> surface, mirroring the CompanyStatutoryOverride bounded-override philosophy.</summary>
    public const decimal MaxIncomeTaxRatePercent = 100m;

    /// <summary>
    /// Validates a CompanyTaxPolicy income-tax rate on write. Returns an error string to reject,
    /// or null to allow. Rules: 0 ≤ rate ≤ 100; and a NON-ZERO rate for a zero-PIT GCC
    /// jurisdiction must be explicitly acknowledged (and the caller logs/audits it) — a fabricated
    /// positive PIT in a zero-PIT GCC state is otherwise refused.
    /// </summary>
    public static string? ValidateIncomeTaxRate(string? countryCode, decimal? ratePercent, bool acknowledgedNonZeroGcc)
    {
        if (ratePercent is null) return null; // no rate configured → nothing to bound
        var r = ratePercent.Value;
        if (r < 0m) return "IncomeTaxRatePercent cannot be negative.";
        if (r > MaxIncomeTaxRatePercent) return $"IncomeTaxRatePercent cannot exceed {MaxIncomeTaxRatePercent}%.";
        if (r > 0m && CountryTier.IsZeroPersonalIncomeTax(countryCode) && !acknowledgedNonZeroGcc)
            return $"'{countryCode?.Trim().ToUpperInvariant()}' is a zero personal-income-tax GCC jurisdiction. " +
                   "A non-zero income tax rate must be explicitly acknowledged (acknowledgeNonZeroGccTax=true) and is logged.";
        return null;
    }

    /// <summary>
    /// Seeded default allow-list of NON-statutory, client-configurable rate keys. Note the EOSB
    /// entry is the ABOVE-floor enhancement parameter (extra days a company grants on top of the
    /// statutory minimum) — never the statutory floor itself, which stays under Surface B.
    /// </summary>
    public static IReadOnlyList<ClientRateDefinition> DefaultClientRateDefinitions(Guid tenantId) => new[]
    {
        Def(tenantId, "allowance.wellness.monthly_amount",   "Allowance",    "decimal", "amount",     0m,    100_000m, "Monthly wellness allowance (company benefit)"),
        Def(tenantId, "allowance.mobile.monthly_amount",     "Allowance",    "decimal", "amount",     0m,    100_000m, "Monthly mobile/telecom allowance"),
        Def(tenantId, "allowance.education.annual_amount",   "Allowance",    "decimal", "amount",     0m,  1_000_000m, "Annual education allowance"),
        Def(tenantId, "deduction.canteen.monthly_amount",    "Deduction",    "decimal", "amount",     0m,    100_000m, "Monthly canteen deduction"),
        Def(tenantId, "deduction.parking.monthly_amount",    "Deduction",    "decimal", "amount",     0m,    100_000m, "Monthly parking deduction"),
        Def(tenantId, "eosb.enhancement_days_per_year",      "EOSB",         "decimal", "days",       0m,        365m, "EXTRA end-of-service days per year granted ABOVE the statutory floor"),
        Def(tenantId, "payparameter.probation_ot_multiplier","PayParameter", "decimal", "multiplier", 0m,          5m, "Overtime multiplier applied during probation (company policy, at or above statutory)"),
        Def(tenantId, "payparameter.notice_period_days",     "PayParameter", "decimal", "days",       0m,        365m, "Company standard notice period in days"),
        // ── POD-C3: mid-month proration + retro/arrears ───────────────────────────────────────────
        // Registered here because RatesController's allow-list REFUSES any key that is not in this
        // registry — without these rows the policy would be unwritable and every tenant would be stuck on
        // the compiled default forever. The three enum keys are DataType "string" and their allowed
        // values come from ProrationRateKeys.AllowedValues, so an unrecognised basis 400s at the write
        // surface instead of 422-ing every future payroll run.
        Def(tenantId, "payparameter.proration_basis",            "PayParameter", "string",  "enum",  null, null, "Mid-month proration basis: Calendar30 (default, day-rate = monthly/30) | CalendarActual | WorkingDays | None"),
        Def(tenantId, "payparameter.proration_gosi_base",        "PayParameter", "string",  "enum",  null, null, "Social-insurance base in a joining/leaving month: FullMonth (KSA default) | Prorated"),
        Def(tenantId, "payparameter.arrears_gosi_treatment",     "PayParameter", "string",  "enum",  null, null, "Statutory treatment of retro arrears: PeriodPaid (default) | None | PeriodEarned (refused — needs an amended-declaration workflow)"),
        Def(tenantId, "payparameter.arrears_max_lookback_months","PayParameter", "decimal", "months",   0m,  120m, "How many past periods a retro/arrears settlement may reach back over (default 24)"),
        Def(tenantId, "payparameter.prorated_components",        "PayParameter", "string",  "list",  null, null, "Comma-separated components proration applies to. Default BASIC,HOUSING,TRANSPORT,OTHER_ALLOWANCES,FIXED_DEDUCTION"),
        // ── POD-C2: monthly end-of-service provision ──────────────────────────────────────────────
        // Under payparameter.* (not eosb.*) on purpose: it is an ACCOUNTING policy choice, not the
        // statutory award, so it belongs on Surface A. Default Off; the effective date of the
        // StatutoryMonthly row is when provisioning starts.
        // ── KSA Border Number (Employees/BorderNumberRule.cs) ─────────────────────────────────────
        // The default window is the dated statutory row ksa.border_number_grace_days (90); these are the
        // per-company overrides. Both are READ by readiness and by the Saudi wage / bank file.
        Def(tenantId, BorderNumberRule.GraceDaysRateKey,        "PayParameter", "decimal", "days", BorderNumberRule.MinGraceDays, BorderNumberRule.MaxGraceDays,
            "Days after joining that a new arrival's Border Number counts in place of the Iqama before first payroll (0 = Iqama needed from the first payroll)"),
        Def(tenantId, BorderNumberRule.UseAsWageFileIdRateKey,  "PayParameter", "string",  "enum", null, null,
            "Use the Border Number as the employee ID in the Saudi wage file until the Iqama is issued: On (default) | Off (such an employee is held from the file)"),
        Def(tenantId, EosbProvisioningRateKeys.Mode,             "PayParameter", "string",  "enum",  null, null, "Monthly end-of-service provision at payroll Lock: Off (default) | StatutoryMonthly"),
    };

    /// <summary>
    /// The registry keys a payroll / EOSB / settlement calculation actually READS. Each entry is the
    /// constant its consumer reads (<see cref="ProrationPolicyResolver"/>, through
    /// <see cref="ICompanyRatePolicyResolver"/>), so a key is listed here only with a proven consumer.
    /// Every other registry key (the allowance/deduction amounts, the EOSB enhancement days, the
    /// probation OT multiplier, the notice period) is RECORDED ONLY: it was accepted and stored, and
    /// nothing ever applied it. The write surface refuses new values for those keys
    /// (COMPANY_RATE_NOT_APPLIED) instead of letting a customer believe pay changed. Writing the
    /// consumer is the only correct way to add a key here.
    /// </summary>
    private static readonly HashSet<string> PayrollAppliedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ProrationRateKeys.Basis,
        ProrationRateKeys.GosiBase,
        ProrationRateKeys.ArrearsGosiTreatment,
        ProrationRateKeys.ArrearsMaxLookback,
        ProrationRateKeys.ProratedComponents,
        // POD-C2 — read by PayrollController.BuildEosbProvisionAsync at every Lock.
        EosbProvisioningRateKeys.Mode,
        BorderNumberRule.GraceDaysRateKey,
        BorderNumberRule.UseAsWageFileIdRateKey,
    };

    /// <summary>True only for a client-rate key some payroll calculation reads (see <see cref="PayrollAppliedKeys"/>).
    /// A tenant-registered custom key is never applied: no code knows to read it.</summary>
    public static bool IsAppliedByPayroll(string? rateKey) =>
        !string.IsNullOrWhiteSpace(rateKey) && PayrollAppliedKeys.Contains(rateKey.Trim());

    private static ClientRateDefinition Def(
        Guid tenantId, string key, string category, string dataType, string unit,
        decimal? min, decimal? max, string description) => new()
    {
        TenantId = tenantId, RateKey = key, RateCategory = category, DataType = dataType,
        Unit = unit, MinValue = min, MaxValue = max, Description = description, IsActive = true,
    };
}
