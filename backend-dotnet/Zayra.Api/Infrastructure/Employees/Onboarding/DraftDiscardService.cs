using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Employees.Onboarding;

/// <summary>
/// The creator may throw away their own unfinished draft. This is deliberately narrow: the hire must still be a Draft,
/// made by the caller, no more than seven days old, with no payroll, approval or history beyond its own creation.
/// Anything else keeps the existing soft-delete path (<c>employees.delete</c>), which retains the record.
/// </summary>
public static class DraftDiscardRules
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    public enum Refusal { None, NotDraft, NotCreator, TooOld, HasActivity }

    public static Refusal Evaluate(string? status, Guid? createdBy, Guid caller, DateTime createdAtUtc, DateTime nowUtc, bool hasActivity)
    {
        if (!string.Equals(status, "Draft", StringComparison.OrdinalIgnoreCase)) return Refusal.NotDraft;
        if (createdBy is null || createdBy != caller) return Refusal.NotCreator;
        if (nowUtc - createdAtUtc > MaxAge) return Refusal.TooOld;
        return hasActivity ? Refusal.HasActivity : Refusal.None;
    }
}

/// <summary>
/// A discard is the platform's ordinary soft delete (the same fields <c>EmployeesController.Delete</c> sets: deleted flag,
/// <c>Inactive</c>, retained for statutory audit for seven years), authorised for the creator of a quiet draft. The row
/// and its employee code are kept; nothing is hard-deleted, so retention and the audit chain are untouched. The draft's
/// never-used staged login is deactivated through the same credential primitives as the delete.
/// </summary>
public sealed class DraftDiscardService
{
    public const string AuditAction = "employee.draft_discarded";
    private readonly ZayraDbContext _db;
    private readonly IAuditService _audit;

    public DraftDiscardService(ZayraDbContext db, IAuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public async Task<bool> HasActivityAsync(Guid tenantId, int employeeId, CancellationToken ct)
    {
        var id = employeeId;
        if (await _db.EmployeeHistories.AnyAsync(h => h.TenantId == tenantId && h.EmployeeId == id && h.EventType != "Created", ct)) return true;
        if (await _db.EmployeeStatusHistories.AnyAsync(h => h.TenantId == tenantId && h.EmployeeId == id, ct)) return true;
        if (await _db.PayrollRunEmployees.AnyAsync(p => p.EmployeeId == id, ct)) return true;
        if (await _db.PayrollSlips.AnyAsync(p => p.EmployeeId == id, ct)) return true;
        if (await _db.ApprovalRequests.AnyAsync(a => a.TenantId == tenantId && a.RequestedForEmployeeId == id, ct)) return true;
        return await _db.EmployeeChangeRequests.AnyAsync(c => c.TenantId == tenantId && c.EmployeeId == id, ct);
    }

    /// <summary>The soft-delete field set. Kept identical to the controller's delete; a test pins the parity.</summary>
    public static void ApplySoftDelete(Employee employee, DateTime deletedAt, Guid? deletedBy)
    {
        employee.IsDeleted = true;
        employee.DeletedAtUtc = deletedAt;
        employee.DeletedBy = deletedBy;
        employee.Status = "Inactive";
        employee.PrivacyStatus = "RetainedForStatutoryAudit";
        employee.RetentionUntilUtc = deletedAt.AddYears(7);
    }

    public async Task DiscardAsync(Guid tenantId, int employeeId, RequestContext context, CancellationToken ct)
    {
        var employee = await Zayra.Api.Infrastructure.Data.ScopedBypass.NullableTenantWide(_db.Employees, tenantId,
                "Draft discard: eligibility and company scope were checked by the caller; the tenant is re-applied.")
            .AsTracking().FirstAsync(e => e.Id == employeeId && !e.IsDeleted, ct);
        var deletedAt = DateTime.UtcNow;
        ApplySoftDelete(employee, deletedAt, context.UserId);

        var (links, users, userIds) = await EmployeeManagementService.LoadCredentialGraphAsync(_db, tenantId, employeeId, employee.UserAccountId, ct);
        var gated = await EmployeeManagementService.GateLinkedLoginsAsync(_db, tenantId, links, users, userIds, context, "employee.draft_discarded", employeeId, deletedAt, ct);
        await EmployeeManagementService.StageCredentialInvalidationAsync(_db, gated.Links, gated.Users, gated.UserIds,
            loginDisabledReason: "Employee record was deleted.", effectiveAtUtc: deletedAt, actorUserId: context.UserId,
            actorIpAddress: context.IpAddress, bulkTokenUpdates: false, ct);

        await _db.SaveChangesAsync(ct);
        await _audit.WriteAsync(AuditAction, "Employee", employeeId.ToString(System.Globalization.CultureInfo.InvariantCulture), context,
            JsonSerializer.Serialize(new { employeeId, employeeCode = employee.EmployeeCode }), ct);
        await EmployeeManagementService.DeactivatePayrollFootprintAsync(_db, _audit, tenantId, employeeId, "draft_discarded", deactivateSalaryStructure: true, context, ct);
    }
}
