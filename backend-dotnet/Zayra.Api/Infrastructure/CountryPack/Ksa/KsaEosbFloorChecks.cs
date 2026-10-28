using Zayra.Api.Application.Compliance;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Infrastructure.CountryPack;

namespace Zayra.Api.Infrastructure.CountryPack.Ksa;

/// <summary>
/// The Art. 84 floors for a company's configured end-of-service rates, as acknowledgeable warnings (owner decision
/// 2026-10-25). The floors are the platform statutory rules (15 and 30 days a year, tenant null so a tenant row cannot
/// move them) with the compiled fallback. A rate of 0 means "not configured" and is never checked. A rate below its
/// floor is allowed only with an audited acknowledgement, and once acknowledged the calculator honours it and shows
/// what the statute would have paid.
/// </summary>
public static class KsaEosbFloorChecks
{
    public const string Tier1 = "eosb_tier1_below_minimum";
    public const string Tier2 = "eosb_tier2_below_minimum";
    public const string Citation = "Labor Law Art. 84";

    public static bool IsKsa(string? countryCode)
        => string.Equals(countryCode?.Trim(), "SA", StringComparison.OrdinalIgnoreCase)
           || string.Equals(countryCode?.Trim(), "SAU", StringComparison.OrdinalIgnoreCase);

    public static async Task<List<FloorCheck>> ChecksAsync(
        IStatutoryRuleReader rules, string? countryCode, decimal tier1Rate, decimal tier2Rate, DateOnly on, CancellationToken ct)
    {
        var checks = new List<FloorCheck>();
        if (!IsKsa(countryCode)) return checks;
        var floor1 = await rules.GetDecimalAsync(CountryCodes.Saudi, Jurisdictions.KsaMainland, RuleKeys.EosbTier1DaysPerYear, on, null, ct) ?? 15m;
        var floor2 = await rules.GetDecimalAsync(CountryCodes.Saudi, Jurisdictions.KsaMainland, RuleKeys.EosbTier2DaysPerYear, on, null, ct) ?? 30m;
        if (tier1Rate > 0m) checks.Add(Make(Tier1, "eosbYears1To5Rate", tier1Rate, floor1, "Years 1 to 5", "السنوات 1 إلى 5", "السنوات الخمس الأولى"));
        if (tier2Rate > 0m) checks.Add(Make(Tier2, "eosbYearsAbove5Rate", tier2Rate, floor2, "Years above 5", "ما بعد السنة الخامسة", "ما بعد السنوات الخمس"));
        return checks;
    }

    private static FloorCheck Make(string code, string field, decimal configured, decimal floor, string label, string labelAr, string bandAr)
        => new(new FloorWarning(code, field,
            $"The end-of-service rate for {label.ToLowerInvariant()} is {configured:0.##} days a year, below the legal minimum of {floor:0.##} days a year ({Citation}). Employees may claim the difference.",
            false, floor, configured, Citation,
            $"معدل مكافأة نهاية الخدمة لـ{bandAr} هو {configured:0.##} يومًا في السنة، وهو أقل من الحد الأدنى النظامي البالغ {floor:0.##} يومًا في السنة (نظام العمل، المادة 84). قد يطالب الموظفون بالفرق."),
            configured < floor, label, labelAr);

    /// <summary>True when the stored acknowledgements answer <paramref name="code"/> for exactly this rate.</summary>
    public static bool IsAcknowledged(string? acksJson, string code, decimal rate)
        => FloorAckStore.Parse(acksJson).Any(a => a.Code == code && a.ConfiguredValue == rate);
}
