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
    /// The IANA timezone a tenant in this jurisdiction should start with.
    ///
    /// <para>This exists because writing a <c>TenantLocalizationSetting</c> row at tenant creation
    /// is not neutral: the entity defaults to <c>America/New_York</c>, and before that row existed
    /// the timezone resolved to null and fell back to UTC. Adding the row therefore MOVED every
    /// tenant's "today" to New York — which for a GCC product is wrong by 7-11 hours, and showed
    /// up immediately as an approved leave request not being live on the day it started.</para>
    ///
    /// <para>Unknown country → UTC, deliberately. UTC is wrong by a known, uniform amount; a
    /// guessed zone is wrong by an amount nobody can predict. The tenant can set the real zone in
    /// Setup, and Attendance and Leave both read it from there.</para>
    /// </summary>
    public static string TimeZoneFor(string? countryCode) => Normalize(countryCode) switch
    {
        "SA" => "Asia/Riyadh",
        "AE" => "Asia/Dubai",
        "QA" => "Asia/Qatar",
        "KW" => "Asia/Kuwait",
        "BH" => "Asia/Bahrain",
        "OM" => "Asia/Muscat",
        "EG" => "Africa/Cairo",
        "JO" => "Asia/Amman",
        "PK" => "Asia/Karachi",
        "IN" => "Asia/Kolkata",
        "GB" => "Europe/London",
        "US" => "America/New_York",
        _    => "UTC",
    };

    /// <summary>
    /// The currency a tenant in this jurisdiction should start with, or EMPTY when the country is
    /// unknown.
    ///
    /// <para>This is the currency half of the same defect <see cref="TimeZoneFor"/> documents.
    /// <c>TenantLocalizationSetting.CurrencyCode</c> defaults to <c>"USD"</c>, so a tenant with no
    /// localization row was served USD as though it had been stated — and the setup assistant then
    /// drafted salary bands in it. A Saudi tenant saw its grades priced in dollars.</para>
    ///
    /// <para><b>Empty, not a guess, for an unknown country.</b> This differs from
    /// <see cref="TimeZoneFor"/>, which falls back to UTC, and the reason is the shape of being
    /// wrong. UTC is wrong by a known, uniform offset and a clock still tells the truth about
    /// instants. A wrong CURRENCY is not an offset — it silently relabels every salary figure on
    /// the screen, and 3,000 reads as 3,000 whether it is riyals or dollars. So an unmapped country
    /// returns empty, which the caller must treat as "not stated" and ask.</para>
    ///
    /// <para>A country's official currency is a fact rather than a policy, which is why this is a
    /// compiled map and not a statutory rule: unlike a contribution rate, it does not need
    /// counsel's sign-off and does not change with the tax year.</para>
    /// </summary>
    public static string CurrencyFor(string? countryCode) => Normalize(countryCode) switch
    {
        "SA" => "SAR",
        "AE" => "AED",
        "QA" => "QAR",
        "KW" => "KWD",
        "BH" => "BHD",
        "OM" => "OMR",
        "EG" => "EGP",
        "JO" => "JOD",
        "PK" => "PKR",
        "IN" => "INR",
        "GB" => "GBP",
        "US" => "USD",
        _    => string.Empty,
    };

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
