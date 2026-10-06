using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.Authorization;
using static Zayra.Api.Tests.Security.SeededRoleBundles;
using static Zayra.Api.Tests.Security.LegacyRoleGateBypassSweepTests.Reason;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// The role-gate bypass ratchet.
///
/// <para>THE MECHANISM. <see cref="PermissionAwareRolesAuthorizationHandler"/> lets a caller satisfy a legacy
/// <c>[Authorize(Roles = "...")]</c> gate by holding the permission <see cref="LegacyRolePermissionResolver"/>
/// maps the endpoint to (explicit <c>[HasPermission]</c> keys, else a key inferred from controller and verb),
/// whether or not the caller's role is in the list. That keeps custom roles working, but it means the role list
/// is NOT the gate: the permission is. Where the inferred key was one every employee or line manager holds
/// (ess.read, manager.approve, leave.approve, employees.read...), HR-only endpoints were open to them.</para>
///
/// <para>THE RATCHET. Every role-gated action is enumerated by reflection, its effective permission resolved
/// exactly as the pipeline does, and every seeded role (AuthSeeder's real bundles) that is NOT named on the gate
/// but holds the permission is a bypass. Each bypass must be on <see cref="AllowList"/> with a reason. A new
/// bypass fails the build: gate the endpoint with an explicit <c>[HasPermission]</c> the intended audience holds,
/// or add it to the allow-list with a reason a reviewer can challenge. An allow-list entry that no longer
/// matches fails too, so the list only shrinks honestly.</para>
/// </summary>
public sealed partial class LegacyRoleGateBypassSweepTests
{
    internal static readonly string[] SeededRoles =
    {
        "Admin", "HR Director", "HR Manager", "Payroll Manager", "HR Officer", "Payroll Officer", "Finance",
        "Finance Approver", "Compliance Officer", "Manager", "Supervisor", "Recruiter", "HR Assistant",
        "Auditor", "Kiosk Operator", "Employee",
    };

    internal sealed record RoleGate(string Endpoint, IReadOnlyCollection<string> NamedRoles, IReadOnlyList<string> Permissions);

    internal sealed record Bypass(string Endpoint, string Role, string Permission)
    {
        public override string ToString() => $"{Endpoint} | {Role} | via {Permission}";
    }

    /// <summary>Every controller action under a legacy role gate (action or controller level), per HTTP verb.</summary>
    internal static IEnumerable<RoleGate> RoleGatedEndpoints()
    {
        var controllers = typeof(AuthController).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract);
        foreach (var controller in controllers.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            var controllerName = controller.Name.EndsWith("Controller", StringComparison.Ordinal)
                ? controller.Name[..^"Controller".Length] : controller.Name;
            var classAuth = controller.GetCustomAttributes<AuthorizeAttribute>(true).ToList();
            var actions = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.DeclaringType != typeof(ControllerBase) && m.DeclaringType != typeof(Controller)
                            && m.DeclaringType != typeof(object) && !m.IsSpecialName
                            && m.GetCustomAttribute<NonActionAttribute>() is null)
                .OrderBy(m => m.Name, StringComparer.Ordinal);
            foreach (var method in actions)
            {
                if (method.GetCustomAttribute<AllowAnonymousAttribute>(true) is not null) continue;
                var auth = classAuth.Concat(method.GetCustomAttributes<AuthorizeAttribute>(true)).ToList();
                var roleLists = auth.Where(a => !string.IsNullOrWhiteSpace(a.Roles))
                    .Select(a => a.Roles!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase))
                    .ToList();
                if (roleLists.Count == 0) continue;
                // Every role requirement must be met, so a role is only "named" when every list names it.
                var named = roleLists.Skip(1).Aggregate(new HashSet<string>(roleLists[0], StringComparer.OrdinalIgnoreCase),
                    (acc, next) => { acc.IntersectWith(next); return acc; });

