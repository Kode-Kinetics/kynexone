using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Recruitment;

public enum PanelStanding { Assigned, NotAssigned, AmbiguousName }

/// <summary>
/// Who may see and write feedback on an interview. HR coordinators run panels and may record for any interviewer; anyone else (a line manager on
/// the panel) only on an interview they are on. Membership is exact: by login id when the interview stored its panel's ids (every interview scheduled
/// since they were recorded), otherwise by the caller's exact normalised full name in the panel list, and never when more than one employee or login
/// in the company carries that name (the person cannot be told apart, so HR must fix the panel).
/// </summary>
public static class InterviewPanel
{
    private static readonly string[] CoordinatorRoles = ["Admin", "HR Manager", "HR Officer", "Recruiter"];
    private static readonly char[] NameSeparators = [',', ';', '\n', '\r', '،', '؛'];

    public static bool IsCoordinator(ClaimsPrincipal user) => CoordinatorRoles.Any(user.IsInRole);

    public static string Normalize(string? name) =>
        string.Join(' ', (name ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    public static IReadOnlyList<string> Names(string? list) =>
        (list ?? string.Empty).Split(NameSeparators, StringSplitOptions.RemoveEmptyEntries).Select(Normalize).Where(n => n.Length > 0).ToList();

    public static IReadOnlyList<Guid> Ids(string? list) =>
        (list ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => Guid.TryParse(x, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList();

    /// <summary>True when the caller is on the interview's panel by exact id, or (no ids stored) by exact name; <see cref="PanelStanding.AmbiguousName"/> when
    /// only the name could say so and it is shared.</summary>
    public static async Task<PanelStanding> StandingAsync(ZayraDbContext db, Guid tenantId, InterviewSchedule interview, Guid? userId, string? userName, CancellationToken ct)
    {
        var stored = Ids(interview.InterviewerUserIds);
        if (stored.Count > 0) return userId is Guid me && stored.Contains(me) ? PanelStanding.Assigned : PanelStanding.NotAssigned;

        var name = Normalize(userName);
        if (name.Length == 0 || !Names(interview.InterviewerNames).Contains(name)) return PanelStanding.NotAssigned;
        return await NameIsSharedAsync(db, tenantId, interview.CompanyId, name, ct) ? PanelStanding.AmbiguousName : PanelStanding.Assigned;
    }

    /// <summary>More than one employee of the company, or more than one login of the workspace, carries this (normalised) name.</summary>
    public static async Task<bool> NameIsSharedAsync(ZayraDbContext db, Guid tenantId, Guid? companyId, string normalizedName, CancellationToken ct)
    {
        var employees = await db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && !e.IsDeleted && (companyId == null || e.CompanyId == companyId)
                && (e.FullName.ToLower() == normalizedName || e.EnglishName.ToLower() == normalizedName || e.ArabicName.ToLower() == normalizedName))
            .CountAsync(ct);
        if (employees > 1) return true;
        var users = await db.Users.AsNoTracking()
            .Where(u => u.TenantId == tenantId && u.IsActive && !u.IsDeleted && u.FullName.ToLower() == normalizedName).CountAsync(ct);
        return users > 1;
    }

    /// <summary>The ids to store on a new interview: the ones the request named (that exist as logins of this workspace) plus every panel name that matches exactly one login.</summary>
    public static async Task<string?> ResolveIdsAsync(ZayraDbContext db, Guid tenantId, string? names, IEnumerable<Guid>? explicitIds, CancellationToken ct, Guid? companyId = null)
    {
        var ids = new List<Guid>();
        var wanted = (explicitIds ?? []).Distinct().ToList();
        if (wanted.Count > 0)
            ids.AddRange(await db.Users.AsNoTracking().Where(u => u.TenantId == tenantId && u.IsActive && !u.IsDeleted && wanted.Contains(u.Id)).Select(u => u.Id).ToListAsync(ct));
        foreach (var name in Names(names))
        {
            var matches = await db.Users.AsNoTracking()
                .Where(u => u.TenantId == tenantId && u.IsActive && !u.IsDeleted && u.FullName.ToLower() == name).Select(u => u.Id).Take(2).ToListAsync(ct);
            // A name more than one person carries (employees of the company, or logins) says nothing about which of them is meant.
            if (matches.Count == 1 && !await NameIsSharedAsync(db, tenantId, companyId, name, ct)) ids.Add(matches[0]);
        }
        return ids.Count == 0 ? null : string.Join(',', ids.Distinct());
    }
}
