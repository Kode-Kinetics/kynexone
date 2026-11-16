using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Leave;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Leave;

/// <summary>
/// Spec M1-M3 (R10), REPORT ONLY. Before month slicing an approved unpaid leave wrote ONE deduction for the whole request,
/// charged to its first month. This lists every such row that spans more than one month and says what the slice model
/// would do with it; it changes nothing (AsNoTracking reads only, no SaveChanges, no audit entry).
///
/// <list type="bullet">
/// <item><b>Would split</b> (M1): Pending, unpaid, request still approved, and its month-by-month days add up to the row's
/// days. Parts share the ORIGINAL amount by days, the last month taking the cent remainder, so they sum to it exactly.</item>
/// <item><b>Do not reconcile</b>: Pending, but the month days do not add up to the row's days, or the request is no longer
/// approved. Listed for HR; never split automatically.</item>
/// <item><b>Already deducted</b> (M2): Processed. Untouched; the parts show how the money would have been timed.</item>
/// <item><b>Sick-leave scale rows</b> (M3): their days are unpaid-equivalent days, not leave days, so they are never split
/// by days; listed for HR.</item>
/// </list>
/// </summary>
public partial class LeaveService
{
    public async Task<LeaveSliceSplitReport> BuildSliceSplitReportAsync(
        Guid tenantId, IReadOnlyCollection<int>? allowedEmployeeIds, CancellationToken ct = default)
    {
        var empty = new LeaveSliceSplitReport([], [], [], []);
        // Only employees the caller can see: the company filter on Employees, then the caller's data scope.
        var ids = await _db.Employees.AsNoTracking().Where(e => e.TenantId == tenantId).Select(e => e.Id).ToListAsync(ct);
        if (allowedEmployeeIds is not null) ids = ids.Where(allowedEmployeeIds.Contains).ToList();
        if (ids.Count == 0) return empty;
        var rows = await _db.LeavePayrollImpacts.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.SliceFrom == null && x.Amount > 0m
                     && x.ImpactType != LeaveArrears.ReversalImpactType && x.ImpactType != LeaveArrears.RemainderImpactType
                     && x.ImpactType.Contains("Deduction") && ids.Contains(x.EmployeeId))
            .ToListAsync(ct);
        if (rows.Count == 0) return empty;
        var requestIds = rows.Select(x => x.LeaveRequestId).Distinct().ToList();
        var requests = await _db.LeaveRequests.AsNoTracking()
            .Where(r => r.TenantId == tenantId && requestIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, ct);
        var people = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && ids.Contains(e.Id))
            .Select(e => new { e.Id, e.EmployeeCode, e.FullName })
            .ToDictionaryAsync(e => e.Id, ct);

        List<LeaveSliceSplitRow> wouldSplit = [], doNotReconcile = [], alreadyDeducted = [], sick = [];
        foreach (var row in rows.OrderBy(x => x.EmployeeId).ThenBy(x => x.PayPeriod, StringComparer.Ordinal))
        {
            if (!requests.TryGetValue(row.LeaveRequestId, out var request)) continue;
            var multiMonth = request.StartDate.Year != request.EndDate.Year || request.StartDate.Month != request.EndDate.Month;
            var singleDate = request.StartDate == request.EndDate
                || request.DayType.StartsWith("Half", StringComparison.OrdinalIgnoreCase)
                || request.DayType.Equals("Hourly", StringComparison.OrdinalIgnoreCase);
            if (!multiMonth || singleDate) continue;

            people.TryGetValue(row.EmployeeId, out var person);
            LeaveSliceSplitRow Row(IReadOnlyList<LeaveSliceSplitPart> parts, string note) => new(
                row.Id, row.LeaveRequestId, row.EmployeeId, person?.EmployeeCode ?? string.Empty, person?.FullName ?? string.Empty,
                request.StartDate, request.EndDate, request.Status, row.PayPeriod, row.Status, row.ImpactType, row.Days, row.Amount, parts, note);

            if (row.ImpactType != CompanyPayrollRules.UnpaidLeaveImpactType)
            {
                sick.Add(Row([], "A sick-leave pay-scale row: its days are unpaid-equivalent days, so it is not split by days. HR reviews it."));
                continue;
            }

            var months = await CountMonthSlicesAsync(tenantId, request, ct);
            var parts = Allocate(months, row.Amount);
            if (row.Status == "Processed")
            {
                alreadyDeducted.Add(Row(parts, $"Already deducted in {row.PayPeriod}. Left as it is: the total is right, only the timing differs."));
                continue;
            }
            var stillApproved = LeaveTakenStatuses.All.Contains(request.Status);
            var monthDays = months.Sum(m => m.Days);
            if (!stillApproved)
                doNotReconcile.Add(Row(parts, $"The request is now '{request.Status}', so it would not be split."));
            else if (months.Count == 0 || monthDays != row.Days)
                doNotReconcile.Add(Row(parts, $"The months add up to {monthDays:0.##} day(s) but the deduction is for {row.Days:0.##}. HR checks the day count before anything is split."));
            else
                wouldSplit.Add(Row(parts, $"Would split into {parts.Count} month(s); the amounts add up to {row.Amount:0.00} exactly."));
        }
        return new LeaveSliceSplitReport(wouldSplit, doNotReconcile, alreadyDeducted, sick);
    }

    /// <summary>The original amount shared by days, rounded to the cent, the last month taking the remainder (R10).</summary>
    internal static List<LeaveSliceSplitPart> Allocate(IReadOnlyList<UnpaidLeaveMonthSlice> months, decimal amount)
    {
        var total = months.Sum(m => m.Days);
        var parts = new List<LeaveSliceSplitPart>(months.Count);
        var given = 0m;
        for (var i = 0; i < months.Count; i++)
        {
            var m = months[i];
            var share = i == months.Count - 1 || total == 0m ? amount - given : Math.Round(amount * m.Days / total, 2);
            parts.Add(new LeaveSliceSplitPart($"{m.From.Year}-{m.From.Month:00}", m.From, m.To, m.Days, share));
            given += share;
        }
        return parts;
    }
}
