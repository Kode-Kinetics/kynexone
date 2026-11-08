using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Employees;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Employees;

/// <summary>
/// G-004: tells the people who can verify a document that one is waiting. The audience is exactly the people the verify endpoint
/// lets act: a login holding employees.documents whose company access covers the employee (and employees.sensitive too when the
/// document is protected: identity, payroll or contract), never the employee themself. A notice that names nobody is not sent;
/// it is never broadcast to the workspace.
/// </summary>
public static class DocumentVerificationNotices
{
    public const string Title = "Document to verify — مستند بانتظار التحقق";

    /// <summary>Returns how many logins were told. Never throws: a failed notice must not fail the upload that raised it.</summary>
    public static async Task<int> NotifyUploadedAsync(ZayraDbContext db, INotificationService notifications, EmployeeDocument document,
        string employeeName, CancellationToken ct)
    {
        if (document.EmployeeId is not int employeeId) return 0;
        try
        {
            var companyId = document.CompanyId
                ?? await db.Employees.AsNoTracking().Where(e => e.TenantId == document.TenantId && e.Id == employeeId).Select(e => e.CompanyId).FirstOrDefaultAsync(ct);
            var keys = EmployeeDocumentSensitivity.IsSensitive(document.DocumentType, document.DocumentCategory)
                ? new[] { "employees.documents", "employees.sensitive" }
                : new[] { "employees.documents" };
            var subject = await NotificationAudience.EmployeeLoginIdsAsync(db, document.TenantId, employeeId, ct);
            var recipients = await NotificationAudience.PermissionHoldersAsync(db, document.TenantId, keys, companyId, subject, ct);
            var message = $"{employeeName} uploaded {document.DocumentType}. Open Compliance, Employee documents, To verify to check it. / "
                + $"رفع {employeeName} المستند «{document.DocumentType}». افتح الامتثال ثم مستندات الموظفين ثم «للتحقق» لمراجعته.";
            return await notifications.NotifyEachAsync(document.TenantId, recipients.Select(id => (Guid?)id), Title, message,
                nameof(EmployeeDocument), document.Id.ToString(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return 0;
        }
    }
}
