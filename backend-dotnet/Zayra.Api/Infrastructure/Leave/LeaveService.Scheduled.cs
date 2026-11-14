using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Leave;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Leave;

/// <summary>
/// The scheduled half of the leave engine (L3.1): the monthly accrual for the months that have ended, and the year rollover.
/// Both are built on the engine that already existed (<see cref="AccrueMonthlyAsync(Guid, CancellationToken)"/> and the front-loaded
/// grant behind <see cref="OpenYearBalancesAsync(Guid, int, CancellationToken)"/>) and inherit its guarantees: a month is one ledger
/// entry per employee and leave type that can only be posted once, and a balance that already holds a figure the engine did not
/// post is left alone.
/// </summary>
public partial class LeaveService
{
    private const string YearlyReferencePrefix = "YEARLY-ENTITLEMENT-";

    /// <summary>The ledger entry that takes a year's unused days out of the old year's row (the matching <c>CarryForward</c> puts them into the new one).</summary>
    public const string CarryForwardOutTransaction = "CarryForwardOut";

    /// <summary>
    /// What the monthly job does for one tenant, on the tenant's OWN calendar day <paramref name="tenantToday"/>:
    /// <list type="number">
    /// <item>accrues every month of the year that has ended and not yet been accrued (the month just finished, and any missed while nothing ran),
    ///   and, when nothing ran in January, the months of last year still missing (its December) before that year is rolled over;
    ///   the current month is never accrued early, so a joiner later this month and a leaver this month are credited by what they served;</item>
    /// <item>front-loads yearly policies for the tenant's current year, for anyone not granted yet (a joiner, or the new year);</item>
    /// <item>rolls last year's unused annual leave into this year and closes the old row (<see cref="RolloverYearAsync"/>) — after the
    ///   December accrual, which is why this is the same call.</item>
    /// </list>
    /// Safe to run any number of times.
    /// </summary>
    public async Task<LeaveScheduledAccrualResult> RunScheduledAccrualAsync(Guid tenantId, DateOnly tenantToday, CancellationToken ct = default)
    {
        var lastCompletedMonthEnd = new DateOnly(tenantToday.Year, tenantToday.Month, 1).AddDays(-1);
        var accrualYear = lastCompletedMonthEnd.Year;
        var asOf = tenantToday.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        // Balances already holding a figure the engine did not post, computed before anything runs.
        var establishedForAccrual = await EstablishedPairsAsync(tenantId, accrualYear, ct);
        var establishedForGrant = accrualYear == tenantToday.Year ? establishedForAccrual : await EstablishedPairsAsync(tenantId, tenantToday.Year, ct);
        var accrualsBefore = await CountLedgerAsync(tenantId, accrualYear, "Accrual", null, ct);
        var allocationsBefore = await CountLedgerAsync(tenantId, tenantToday.Year, "Allocation", YearlyReferencePrefix + tenantToday.Year, ct);

        // A run that resumes after missing all of January lands with its last completed month in the NEW year, so the old year's
        // December (and anything else of it still missing) would never be accrued, yet the rollover below closes that year. Complete
        // the old year first. Only a year the engine was already accruing is completed: a tenant whose first run falls after
        // January is not credited for the year before. Each month posts once per pair, so a month already accrued is skipped.
        var previousYear = tenantToday.Year - 1;
        var previousYearAccrualsBefore = 0;
        if (accrualYear == tenantToday.Year
            && (previousYearAccrualsBefore = await CountLedgerAsync(tenantId, previousYear, "Accrual", null, ct)) > 0)
        {
            var establishedForPreviousYear = await EstablishedPairsAsync(tenantId, previousYear, ct);
            for (var month = 1; month <= 12; month++)
                await AccrueMonthlyAsync(tenantId,
                    new DateTime(previousYear, month, DateTime.DaysInMonth(previousYear, month), 0, 0, 0, DateTimeKind.Utc), establishedForPreviousYear, ct);
        }
        var previousYearCaughtUp = previousYearAccrualsBefore == 0 ? 0
            : await CountLedgerAsync(tenantId, previousYear, "Accrual", null, ct) - previousYearAccrualsBefore;

        for (var month = 1; month <= lastCompletedMonthEnd.Month; month++)
            await AccrueMonthlyAsync(tenantId,
                new DateTime(accrualYear, month, DateTime.DaysInMonth(accrualYear, month), 0, 0, 0, DateTimeKind.Utc), establishedForAccrual, ct);

        await RunSerializedAsync($"leave-open-year:{tenantId:N}:{tenantToday.Year}",
            () => FrontLoadYearlyCoreAsync(tenantId, tenantToday.Year, asOf, establishedForGrant, YearlyReferencePrefix + tenantToday.Year, ct), ct);

        var rollover = await RolloverYearAsync(tenantId, tenantToday.Year - 1, ct);
        return new LeaveScheduledAccrualResult(
            accrualYear,
            await CountLedgerAsync(tenantId, accrualYear, "Accrual", null, ct) - accrualsBefore + previousYearCaughtUp,
            await CountLedgerAsync(tenantId, tenantToday.Year, "Allocation", YearlyReferencePrefix + tenantToday.Year, ct) - allocationsBefore,
            rollover);
    }

