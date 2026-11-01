using System.Collections.Concurrent;

namespace Zayra.Api.Infrastructure.Employees.Onboarding;

/// <summary>
/// In-process memory of when each company's dead-end check last ran. Two uses, neither of which affects any approval
/// decision: (1) a short debounce so a burst of changes does not re-evaluate the same company over and over, and
/// (2) fair ordering, so a per-call or per-run company cap serves the longest-unchecked companies first instead of
/// always the same ones. Lost on restart, which only means every company counts as "not checked yet".
/// </summary>
public static class PayApprovalDeadEndRotation
{
    private const int PruneAbove = 20_000;
    private static readonly ConcurrentDictionary<(Guid Tenant, Guid Company), long> LastChecked = new();

    public static void Touch(Guid tenantId, Guid companyId, DateTime? nowUtc = null)
    {
        LastChecked[(tenantId, companyId)] = (nowUtc ?? DateTime.UtcNow).Ticks;
        if (LastChecked.Count > PruneAbove) Prune();
    }

    /// <summary>True when the company was checked less than <paramref name="window"/> ago.</summary>
    public static bool CheckedWithin(Guid tenantId, Guid companyId, TimeSpan window, DateTime? nowUtc = null) =>
        window > TimeSpan.Zero
        && LastChecked.TryGetValue((tenantId, companyId), out var ticks)
        && (nowUtc ?? DateTime.UtcNow).Ticks - ticks < window.Ticks;

    /// <summary>Never-checked companies first, then the longest-unchecked; ties broken by id so the order is stable.</summary>
    public static List<Guid> OldestFirst(Guid tenantId, IEnumerable<Guid> companyIds) =>
        companyIds.Distinct()
            .OrderBy(c => LastChecked.TryGetValue((tenantId, c), out var ticks) ? ticks : 0L)
            .ThenBy(c => c).ToList();

    private static void Prune()
    {
        var cutoff = DateTime.UtcNow.AddDays(-1).Ticks;
        foreach (var pair in LastChecked.Where(p => p.Value < cutoff).ToList()) LastChecked.TryRemove(pair.Key, out _);
        if (LastChecked.Count > PruneAbove) LastChecked.Clear();
    }
}
