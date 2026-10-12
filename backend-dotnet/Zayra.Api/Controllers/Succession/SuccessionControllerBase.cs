using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Succession;

namespace Zayra.Api.Controllers.Succession;

/// <summary>
/// Shared plumbing for the succession controllers: the actor — tenant, user, their own employee record, the permissions
/// and roles in their token, and the employee scope IDataScopeService resolves (HR to their companies, a manager to their
/// reporting tree). Every refusal is a <see cref="SuccessionException"/> with a stable code.
/// </summary>
[SuccessionErrors]
public abstract class SuccessionControllerBase : ControllerBase
{
    protected async Task<SuccessionActor> ActorAsync(CancellationToken ct)
    {
        var tenantId = this.GetTenantId() ?? throw new SuccessionException(401, "unauthenticated", "Sign in again.");
        var services = HttpContext.RequestServices;
        var scope = await services.GetRequiredService<IDataScopeService>().ResolveAsync(User, tenantId, ct);
        var employeeId = await CallerEmployeeResolver.ResolveAsync(services.GetRequiredService<ZayraDbContext>(), User, tenantId, ct);
        var permissions = User.Claims.Where(c => c.Type == "permission").Select(c => c.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var roles = User.Claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value).ToList();
        return new SuccessionActor(tenantId, this.GetUserId(), employeeId, permissions, roles, scope,
            HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString());
    }
}
