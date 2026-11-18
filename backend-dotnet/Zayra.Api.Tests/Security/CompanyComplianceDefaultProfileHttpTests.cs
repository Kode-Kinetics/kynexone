using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// The tenant-wide default compliance profile (no company) is shared by every company, so only an Admin with group-level authority
/// may create, change, retire or restore it, through the real pipeline. A company-scoped Admin keeps full control of their own
/// company's profile.
/// </summary>
public sealed class CompanyComplianceDefaultProfileHttpTests(LaunchSecurityFixture f) : IClassFixture<LaunchSecurityFixture>
{
    private const string Route = "/api/company-compliance-profiles";

    private sealed record Seed(Guid DefaultProfile, Guid CompanyAProfile);

    private Task<Seed> SeedAsync() => f.OnceAsync("compliance-default-profile", async () =>
    {
        var defaultId = Guid.NewGuid();
        var companyAId = Guid.NewGuid();
        await f.SeedAsync(db =>
        {
            db.CompanyComplianceProfiles.Add(new CompanyComplianceProfile
            {
                Id = defaultId, TenantId = f.TenantId, CompanyId = null, CountryCode = "SA", EffectiveFrom = new DateOnly(2026, 1, 1), Status = CompanyPolicyStatuses.Draft,
            });
            db.CompanyComplianceProfiles.Add(new CompanyComplianceProfile
            {
                Id = companyAId, TenantId = f.TenantId, CompanyId = f.CompanyA, CountryCode = "SA", EffectiveFrom = new DateOnly(2026, 1, 1), Status = CompanyPolicyStatuses.Draft,
            });
            return Task.CompletedTask;
        });
        return new Seed(defaultId, companyAId);
    });

    private static object Body(Guid? companyId, string notes = "n") =>
        new { companyId, countryCode = "SA", effectiveFrom = "2026-01-01", status = "Draft", notes };

    [Fact]
    public async Task ACompanyScopedAdmin_CannotCreateTheTenantDefaultProfile_ButAGroupAdminCan()
    {
        await SeedAsync();
        (await f.Status(HttpMethod.Post, Route, f.Token("AdminA"), Body(null))).Should().Be(HttpStatusCode.Forbidden);
        (await f.Status(HttpMethod.Post, Route, f.Token("AdminGroup"), Body(null))).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ACompanyScopedAdmin_CannotChangeTheTenantDefaultProfile_ButAGroupAdminCan()
    {
        var s = await SeedAsync();
        (await f.Status(HttpMethod.Put, $"{Route}/{s.DefaultProfile}", f.Token("AdminA"), Body(null, "changed by company admin"))).Should().Be(HttpStatusCode.Forbidden);
        (await f.Status(HttpMethod.Put, $"{Route}/{s.DefaultProfile}", f.Token("AdminGroup"), Body(null, "changed by group admin"))).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ACompanyScopedAdmin_CannotRepointTheirOwnProfileAtTheTenantDefault()
    {
        var s = await SeedAsync();
        (await f.Status(HttpMethod.Put, $"{Route}/{s.CompanyAProfile}", f.Token("AdminA"), Body(null))).Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ACompanyScopedAdmin_StillChangesTheirOwnCompanysProfile()
    {
        var s = await SeedAsync();
        (await f.Status(HttpMethod.Put, $"{Route}/{s.CompanyAProfile}", f.Token("AdminA"), Body(f.CompanyA, "own company"))).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ACompanyScopedAdmin_CannotRemoveTheTenantDefaultProfile()
    {
        var s = await SeedAsync();
        // A profile the caller may not change reads as not found, the same as a wrong id.
        (await f.Status(HttpMethod.Delete, $"{Route}/{s.DefaultProfile}", f.Token("AdminA"))).Should().Be(HttpStatusCode.NotFound);
        (await f.ReadAsync(async db => await db.CompanyComplianceProfiles.FindAsync(s.DefaultProfile))).Should().NotBeNull();
    }

    [Fact]
    public async Task ACompanyScopedAdmin_CannotSetTheJawazatPolicyOfTheTenantDefaultProfile()
    {
        // Called directly: the HTTP route answers 400 to every caller for an unrelated request-binding reason, so the scope check is
        // exercised on the controller itself.
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Zayra.Api.Data.ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new Zayra.Api.Data.ZayraDbContext(options);
        var tenant = Guid.NewGuid();
        var profile = new CompanyComplianceProfile { TenantId = tenant, CompanyId = null, CountryCode = "SA", EffectiveFrom = new DateOnly(2026, 1, 1), Status = CompanyPolicyStatuses.Draft };
        db.CompanyComplianceProfiles.Add(profile);
        await db.SaveChangesAsync();
        var scope = System.Text.Json.JsonSerializer.Serialize(new { v = 2, m = "companies", c = new[] { Guid.NewGuid() } });
        var controller = new Zayra.Api.Controllers.CompanyComplianceProfilesController(db)
        {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
                {
                    User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(new[]
                    {
                        new System.Security.Claims.Claim("tenant_id", tenant.ToString()),
                        new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                        new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "Admin"),
                        new System.Security.Claims.Claim("entity_scope", scope),
                    }, "test")),
                },
            },
        };

        var result = await controller.UpdateJawazatPolicy(profile.Id, new Zayra.Api.Controllers.UpdateJawazatPolicyRequest("{}"), default);

        result.Should().BeOfType<Microsoft.AspNetCore.Mvc.ForbidResult>();
    }
}
