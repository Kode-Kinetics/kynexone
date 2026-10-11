using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Compensation;

/// <summary>
/// On the effective date, a merit award is re-checked against its cycle's eligibility rules (the same function used at
/// planning and at apply). An employee who went on notice, started offboarding, was terminated, or moved grade or
/// department after the awards were scheduled is not paid the award: the change is cancelled with the reason, the line
/// records it, and HR (whoever scheduled the awards) is notified. Changes that did not come from a merit cycle are not
/// this gate's business.
/// </summary>
public sealed class MeritEffectiveChangeGate : IEffectiveChangeGate
{
    public const string ReasonCode = "merit_no_longer_eligible";

    public async Task<EffectiveChangeRefusal?> CheckAsync(ZayraDbContext db, Guid tenantId, EmployeeChangeRequest change, Employee employee,
        DateOnly localToday, CancellationToken ct)
    {
        // The job runs without a user, so the company filter is dropped for these reads; the tenant is pinned.
        var line = await ScopedBypass.TenantWide(db.CompWorksheetLines, tenantId, "effective-date job: find the merit line that raised this change")
            .FirstOrDefaultAsync(l => l.ChangeRequestId == change.Id, ct);
        if (line is null) return null;
        var cycle = await ScopedBypass.TenantWide(db.CompCycles, tenantId, "effective-date job: the merit cycle of this change")
            .AsNoTracking().FirstOrDefaultAsync(c => c.Id == line.CycleId, ct);
        if (cycle is null) return null;

        var separation = await MeritBasisLoader.SeparationAsync(db, tenantId, employee.Id, ct);
        var (reason, _) = MeritBasisLoader.Eligibility(cycle, employee, separation);
        if (reason is null && employee.GradeId != line.GradeId) reason = "The employee's grade changed after the award was scheduled.";
        if (reason is null && employee.DepartmentId != line.DepartmentId) reason = "The employee moved department after the award was scheduled.";
        if (reason is null) return null;

        var message = $"Merit award from cycle '{cycle.Name}' was not applied on {change.EffectiveDate:yyyy-MM-dd}: no longer eligible. {reason}";
        line.ApplyBlockedReason = message.Length <= 1000 ? message : message[..1000];
        return new EffectiveChangeRefusal(ReasonCode, message, cycle.AppliedByUserId ?? cycle.CreatedByUserId);
    }
}
