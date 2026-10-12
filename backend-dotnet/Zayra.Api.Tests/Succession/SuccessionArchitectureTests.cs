using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Zayra.Api.Controllers.Ess;
using Zayra.Api.Controllers.Succession;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Modules;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Infrastructure.Succession;
using Zayra.Api.Models;
using Zayra.Api.Tests.SelfieAttendance;

namespace Zayra.Api.Tests.Succession;

/// <summary>
/// SUC-08 (no ranking by protected characteristics, no AI) and the access contract of SUC-07 at the attribute level:
/// the projection carries no protected field and the module's source never reads one; the 12 keys are granted by
/// explicit lists only (no prefix bundle can ever reach them), Admin is withheld three, and every endpoint is gated by
/// [HasPermission] alone — so the seeded Employee, Manager and Admin bundles reach exactly what the design says.
/// </summary>
public sealed class SuccessionArchitectureTests
{
    private static readonly string[] Forbidden = ["Gender", "DateOfBirth", "Nationality", "MaritalStatus", "SaudiOrNonSaudi", "Religion", "Age"];

    private static string ApiRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Zayra.Api"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "Zayra.Api");
    }

    private static IEnumerable<string> ModuleSources()
    {
        var root = ApiRoot();
        return Directory.GetFiles(Path.Combine(root, "Infrastructure", "Succession"), "*.cs")
            .Concat(Directory.GetFiles(Path.Combine(root, "Controllers", "Succession"), "*.cs"))
            .Append(Path.Combine(root, "Controllers", "Ess", "EssDevelopmentPlanController.cs"))
            .Append(Path.Combine(root, "Infrastructure", "Performance", "PerformanceBands.cs"));
    }

    [Fact]
    public void SUC08_TheCandidateProjection_AndTheSnapshot_CarryNoProtectedCharacteristic()
    {
        foreach (var type in new[] { typeof(SuccessionCandidateFacts), typeof(SnapshotCandidate), typeof(SnapshotIncumbent), typeof(PersonRef), typeof(NineBoxPlacement) })
            type.GetProperties().Select(p => p.Name).Should().NotIntersectWith(Forbidden, $"{type.Name} is read or shown when people are compared");
    }

    [Fact]
    public void SUC08_TheModuleNeverReadsAProtectedField_NorCallsAnyModel()
    {
        foreach (var file in ModuleSources())
        {
            var source = File.ReadAllText(file);
            foreach (var field in Forbidden.Where(f => f != "Age"))
                Regex.IsMatch(source, $@"\.{field}\b").Should().BeFalse($"{Path.GetFileName(file)} must not read Employee.{field}");
            Regex.IsMatch(source, @"\bIAi[A-Z]\w*|\bAiGateway\b|OpenAI|Anthropic|HttpClient\b").Should().BeFalse(
                $"{Path.GetFileName(file)} must make no AI or outbound call: ranking is manual, with a rationale");
            Regex.IsMatch(source, @"OrderBy(Descending)?\(\s*\w+\s*=>\s*\w+\.(PerformanceScore|NineBoxCell|ProposedPotential|CalibratedPotential)")
                .Should().BeFalse($"{Path.GetFileName(file)} must not rank people by a score");
        }
    }

    [Fact]
    public void SUC07_SuccessionKeys_AreGrantedOnlyByExplicitLists()
    {
        AuthSeeder.SuccessionPermissionKeys.Should().BeEquivalentTo(SuccessionPermissions.All);
        SuccessionPermissions.All.Should().HaveCount(12).And.OnlyContain(k => k.StartsWith("succession.", StringComparison.Ordinal));
        var source = File.ReadAllText(Path.Combine(ApiRoot(), "Infrastructure", "Seed", "AuthSeeder.cs"));
        var prefixes = Regex.Matches(source, @"StartsWith\(""([^""]+)""\)").Select(m => m.Groups[1].Value).Distinct().ToList();
        prefixes.Should().NotBeEmpty("the parser must still see the role bundles' prefixes");
        foreach (var prefix in prefixes)
            SuccessionPermissions.All.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Should().BeEmpty(
                $"no role bundle may reach a succession key through the prefix '{prefix}'");
        source.Should().NotContain("\"performance.succession", "no succession key is named under performance.");
        AuthSeeder.AdminWithheldPermissions.Should().Contain(SuccessionPermissions.All.Except(
            [SuccessionPermissions.Read, SuccessionPermissions.PositionsManage, SuccessionPermissions.PoolsManage]));
    }

    [Fact]
    public async Task SUC07_TheSeededRolesHoldExactlyTheDesignedKeys()
    {
        static IEnumerable<string> Suc(string[] keys) => keys.Where(k => k.StartsWith("succession.", StringComparison.Ordinal));
        Suc(await SelfieWorld.SeededAsync("HR Director")).Should().BeEquivalentTo(SuccessionPermissions.All);
        Suc(await SelfieWorld.SeededAsync("HR Manager")).Should().BeEquivalentTo(SuccessionPermissions.All.Where(k => k != SuccessionPermissions.Calibrate));
        Suc(await SelfieWorld.SeededAsync("Manager")).Should().BeEquivalentTo(
            ["succession.nominate", "succession.devplans.read", "succession.devplans.manage", "succession.self"]);
        Suc(await SelfieWorld.SeededAsync("Employee")).Should().BeEquivalentTo(["succession.self"]);
        Suc(await SelfieWorld.SeededAsync("Admin")).Should().BeEquivalentTo(
            [SuccessionPermissions.Read, SuccessionPermissions.PositionsManage, SuccessionPermissions.PoolsManage], "F2: configuration only");
        foreach (var role in new[] { "Auditor", "HR Officer", "Payroll Manager", "Supervisor", "Recruiter" })
            Suc(await SelfieWorld.SeededAsync(role)).Should().BeEmpty($"{role} has no succession access by default");
        foreach (var key in SuccessionPermissions.All)
            (PrivilegedMfaPolicy.NonPrivilegedPermissions.Contains(key) ^ PrivilegedMfaPolicy.PrivilegedPermissions.Contains(key))
                .Should().BeTrue($"{key} is classified exactly once for two-step sign-in");
    }

    private sealed record Endpoint(string Route, string Verb, string[] Permissions);

    private static IReadOnlyList<Endpoint> Endpoints()
    {
        var list = new List<Endpoint>();
        foreach (var controller in new[] { typeof(SuccessionController), typeof(DevelopmentPlansController), typeof(EssDevelopmentPlanController) })
        {
            controller.GetCustomAttributes<AuthorizeAttribute>().OfType<AuthorizeAttribute>()
                .Where(a => a is not HasPermissionAttribute).Should().OnlyContain(a => string.IsNullOrEmpty(a.Roles), "no role-name gate");
            var prefix = controller.GetCustomAttribute<RouteAttribute>()!.Template;
            foreach (var method in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var verb = method.GetCustomAttributes().OfType<HttpMethodAttribute>().SingleOrDefault();
                if (verb is null) continue;
                method.GetCustomAttributes<AuthorizeAttribute>().Where(a => a is not HasPermissionAttribute)
                    .Should().BeEmpty($"{method.Name} is gated by [HasPermission] only");
                var gate = method.GetCustomAttribute<HasPermissionAttribute>();
                gate.Should().NotBeNull($"{controller.Name}.{method.Name} has no [HasPermission]");
                list.Add(new Endpoint($"{prefix}/{verb.Template}".TrimEnd('/'), verb.HttpMethods.Single(), gate!.Permissions.ToArray()));
            }
        }
        return list;
    }

    [Fact]
    public async Task SUC07_EveryEndpointIsPermissionGated_AndTheSeededBundlesReachOnlyTheirDesignedSurface()
    {
        var endpoints = Endpoints();
        endpoints.Should().HaveCountGreaterThan(40);
        endpoints.SelectMany(e => e.Permissions).Distinct().Should().OnlyContain(p => p.StartsWith("succession.") || p == "approvals.decide");

        IEnumerable<string> Reach(string[] keys) => endpoints.Where(e => e.Permissions.Any(keys.Contains)).Select(e => $"{e.Verb} {e.Route}");

        // Employee: their own plan, plus the relationship-guarded reads (an accountable executive; 404 for anyone else).
        Reach(await SelfieWorld.SeededAsync("Employee")).Should().BeEquivalentTo([
            "GET api/succession/positions/mine", "GET api/succession/positions/{id:guid}", "GET api/succession/positions/{id:guid}/slates",
            "GET api/succession/slates/{id:guid}", "GET api/ess/development-plan", "PUT api/ess/development-plan/actions/{id:guid}"]);

        // Manager: nominate and development plans; never candidate data, the 9-box, calibration or slate changes.
        var manager = Reach(await SelfieWorld.SeededAsync("Manager")).ToList();
        manager.Should().Contain(["POST api/succession/nominations", "GET api/succession/nominations/mine", "POST api/succession/development-plans/{id:guid}/actions"]);
        manager.Should().NotContain(["GET api/succession/nominations", "GET api/succession/nine-box", "GET api/succession/assessments",
            "POST api/succession/assessments/{id:guid}/calibrate", "PUT api/succession/slates/{id:guid}", "GET api/succession/reports/summary"]);

        // Admin (F2): configuration only — positions and pools; never a candidate, assessment, slate change, plan or report.
        var adminKeys = await SelfieWorld.SeededAsync("Admin");
        var admin = Reach(adminKeys).ToList();
        admin.Should().Contain(["GET api/succession/positions", "POST api/succession/positions", "POST api/succession/pools"]);
        admin.Should().NotContain(["GET api/succession/nominations", "GET api/succession/nine-box", "POST api/succession/assessments",
            "POST api/succession/assessments/{id:guid}/calibrate", "GET api/succession/assessments", "GET api/succession/nominations/{id:guid}/history",
            "POST api/succession/nominations", "PUT api/succession/slates/{id:guid}", "POST api/succession/positions/{id:guid}/slates",
            "GET api/succession/development-plans", "GET api/succession/reports/summary", "GET api/ess/development-plan"]);
        // The one candidate-bearing read Admin can call (slates/{id} via approvals.decide) is relationship-guarded: it
        // answers only the routed approver, and approvals.override is no way in (SuccessionAccessTests).

        // HR Manager: everything but calibration.
        var hrManager = Reach(await SelfieWorld.SeededAsync("HR Manager")).ToList();
        hrManager.Should().NotContain(["POST api/succession/assessments/{id:guid}/calibrate", "POST api/succession/nominations/{id:guid}/calibrate"]);
        hrManager.Should().Contain(["POST api/succession/slates/{id:guid}/submit", "GET api/succession/nine-box"]);
    }

    [Fact]
    public void SUC_TheModuleIsOptIn_AndItsRoutesHaveOneOwner()
    {
        OptInFeatures.IsOptIn(FeatureKeys.Succession).Should().BeTrue();
        OptInFeatures.IsOptIn(FeatureKeys.SuccessionCandidateVisibility).Should().BeTrue();
        OptInFeatures.RoutePrefixes[FeatureKeys.Succession].Should().BeEquivalentTo(["/api/succession", "/api/ess/development-plan"]);
        ModuleCatalog.All.Single(m => m.Key == ModuleKeys.Performance).RoutePrefixes.Should().Contain("/api/succession");
    }
}
