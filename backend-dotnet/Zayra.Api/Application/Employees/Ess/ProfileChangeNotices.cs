using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Application.Employees.Ess;

/// <summary>
/// HR is told when an employee sends a change that waits for their review (the older mobile request or a profile
/// section), the way a transfer step is: the HR Manager queue covering the employee's company, addressed, never a
/// broadcast. Best-effort and post-commit: a notification outage must not turn a saved request into an error.
/// </summary>
public static class ProfileChangeNotices
{
    public static async Task NotifyHrAsync(INotificationService? notifications, ZayraDbContext db, Guid tenantId,
        Employee employee, EmployeeProfileChangeRequest request, CancellationToken ct)
    {
        if (notifications is null) return;
        try
        {
            var holders = await NotificationAudience.QueueHoldersAsync(db, tenantId, null, employee.CompanyId, ct);
            var name = string.IsNullOrWhiteSpace(employee.EnglishName) ? employee.FullName : employee.EnglishName;
            await notifications.NotifyEachAsync(tenantId, holders.Select(h => (Guid?)h), "Employee details awaiting HR review",
                $"{employee.EmployeeCode} ({name}) sent a change to their details. Open Profile completion review to accept it or send it back.",
                nameof(EmployeeProfileChangeRequest), request.Id.ToString(), ct);
        }
        catch { /* the request is saved; HR still finds it in the review inbox */ }
    }
}
