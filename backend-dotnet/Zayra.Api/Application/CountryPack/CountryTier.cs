using Zayra.Api.Application.Common;

namespace Zayra.Api.Application.CountryPack;

/// <summary>
/// OD-4 honest-country-pack tiering (owner decision 2026-07-27). Payroll correctness is
/// certified only where a real executable pack exists; everything else is HR/org-only with
/// payroll hard-blocked at the run (the existing statutory-pack fail-loud guard). This is the
/// single source of tier truth used by provisioning metadata and the tax compliance floor.
/// </summary>
public enum PayrollCertificationTier
{
    /// <summary>Tier 1 — certified payroll (fail-loud correctness): KSA, UAE.</summary>
    Certified = 1,
    /// <summary>Tier 2 — payroll allowed behind fail-loud statutory validation, gaps published: QA, KW, OM, BH.</summary>
    FailLoud = 2,
    /// <summary>Tier 3 — HR/org only, payroll hard-blocked (contact vendor): everything else.</summary>
    HrOnly = 3,
}

public static class CountryTier
{
    // ISO-2 canonical. Kept in code (not seed data) so the tier gate is fail-safe and cannot be
    // widened by editing a tenant's rows.
    private static readonly HashSet<string> Tier1 = new(StringComparer.OrdinalIgnoreCase) { "SA", "AE" };
    private static readonly HashSet<string> Tier2 = new(StringComparer.OrdinalIgnoreCase) { "QA", "KW", "OM", "BH" };
    private static readonly HashSet<string> GccStates = new(StringComparer.OrdinalIgnoreCase) { "SA", "AE", "QA", "KW", "OM", "BH" };

    private static string Normalize(string? cc) =>
        (CountryCodeStandard.NormalizeToIso2(cc) ?? cc ?? string.Empty).Trim().ToUpperInvariant();

    public static PayrollCertificationTier GetTier(string? countryCode)
    {
        var c = Normalize(countryCode);
        if (Tier1.Contains(c)) return PayrollCertificationTier.Certified;
        if (Tier2.Contains(c)) return PayrollCertificationTier.FailLoud;
        return PayrollCertificationTier.HrOnly;
    }

    public static bool IsCertified(string? countryCode) => GetTier(countryCode) == PayrollCertificationTier.Certified;

    public static bool IsGcc(string? countryCode) => GccStates.Contains(Normalize(countryCode));

    /// <summary>All six GCC states levy 0% statutory personal income tax — the compliance floor
    /// a CompanyTaxPolicy must not silently breach.</summary>
    public static bool IsZeroPersonalIncomeTax(string? countryCode) => IsGcc(countryCode);

    public static string TierLabel(PayrollCertificationTier tier) => tier switch
    {
        PayrollCertificationTier.Certified => "certified",
        PayrollCertificationTier.FailLoud => "fail-loud",
        _ => "hr-only",
    };
}
