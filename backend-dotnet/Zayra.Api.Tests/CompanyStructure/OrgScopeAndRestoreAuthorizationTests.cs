using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;
using Zayra.Api.Tests.Onboarding;

namespace Zayra.Api.Tests.CompanyStructure;

/// <summary>
/// Where company scope is enforced for the organisation rows, and who can reach Remove / Restore.
/// </summary>
public sealed class OrgScopeAndRestoreAuthorizationTests : IClassFixture<HireIntegrityHttpFixture>
{
    private readonly HireIntegrityHttpFixture _fx;
    public OrgScopeAndRestoreAuthorizationTests(HireIntegrityHttpFixture fixture) => _fx = fixture;

    private static ZayraDbContext Db() => new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static string Filter<T>(ZayraDbContext db) where T : class =>
        db.Model.FindEntityType(typeof(T))!.GetQueryFilter()?.ToString() ?? string.Empty;

    [Fact]
    public void TheCompanyScopeQueryFilter_AppliesToDepartmentDesignationGradeAndCostCentre_NotToBranchOrLocation()
    {
        using var db = Db();
        Filter<Department>(db).Should().Contain("CompanyId");
        Filter<Designation>(db).Should().Contain("CompanyId");
        Filter<Grade>(db).Should().Contain("CompanyId");
        Filter<CostCenter>(db).Should().Contain("CompanyId");
        // Branch and Location only carry the tenant + soft-delete filter: their company scope is enforced by the controllers.
        Filter<Branch>(db).Should().NotContain("CompanyId");
        Filter<Location>(db).Should().NotContain("CompanyId");
    }

    public static IEnumerable<object[]> Routes() => new[]
    {
        "/api/departments", "/api/designations", "/api/grades", "/api/branches", "/api/cost-centers", "/api/locations", "/api/admin/locations",
    }.Select(r => new object[] { r });

    // The permission is the gate: [HasPermission] is ANDed with the class-level role gate, and a roles requirement is satisfied by
    // holding the permission it resolves to, so a role named on the class (Auditor, HR Officer) that is GRANTED organization.delete
    // reaches Delete and Restore alike; without the grant it reaches neither.
    [Theory]
    [MemberData(nameof(Routes))]
    public async Task RemoveAndRestore_FollowTheOrganizationDeletePermission_ForClassListedRoles(string route)
    {
        foreach (var role in new[] { "Auditor", "HR Officer" })
        {
            var granted = await _fx.NewUserInRoleAsync(role, $"rg{role[..2]}", "organization.delete", "organization.costcenter.manage", "organization.read");
            var plain = await _fx.NewUserInRoleAsync(role, $"rp{role[..2]}", "organization.read");
            var id = Guid.NewGuid();
            foreach (var (method, path) in new[] { (HttpMethod.Delete, $"{route}/{id}?reason=check"), (HttpMethod.Post, $"{route}/{id}/restore") })
            {
                using var withGrant = await _fx.SendAsync(method, path, null, granted);
                withGrant.StatusCode.Should().NotBe(HttpStatusCode.Forbidden, $"{role} + organization.delete on {method} {path}");
                using var without = await _fx.SendAsync(method, path, null, plain);
                without.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{role} without the grant on {method} {path}");
            }
        }
    }
}
