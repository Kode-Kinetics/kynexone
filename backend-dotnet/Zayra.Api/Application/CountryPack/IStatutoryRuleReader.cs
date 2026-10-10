namespace Zayra.Api.Application.CountryPack;

// Reads effective-dated configuration values from the statutory_rules table.
// Lookup order: company override (only when a companyId is passed) → tenant-specific row →
// platform default (TenantId = null).
// Both decimal and string accessors return null when no row is found —
// callers should fall back to a hard-coded safe default.

public interface IStatutoryRuleReader
{
    Task<decimal?> GetDecimalAsync(
        string countryCode,
        string jurisdiction,
        string ruleKey,
        DateOnly effectiveDate,
        Guid? tenantId = null,
        CancellationToken ct = default);

    Task<string?> GetStringAsync(
        string countryCode,
        string jurisdiction,
        string ruleKey,
        DateOnly effectiveDate,
        Guid? tenantId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Company-aware read (the optional trailing <paramref name="companyId"/>). When it is set and a
    /// tenant is given, an Active, date-covered <c>CompanyStatutoryOverride</c> for that legal entity
    /// wins over the tenant row and the platform default, unless it weakens a statutory floor (see
    /// <c>StatutoryOverridePolicy</c>). A null companyId is exactly the overload above.
    ///
    /// <para>Declared as an overload rather than a seventh optional parameter so the existing call
    /// shape cannot become ambiguous; the default implementation ignores the company, so a test
    /// double that only models tenant/platform rows keeps compiling and keeps its meaning.</para>
    /// </summary>
    Task<decimal?> GetDecimalAsync(
        string countryCode,
        string jurisdiction,
        string ruleKey,
        DateOnly effectiveDate,
        Guid? tenantId,
        CancellationToken ct,
        Guid? companyId)
        => GetDecimalAsync(countryCode, jurisdiction, ruleKey, effectiveDate, tenantId, ct);

    /// <summary>String twin of the company-aware decimal read.</summary>
    Task<string?> GetStringAsync(
        string countryCode,
        string jurisdiction,
        string ruleKey,
        DateOnly effectiveDate,
        Guid? tenantId,
        CancellationToken ct,
        Guid? companyId)
        => GetStringAsync(countryCode, jurisdiction, ruleKey, effectiveDate, tenantId, ct);
}
