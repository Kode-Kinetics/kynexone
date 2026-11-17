using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.Authorization;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// The legacy Benefits page (/benefits) opens for employees.write only. Its plan and eligibility reads used to resolve to employees.read, so a line
/// manager, supervisor, recruiter, HR assistant or compliance officer could list every plan and tier limit through the API although the page refused
/// them. They now carry the explicit gate of the enrolment reads beside them (HR writers, payroll and finance readers): the page is never wider than
/// its API and the API is never wider than its HR, payroll and finance audience. Nothing is loosened.
/// </summary>
public sealed class LegacyBenefitsReadGateTests
{
    private static string Gate(string action)
    {
        var method = typeof(BenefitsController).GetMethod(action)!;
        return string.Join("|", method.GetCustomAttributes(true).Select(a => a switch
        {
            HasPermissionAttribute p => "perm:" + string.Join(",", p.Permissions),
            AuthorizeAttribute z => "roles:" + z.Roles,
            _ => null,
        }).Where(x => x is not null).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(nameof(BenefitsController.ListPlans))]
    [InlineData(nameof(BenefitsController.ListEligibility))]
    public void ThePlanReads_HaveTheEnrolmentReadsGate(string action) =>
        Gate(action).Should().Be(Gate(nameof(BenefitsController.ListEnrollments)));

    [Fact]
    public void TheLegacyBenefitsPage_IsGatedOnEmployeesWrite_WhichTheApiGateAdmits()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "frontend", "app"))) root = root.Parent;
        var page = File.ReadAllText(Path.Combine(root!.FullName, "frontend", "app", "(dashboard)", "benefits", "page.tsx"));
        page.Should().Contain("permissions={['employees.write']}");
        Gate(nameof(BenefitsController.ListPlans)).Should().Contain("employees.write");
    }
}
