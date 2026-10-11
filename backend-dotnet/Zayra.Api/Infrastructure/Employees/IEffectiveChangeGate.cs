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
