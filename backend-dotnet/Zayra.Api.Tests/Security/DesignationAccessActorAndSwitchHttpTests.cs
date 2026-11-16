using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Designation-driven access through the real pipeline. (1) A designation change hands out a mapped role only when the PERSON MAKING THE
/// CHANGE holds everything that role carries: an HR officer who may edit employees cannot give someone a finance approval role by
/// choosing their designation. (2) Switching the feature off really stops it: access already given through a designation is taken away
/// by the next change and by a leaver's removal, and the mapping is never applied again.
/// </summary>
public sealed class DesignationAccessActorAndSwitchHttpTests(LaunchSecurityFixture f) : IClassFixture<LaunchSecurityFixture>
{
    private const string MappedRole = "Finance Approver";

    private sealed record Seeded(int EmployeeId, Guid LoginUserId, string DesignationTitle);

    private static int _nextEmployee = 9000;

    /// <summary>A company-A employee with a login, and a company-A designation mapped (by the group admin) to <see cref="MappedRole"/>.</summary>
    private async Task<Seeded> SeedAsync(bool flagOn = true, bool alreadyGranted = false)
    {
        var employeeId = Interlocked.Increment(ref _nextEmployee);
        var title = $"Desk {employeeId}";
        var loginId = Guid.NewGuid();
        await f.SeedAsync(async db =>
        {
            if (flagOn && !await db.TenantFeatureFlags.AnyAsync(x => x.TenantId == f.TenantId && x.FeatureKey == FeatureKeys.DesignationAccess))
                db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = f.TenantId, FeatureKey = FeatureKeys.DesignationAccess, IsEnabled = true });
            var designation = new Designation { TenantId = f.TenantId, CompanyId = f.CompanyA, Code = $"D{employeeId}", TitleEn = title };
            db.Designations.Add(designation);
            var role = await db.Roles.SingleAsync(r => r.TenantId == f.TenantId && r.Name == MappedRole);
            db.DesignationAccessRoles.Add(new DesignationAccessRole { TenantId = f.TenantId, DesignationId = designation.Id, RoleId = role.Id, CreatedBy = f.UserId("AdminGroup") });
            var employeeRole = await db.Roles.SingleAsync(r => r.TenantId == f.TenantId && r.Name == "Employee");
            db.Employees.Add(new Employee
            {
                Id = employeeId, TenantId = f.TenantId, CompanyId = f.CompanyA, EmployeeCode = $"E-{employeeId}", FullName = $"Person {employeeId}",
                EnglishName = $"Person {employeeId}", Status = EmployeeStatuses.Active, JoiningDate = new DateTime(2024, 1, 1), UserAccountId = loginId,
            });
            db.Users.Add(new User
            {
                Id = loginId, TenantId = f.TenantId, Email = $"p{employeeId}@launch.local", NormalizedEmail = $"P{employeeId}@LAUNCH.LOCAL", FullName = $"Person {employeeId}",
                PasswordHash = "x", Status = "Active", AccessMode = AccessModes.EssOnly, IsActive = true,
            });
            db.UserRoles.Add(new UserRole { UserId = loginId, RoleId = employeeRole.Id });
            if (alreadyGranted)
                db.UserRoles.Add(new UserRole { UserId = loginId, RoleId = role.Id, GrantedByDesignationId = designation.Id });
            db.EmployeeUserAccounts.Add(new EmployeeUserAccount { TenantId = f.TenantId, EmployeeId = employeeId, UserId = loginId, AccessMode = AccessModes.EssOnly, Status = "Active", IsPrimary = true });
        });
        return new Seeded(employeeId, loginId, title);
    }

    private Task<List<string>> RolesOfAsync(Guid userId) => f.ReadAsync(db =>
        db.UserRoles.Where(x => x.UserId == userId).Select(x => x.Role!.Name).OrderBy(x => x).ToListAsync());

    private Task<HttpResponseMessage> SetDesignation(string actor, int employeeId, string? designation) =>
        f.Send(HttpMethod.Put, $"/api/employees/{employeeId}", f.Token(actor),
            new { effectiveDate = "2026-11-12", changes = new Dictionary<string, object?> { ["designation"] = designation } });

    [Fact]
    public async Task A_designation_set_by_someone_below_the_roles_access_does_not_grant_it()
    {
        var s = await SeedAsync();
        using var response = await SetDesignation("HrOfficerA", s.EmployeeId, s.DesignationTitle);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        (await RolesOfAsync(s.LoginUserId)).Should().NotContain(MappedRole,
            "an HR officer holds no payroll approval, so choosing a designation must not hand it out");
        var skipped = await f.ReadAsync(db => db.AuditLogs.Where(a => a.TenantId == f.TenantId && a.Action == "access.designation_access_skipped"
            && a.EntityId == s.LoginUserId.ToString()).Select(a => a.Metadata).ToListAsync());
        skipped.Should().ContainSingle().Which.Should().Contain("above_actor");
    }

    [Fact]
    public async Task The_same_designation_set_by_someone_who_holds_the_roles_access_grants_it()
    {
        var s = await SeedAsync();
        using var response = await SetDesignation("AdminGroup", s.EmployeeId, s.DesignationTitle);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await RolesOfAsync(s.LoginUserId)).Should().Contain(MappedRole);
    }

    [Fact]
    public async Task A_role_already_granted_stays_when_a_below_ceiling_actor_re_saves_the_same_designation()
    {
        var s = await SeedAsync(alreadyGranted: true);
        await f.SeedAsync(async db =>
        {
            var e = await db.Employees.IgnoreQueryFilters().SingleAsync(x => x.Id == s.EmployeeId);
            e.DesignationId = (await db.Designations.IgnoreQueryFilters().SingleAsync(d => d.TitleEn == s.DesignationTitle)).Id;
        });
        using var response = await SetDesignation("HrOfficerA", s.EmployeeId, s.DesignationTitle);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await RolesOfAsync(s.LoginUserId)).Should().Contain(MappedRole, "taking nothing away and adding nothing leaves the roles as they were");
    }

    [Fact]
    public async Task With_the_feature_off_a_designation_change_takes_away_the_access_it_had_granted()
    {
        var s = await SeedAsync(alreadyGranted: true);
        await f.SeedAsync(async db =>
        {
            var e = await db.Employees.IgnoreQueryFilters().SingleAsync(x => x.Id == s.EmployeeId);
            e.DesignationId = (await db.Designations.IgnoreQueryFilters().SingleAsync(d => d.TitleEn == s.DesignationTitle)).Id;
            var flag = await db.TenantFeatureFlags.SingleAsync(x => x.TenantId == f.TenantId && x.FeatureKey == FeatureKeys.DesignationAccess);
            flag.IsEnabled = false;
        });
        try
        {
            await f.SeedAsync(async db => db.Designations.Add(new Designation { TenantId = f.TenantId, CompanyId = f.CompanyA, Code = $"N{s.EmployeeId}", TitleEn = $"Plain {s.EmployeeId}" }));
            using var response = await SetDesignation("AdminGroup", s.EmployeeId, $"Plain {s.EmployeeId}");
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            (await RolesOfAsync(s.LoginUserId)).Should().NotContain(MappedRole, "switching the feature off must not leave its grants in force");
        }
        finally
        {
            await f.SeedAsync(async db => (await db.TenantFeatureFlags.SingleAsync(x => x.TenantId == f.TenantId && x.FeatureKey == FeatureKeys.DesignationAccess)).IsEnabled = true);
        }
    }

    [Fact]
    public async Task With_the_feature_off_a_designation_is_never_applied()
    {
        var s = await SeedAsync();
        await f.SeedAsync(async db => (await db.TenantFeatureFlags.SingleAsync(x => x.TenantId == f.TenantId && x.FeatureKey == FeatureKeys.DesignationAccess)).IsEnabled = false);
        try
        {
            using var response = await SetDesignation("AdminGroup", s.EmployeeId, s.DesignationTitle);
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            (await RolesOfAsync(s.LoginUserId)).Should().NotContain(MappedRole);
        }
        finally
        {
            await f.SeedAsync(async db => (await db.TenantFeatureFlags.SingleAsync(x => x.TenantId == f.TenantId && x.FeatureKey == FeatureKeys.DesignationAccess)).IsEnabled = true);
        }
    }
}
