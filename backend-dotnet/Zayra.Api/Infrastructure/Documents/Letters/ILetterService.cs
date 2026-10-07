using Zayra.Api.Infrastructure.Payroll;
namespace Zayra.Api.Infrastructure.Documents.Letters;

public interface ILetterService
{
    /// <summary>Generate a payslip PDF for a single PayrollSlip with itemized earnings/deductions.</summary>
    Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken cancellationToken = default);

    /// <summary>Generate an appointment letter PDF for a newly hired employee.</summary>
    Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken cancellationToken = default);

    /// <summary>Generate an experience letter PDF for a current or former employee.</summary>
    Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken cancellationToken = default);

    /// <summary>Generate an offer letter PDF for a candidate.</summary>
    Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Render a tenant-configured HR letter: merged template body, real letterhead, a stored
    /// reference number and a named signatory. This is the path every new letter type uses; the
    /// four methods above are the pre-existing hard-coded documents.
    ///
    /// <para>Declared as a C# 8 default interface method for the same reason
    /// <c>INotificationService.EnqueueAsync</c> is: roughly twenty test doubles across the suite
    /// implement <see cref="ILetterService"/>, and none of them renders a letter. The default
    /// throws rather than returning empty bytes — a double that IS asked for a letter should say
    /// so loudly, not hand back a zero-byte "PDF".</para>
    /// </summary>
    Task<byte[]> GenerateTemplateLetterAsync(TemplateLetterData data, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            $"{GetType().Name} does not render template letters. Use LetterService, or override this method in the double.");
}

/// <summary>
/// The letterhead a document prints. Sourced from the real company record and tenant branding,
/// not from the hard-coded "KynexOne Technologies" / "HR Department" fallbacks the previous
/// letters shipped with.
/// </summary>
public record LetterheadData(
    string CompanyNameEn,
    string CompanyNameAr = "",
    string RegistrationNumber = "",
    byte[]? LogoBytes = null,
    string PrimaryColorHex = "#1E3A5F",
    string FooterTextEn = "",
    string FooterTextAr = ""
);

/// <summary>One language's worth of already-merged letter text.</summary>
public record LetterSection(
    string Title,
    IReadOnlyList<string> Paragraphs,
    string Closing
);

/// <summary>
/// A letter ready to render. Both sections may be present (bilingual), or exactly one.
/// Nothing here is computed by the renderer — the merge and the reference allocation have
/// already happened, so the PDF is a pure function of this record.
/// </summary>
public record TemplateLetterData(
    string LetterType,
    string ReferenceNumber,
    string Language,
    LetterSection? English,
    LetterSection? Arabic,
    LetterheadData Letterhead,
    string IssuerName,
    string IssuerTitle,
    DateTime IssuedOn
);

public record PayslipLineItem(string Name, decimal Amount, string Type);

/// <summary>
/// The line types a payslip carries (<see cref="PayslipLineItem.Type"/> and the stored
/// <c>PayslipComponent.ComponentType</c>). An employer contribution (the employer's GOSI annuities,
/// SANED and occupational-hazard shares, or a tenant-configured employer-funded component) is an
/// employer COST: it is shown in its own section and never counted in the employee's deductions, so on
/// every payslip gross − deductions = net.
/// </summary>
public static class PayslipLineTypes
{
    public const string Earning = "Earning";
    public const string Deduction = "Deduction";
    public const string EmployerContribution = "EmployerContribution";
    public const string Net = "Net";

    /// <summary>
    /// Payslips generated before employer lines had their own type stored them as "Deduction". The
    /// authoritative flag is <c>PayrollDeduction.IsEmployerContribution</c> on the run's own lines, so a
    /// stored Deduction line that matches an employer line of the same run (same name and amount) is
    /// re-typed as an employer contribution — one stored line per employer line, never more. Amounts are
    /// untouched; only the section the line belongs to changes.
    /// </summary>
    public static List<PayslipLineItem> ReclassifyLegacyEmployerLines(
        IEnumerable<PayslipLineItem> items, IEnumerable<(string Name, decimal Amount)> employerLines)
    {
        var remaining = employerLines
            .GroupBy(l => (l.Name, l.Amount))
            .ToDictionary(g => g.Key, g => g.Count());
        var result = new List<PayslipLineItem>();
        foreach (var item in items)
        {
            if (item.Type == Deduction
                && remaining.TryGetValue((item.Name, item.Amount), out var left) && left > 0)
            {
                remaining[(item.Name, item.Amount)] = left - 1;
                result.Add(item with { Type = EmployerContribution });
            }
            else
            {
                result.Add(item);
            }
        }
        return result;
    }
}

public record PayslipData(
    string PayslipNumber,
    string EmployeeCode,
    string EmployeeName,
    string Department,
    string Designation,
    int PayYear,
    int PayMonth,
    string Currency,
    IReadOnlyList<PayslipLineItem> Items,
    string CompanyName,
    string CompanyNameAr = "",
    DateTime? GeneratedOn = null,
    PayslipBrandingConfig? Branding = null
);

public record LetterData(
    string EmployeeName,
    string EmployeeCode,
    string Department,
    string Designation,
    DateTime JoiningDate,
    DateTime? LeavingDate,
    decimal BasicSalary,
    string Currency,
    string CompanyName,
    string IssuedBy,
    DateTime IssuedDate,
    string? AdditionalNote = null
);

public record OfferLetterData(
    string CandidateName,
    string Position,
    string Department,
    decimal Salary,
    string Currency,
    DateTime StartDate,
    string CompanyName,
    string IssuedBy,
    DateTime IssuedDate
);
