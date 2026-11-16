using Zayra.Api.Application.CountryPack;

namespace Zayra.Api.Infrastructure.CountryPack.Ksa;

/// <summary>
/// L1.3 — how a GOSI contribution is rounded to two decimals. Half-up (away from zero) by default, per component, per employee, per side;
/// the sum of the rounded parts is the total and is never re-rounded. The mode is an effective-dated PLATFORM rule
/// (<see cref="RuleKey"/>: <c>HALF_UP</c> | <c>HALF_EVEN</c>) so that if a client's real GOSI invoice proves GOSI rounds another way, a row
/// changes, not code. It differs from banker's rounding (.NET's default) only when wage x rate ends in exactly half a halala, by 0.01.
/// An unrecognised or missing value is HALF_UP: a typo never silently switches the payroll to banker's rounding.
/// </summary>
public static class GosiRounding
{
    public const string RuleKey = "gosi.rounding_mode";
    public const string HalfUp = "HALF_UP";
    public const string HalfEven = "HALF_EVEN";

    /// <summary>
    /// The first day half-up rounding applies. The rule shipped in the release that introduced it, so every month processed before then was
    /// rounded half-even (.NET's default); seeding half-up from 2016 would recompute a closed month differently if it were reopened or
    /// previewed. The seeded rule is therefore HALF_EVEN from 2016 and HALF_UP from this date (the first month after that release).
    /// </summary>
    public static readonly DateOnly HalfUpEffectiveFrom = new(2026, 12, 1);

    /// <summary>The mode the seeded rule gives a period, for the pure per-branch arithmetic that has no rule reader.</summary>
    public static MidpointRounding DefaultModeFor(DateOnly periodDate) =>
        periodDate >= HalfUpEffectiveFrom ? MidpointRounding.AwayFromZero : MidpointRounding.ToEven;

    public static MidpointRounding ModeOf(string? value) =>
        string.Equals(value?.Trim(), HalfEven, StringComparison.OrdinalIgnoreCase) ? MidpointRounding.ToEven : MidpointRounding.AwayFromZero;

    /// <summary>Read from the platform row only (no tenant), exactly like the rates, so the payslip, the preview and the report agree.</summary>
    public static async Task<MidpointRounding> ResolveAsync(IStatutoryRuleReader rules, DateOnly effectiveDate, CancellationToken ct = default) =>
        ModeOf(await rules.GetStringAsync(CountryCodes.Saudi, Jurisdictions.KsaMainland, RuleKey, effectiveDate, null, ct));
}
