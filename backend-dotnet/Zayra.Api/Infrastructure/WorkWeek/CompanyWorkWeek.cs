using Zayra.Api.Application.WorkWeek;

namespace Zayra.Api.Infrastructure.WorkWeek;

/// <summary>
/// The one place that decides which days are a company's rest days. Attendance (overtime minutes, comp-off) and payroll
/// (the day-category multiplier) both ask here, and both resolve it for the EMPLOYING COMPANY and its own country — never an
/// individual's passport/nationality country — so a day cannot be a rest day to one and an ordinary day to the other.
/// </summary>
public static class CompanyWorkWeek
{
    public static Task<WorkWeekConfig> ResolveAsync(IWorkWeekService workWeek, Guid tenantId, Guid? companyId, CancellationToken ct)
        // A null country makes the service read the company's own country.
        => workWeek.ResolveAsync(tenantId, companyId, null, ct);
}
