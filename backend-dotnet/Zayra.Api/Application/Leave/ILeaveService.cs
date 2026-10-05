using Zayra.Api.Models;

namespace Zayra.Api.Application.Leave;

public interface ILeaveService
{
    // Balance engine
    Task<EmployeeLeaveBalance> GetOrCreateBalanceAsync(Guid tenantId, int employeeId, Guid leaveTypeId, int year, CancellationToken ct = default);
    Task AccrueMonthlyAsync(Guid tenantId, CancellationToken ct = default);
    Task<decimal> CalculateWorkingDaysAsync(Guid tenantId, DateOnly start, DateOnly end, Guid? policyId, CancellationToken ct = default);
    /// <summary>
    /// As above, but with the employee's employing company so the tenant's configured rest days are
    /// still excluded when NO leave policy matches the employee. Without it the no-policy path fell
    /// back to raw calendar days and charged a Thu–Sun request 4 days on a Fri–Sat working week.
    /// </summary>
    Task<decimal> CalculateWorkingDaysAsync(Guid tenantId, DateOnly start, DateOnly end, Guid? policyId, Guid? companyId, CancellationToken ct = default);
    Task<bool> HasSufficientBalanceAsync(Guid tenantId, int employeeId, Guid leaveTypeId, decimal requestedDays, int year, CancellationToken ct = default);
    Task<bool> HasOverlappingLeaveAsync(Guid tenantId, int employeeId, DateOnly start, DateOnly end, Guid? excludeRequestId, CancellationToken ct = default);
    Task ApplyLeaveBalanceAsync(Guid tenantId, int employeeId, Guid leaveTypeId, decimal days, int year, string action, string reference, string performedBy, CancellationToken ct = default);
    Task ReverseLeaveBalanceAsync(Guid tenantId, int employeeId, Guid leaveTypeId, decimal days, int year, string reference, string performedBy, CancellationToken ct = default);
    // Leave processing
    Task<LeaveRequest> SubmitRequestAsync(Guid tenantId, LeaveRequest request, CancellationToken ct = default);
    Task<LeaveRequest> SubmitRequestAsync(Guid tenantId, LeaveRequest request, Guid? requestedByUserId, CancellationToken ct = default);
    Task<LeaveRequest> ApproveRequestAsync(Guid tenantId, Guid requestId, Guid approverId, string approverName, string? notes, CancellationToken ct = default);
    Task<LeaveRequest> RejectRequestAsync(Guid tenantId, Guid requestId, Guid approverId, string approverName, string reason, CancellationToken ct = default);
    Task<LeaveRequest> CancelRequestAsync(Guid tenantId, Guid requestId, string cancelledByName, string reason, CancellationToken ct = default);
    /// <summary>For each request that is KSA statutory leave: the employee's other pending or approved
    /// leave of the same kind, marked whether it is the same statutory event — the approver's view.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<StatutoryLeaveHistoryItem>>> GetKsaStatutoryLeaveHistoryAsync(
        Guid tenantId, IReadOnlyCollection<Guid> requestIds, CancellationToken ct = default);
    /// <summary>Statutory days per event, keyed by leave type, for the KSA statutory leave types among
    /// <paramref name="leaveTypeIds"/> when the employee is on a Saudi entity.</summary>
    Task<IReadOnlyDictionary<Guid, decimal>> GetKsaStatutoryEntitlementsAsync(
        Guid tenantId, int employeeId, IReadOnlyCollection<Guid> leaveTypeIds, CancellationToken ct = default);
    // Audit
    Task LogAuditAsync(Guid tenantId, string entityType, string entityId, string action, string oldValue, string newValue, string reason, string performedByName, CancellationToken ct = default);
    // AI insights
    Task GenerateInsightsAsync(Guid tenantId, CancellationToken ct = default);
}

/// <summary>One earlier leave of the same KSA statutory kind, shown to the approver.</summary>
public sealed record StatutoryLeaveHistoryItem(
    Guid RequestId, string StatutoryKind, string LeaveTypeName, DateOnly StartDate, DateOnly EndDate, decimal TotalDays,
    string Status, bool SameEvent);
