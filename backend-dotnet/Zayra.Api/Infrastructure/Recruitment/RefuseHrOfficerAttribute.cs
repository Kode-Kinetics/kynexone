using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Zayra.Api.Infrastructure.Recruitment;

/// <summary>
/// Keeps the built-in HR Officer out of a route that its recruitment.write key would otherwise open through the permission-to-role bridge
/// (offer letter templates, workforce plans; owner ruling 2026-11-17). A login that is an HR Officer AND holds a senior recruitment role
/// (Admin, HR Manager, HR Director, Recruiter) is not refused: the rule is about the HR Officer role alone, and every other caller is unchanged.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RefuseHrOfficerAttribute : Attribute, IAuthorizationFilter
{
    private static readonly string[] Senior = ["Admin", "HR Manager", "HR Director", "Recruiter"];

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var user = context.HttpContext.User;
        if (user.IsInRole("HR Officer") && !Senior.Any(user.IsInRole)) context.Result = new ForbidResult();
    }
}
