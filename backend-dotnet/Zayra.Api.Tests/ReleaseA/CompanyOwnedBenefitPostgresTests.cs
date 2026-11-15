using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Entitlements;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// A company's own benefits on real PostgreSQL 16, through a DbContext that carries the caller's scope, so the database's config write guard (a
/// company-scoped user may write only rows of a company it reaches, never a tenant-wide row) and the real unique keys are part of the test. Two company
/// admins add a "Car" at the same moment and each gets their own; a sister company's admin cannot change it or see it; group HR widens it.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class CompanyOwnedBenefitPostgresTests(PostgresFixture fixture)
{
    private static readonly DateOnly Today = new(2026, 10, 6);
    private static readonly DateOnly Nov1 = new(2026, 11, 1);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(60);

    private sealed record Seed(Guid TenantId, Guid Alpha, Guid Beta, Guid GradeId);

    private sealed class FixedClock(DateOnly today) : ITenantClock
    {
        public Task<DateOnly> TodayAsync(Guid tenantId, CancellationToken ct) => Task.FromResult(today);
    }

    private async Task<Seed> SeedAsync()
    {
        await using var db = fixture.CreateDb();
        var tid = await PostgresFixture.SeedMinimalTenant(db);
        var alpha = new Company { TenantId = tid, LegalNameEn = $"Alpha {Guid.NewGuid():N}", CountryCode = "SA", DefaultCurrency = "SAR", IsActive = true };
        var beta = new Company { TenantId = tid, LegalNameEn = $"Beta {Guid.NewGuid():N}", CountryCode = "SA", DefaultCurrency = "SAR", IsActive = true };
        var grade = new Grade { TenantId = tid, Code = "G1", Name = "Grade 1", Level = 10 };
        db.AddRange(alpha, beta, grade);
        db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = tid, FeatureKey = FeatureKeys.BenefitDefinitionV1, IsEnabled = true });
        db.GlDrivers.Add(new GlDriver
        {
            TenantId = tid, CompanyId = null, Key = "EARN:CAR", Label = "Car", Category = GlDriverCategories.Earning, PostingSide = "DR",
            DefaultCode = "5210", DefaultName = "Car allowance expense", MatchMode = "Exact", MatchComponentCode = "CAR", SortOrder = 70,
        });
        await db.SaveChangesAsync();
        // The group set the catalogue up already (as every tenant using the matrix has): a company admin's request never seeds tenant rows.
        await PayComponentSeeder.EnsureEntitlementCatalogAsync(db, tid, default);
        await db.SaveChangesAsync();
        return new Seed(tid, alpha.Id, beta.Id, grade.Id);
    }

    private static ClaimsPrincipal User(Guid tenant, Guid user, Guid[]? companies, bool finance = false)
    {
        var claims = new List<Claim> { new("tenant_id", tenant.ToString()), new(ClaimTypes.NameIdentifier, user.ToString()), new(ClaimTypes.Role, "HR Director") };
        if (finance) claims.Add(new Claim("permission", "payroll.approve"));
        claims.Add(new Claim(EntityScopeContext.V2ClaimType, companies is null
            ? JsonSerializer.Serialize(new { v = 2, m = "group", c = Array.Empty<Guid>() })
            : JsonSerializer.Serialize(new { v = 2, m = "companies", c = companies })));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    /// <summary>One request: a DbContext that carries the caller (so the config write guard applies), the service on it, and the controller.</summary>
    private async Task<T> AsAsync<T>(ClaimsPrincipal user, Func<EntitlementMatrixController, EntitlementMatrixService, Task<T>> call)
    {
        var http = new DefaultHttpContext { User = user };
        await using var db = fixture.CreateDbWithAccessor(new HttpContextAccessor { HttpContext = http });
        var service = new EntitlementMatrixService(db, new FixedClock(Today));
        var controller = new EntitlementMatrixController { ControllerContext = new ControllerContext { HttpContext = http } };
        return await call(controller, service);
    }

    private static (int? Status, string? Error) Outcome(IActionResult r)
    {
        var o = (ObjectResult)r;
        var json = JsonSerializer.SerializeToElement(o.Value, Web);
        return (o.StatusCode, json.ValueKind == JsonValueKind.Object && json.TryGetProperty("error", out var e) ? e.GetString() : null);
    }

    private static string Describe(IActionResult r) => r is ObjectResult o ? $"{o.StatusCode}: {JsonSerializer.Serialize(o.Value, Web)}" : r.GetType().Name;

    private static MatrixComponentDto Component(IActionResult r)
    {
        ((ObjectResult)r).StatusCode.Should().Be(200, Describe(r));
        return (MatrixComponentDto)((ObjectResult)r).Value!;
    }

    private static CreateBenefitRequest Own(Guid company, string name, bool cash = false) =>
        new(name, null, cash ? BenefitKinds.Cash : BenefitKinds.Contract, cash ? "EARN:CAR" : null) { CompanyId = company };

    private async Task<List<PayComponent>> RowsAsync(Seed seed)
    {
        await using var db = fixture.CreateDb();
        return await db.PayComponents.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.TenantId == seed.TenantId && (p.Code.StartsWith("BEN_") || p.Code.StartsWith("ALW_"))).ToListAsync();
    }

    [Fact]
    public async Task ACompanyAdmin_AddsEditsPublishesAndRemovesItsOwnBenefit_ThroughTheRealCompanyWriteGuard()
    {
        var seed = await SeedAsync();
        var alphaAdmin = User(seed.TenantId, Guid.NewGuid(), [seed.Alpha]);

        var parking = Component(await AsAsync(alphaAdmin, (c, s) => c.AddBenefit(s, Own(seed.Alpha, "Parking"), default)));
        var car = Component(await AsAsync(alphaAdmin, (c, s) => c.AddBenefit(s, Own(seed.Alpha, "Car", cash: true), default)));

        var rows = await RowsAsync(seed);
        rows.Should().HaveCount(2).And.OnlyContain(r => r.CompanyId == seed.Alpha && r.IsOffered);
        await using (var db = fixture.CreateDb())
            (await db.PayComponents.IgnoreQueryFilters().CountAsync(p => p.TenantId == seed.TenantId && p.CompanyId == seed.Alpha))
                .Should().Be(2, "nothing tenant-wide was written (and so stamped to the company) by the company admin's requests");

        var edited = Component(await AsAsync(alphaAdmin, (c, s) => c.UpdateBenefit(s, parking.Code, new UpdateBenefitRequest(parking.Definition!.Version, "Staff parking", null), default)));
        edited.NameEn.Should().Be("Staff parking");
        var cell = new MatrixCellInput { GradeId = seed.GradeId, ComponentCode = parking.Code, Eligible = true, ValueType = "Amount", Amount = 250m };
        var published = await AsAsync(alphaAdmin, (c, s) => c.Publish(s, new PublishMatrixRequest(seed.Alpha, Nov1, [cell]), false, default));
        ((ObjectResult)published).StatusCode.Should().Be(200, Describe(published));
        (await AsAsync(alphaAdmin, (c, s) => c.RemoveBenefit(s, car.Code, default)).ContinueWith(t => Outcome(t.Result))).Should().Be(((int?)200, (string?)null));

        var betaView = await AsAsync(User(seed.TenantId, Guid.NewGuid(), [seed.Beta]), (c, s) => c.Matrix(s, seed.Beta, Today, default));
        ((EntitlementMatrixDto)((OkObjectResult)betaView).Value!).Components.Should().NotContain(x => x.Code == parking.Code || x.Code == car.Code);
    }

    [Fact]
    public async Task ASisterCompanysAdmin_CannotChangeOrRemoveIt_AndTheRowIsUntouched()
    {
        var seed = await SeedAsync();
        var parking = Component(await AsAsync(User(seed.TenantId, Guid.NewGuid(), [seed.Alpha]), (c, s) => c.AddBenefit(s, Own(seed.Alpha, "Parking"), default)));
        var betaAdmin = User(seed.TenantId, Guid.NewGuid(), [seed.Beta]);

        Outcome(await AsAsync(betaAdmin, (c, s) => c.UpdateBenefit(s, parking.Code, new UpdateBenefitRequest(parking.Definition!.Version, "Ours", null), default)))
            .Should().Be(((int?)404, "unknown_component"));
        Outcome(await AsAsync(betaAdmin, (c, s) => c.RemoveBenefit(s, parking.Code, default))).Should().Be(((int?)404, "unknown_component"));
        Outcome(await AsAsync(betaAdmin, (c, s) => c.AddBenefit(s, Own(seed.Alpha, "Ours"), default))).Should().Be(((int?)403, "company_not_accessible"));

        var row = (await RowsAsync(seed)).Single();
        (row.CompanyId, row.NameEn, row.IsDeleted).Should().Be(((Guid?)seed.Alpha, "Parking", false));
    }

    [Fact]
    public async Task TwoCompanyAdmins_AddingACarAtTheSameMoment_EachGetTheirOwn_AndOneCompanyCannotGetTwo()
    {
        var seed = await SeedAsync();
        var alphaAdmin = User(seed.TenantId, Guid.NewGuid(), [seed.Alpha]);
        var betaAdmin = User(seed.TenantId, Guid.NewGuid(), [seed.Beta]);

        var attempts = new[]
        {
            Task.Run(() => AsAsync(alphaAdmin, (c, s) => c.AddBenefit(s, Own(seed.Alpha, "Car", cash: true), default))),
            Task.Run(() => AsAsync(betaAdmin, (c, s) => c.AddBenefit(s, Own(seed.Beta, "Car", cash: true), default))),
            Task.Run(() => AsAsync(alphaAdmin, (c, s) => c.AddBenefit(s, Own(seed.Alpha, "Car", cash: true), default))),
        };
        var results = await Task.WhenAll(attempts).WaitAsync(Generous);

        var outcomes = results.Select(Outcome).ToList();
        outcomes.Count(o => o.Status == 200).Should().Be(2, string.Join(" | ", results.Select(Describe)));
        outcomes.Should().ContainSingle(o => o.Status == 409 && o.Error == "benefit_exists", "the per-tenant lock serialises the two Alpha requests");
        var rows = await RowsAsync(seed);
        rows.Select(r => r.CompanyId).Should().BeEquivalentTo([(Guid?)seed.Alpha, seed.Beta]);
        rows.Select(r => r.Code).Distinct().Should().HaveCount(2).And.OnlyContain(c => c.StartsWith("ALW_CAR_"));
    }

    [Fact]
    public async Task GroupHr_WidensACompanysBenefit_AndFromThenOnTheCompanyAdminCannotTouchIt()
    {
        var seed = await SeedAsync();
        var alphaAdmin = User(seed.TenantId, Guid.NewGuid(), [seed.Alpha]);
        var parking = Component(await AsAsync(alphaAdmin, (c, s) => c.AddBenefit(s, Own(seed.Alpha, "Parking"), default)));

        var widened = Component(await AsAsync(User(seed.TenantId, Guid.NewGuid(), null), (c, s) =>
            c.UpdateBenefit(s, parking.Code, new UpdateBenefitRequest(parking.Definition!.Version, "Parking", null) { Scope = new BenefitScopeInput([seed.Alpha, seed.Beta]) }, default)));

        widened.OwnerCompanyId.Should().BeNull();
        (await RowsAsync(seed)).Single().CompanyId.Should().BeNull();
        Outcome(await AsAsync(alphaAdmin, (c, s) => c.UpdateBenefit(s, parking.Code, new UpdateBenefitRequest(widened.Definition!.Version, "Alpha only", null), default)))
            .Should().Be(((int?)403, "group_scope_required"));
        Outcome(await AsAsync(alphaAdmin, (c, s) => c.RemoveBenefit(s, parking.Code, default))).Should().Be(((int?)403, "group_scope_required"));
    }
}
