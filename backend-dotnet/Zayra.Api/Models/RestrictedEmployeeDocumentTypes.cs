namespace Zayra.Api.Models;

/// <summary>
/// Employee document types that are HR evidence, not the employee's own paperwork: restricted to company HR
/// (and auditors) and never listed, alerted on, attached or downloadable through self-service.
///
/// <para><b>Why hidden from the employee.</b> A Qiwa evidence capture can show other people's data on the same
/// screen; a non-renewal notice is served through its own channel and recorded here as the employer's proof;
/// a loan-deduction consent is the employer's Art. 92 evidence. Each is reachable by HR through the case or loan
/// it belongs to (Release A slices R3 and R6), which audit every download.</para>
/// </summary>
public static class RestrictedEmployeeDocumentTypes
{
    /// <summary>A screenshot or PDF proving the Qiwa contract outcome (renewal case, R6).</summary>
    public const string QiwaEvidence = "QiwaEvidence";
    /// <summary>The employee's written consent to a loan instalment above 10% of the wage (Art. 92, R3).</summary>
    public const string LoanDeductionConsent = "LoanDeductionConsent";
    /// <summary>The served non-renewal notice (Art. 74(2), R6).</summary>
    public const string NonRenewalNotice = "NonRenewalNotice";

    public static readonly string[] All = [QiwaEvidence, LoanDeductionConsent, NonRenewalNotice];

    public static bool IsRestricted(string? documentType) =>
        documentType is not null && All.Contains(documentType, StringComparer.OrdinalIgnoreCase);
}
