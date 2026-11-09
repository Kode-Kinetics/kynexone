using System.Reflection;
using FluentAssertions;
using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Tests.Security;

namespace Zayra.Api.Tests;

/// <summary>
/// A designation gives permissions only: it never touches pay or benefits, and a grade never changes anyone's roles. Source-level guard, because the
/// way a grade could leak in is a caller deciding to reconcile access because the GRADE changed.
/// </summary>
public sealed class DesignationAccessScopeTests
{
    [Fact]
    public void A_grade_never_changes_roles_nothing_reconciles_access_on_a_grade_change()
    {
        var api = SourceScan.ResolveApiRoot();
        var files = Directory.EnumerateFiles(api, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();
        var offenders = new List<string>();
        foreach (var file in files)
        {
            var lines = SourceScan.StripCSharpComments(File.ReadAllText(file)).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("DesignationAccessService")) continue;
                var window = string.Join('\n', lines.Skip(Math.Max(0, i - 2)).Take(3));
                if (window.Contains("GradeId", StringComparison.Ordinal))
                    offenders.Add($"{Path.GetRelativePath(api, file)}:{i + 1}");
            }
        }
        offenders.Should().BeEmpty("a grade change must not apply designation access");

        var service = File.ReadAllText(Path.Combine(api, "Infrastructure", "Auth", "DesignationAccessService.cs"));
        SourceScan.StripCSharpComments(service).Should().NotContain("GradeId", "the access service reads the designation's mapping only");
    }

    [Fact]
    public void A_designation_gives_permissions_only_the_access_service_never_touches_pay_or_benefits()
    {
        var service = SourceScan.StripCSharpComments(File.ReadAllText(Path.Combine(SourceScan.ResolveApiRoot(), "Infrastructure", "Auth", "DesignationAccessService.cs")));
        foreach (var forbidden in new[] { "Salary", "Allowance", "Benefit", "Payroll", "PayComponent", "Grade" })
            service.Should().NotContain(forbidden, $"designation access must not reach {forbidden}");
    }

    [Fact]
    public void The_access_options_endpoint_answers_only_to_someone_holding_security_manage()
    {
        var method = typeof(DesignationsController).GetMethod(nameof(DesignationsController.AccessOptions))!;
        method.GetCustomAttributes<HasPermissionAttribute>().Should().ContainSingle(a => a.Policy!.EndsWith("security.manage"),
            "the form's option list is only for people who may set designation access, so it needs the same permission as the save");
    }
}