    private async Task<int> CountLedgerAsync(Guid tenantId, int year, string transactionType, string? reference, CancellationToken ct)
        => await _db.LeaveBalanceTransactions.AsNoTracking().CountAsync(t => t.TenantId == tenantId && t.Year == year
            && t.TransactionType == transactionType && (reference == null || t.Reference == reference), ct);

    /// <summary>(employee, leave type) pairs whose balance for <paramref name="year"/> holds a figure the engine did not post (imported at cutover, entered by hand).</summary>
    private async Task<IReadOnlySet<(int EmployeeId, Guid LeaveTypeId)>> EstablishedPairsAsync(Guid tenantId, int year, CancellationToken ct)
    {
        var engineLedger = (await _db.LeaveBalanceTransactions.AsNoTracking()
                .Where(t => t.TenantId == tenantId && t.Year == year
                    && (t.TransactionType == "Accrual" || (t.TransactionType == "Allocation" && t.Reference == YearlyReferencePrefix + year)))
                .Select(t => new { t.EmployeeId, t.LeaveTypeId })
                .Distinct().ToListAsync(ct))
            .Select(t => (t.EmployeeId, t.LeaveTypeId)).ToHashSet();
        return (await _db.EmployeeLeaveBalances.AsNoTracking()
                .Where(b => b.TenantId == tenantId && b.Year == year && (b.Entitled > 0m || b.Accrued > 0m))
                .Select(b => new { b.EmployeeId, b.LeaveTypeId })
                .ToListAsync(ct))
            .Select(b => (b.EmployeeId, b.LeaveTypeId))
            .Where(pair => !engineLedger.Contains(pair))
            .ToHashSet();
    }

    /// <summary>
    /// Year rollover: for every annual-leave balance of <paramref name="fromYear"/> with days left, the unused days move into the next
    /// year's <c>CarriedForward</c> and the old row is CLOSED, so a day is never counted in both. Default (CTO 2026-11-12): carry all, no
    /// expiry — Labour Law Art. 110 does not allow annual leave to be waived, so a cap or an expiry that forfeits days is not applied (and
    /// the policy screen refuses to store one).
    ///
    /// <para>The old row is closed with a negative <c>ManualAdjustment</c> and a <c>CarryForwardOut</c> ledger entry (reference
    /// <c>CARRYFORWARD-year</c>), so <see cref="EmployeeLeaveBalance.Available"/> — the one definition every screen, the provision and a final
    /// settlement read — shows it as spent without a second place to keep the days. No schema change.</para>
    ///
    /// <para>Safe to run again: it moves only what is still open, so a day returned to the closed year later (a December leave cancelled
    /// after the rollover) is carried by the next run. Left alone: sick and event leave (not carried), employees who left during the year (their
    /// settlement reads the row as it stands), negative balances, and a pair whose new year already carries days the engine did not post
    /// (carried by hand or imported; a final settlement nets those).</para>
    /// </summary>
    public async Task<LeaveYearRolloverResult> RolloverYearAsync(Guid tenantId, int fromYear, CancellationToken ct = default)
    {
        var result = new LeaveYearRolloverResult(fromYear, 0, 0m);
        await RunSerializedAsync($"leave-rollover:{tenantId:N}:{fromYear}", async () => result = await RolloverYearCoreAsync(tenantId, fromYear, ct), ct);
        return result;
    }

    private static bool IsAnnualLeaveType(string? code, string? category, string? nameEn)
        => string.Equals(code, "ANNUAL", StringComparison.OrdinalIgnoreCase)
           || string.Equals(category, "Annual", StringComparison.OrdinalIgnoreCase)
           || (nameEn ?? string.Empty).Contains("Annual", StringComparison.OrdinalIgnoreCase);

