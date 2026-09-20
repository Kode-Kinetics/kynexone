using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Assets;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Assets;

/// <summary>
/// W2-C — what offboarding needs from the asset register.
///
/// <para><b>"Outstanding"</b> = an Active custody period for the leaver, UNLESS a write-off for it has been
/// approved by its workflow's final step (the approval engine is the source of truth; the register catches up
/// when <see cref="IAssetCustodyService.ReconcileWriteOffsAsync"/> runs). A write-off that is merely
/// <i>requested</i> does not clear anything.</para>
///
/// <para><b>Scope.</b> The clearance question is "does this person still hold ANY of our property?", so it is
/// asked tenant-wide via <see cref="ScopedBypass.TenantWide{T}"/> — a leaver holding an item owned by a sister
/// company must still block the archive, even for an HR user scoped to one company. The tenant is re-applied;
/// only the leaver's own custody rows are read.</para>
/// </summary>
public sealed class AssetClearanceService
{
    private const string ClearanceJustification =
        "Offboarding clearance must see every asset the leaver holds across all legal entities of the tenant; tenant re-applied, rows limited to one employee.";

    private readonly ZayraDbContext _db;

    public AssetClearanceService(ZayraDbContext db) => _db = db;

    /// <summary>Active custody rows for the employee that are not covered by an approved write-off.</summary>
    public IQueryable<AssetAssignment> OutstandingQuery(Guid tenantId, int employeeId)
    {
        var writeOffs = ScopedBypass.TenantWide(_db.AssetWriteOffRequests, tenantId, ClearanceJustification);
        var approvals = ScopedBypass.TenantWide(_db.ApprovalRequests, tenantId, ClearanceJustification);
        return ScopedBypass.TenantWide(_db.AssetAssignments, tenantId, ClearanceJustification)
            .Where(a => a.EmployeeId == employeeId && a.Status == AssetAssignmentStatuses.Active)
            .Where(a => !writeOffs.Any(w => w.AssignmentId == a.Id
                                            && w.Status == AssetWriteOffStatuses.Pending
                                            && approvals.Any(r => r.Id == w.ApprovalRequestId && r.Status == "Approved")));
    }

    public Task<int> CountOutstandingAsync(Guid tenantId, int employeeId, CancellationToken ct)
        => OutstandingQuery(tenantId, employeeId).CountAsync(ct);

    public async Task<AssetClearanceDto> GetClearanceAsync(Guid tenantId, int employeeId, CancellationToken ct)
    {
        var today = AssetCustodyService.Today();
        var outstanding = await (
                from a in OutstandingQuery(tenantId, employeeId)
                join s in ScopedBypass.TenantWide(_db.Assets, tenantId, ClearanceJustification) on a.AssetId equals s.Id
                orderby a.IssuedAtUtc
                select new { a, s.AssetTag, s.Name, s.CategoryCode })
            .AsNoTracking()
            .ToListAsync(ct);
        var pending = await ScopedBypass.TenantWide(_db.AssetWriteOffRequests, tenantId, ClearanceJustification)
            .AsNoTracking()
            .Where(w => w.EmployeeId == employeeId && w.Status == AssetWriteOffStatuses.Pending)
            .OrderBy(w => w.RequestedAtUtc)
            .ToListAsync(ct);

        var items = outstanding.Select(x => AssetMapping.ToDto(x.a, x.AssetTag, x.Name, x.CategoryCode, today)).ToList();
        return new AssetClearanceDto(employeeId, items.Count == 0, items.Count, items,
            pending.Select(AssetMapping.ToDto).ToList());
    }

    /// <summary>
    /// Keeps the legacy <see cref="EmployeeOffboarding.AssetsReturned"/> flag in step with the register for an
    /// employee who is serving notice. Outstanding items ⇒ false, always. When a clearing event (return,
    /// transfer away, approved write-off) empties the leaver's custody ⇒ true — the register is the evidence.
    /// Staged on the change tracker; the caller's transaction saves it.
    /// </summary>
    public async Task SyncOffboardingFlagAsync(Guid tenantId, int employeeId, bool cleared, CancellationToken ct)
    {
        var offboarding = await _db.EmployeeOffboardings
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.EmployeeId == employeeId && o.Status == "InProgress", ct);
        if (offboarding is null) return;

