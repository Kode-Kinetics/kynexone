using Zayra.Api.Application.Common;

namespace Zayra.Api.Infrastructure.Employees;

/// <summary>
/// The employing company has no stated country, so an employee CANNOT be created under it.
///
/// <para>Every statutory requirement — identity documents, leave entitlements, activation gates — is
/// resolved from <c>Company.CountryCode</c> (<see cref="HomeJurisdiction"/>). A blank one resolves an
/// EMPTY requirement set, which is why the create used to fail with nothing to point at.</para>
///
/// <para>The Add Employee modal disables its submit button in this state, but a disabled button is a
/// UX affordance, not authorization: this exception is the authoritative refusal, so a direct API
/// call, a stale tab or an integration cannot create an employee under a country-less company. The
/// controller maps it to <c>400 { error:"company_country_missing", message, companyId, fixLocation }</c>
/// and the modal shows the server's message verbatim.</para>
/// </summary>
public sealed class CompanyCountryMissingException : System.Exception
{
    public Guid CompanyId { get; }

    /// <summary>The name the message speaks — the same label the operator sees in Setup → Companies.</summary>
    public string CompanyLabel { get; }

    public CompanyCountryMissingException(Guid companyId, string? companyLabel)
        : base(HomeJurisdiction.CompanyMessage(companyLabel))
    {
        CompanyId = companyId;
        CompanyLabel = string.IsNullOrWhiteSpace(companyLabel) ? "This company" : companyLabel.Trim();
    }
}
