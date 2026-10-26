using System.Text.Json;

namespace Zayra.Api.Infrastructure.Employees.Onboarding;

/// <summary>
/// "Create anyway" on a detected duplicate is a deliberate override, not a click-through. It needs the
/// <c>employees.duplicate_override</c> permission (Admin and HR Manager by default) and a written reason of 10-500
/// characters. The audit record lists only the matches the actor may open, plus a count of the rest, so an override
/// never becomes a way to learn who exists in a company the actor cannot see.
/// </summary>
public static class DuplicateOverridePolicy
{
    public const string Permission = "employees.duplicate_override";
    public const string ReasonRequired = "override_reason_required";
    public const string NotPermitted = "override_not_permitted";
    public const int MinReason = 10;
    public const int MaxReason = 500;

    public enum Verdict { Allowed, NotPermitted, ReasonRequired }

    /// <summary>No acknowledgement means nothing to check. Permission is judged before the reason, so a caller
    /// without the permission learns nothing about reason rules.</summary>
    public static Verdict Check(bool acknowledgeDuplicate, string? reason, bool holdsPermission)
    {
        if (!acknowledgeDuplicate) return Verdict.Allowed;
        if (!holdsPermission) return Verdict.NotPermitted;
        var trimmed = reason?.Trim() ?? string.Empty;
        return trimmed.Length is < MinReason or > MaxReason ? Verdict.ReasonRequired : Verdict.Allowed;
    }

    public static string? Normalise(string? reason)
    {
        var t = reason?.Trim();
        return string.IsNullOrEmpty(t) ? null : t;
    }

    /// <summary>The audit payload: ids the actor can access, a count for the rest, the match type, the signals and
    /// the reason. Never an identity value.</summary>
    public static string AuditPayload(
        IReadOnlyList<DuplicateMatch> matches, Func<Guid?, bool> canAccessCompany, bool strong, string? reason)
    {
        var visible = matches.Where(m => canAccessCompany(m.CompanyId)).ToList();
        return JsonSerializer.Serialize(new
        {
            matchedEmployeeIds = visible.Select(m => m.EmployeeId),
            outOfScopeMatchCount = matches.Count - visible.Count,
            matchType = strong ? "strong" : "probable",
            signals = matches.SelectMany(m => m.Signals).Distinct(),
            reason = Normalise(reason),
        });
    }
}
