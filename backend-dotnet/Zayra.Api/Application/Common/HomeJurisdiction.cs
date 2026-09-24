namespace Zayra.Api.Application.Common;

/// <summary>
/// The tenant's HOME JURISDICTION — the country a customer is actually in, stated once by the
/// platform administrator at tenant creation and inherited by the tenant's first company.
///
/// <para><b>Why this exists.</b> Provisioning used to run <c>InstallCountryPayrollRulesAsync</c> and
/// <c>InstallDefaultLeaveAsync</c> before anybody had said which country the customer was in, and the
/// first company was created with <c>CountryCode = ""</c>. Statutory resolution
/// (<c>GccReadinessFloor</c> → <c>EmployeeReadinessPolicyResolver</c> → <c>EmployeeActivationGuard</c>)
/// is keyed on the EMPLOYING COMPANY'S country, so a blank one resolves an EMPTY requirement set and
/// the Add Employee modal fails with nothing to point at.</para>
///
/// <para><b>Storage.</b> No new column: the tenant-level country is
/// <c>TenantLocalizationSetting.CountryCode</c>, which <c>TenantModuleService</c> and
/// <c>OvertimeController</c> already read as the tenant's country. A COMPANY's own
/// <c>Company.CountryCode</c> stays independently editable per legal entity, because a group may hold
/// one entity in Saudi Arabia and two in Qatar.</para>
///
/// <para><b>Never guessed.</b> A country inferred from a currency, a slug or a locale seeds the wrong
/// labour law in silence. Every surface here either has a stated country or says out loud which
/// tenant/company is missing one and where to set it.</para>
/// </summary>
public static class HomeJurisdiction
{
    /// <summary>Machine-readable error code for an API refusal caused by a company with no country.</summary>
    public const string MissingCompanyCountryError = "company_country_missing";

    /// <summary>Machine-readable error code for a tenant with no stated home jurisdiction.</summary>
    public const string MissingTenantCountryError = "tenant_country_missing";

    /// <summary>Where a tenant administrator fixes a company's country.</summary>
    public const string CompanyFixLocation = "Setup → Companies";

    /// <summary>
    /// Canonical ISO-2 for a stated country, or null when nothing usable was stated. Reuses
    /// <see cref="CountryCodeStandard"/> — the product's single country list (ISO-2 canonical,
    /// ISO-3 accepted and mapped) — so no second list of countries is introduced.
    /// </summary>
    public static string? Normalize(string? countryCode) => CountryCodeStandard.NormalizeToIso2(countryCode);

    /// <summary>True when no recognized country has been stated (blank, whitespace or free text).</summary>
    public static bool IsMissing(string? countryCode) => Normalize(countryCode) is null;

    /// <summary>
    /// Canonical ISO-2 or a loud failure. Used where a country is a precondition rather than a
    /// preference — tenant provisioning above all, which seeds statutory defaults.
    /// </summary>
    public static string Require(string? countryCode, string what = "home country")
        => Normalize(countryCode)
           ?? throw new ArgumentException(
               string.IsNullOrWhiteSpace(countryCode)
                   ? $"A {what} is required. It drives statutory seeding and cannot be guessed — "
                     + "state an ISO 3166-1 country (e.g. SA, AE, QA)."
                   : $"Unrecognized {what} '{countryCode}'. Use an ISO 3166-1 country code (e.g. SA, AE, QA).",
               nameof(countryCode));

    /// <summary>
    /// The message an operator can act on. It NAMES THE COMPANY and where the fix lives — the whole
    /// point: "Select the employing company and nationality to see the required identity documents"
    /// never mentioned the company's country, so the cause could not be deduced.
    /// </summary>
    public static string CompanyMessage(string? companyName)
    {
        var name = string.IsNullOrWhiteSpace(companyName) ? "This company" : companyName.Trim();
        return $"{name} has no country set — set it in {CompanyFixLocation} before adding employees.";
    }

    /// <summary>The same, for a tenant whose home jurisdiction was never stated.</summary>
    public static string TenantMessage(string? tenantName)
    {
        var name = string.IsNullOrWhiteSpace(tenantName) ? "This tenant" : tenantName.Trim();
        return $"{name} has no home country set — a platform administrator must set it on the tenant "
             + $"before statutory rules, leave entitlements and identity documents can be resolved.";
    }
}
