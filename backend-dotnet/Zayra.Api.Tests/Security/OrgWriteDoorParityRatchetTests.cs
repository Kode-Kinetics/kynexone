using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Zayra.Api.Application.Organization;
using Zayra.Api.Controllers;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// RATCHET (door parity). Organization master data — companies, branches, departments, designations,
/// grades, cost centres — has been reachable through more than one controller, and a second door
/// once applied a weaker scope rule than the first. Every write action that touches it must go
/// through the shared rules (<c>OrgWriteRules</c>, <c>CompanyEndpoints</c>, <c>CostCenterEndpoints</c>)
/// or the group-grant check itself, or be listed below with the reason it is different. A new
/// endpoint for any of these entities therefore cannot appear without someone deciding its rule.
/// </summary>
public sealed class OrgWriteDoorParityRatchetTests
{
    /// <summary>Controllers whose every write action is organization master data.</summary>
    private static readonly HashSet<Type> MasterDataControllers = new()
    {
        typeof(CompaniesController), typeof(BranchesController), typeof(DepartmentsController),
        typeof(DesignationsController), typeof(GradesController), typeof(CostCentersController),
        typeof(OrganizationController),
    };

    /// <summary>Request bodies that write master data, wherever they are accepted.</summary>
    private static readonly HashSet<string> MasterDataBodies = new(StringComparer.Ordinal)
    {
        nameof(CompanyRequest), nameof(BranchRequest), nameof(DepartmentRequest), nameof(DesignationRequest),
        nameof(GradeRequest), nameof(CostCenterRequest),
        "CompanyImportRequest", "BranchImportRequest", "DeptImportRequest", "DesigImportRequest",
        "GradeImportRequest", "CostCenterImportRequest",
    };

    /// <summary>Controller.Action → why its body does not carry the rule itself.</summary>
    private static readonly Dictionary<string, string> Exceptions = new(StringComparer.Ordinal)
    {
        ["CompaniesController.SetStatus"] = "Disabled: always answers 409 and writes nothing.",
        ["BranchesController.Import"] = "Row-level rule in RunImportAsync (asserted below).",
        ["BranchesController.ImportPreview"] = "Row-level rule in RunImportAsync (asserted below).",
        ["CostCentersController.Import"] = "Row-level rule in RunImportAsync (asserted below).",
        ["CostCentersController.ImportPreview"] = "Row-level rule in RunImportAsync (asserted below).",
        ["PlatformController.CreatePlatformCompany"] = "Platform-operator route (RequirePlatformRole); acts on a named tenant by design, not a tenant user's company scope.",
    };

    /// <summary>Private helpers that carry the rule for the actions listed as delegating to them.</summary>
    private static readonly (string File, string Method)[] RuleCarryingHelpers =
    {
        ("Controllers/BranchesController.cs", "RunImportAsync"),
        ("Controllers/CostCentersController.cs", "RunImportAsync"),
    };

    private static readonly Regex Rule = new(
        @"OrgWriteRules\.|CompanyEndpoints\.|CostCenterEndpoints\.|HasGroupEntityScope\(\)|MayWriteCompany\(",
        RegexOptions.Compiled);

    [Fact]
    public void EveryMasterDataWriteAction_AppliesTheSharedScopeRule()
    {
        var root = SourceScan.ResolveApiRoot();
        var actions = WriteActions().ToList();
        actions.Should().NotBeEmpty();

        var missing = new List<string>();
        foreach (var (controller, method) in actions)
        {
            var key = $"{controller.Name}.{method.Name}";
            if (Exceptions.ContainsKey(key)) continue;
            var body = Body(root, $"Controllers/{controller.Name}.cs", method.Name);
            if (body is null || !Rule.IsMatch(body)) missing.Add(key);
        }
        missing.Should().BeEmpty("a write to organization master data must apply OrgWriteRules / CompanyEndpoints / CostCenterEndpoints or the group-grant check, or be listed in Exceptions with its reason");

        var present = actions.Select(a => $"{a.Controller.Name}.{a.Method.Name}").ToHashSet(StringComparer.Ordinal);
        Exceptions.Keys.Should().OnlyContain(k => present.Contains(k), "an exception for an action that no longer exists must be removed");

        foreach (var (file, helper) in RuleCarryingHelpers)
            (Body(root, file, helper) ?? string.Empty).Should().MatchRegex(Rule.ToString(), $"{file} {helper} carries the row-level rule for its import actions");
    }

    private static IEnumerable<(Type Controller, MethodInfo Method)> WriteActions()
    {
        var controllers = typeof(CompaniesController).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract);
        foreach (var controller in controllers)
            foreach (var method in controller.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                var verbs = method.GetCustomAttributes<HttpMethodAttribute>(inherit: true)
                    .SelectMany(a => a.HttpMethods).ToList();
                if (!verbs.Any(v => v is "POST" or "PUT" or "PATCH" or "DELETE")) continue;
                var touchesMasterData = MasterDataControllers.Contains(controller)
                    || method.GetParameters().Any(p => MasterDataBodies.Contains(p.ParameterType.Name));
                if (touchesMasterData) yield return (controller, method);
            }
    }

    /// <summary>The comment-stripped text of a member, from its declaration to the next member.</summary>
    private static string? Body(string root, string relativeFile, string method)
    {
        var path = Path.Combine(root, relativeFile);
        if (!File.Exists(path)) return null;
        var code = SourceScan.StripCSharpComments(File.ReadAllText(path));
        var decl = Regex.Match(code, $@"\n    (public|private|internal)[^\n(]*\b{Regex.Escape(method)}\s*\(");
        if (!decl.Success) return null;
        var start = decl.Index + 1;
        var next = Regex.Match(code[(start + decl.Length - 1)..], @"\n    (\[|public |private |internal |protected )");
        return next.Success ? code.Substring(start, decl.Length - 1 + next.Index) : code[start..];
    }
}
