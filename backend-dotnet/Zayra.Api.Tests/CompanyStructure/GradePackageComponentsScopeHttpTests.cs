using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Data;
using Zayra.Api.Models;
using Zayra.Api.Tests.Onboarding;

namespace Zayra.Api.Tests.CompanyStructure;

/// <summary>The grade editor's component list is read for a company the caller holds, never for a sibling company.</summary>
public sealed class GradePackageComponentsScopeHttpTests : IClassFixture<HireIntegrityHttpFixture>
{
    private readonly HireIntegrityHttpFixture _fx;
    public GradePackageComponentsScopeHttpTests(HireIntegrityHttpFixture fixture) => _fx = fixture;

    private async Task<string> SeedComponentAsync(Guid companyId)
    {
        var code = $"PKG_{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        using var s = _fx.Host.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<ZayraDbContext>();
        db.PayComponents.Add(new PayComponent
        {
            TenantId = _fx.TenantId, CompanyId = companyId, Code = code, NameEn = $"Allowance {code}",
            ComponentType = PayComponentTypes.Earning, CalcMethod = PayComponentCalcMethods.GradePackage, IsActive = true, IsOffered = true,
        });
        await db.SaveChangesAsync();
        return code;
    }

    [Fact]
    public async Task ACompanyScopedCaller_CannotReadAnotherCompanysComponents_ButCanReadTheirOwn()
    {
        var far = await SeedComponentAsync(_fx.OtherCompanyId);
        var own = await SeedComponentAsync(_fx.CompanyId);
        var scoped = await _fx.NewScopedUserAsync($"pc{Guid.NewGuid():N}"[..10], _fx.CompanyId, "organization.read");

        using var denied = await _fx.SendAsync(HttpMethod.Get, $"/api/grades/package-components?companyId={_fx.OtherCompanyId}", null, scoped);
        denied.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await denied.Content.ReadAsStringAsync()).Should().NotContain(far);

        using var allowed = await _fx.SendAsync(HttpMethod.Get, $"/api/grades/package-components?companyId={_fx.CompanyId}", null, scoped);
        allowed.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await allowed.Content.ReadAsStringAsync();
        body.Should().Contain(own).And.NotContain(far);
    }

    [Fact]
    public async Task AGroupCaller_CanReadAnyCompanysComponents()
    {
        var far = await SeedComponentAsync(_fx.OtherCompanyId);
        var group = await _fx.NewUserInRoleAsync("HR Manager", $"pg{Guid.NewGuid():N}"[..10], "organization.read");
        using var r = await _fx.SendAsync(HttpMethod.Get, $"/api/grades/package-components?companyId={_fx.OtherCompanyId}", null, group);
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        (await r.Content.ReadAsStringAsync()).Should().Contain(far);
    }
}
