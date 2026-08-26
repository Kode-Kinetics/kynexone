using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.Authorization;

namespace Zayra.Api.Tests.Security;

public sealed class LegacyRolePermissionAuthorizationTests
{
    [Fact]
    public void EveryLegacyRoleEndpoint_HasAnAuthoritativePermissionMapping()
    {
        var missing = new List<string>();
        var controllerTypes = typeof(AuthController).Assembly.GetTypes()
            .Where(x => typeof(ControllerBase).IsAssignableFrom(x) && !x.IsAbstract);
        foreach (var controller in controllerTypes)
        {
            var classHasRoles = controller.GetCustomAttributes<AuthorizeAttribute>(true)
                .Any(x => !string.IsNullOrWhiteSpace(x.Roles));
            foreach (var method in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var methodHasRoles = method.GetCustomAttributes<AuthorizeAttribute>(true)
                    .Any(x => !string.IsNullOrWhiteSpace(x.Roles));
                if (!classHasRoles && !methodHasRoles) continue;
                if (method.GetCustomAttributes<HasPermissionAttribute>(true).Any()
                    || controller.GetCustomAttributes<HasPermissionAttribute>(true).Any()) continue;
                var verbs = method.GetCustomAttributes<HttpMethodAttribute>(true)
                    .SelectMany(x => x.HttpMethods).Distinct().ToList();
                if (LegacyRolePermissionResolver.Resolve(
                        controller.Name.Replace("Controller", string.Empty), method.Name, verbs) is null)
                    missing.Add($"{controller.FullName}.{method.Name}");
            }
        }

        Assert.True(missing.Count == 0, "Unmapped legacy role endpoints:\n" + string.Join('\n', missing));
    }

    [Fact]
    public async Task CustomRole_WithMappedPermission_SatisfiesLegacyRoleRequirement()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim("permission", "attendance.read")
        }, "test"));
        var descriptor = new ControllerActionDescriptor { ControllerName = "Attendance", ActionName = "Dashboard" };
        var endpoint = new Endpoint(_ => Task.CompletedTask,
            new EndpointMetadataCollection(new AuthorizeAttribute { Roles = "Admin" }, descriptor, new HttpMethodMetadata(new[] { "GET" })),
            "attendance-dashboard");
        var http = new DefaultHttpContext();
        http.SetEndpoint(endpoint);
        var requirement = new RolesAuthorizationRequirement(new[] { "Admin" });
        var context = new AuthorizationHandlerContext(new[] { requirement }, principal, http);

        await new PermissionAwareRolesAuthorizationHandler().HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }
}