                // Same precedence as LegacyRolePermissionResolver.ResolveAny: explicit [HasPermission] keys
                // (ANY-of) win; otherwise the single key inferred from controller, action and verb.
                var explicitPolicy = auth.Select(a => a.Policy)
                    .FirstOrDefault(p => p?.StartsWith(HasPermissionAttribute.PolicyPrefix, StringComparison.Ordinal) == true);
                var verbAttributes = method.GetCustomAttributes<HttpMethodAttribute>(true).ToList();
                var verbs = verbAttributes.Count == 0
                    ? new[] { "ANY" }
                    : verbAttributes.SelectMany(a => a.HttpMethods).Distinct().ToArray();
                foreach (var verb in verbs)
                {
                    IReadOnlyList<string>? permissions = explicitPolicy is not null
                        ? explicitPolicy[HasPermissionAttribute.PolicyPrefix.Length..]
                            .Split(HasPermissionAttribute.Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        : LegacyRolePermissionResolver.Resolve(controllerName, method.Name,
                            verb == "ANY" ? Array.Empty<string>() : new[] { verb }) is { } inferred ? new[] { inferred } : null;
                    if (permissions is null || permissions.Count == 0) continue;
                    yield return new RoleGate($"{verb} {controllerName}.{method.Name}", named, permissions);
                }
            }
        }
    }

    internal static async Task<List<Bypass>> CurrentBypassesAsync()
    {
        var (db, tenantId) = await NewTenantAsync("role-gate-sweep");
        var bundles = new Dictionary<string, HashSet<string>>();
        foreach (var role in SeededRoles)
            bundles[role] = (await PermissionsOfAsync(db, tenantId, role)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return RoleGatedEndpoints()
            .SelectMany(gate => SeededRoles
                .Where(role => !gate.NamedRoles.Contains(role))
                .Select(role => (role, key: gate.Permissions.FirstOrDefault(bundles[role].Contains)))
                .Where(x => x.key is not null)
                .Select(x => new Bypass(gate.Endpoint, x.role, x.key!)))
            .ToList();
    }

    [Fact]
    public void TheSweepSeesTheRoleGatedSurface()
    {
        // Guards the enumerator itself: if reflection stopped finding role gates the ratchet would pass vacuously.
        var gates = RoleGatedEndpoints().ToList();
        gates.Should().HaveCountGreaterThan(300);
        gates.Should().Contain(g => g.Endpoint == "GET EmployeeSelfService.ProfileChangeRequests");
        gates.Should().Contain(g => g.Endpoint == "POST Encashment.PayrollApprove");
    }

    [Fact]
    public async Task EveryRoleGateBypass_IsReviewed_AndNoNewOneAppears()
    {
        var current = await CurrentBypassesAsync();
        var allowed = AllowList
            .SelectMany(e => e.Roles.Split(',', StringSplitOptions.TrimEntries).Select(r => (e.Endpoint, Role: r)))
            .ToHashSet();
        var seen = current.Select(b => (b.Endpoint, b.Role)).ToHashSet();

        var unreviewed = current.Where(b => !allowed.Contains((b.Endpoint, b.Role))).Select(b => b.ToString()).ToList();
        var stale = allowed.Where(a => !seen.Contains(a)).Select(a => $"{a.Endpoint} | {a.Role}").ToList();

        unreviewed.Should().BeEmpty(
            "a seeded role that is NOT named on a legacy role gate reaches the endpoint through the permission the " +
            "gate resolves to. Gate it with an explicit [HasPermission] that only the intended audience holds, or add " +
            "it to LegacyRoleGateBypassSweepTests.AllowList with a reason. Unreviewed:\n" + string.Join('\n', unreviewed));
        stale.Should().BeEmpty("these allow-list entries no longer match a bypass; delete them:\n" + string.Join('\n', stale));
    }

    [Fact]
    public void EveryAllowListEntryCarriesAnExplainedReason()
    {
        AllowList.Should().OnlyContain(e => Reasons.ContainsKey(e.Why) && !string.IsNullOrWhiteSpace(Reasons[e.Why]));
        AllowList.Select(e => (e.Endpoint, e.Why)).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void SelfServiceAndFrontLineRoles_AreOnlyAllowListedForReadsOrScopedWork()
    {
        // A plain employee, kiosk operator or line supervisor bypassing a role gate is only ever acceptable for
        // aggregate or data-scoped reads, scoped attendance processing, or an action whose body refuses them anyway.
        var frontLine = new[] { "Employee", "Kiosk Operator", "Supervisor", "Manager" };
        var acceptable = new[] { ScopedRead, AggregateOnly, AttendanceProcessScoped, OpenUnscopedNames, OpenDeviceConfigRead, WorkflowStepAuthority, BodyRechecksRole };
        var offending = AllowList
            .Where(e => e.Roles.Split(',', StringSplitOptions.TrimEntries).Intersect(frontLine).Any() && !acceptable.Contains(e.Why))
            .Select(e => $"{e.Endpoint} [{e.Roles}] {e.Why}")
            .ToList();
        offending.Should().BeEmpty();
    }
}
