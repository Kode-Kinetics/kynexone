using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Organization;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Organization;

namespace Zayra.Api.Infrastructure.Employees;

/// <summary>
/// The establishment (per-level headcount budget) side of a CSV import, SHARED by Import (commit) and
/// ImportPreview (dry-run) so the two paths agree byte-for-byte on (a) which master data is loaded and
/// (b) whether a row is over budget and downgraded to Draft, plus the exact warning/gap text.
///
/// ACCEPT-NEVER-BLOCK: an over-budget row is NEVER dropped. In Advisory mode it still consumes the slot
/// (budget warns only); in Enforced mode the caller downgrades it to Draft (Draft never occupies a seat,
/// so it cannot breach the budget) and emits an org:establishment gap. The per-row decision OWNS the
/// cumulative <c>claimedLevelSlots</c> mutation so file-order-wins counting is identical in both paths.
/// </summary>
public static class EmployeeImportEstablishmentEvaluator
{
    /// <summary>Master-data preloads for the establishment budget check, loaded ONCE per import. Loaded
    /// identically by preview and commit so the two endpoints measure against the same budgets/occupancy.
    /// When enforcement is Off (or no budget rows exist) the level maps are left empty and the per-row
    /// <see cref="Evaluate"/> short-circuits — no row is ever measured.</summary>
    public sealed class Context
    {
        public required string Mode { get; init; }
        public required IReadOnlyDictionary<(Guid Dept, Guid Level), int> LevelBudgets { get; init; }
        public required IReadOnlyDictionary<Guid, Guid> LevelByDesignation { get; init; }
        public required IReadOnlyDictionary<Guid, (string Code, string NameEn, string NameAr)> LevelNamesById { get; init; }
        public required IReadOnlyDictionary<(Guid Dept, Guid Level), int> CurrentByCell { get; init; }
        public required IReadOnlyDictionary<Guid, string> DeptNameById { get; init; }
    }

    /// <summary>The over-budget verdict for one row. When <see cref="OverBudget"/> is false the row fits
    /// (or is unmeasured) and the caller does nothing.</summary>
    public readonly record struct Decision(
        bool OverBudget, bool Advisory, Guid DeptId, Guid LevelId,
        int Budgeted, int Current, string Detail, string DeptDisplay);

    private static readonly Decision NotOverBudget =
        new(false, false, Guid.Empty, Guid.Empty, 0, 0, string.Empty, string.Empty);

    /// <summary>
    /// Loads the establishment preloads exactly as the commit path does. <c>levelBudgets</c> is always
    /// loaded; the level/occupancy maps only when budget rows exist AND enforcement is not Off (mirrors
    /// commit). Occupancy uses the SAME shared predicate as the panel/guard, with absolute counts via
    /// IgnoreQueryFilters so import checks equal the guard's (explicit TenantId/!IsDeleted applied inline).
    /// </summary>
    public static async Task<Context> LoadAsync(
        ZayraDbContext db, IEstablishmentGuard guard, Guid tenantId, CancellationToken ct)
    {
        var mode = await guard.GetEnforcementModeAsync(tenantId, ct);
        var levelBudgets = await db.DepartmentStaffingBudgets.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.TenantId == tenantId && !b.IsDeleted)
            .ToDictionaryAsync(b => (b.DepartmentId, b.StaffingLevelId), b => b.BudgetedHeadcount, ct);

        var levelByDesignation = new Dictionary<Guid, Guid>();
        var levelNamesById = new Dictionary<Guid, (string Code, string NameEn, string NameAr)>();
        var currentByCell = new Dictionary<(Guid Dept, Guid Level), int>();
        var deptNameById = new Dictionary<Guid, string>();