    private async Task<LeaveYearRolloverResult> RolloverYearCoreAsync(Guid tenantId, int fromYear, CancellationToken ct)
    {
        var none = new LeaveYearRolloverResult(fromYear, 0, 0m);
        var toYear = fromYear + 1;
        var reference = $"CARRYFORWARD-{fromYear}";

        var annualTypeIds = (await _db.LeaveTypes.AsNoTracking().Where(t => t.TenantId == tenantId && t.IsActive)
                .Select(t => new { t.Id, t.Code, t.Category, t.NameEn }).ToListAsync(ct))
            .Where(t => IsAnnualLeaveType(t.Code, t.Category, t.NameEn)).Select(t => t.Id).ToHashSet();
        if (annualTypeIds.Count == 0) return none;

        // Available is computed, not stored, so "days left" is decided here and not in SQL.
        var rows = (await _db.EmployeeLeaveBalances
                .Where(b => b.TenantId == tenantId && b.Year == fromYear && annualTypeIds.Contains(b.LeaveTypeId))
                .ToListAsync(ct))
            .Where(b => b.Available > 0m).ToList();
        if (rows.Count == 0) return none;

        var employeeIds = rows.Select(r => r.EmployeeId).Distinct().ToList();
        var employees = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && employeeIds.Contains(e.Id) && !e.IsDeleted
                && (e.Status == EmployeeStatuses.Active || e.Status == EmployeeStatuses.Offboarded))
            .Select(e => new { e.Id, e.CompanyId, e.FullName })
            .ToDictionaryAsync(e => e.Id, ct);
        var yearEnd = new DateOnly(fromYear, 12, 31);
        var leftInYear = (await _db.EmployeeOffboardings.AsNoTracking()
                .Where(o => o.TenantId == tenantId && employeeIds.Contains(o.EmployeeId) && o.Status != "Cancelled"
                    && o.LastWorkingDay != default && o.LastWorkingDay <= yearEnd)
                .Select(o => o.EmployeeId).ToListAsync(ct))
            .ToHashSet();

        var closed = 0;
        var carriedTotal = 0m;
        foreach (var row in rows)
        {
            if (!employees.TryGetValue(row.EmployeeId, out var employee) || leftInYear.Contains(row.EmployeeId)) continue;

            var existing = await _db.EmployeeLeaveBalances.FirstOrDefaultAsync(b => b.TenantId == tenantId
                && b.EmployeeId == row.EmployeeId && b.LeaveTypeId == row.LeaveTypeId && b.Year == toYear, ct);
            if (existing is { CarriedForward: > 0m }
                && !await _db.LeaveBalanceTransactions.AsNoTracking().AnyAsync(t => t.TenantId == tenantId && t.EmployeeId == row.EmployeeId
                    && t.LeaveTypeId == row.LeaveTypeId && t.Year == toYear && t.TransactionType == "CarryForward" && t.Reference == reference, ct))
                continue;

            var carry = Math.Round(row.Available, 2);
            if (carry <= 0m) continue;
            var target = existing ?? await GetOrCreateBalanceAsync(tenantId, row.EmployeeId, row.LeaveTypeId, toYear, ct);
            var now = DateTime.UtcNow;

            var oldBefore = row.Available;
            row.ManualAdjustment -= carry;
            row.UpdatedAtUtc = now;
            _db.LeaveBalanceTransactions.Add(new LeaveBalanceTransaction
            {
                TenantId = tenantId, CompanyId = employee.CompanyId, EmployeeId = row.EmployeeId, LeaveTypeId = row.LeaveTypeId, Year = fromYear,
                TransactionType = CarryForwardOutTransaction, Amount = -carry, BalanceBefore = oldBefore, BalanceAfter = row.Available,
                Reference = reference, Reason = $"Unused days carried forward into {toYear}", PerformedByName = "System",
            });

            var newBefore = target.Available;
            target.EmployeeName = string.IsNullOrWhiteSpace(target.EmployeeName) ? employee.FullName : target.EmployeeName;
            target.CarriedForward += carry;
            target.UpdatedAtUtc = now;
            _db.LeaveBalanceTransactions.Add(new LeaveBalanceTransaction
            {
                TenantId = tenantId, CompanyId = employee.CompanyId, EmployeeId = row.EmployeeId, LeaveTypeId = row.LeaveTypeId, Year = toYear,
                TransactionType = "CarryForward", Amount = carry, BalanceBefore = newBefore, BalanceAfter = target.Available,
                Reference = reference, Reason = $"Unused days carried forward from {fromYear}", PerformedByName = "System",
            });
            closed++;
            carriedTotal += carry;
        }

        await _db.SaveChangesAsync(ct);
        return new LeaveYearRolloverResult(fromYear, closed, carriedTotal);
    }
}
