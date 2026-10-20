using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Zayra.Api.Infrastructure.Authorization;

namespace Zayra.Api.Infrastructure.Recruitment;

// These financial-workflow audiences are not widened by the global legacy permission-to-role bridge.
// Use the same checks for every retained adapter and the workspace capability DTO.
public static class OfferAccess
{
    private static bool Manager(ClaimsPrincipal user) => user.IsInRole("Admin") || user.IsInRole("HR Manager");
    public static bool CanRead(ClaimsPrincipal user) => user.HasPermission("recruitment.read")
        && (Manager(user) || user.IsInRole("HR Officer") || user.IsInRole("Recruiter"));
    public static bool CanAuthor(ClaimsPrincipal user) => user.HasPermission("recruitment.write")
        && (Manager(user) || user.IsInRole("Recruiter"));
    public static bool CanManage(ClaimsPrincipal user) => user.HasPermission("recruitment.write") && Manager(user);
    public static bool CanApprove(ClaimsPrincipal user) => user.HasPermission("recruitment.approve") && Manager(user);
}

public enum OfferAudienceRequirement { Author, Manager, Approver }

[AttributeUsage(AttributeTargets.Method)]
public sealed class OfferAudienceAttribute(OfferAudienceRequirement requirement) : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var user = context.HttpContext.User;
        var allowed = requirement switch
        {
            OfferAudienceRequirement.Author => OfferAccess.CanAuthor(user),
            OfferAudienceRequirement.Manager => OfferAccess.CanManage(user),
            OfferAudienceRequirement.Approver => OfferAccess.CanApprove(user),
            _ => false
        };
        if (!allowed) context.Result = new ForbidResult();
    }
}