        if (levelBudgets.Count > 0 && mode != EstablishmentGuardService.ModeOff)
        {
            // IgnoreQueryFilters is intentional: establishment budget lookups/counts must be absolute (independent of the caller's company scope) so import checks equal the guard's; explicit TenantId (+ !IsDeleted where applicable) filters are applied inline.
            levelByDesignation = await db.Designations.IgnoreQueryFilters().AsNoTracking()
                .Where(d => d.TenantId == tenantId && !d.IsDeleted && d.StaffingLevelId != null)
                .ToDictionaryAsync(d => d.Id, d => d.StaffingLevelId!.Value, ct);
            levelNamesById = await db.StaffingLevels.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.TenantId == tenantId && !l.IsDeleted)
                .ToDictionaryAsync(l => l.Id, l => (l.Code, l.NameEn, l.NameAr), ct);
            deptNameById = await db.Departments.IgnoreQueryFilters().AsNoTracking()
                .Where(d => d.TenantId == tenantId && !d.IsDeleted)
                .ToDictionaryAsync(d => d.Id, d => d.NameEn, ct);
            var occupying = await EstablishmentOccupancy
                // IgnoreQueryFilters is intentional: establishment budget lookups/counts must be absolute (independent of the caller's company scope) so import checks equal the guard's; explicit TenantId (+ !IsDeleted where applicable) filters are applied inline.
                .Occupying(db.Employees.IgnoreQueryFilters().AsNoTracking(), tenantId)
                .Where(e => e.DesignationId != null)
                .Select(e => new { e.DepartmentId, e.Department, e.DesignationId })
                .ToListAsync(ct);
            var deptIdByName = deptNameById.ToLookup(kv => kv.Value, kv => kv.Key)
                .ToDictionary(g => g.Key, g => g.First());
            foreach (var e in occupying)
            {
                if (!levelByDesignation.TryGetValue(e.DesignationId!.Value, out var lvl)) continue;
                var deptId = e.DepartmentId
                    ?? (e.Department is { Length: > 0 } dn && deptIdByName.TryGetValue(dn, out var byName) ? byName : (Guid?)null);
                if (deptId is null) continue;
                var cell = (deptId.Value, lvl);
                currentByCell[cell] = currentByCell.GetValueOrDefault(cell) + 1;
            }
        }

        return new Context
        {
            Mode = mode,
            LevelBudgets = levelBudgets,
            LevelByDesignation = levelByDesignation,
            LevelNamesById = levelNamesById,
            CurrentByCell = currentByCell,
            DeptNameById = deptNameById,
        };
    }

    /// <summary>
    /// Applies only when the row lands in a MAPPED level of a department that HAS a budget row AND the
    /// row's status occupies a seat (non-occupying imports never consume). On over budget it records the
    /// cumulative claim for Advisory rows (Enforced rows downgrade to Draft in the caller and stay
    /// non-occupying, so they do NOT claim); an under-budget row always claims its slot. Mutates
    /// <paramref name="claimedLevelSlots"/> so both callers share the identical file-order-wins counter.
    /// </summary>
    public static Decision Evaluate(
        Guid? deptId,
        Guid? designationId,
        string status,
        Context context,
        Dictionary<(Guid Dept, Guid Level), int> claimedLevelSlots,
        string deptNameFallback)
    {
        if (deptId is null || designationId is null
            || !EstablishmentOccupancy.IsOccupyingStatus(status)
            || !context.LevelByDesignation.TryGetValue(designationId.Value, out var levelId)
            || !context.LevelBudgets.TryGetValue((deptId.Value, levelId), out var budget))
            return NotOverBudget;

        var cell = (deptId.Value, levelId);
        var current = context.CurrentByCell.GetValueOrDefault(cell);
        var claimed = claimedLevelSlots.GetValueOrDefault(cell);
        if (current + claimed + 1 > budget)
        {
            var levelName = context.LevelNamesById.TryGetValue(levelId, out var ln) ? ln.NameEn : "budgeted-level";
            var deptDisplay = context.DeptNameById.GetValueOrDefault(deptId.Value, deptNameFallback);
            var detail = $"Department '{deptDisplay}' has {current + claimed} of {budget} budgeted {levelName}(s); no {levelName} slot is available for assignment.";
            var advisory = context.Mode == EstablishmentGuardService.ModeAdvisory;
            if (advisory) claimedLevelSlots[cell] = claimed + 1; // advisory rows still consume; enforced downgrades to Draft (non-occupying)
            return new Decision(true, advisory, deptId.Value, levelId, budget, current + claimed, detail, deptDisplay);
        }
        claimedLevelSlots[cell] = claimed + 1;
        return NotOverBudget;
    }
}
