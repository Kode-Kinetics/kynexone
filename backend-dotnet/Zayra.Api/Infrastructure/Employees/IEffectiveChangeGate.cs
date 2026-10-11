using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Employees;

/// <summary>A refusal from the module that originated an approved change: the change is closed (Cancelled) with this
/// reason instead of being applied, and <see cref="NotifyUserId"/> (when set) is told.</summary>
public sealed record EffectiveChangeRefusal(string ReasonCode, string Reason, Guid? NotifyUserId = null);

/// <summary>
/// A last check by the module that raised an approved change, run by <see cref="EffectiveChangeJobHandler"/> on the
/// effective date inside the change's row lock, before anything is written. The job knows nothing about the module;
/// a gate that does not recognise the change returns null. A gate may stage writes on its own records in the same
/// transaction (they commit or roll back with the job item).
/// </summary>
public interface IEffectiveChangeGate
{
    Task<EffectiveChangeRefusal?> CheckAsync(ZayraDbContext db, Guid tenantId, EmployeeChangeRequest change, Employee employee,
        DateOnly localToday, CancellationToken ct);
}

/// <summary>
/// Runs the registered gates for a change about to be written, on EVERY path that writes an approved
/// <see cref="EmployeeChangeRequest"/>: the effective-date job, and the Approval Center decision that applies a change
/// whose effective date has already passed (e.g. a re-review approved late). Same refusal on both paths: the change is
/// Cancelled with the reason, an employee-history row and an audit row are staged, and the gate's recipient is told.
/// </summary>
public static class EffectiveChangeGateRunner
{
    public static async Task<EffectiveChangeRefusal?> CheckAsync(IEnumerable<IEffectiveChangeGate> gates, ZayraDbContext db, Guid tenantId,
        EmployeeChangeRequest change, Employee employee, DateOnly today, CancellationToken ct)
    {
        foreach (var gate in gates)
            if (await gate.CheckAsync(db, tenantId, change, employee, today, ct) is { } refusal)
                return refusal;
        return null;
    }

    /// <summary>Stages the cancellation into the caller's unit of work (nothing is saved here), for the decision path.
    /// The effective-date job records its own through its Cancel outcome, with the same action and event type.</summary>
    public static void StageRefusal(ZayraDbContext db, Guid tenantId, EmployeeChangeRequest change, Employee employee,
        EffectiveChangeRefusal refusal, Guid? actorUserId, string path)
    {
        var reason = refusal.Reason.Length <= 1000 ? refusal.Reason : refusal.Reason[..1000];
        change.Status = Zayra.Api.Application.Employees.EmployeeChangeStatuses.Cancelled;
        change.RejectionReason = reason;
        db.EmployeeHistories.Add(new EmployeeHistory
        {
            TenantId = tenantId,
            EmployeeId = employee.Id,
            EventType = EffectiveChangeJobHandler.CancelledEventType,
            FieldName = change.SensitiveFields,
            EffectiveDate = change.EffectiveDate,
            Reason = reason,
            ApprovedByUserId = change.ApprovedByUserId,
            SnapshotJson = Zayra.Api.Application.Employees.EmployeeChangeBaseline.SnapshotJson(change.Id, null),
            CreatedByUserId = actorUserId,
        });
        db.AuditLogs.Add(new Zayra.Api.Domain.Entities.AuditLog
        {
            TenantId = tenantId,
            CompanyId = employee.CompanyId,
            UserId = actorUserId,
            Action = EffectiveChangeJobHandler.CancelledAction,
            EntityName = nameof(EmployeeChangeRequest),
            EntityId = change.Id.ToString(),
            Metadata = System.Text.Json.JsonSerializer.Serialize(new
            {
                employeeId = employee.Id,
                effectiveDate = change.EffectiveDate.ToString("yyyy-MM-dd"),
                fields = change.SensitiveFields,
                reasonCode = refusal.ReasonCode,
                reason,
                path,
            }),
            CreatedAtUtc = DateTime.UtcNow,
        });
        if (refusal.NotifyUserId is Guid recipient)
            db.Notifications.Add(new Notification
            {
                TenantId = tenantId,
                UserId = recipient,
                Title = "Approved employee change cancelled",
                Message = $"The approved {change.SensitiveFields} change for {employee.EmployeeCode} was cancelled. {reason}",
                EntityName = nameof(EmployeeChangeRequest),
                EntityId = change.Id.ToString(),
            });
    }
}
