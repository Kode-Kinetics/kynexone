using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Infrastructure.Scope;

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

    /// <summary>The 422 problem for a refused override, or null when the request may proceed.</summary>
    public static ActionResult? Refusal(bool acknowledgeDuplicate, string? reason, bool holdsPermission)
    {
        var (status, code, detail) = Check(acknowledgeDuplicate, reason, holdsPermission) switch
        {
            Verdict.NotPermitted => (422, NotPermitted, "You do not have permission to create a duplicate employee."),
            Verdict.ReasonRequired => (422, ReasonRequired, "Say why this person is being created again (10 to 500 characters)."),
            _ => (0, string.Empty, string.Empty),
        };
        if (status == 0) return null;
        var problem = new ProblemDetails { Status = status, Title = code, Detail = detail };
        problem.Extensions["code"] = code;
        return new ObjectResult(problem) { StatusCode = status, ContentTypes = { "application/problem+json" } };
    }

    public static string? Normalise(string? reason)
    {
        var t = reason?.Trim();
        return string.IsNullOrEmpty(t) ? null : t;
    }

    /// <summary>The audit payload for the current request: scope is read from the request, so a caller sees (and the
    /// record keeps) only the matches in companies they can open. No request means a system caller: all visible.</summary>
    public static string AuditPayload(IReadOnlyList<DuplicateMatch> matches, IHttpContextAccessor? http, bool strong, string? reason)
    {
        var scope = http?.HttpContext?.RequestServices.GetService<IRequestEntityScopeResolver>()?.Resolve().ToEntityScopeContext();
        return AuditPayload(matches, c => scope is null || scope.CanAccessCompany(c), strong, reason);
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
