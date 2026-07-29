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
    };

    public static bool IsStatutoryKey(string? rateKey)
    {
        if (string.IsNullOrWhiteSpace(rateKey)) return false;
        var k = rateKey.Trim().ToLowerInvariant();
        return StatutoryPrefixes.Any(p => k.StartsWith(p, StringComparison.Ordinal));
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
    };

    private static ClientRateDefinition Def(
        Guid tenantId, string key, string category, string dataType, string unit,
        decimal? min, decimal? max, string description) => new()
    {
        TenantId = tenantId, RateKey = key, RateCategory = category, DataType = dataType,
        Unit = unit, MinValue = min, MaxValue = max, Description = description, IsActive = true,
    };
}
