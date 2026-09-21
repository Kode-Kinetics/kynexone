using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Routing;
using Zayra.Api.Infrastructure.Authorization;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// <c>Controllers/Performance/AnalyticsController</c> was the only controller in
/// <c>Controllers/Performance/</c> gated by nothing but bare <c>[Authorize]</c>. Every sibling
/// (Cycles, Reviews, Goals, Calibration, Recommendations, PIP, Probation, Competencies,
/// ScorecardTemplates) carries a role or permission requirement on its actions.
///
/// The payload is not aggregate-only. <c>GET cycle/{id}</c> returns <c>topPerformers</c> and
/// <c>lowPerformers</c> — employee id, name, department, designation, final score and rating —
/// plus per-manager <c>PossibleLeniency</c>/<c>PossibleSeverity</c> flags; <c>GET dashboard</c>
/// returns the last 20 <c>PerformanceAuditLog</c> rows verbatim. Any authenticated employee could
/// read all of it, for the whole tenant.
///
/// WATCHED FAIL, THEN PASS (2026-09-21):
///   before the gate -> "Performance AnalyticsController must require the 'performance.read'
///                       permission ... Expected collection not to be empty."  Failed: 1
///   after the gate  -> Passed: 1
/// </summary>
public sealed class PerformanceAnalyticsPermissionGateTests
{
    private static readonly Type Controller =
        typeof(Zayra.Api.Controllers.Performance.AnalyticsController);

    [Fact]
    public void PerformanceAnalyticsController_RequiresPerformanceReadPermission()
    {
        var permissions = Controller
            .GetCustomAttributes<HasPermissionAttribute>(inherit: true)
            .SelectMany(a => a.Permissions)
            .ToList();

        permissions.Should().NotBeEmpty(
            "Performance AnalyticsController must require the 'performance.read' permission — it " +
            "returns named low performers, per-manager bias flags and the raw performance audit " +
            "feed, and bare [Authorize] lets any authenticated employee read the whole tenant's.");
        permissions.Should().Contain("performance.read");
    }

    [Fact]
    public void EveryPerformanceAnalyticsAction_IsCoveredByTheControllerGate()
    {
        // The gate is class-level and HasPermissionAttribute is Inherited, so no action may opt out
        // by carrying its own weaker [Authorize]. Fails if someone adds an ungated action later.
        var actions = Controller
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>(inherit: true).Any())
            .ToList();

        actions.Should().NotBeEmpty("the controller must still expose its analytics endpoints");

        foreach (var action in actions)
        {
            action.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Should().BeEmpty(
                $"{action.Name} must not opt out of the controller's permission gate");
        }
    }
}
