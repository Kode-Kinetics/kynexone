using System.Net;
using FluentAssertions;
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
        var s = await SeedAsync();
        (await f.Status(HttpMethod.Patch, $"{Route}/{s.DefaultProfile}/jawazat-policy", f.Token("AdminA"), new { jawazatPolicyJson = "{}" })).Should().Be(HttpStatusCode.Forbidden);
    }
}
