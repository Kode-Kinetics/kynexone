using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Performance;

/// <summary>
/// The Performance permission keys, as constants usable in <c>[HasPermission]</c>. The frontend mirrors
/// them in <c>frontend/src/lib/performanceAccess.ts</c>: any one of the four opens the module.
/// </summary>
public static class PerformancePermissions
{
    public const string Read = "performance.read";
    public const string Write = "performance.write";
    public const string Approve = "performance.approve";
    public const string Setup = "performance.cycle_manage";
}

/// <summary>
/// The ONE projection of 360 feedback, used by both <c>GET /reviews/{id}</c> and
/// <c>GET /feedback/360/{reviewId}</c> so the two can never disagree about who sees what.
/// <list type="bullet">
/// <item>Anonymous entries are shown only to the rating-approver tier (<c>performance.approve</c>); the
/// subject, their manager and everyone else see named entries only.</item>
/// <item>An anonymous entry never carries its author: name "Anonymous", employee id 0, and no timestamps
/// (submission time correlates with who was asked when).</item>
/// <item>If ANY role among the anonymous entries has fewer than <see cref="MinimumAnonymousRoleGroup"/>
/// entries, EVERY anonymous entry's role is shown as <see cref="SuppressedRole"/>: hiding only the small
/// group would still let a reader subtract the visible groups from the total.</item>
/// <item>Anonymous entries are ordered by their random id, not by time, so position does not reveal order of
/// submission.</item>
/// </list>
/// </summary>
public static class Feedback360View
{
    public const int MinimumAnonymousRoleGroup = 3;
    public const string SuppressedRole = "Other";

    public sealed record Entry(
        Guid Id, Guid ReviewId, int ReviewerEmployeeId, string ReviewerName, string ReviewerRole,
        bool IsAnonymous, decimal Score, string Strengths, string Improvements, string Comments,
        DateTime? SubmittedAt, DateTime? CreatedAtUtc);

    public static List<Entry> Project(IEnumerable<Feedback360> items, bool canSeeAnonymous)
    {
        var all = items.ToList();
        var anonymous = canSeeAnonymous ? all.Where(f => f.IsAnonymous).ToList() : new List<Feedback360>();
        var hideRoles = anonymous
            .GroupBy(f => f.ReviewerRole ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Any(g => g.Count() < MinimumAnonymousRoleGroup);

        var named = all.Where(f => !f.IsAnonymous)
            .OrderBy(f => f.SubmittedAt ?? f.CreatedAtUtc)
            .Select(f => new Entry(f.Id, f.ReviewId, f.ReviewerEmployeeId, f.ReviewerName, f.ReviewerRole,
                false, f.Score, f.Strengths, f.Improvements, f.Comments, f.SubmittedAt, f.CreatedAtUtc));
        var masked = anonymous
            .OrderBy(f => f.Id)
            .Select(f => new Entry(f.Id, f.ReviewId, 0, "Anonymous", hideRoles ? SuppressedRole : f.ReviewerRole,
                true, f.Score, f.Strengths, f.Improvements, f.Comments, null, null));
        return named.Concat(masked).ToList();
    }
}
