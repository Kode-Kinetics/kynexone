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
}

public record PayslipLineItem(string Name, decimal Amount, string Type);

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
