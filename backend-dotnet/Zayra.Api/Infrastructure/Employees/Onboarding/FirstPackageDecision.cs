using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Employees.Onboarding;

/// <summary>
/// The scoped pay-approval view and <c>POST /api/employees/{id}/compensation/approve</c> decide a hire's pay for a Finance
/// approver, but the figures themselves are a pending <see cref="SalaryChangeRequest"/> whose ONLY decision code is
/// <see cref="SalaryChangeDecisionService"/> (maker/subject separation, the "who may approve a hire's pay" test, the grade-range
/// override with a reason, stale checks, the locked transaction, and the close of the pay ledger). These helpers call that same
/// service for the employee's pending first-package requests. Visibility is this door's own rule: the caller has already
/// resolved the employee inside their company scope, so no further data-scope filter is applied here.
/// </summary>
public static class FirstPackageDecision
{
    public static async Task<List<Guid>> PendingIdsAsync(ZayraDbContext db, Guid tenantId, int employeeId, CancellationToken ct) =>
        await ScopedBypass.TenantWide(db.SalaryChangeRequests, tenantId,
                "Pay approval: the pending first salary packages of one employee, to be decided through the salary-change decision service; tenant and employee pinned.")
            .AsNoTracking()
            .Where(x => x.EmployeeId == employeeId && x.Status == SalaryChangeRequest.Pending && (x.Source == SalaryChangeSources.NewHire || x.Source == SalaryChangeSources.EmployeeImport))
            .OrderBy(x => x.RequestedAtUtc).Select(x => x.Id).ToListAsync(ct);

    /// <summary>Approves every pending first package of the employee. Returns null when all were applied (or none was pending), else the first refusal.</summary>
    public static Task<IActionResult?> ApproveAsync(ControllerBase host, Guid tenantId, int employeeId, string? note, bool overrideGradeRange, CancellationToken ct) =>
        DecideAsync(host, tenantId, employeeId, (svc, actor, id) => svc.ApproveAsync(tenantId, id, actor, note, overrideGradeRange, null, ct), ct);

    /// <summary>Rejects every pending first package of the employee. Returns null when all were rejected (or none was pending), else the first refusal.</summary>
    public static Task<IActionResult?> RejectAsync(ControllerBase host, Guid tenantId, int employeeId, string? note, CancellationToken ct) =>
        DecideAsync(host, tenantId, employeeId, (svc, actor, id) => svc.RejectAsync(tenantId, id, actor, note, null, ct), ct);

    private static async Task<IActionResult?> DecideAsync(ControllerBase host, Guid tenantId, int employeeId,
        Func<SalaryChangeDecisionService, SalaryDecisionActor, Guid, Task<IActionResult>> decide, CancellationToken ct)
    {
        var services = host.HttpContext.RequestServices;
        var db = services.GetRequiredService<ZayraDbContext>();
        var ids = await PendingIdsAsync(db, tenantId, employeeId, ct);
        if (ids.Count == 0) return null;
        var user = host.User;
        var actor = new SalaryDecisionActor(
            Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub"), out var uid) ? uid : null,
            user.FindFirstValue(ClaimTypes.Name) ?? user.FindFirstValue("name") ?? "system",
            host.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        var service = services.GetRequiredService<SalaryChangeDecisionService>();
        foreach (var id in ids)
        {
            var result = await decide(service, actor, id);
            if (result is not OkObjectResult) return result;
        }
        return null;
    }
}
