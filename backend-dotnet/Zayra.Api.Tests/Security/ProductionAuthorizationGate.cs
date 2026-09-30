using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Zayra.Api.Infrastructure.Authorization;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Puts a direct controller call behind the SAME authorization the application runs: ASP.NET Core's own
/// <see cref="AuthorizationMiddleware"/>, composed with the four registrations Program.cs makes
/// (<see cref="PermissionPolicyProvider"/>, <see cref="PermissionAuthorizationHandler"/>,
/// <see cref="PermissionAwareRolesAuthorizationHandler"/>, <see cref="PermissionAwareAuthorizationResultHandler"/>)
/// and the default-deny fallback policy, over an endpoint whose metadata is the action's real attributes.
///
/// <para>Why not just check <see cref="LegacyRolePermissionResolver"/>'s answer: an <c>[Authorize(Roles=…)]</c>
/// gate is satisfied by the role OR by the resolved permission (the roles handler above), and then refused
/// without the permission (the result handler). Replaying that by hand would test the replay. This runs it.</para>
///
/// <para>Only when the middleware lets the request through is the action invoked, and its result is the
/// status the caller would receive.</para>
/// </summary>
internal static class ProductionAuthorizationGate
{
    private const string Scheme = "ProductionAuthorizationGate";
    private static readonly IServiceProvider Services = BuildServices();

    public static async Task<int> StatusAsync<TController>(
        ClaimsPrincipal caller, string action, Func<Task<IActionResult>> invoke) where TController : ControllerBase
    {
        using var scope = Services.CreateScope();
        var http = new DefaultHttpContext { User = caller, RequestServices = scope.ServiceProvider };
        http.SetEndpoint(EndpointFor(typeof(TController), action));

        var reached = false;
        var middleware = new AuthorizationMiddleware(
            _ => { reached = true; return Task.CompletedTask; },
            scope.ServiceProvider.GetRequiredService<IAuthorizationPolicyProvider>(),
            scope.ServiceProvider);
        await middleware.Invoke(http);

        return reached ? SeededRoleBundles.StatusOf(await invoke()) : http.Response.StatusCode;
    }

    /// <summary>The endpoint MVC builds for the action: controller then action attributes, the verb, the descriptor.</summary>
    private static Endpoint EndpointFor(Type controller, string action)
    {
        var method = controller.GetMethod(action, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            ?? throw new InvalidOperationException($"{controller.Name} has no public action {action}.");
        var verbs = method.GetCustomAttributes<HttpMethodAttribute>(inherit: true)
            .SelectMany(a => a.HttpMethods).Distinct().ToArray();
        if (verbs.Length == 0)
            throw new InvalidOperationException($"{controller.Name}.{action} is not an HTTP action.");

        var metadata = new List<object>();
        metadata.AddRange(controller.GetCustomAttributes(inherit: true));
        metadata.AddRange(method.GetCustomAttributes(inherit: true));
        metadata.Add(new HttpMethodMetadata(verbs));
        metadata.Add(new ControllerActionDescriptor
        {
            ControllerName = controller.Name[..^"Controller".Length],
            ActionName = method.Name,
            MethodInfo = method,
            ControllerTypeInfo = controller.GetTypeInfo(),
        });
        return new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(metadata), $"{controller.Name}.{action}");
    }

    private static IServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // Stands in for the JWT scheme only to turn a Forbid/Challenge into 403/401; the caller is already
        // authenticated, exactly as UseAuthentication() leaves it before UseAuthorization() runs.
        services.AddAuthentication(Scheme).AddScheme<AuthenticationSchemeOptions, StatusOnlyHandler>(Scheme, _ => { });
        services.AddAuthorization(options =>
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        // Program.cs, verbatim.
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddSingleton<IAuthorizationHandler, PermissionAwareRolesAuthorizationHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, PermissionAwareAuthorizationResultHandler>();
        return services.BuildServiceProvider();
    }

    private sealed class StatusOnlyHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public StatusOnlyHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.NoResult());
    }
}
