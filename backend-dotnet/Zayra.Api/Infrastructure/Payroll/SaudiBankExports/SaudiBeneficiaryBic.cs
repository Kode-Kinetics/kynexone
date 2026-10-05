using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll.SaudiBankExports;

/// <summary>
/// The ONE answer to "which bank (BIC) is this employee credited at?", for the pre-lock payroll
/// validation, the legacy WPS/SIF validation and the Saudi bank export alike. They used to read two
/// different fields — pre-lock the payroll profile's BankRoutingCode, the export the approved
/// Employee.WpsBankDetails snapshot — so an ANB employee whose BIC was recorded in only one of them
/// passed one stage and failed the other.
///
/// <para>Precedence: the approved beneficiary snapshot (Employee.WpsBankDetails, set through the
/// sensitive-change approval), else the payroll profile's BankRoutingCode. Trimmed and upper-cased.
/// Nothing is inferred from the IBAN.</para>
/// </summary>
public static class SaudiBeneficiaryBic
{
    public static string? Resolve(string? wpsBankDetails, string? profileRoutingCode)
    {
        var bic = ApprovedSaudiBeneficiaryDetails.Read(wpsBankDetails)?.BicCode;
        if (string.IsNullOrWhiteSpace(bic)) bic = profileRoutingCode;
        return string.IsNullOrWhiteSpace(bic) ? null : bic.Trim().ToUpperInvariant();
    }

    public static string? Resolve(Employee? employee, EmployeePayrollProfile? profile) =>
        Resolve(employee?.WpsBankDetails, profile?.BankRoutingCode);

    /// <summary>No IBAN, but a 16-digit ANB account credited at ANB: a valid ANB-to-ANB credit.</summary>
    public static bool IsAnbInternalCredit(Employee? employee, EmployeePayrollProfile? profile) =>
        string.IsNullOrWhiteSpace(profile?.Iban)
        && KsaWageFileRules.IsAnbInternalAccount(profile?.AccountNumber, Resolve(employee, profile));
}
