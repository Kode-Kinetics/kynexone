using Zayra.Api.Infrastructure.Payroll;

namespace Zayra.Api.Infrastructure.Employees;

/// <summary>
/// A probation longer than the employing company's own maximum (Setup > Payroll rules) was offered without the
/// acknowledgement. Advisory, never a hard stop: the caller repeats the request with the acknowledgement and the
/// override is audited. Mapped to 409 <c>probation_over_limit</c>.
/// </summary>
public sealed class ProbationOverLimitException : Exception
{
    public int Days { get; }
    public int MaxDays { get; }
    public ProbationOverLimitException(ProbationFinding finding) : base(finding.Message)
    {
        Days = finding.Days;
        MaxDays = finding.MaxDays;
    }
}
