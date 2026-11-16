using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Employees;

/// <summary>
/// The ONE status rule for every employee list, archive and bulk "select all matching": the typed status is compared
/// without regard to case, so a legacy "active" row still matches "Active". Both sides are lower-cased in SQL
/// (the same pattern as <see cref="ExitEmployeeStatuses.ExitLower"/>); nothing is filtered client-side and no stored status is rewritten.
/// </summary>
public static class EmployeeStatusFilter
{
    public static IQueryable<Employee> Apply(IQueryable<Employee> query, string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return query;
        var lower = status.Trim().ToLowerInvariant();
        return query.Where(e => e.Status.ToLower() == lower);
    }

    /// <summary>Folds per-spelling counts into one row per status (case-insensitive). The canonical spelling is the most common one, ties to the first seen.</summary>
    public static List<(string Name, int Count)> Collapse(IEnumerable<(string Name, int Count)> rows)
        => rows.GroupBy(r => (r.Name ?? string.Empty).Trim().ToLowerInvariant())
            .Select(g => (Name: g.OrderByDescending(r => r.Count).ThenBy(r => r.Name, StringComparer.Ordinal).First().Name, Count: g.Sum(r => r.Count)))
            .ToList();
}