        // Count what the database will hold once this unit commits: rows staged in the change tracker are
        // not visible to the query yet, so adjust for them.
        var persisted = await OutstandingQuery(tenantId, employeeId).Select(a => a.Id).ToListAsync(ct);
        var staged = _db.ChangeTracker.Entries<AssetAssignment>()
            .Where(e => e.Entity.TenantId == tenantId && e.Entity.EmployeeId == employeeId)
            .ToList();
        var outstanding = new HashSet<Guid>(persisted);
        foreach (var e in staged)
        {
            if (e.Entity.Status == AssetAssignmentStatuses.Active) outstanding.Add(e.Entity.Id);
            else outstanding.Remove(e.Entity.Id);
        }

        if (outstanding.Count > 0) offboarding.AssetsReturned = false;
        else if (cleared) offboarding.AssetsReturned = true;
        offboarding.UpdatedAtUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Called when an offboarding starts: every item the leaver holds becomes due back by the last working day
    /// (an earlier existing due date is kept). This is what arms the return reminders for leavers.
    /// </summary>
    public async Task<int> AlignReturnDatesToLastWorkingDayAsync(Guid tenantId, int employeeId, DateOnly lastWorkingDay, CancellationToken ct)
    {
        // Normal (company-filtered) read: these rows are WRITTEN, and the company-scope write guard only
        // lets the caller modify rows of companies they can see. The gate itself stays tenant-wide.
        var active = await _db.AssetAssignments
            .Where(a => a.TenantId == tenantId && a.EmployeeId == employeeId && a.Status == AssetAssignmentStatuses.Active)
            .ToListAsync(ct);
        var changed = 0;
        foreach (var a in active.Where(a => a.ExpectedReturnDate is null || a.ExpectedReturnDate > lastWorkingDay))
        {
            // Deliberately not clamped to the issue date: an item handed out after the last working day is
            // still due back by it, i.e. it is overdue at once.
            a.ExpectedReturnDate = lastWorkingDay;
            a.DueSoonReminderSentAtUtc = null;
            a.OverdueReminderSentAtUtc = null;
            changed++;
        }
        return changed;
    }
}

internal static class AssetMapping
{
    public static bool IsOverdue(AssetAssignment a, DateOnly today)
        => a.Status == AssetAssignmentStatuses.Active && a.ExpectedReturnDate is { } d && d < today;

    public static AssetAssignmentDto ToDto(AssetAssignment a, string assetTag, string assetName, string categoryCode, DateOnly today) => new(
        a.Id, a.AssetId, assetTag, assetName, categoryCode, a.EmployeeId, a.EmployeeName, a.EmployeeCode, a.Status,
        a.IssuedOn, a.IssuedAtUtc, a.IssuedByName, a.ConditionOnIssue, a.IssueNotes,
        a.ExpectedReturnDate, a.ReturnedOn, a.ClosedAtUtc, a.ClosedByName, a.ConditionOnReturn, a.ReturnNotes,
        a.TransferredToAssignmentId, a.WriteOffRequestId, IsOverdue(a, today));

    public static AssetWriteOffDto ToDto(AssetWriteOffRequest w) => new(
        w.Id, w.AssetId, w.AssignmentId, w.EmployeeId, w.Kind, w.Reason, w.Status, w.ApprovalRequestId,
        w.RequestedByName, w.RequestedAtUtc, w.DecidedAtUtc, w.DecisionComments);

    public static AssetListItemDto ToListItem(Asset s, AssetAssignment? holder, bool pendingWriteOff, DateOnly today) => new(
        s.Id, s.AssetTag, s.Name, s.SerialNumber, s.CategoryCode, s.Make, s.Model, s.Status, s.Condition,
        s.CompanyId, s.BranchId, s.LocationId, s.LocationNote, s.PurchaseDate, s.PurchaseCost, s.Currency,
        holder is null ? null : new AssetHolderDto(holder.Id, holder.EmployeeId, holder.EmployeeName, holder.EmployeeCode,
            holder.IssuedOn, holder.ExpectedReturnDate, IsOverdue(holder, today)),
        pendingWriteOff, s.Version);
}
