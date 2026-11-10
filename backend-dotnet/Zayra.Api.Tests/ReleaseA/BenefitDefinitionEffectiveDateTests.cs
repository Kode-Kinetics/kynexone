using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Models;
using static Zayra.Api.Tests.ReleaseA.Bd;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// A benefit's effective window and active switch are consulted, not just stored (feature <c>benefit_definition_v1</c>): the matrix read and the
/// entitlement resolver treat a benefit outside its window, or switched off, as not offered. With the feature off both behave exactly as before.
/// </summary>
public class BenefitDefinitionEffectiveDateTests
{
    private static readonly DateOnly Dec1 = new(2026, 12, 1);

    private static async Task<Bd> WithWindowedParking(bool flag = true)
    {
        var h = await Bd.Create(flag);
        // Window: 1 Nov 2026 – 31 Oct 2027.
        h.Db.PayComponents.Add(Row(h.Tid, "BEN_PARK", PayComponentTypes.Benefit, from: Bd.Nov1, to: Bd.Oct31Next));
        await h.Db.SaveChangesAsync();
        return h;
    }

    private static PayComponent Row(Guid tid, string code, string type, DateOnly? from, DateOnly? to, bool active = true) => new()
    {
        TenantId = tid, CompanyId = null, Code = code, NameEn = code, NameAr = code, ComponentType = type, IsActive = active, IsOffered = true,
        EntitlementClass = type == PayComponentTypes.Benefit ? PayEntitlementClasses.Contractual : PayEntitlementClasses.QiwaWage,
        CalcMethod = type == PayComponentTypes.Benefit ? PayComponentCalcMethods.Fixed : PayComponentCalcMethods.GradePackage,
        EffectiveFrom = from, EffectiveTo = to, GlDriverKey = type == PayComponentTypes.Benefit ? null : CarDriver, FinanceConfirmedAtUtc = type == PayComponentTypes.Benefit ? null : DateTime.UtcNow,
    };

    // ══ the matrix read ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task BeforeItsWindow_TheBenefitIsListedButNotOffered_AndHasNoGaps()
    {
        await using var h = await WithWindowedParking();

        var before = await h.Read(new DateOnly(2026, 10, 15));
        var during = await h.Read(Dec1);

        var offering = before.Offerings.Single(o => o.ComponentCode == "BEN_PARK");
        (offering.Offered, offering.Mode).Should().Be((false, EntitlementMatrixService.Modes.NotInEffect));
        before.Gaps.Should().NotContain(g => g.ComponentCode == "BEN_PARK");
        before.Components.Should().Contain(c => c.Code == "BEN_PARK", "it stays visible and editable");
        during.Offerings.Single(o => o.ComponentCode == "BEN_PARK").Offered.Should().BeTrue();
        during.Gaps.Where(g => g.ComponentCode == "BEN_PARK").Select(g => g.GradeId).Should().BeEquivalentTo([h.G[1].Id, h.G[2].Id]);
    }

    [Fact]
    public async Task AfterItsWindow_TheBenefitIsNotOffered_AlsoInACompanysView()
    {
        await using var h = await WithWindowedParking();

        var after = await h.Read(new DateOnly(2027, 11, 15), h.Alpha.Id);

        var offering = after.Offerings.Single(o => o.ComponentCode == "BEN_PARK");
        (offering.Offered, offering.Mode).Should().Be((false, EntitlementMatrixService.Modes.NotInEffect));
        after.Gaps.Should().NotContain(g => g.ComponentCode == "BEN_PARK");
    }

    [Fact]
    public async Task WithTheFlagOff_TheWindowIsIgnored_AsToday()
    {
        await using var h = await WithWindowedParking(flag: false);

        var before = await h.Read(new DateOnly(2026, 10, 15));

        before.Offerings.Single(o => o.ComponentCode == "BEN_PARK").Offered.Should().BeTrue();
        before.Gaps.Should().Contain(g => g.ComponentCode == "BEN_PARK");
    }

    [Fact]
    public async Task ACellCanOnlyBePublishedFromADateInsideTheWindow()
    {
        await using var h = await WithWindowedParking();
        h.Db.PayComponents.Add(Row(h.Tid, "BEN_GYM", PayComponentTypes.Benefit, from: null, to: new DateOnly(2026, 12, 31)));
        await h.Db.SaveChangesAsync();

        var tooEarly = await h.PublishGroup(new DateOnly(2026, 10, 6), Amount(h.G[1], "BEN_PARK", 100m));
        var tooLate = await h.PublishGroup(new DateOnly(2027, 1, 1), Amount(h.G[1], "BEN_GYM", 100m));
        var inside = await h.PublishGroup(Bd.Nov1, Amount(h.G[1], "BEN_PARK", 100m), Amount(h.G[1], "BEN_GYM", 50m));

        (Status(tooEarly), Status(tooLate), Status(inside)).Should().Be((400, 400, 200));
        System.Text.Json.JsonSerializer.Serialize(((Microsoft.AspNetCore.Mvc.ObjectResult)tooEarly).Value, Bd.Web).Should().Contain("benefit_not_in_effect");
        (await h.Db.GradeEntitlements.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task WithTheFlagOff_APublishOutsideTheWindowIsNotChecked()
    {
        await using var h = await WithWindowedParking(flag: false);

        Status(await h.PublishGroup(new DateOnly(2026, 10, 6), Amount(h.G[1], "BEN_PARK", 100m))).Should().Be(200);
    }

    // ══ the resolver and the grade standard ═════════════════════════════════════════════════════

    private static async Task<(Guid TenantId, EntitlementResolver Resolver, Guid GradeId, Guid CompanyId, Bd H)> ResolverSetup(bool flag, Action<Bd>? tweak = null)
    {
        var h = await WithWindowedParking(flag);
        h.Db.PayComponents.Add(Row(h.Tid, "ALW_CAR", PayComponentTypes.Earning, from: Bd.Nov1, to: Bd.Oct31Next));
        foreach (var code in new[] { "BEN_PARK", "ALW_CAR" })
            h.Db.GradeEntitlements.Add(new GradeEntitlement
            {
                TenantId = h.Tid, GradeId = h.G[1].Id, PayComponentCode = code,
                EntitlementClass = code.StartsWith("BEN_") ? PayEntitlementClasses.Contractual : PayEntitlementClasses.QiwaWage,
                Eligible = true, ValueType = GradeEntitlementValueTypes.Amount, Amount = 300m, EffectiveFrom = new DateOnly(2026, 1, 1),
                LimitPeriod = code.StartsWith("BEN_") ? EntitlementLimitPeriods.Annual : EntitlementLimitPeriods.Monthly,
            });
        await h.Db.SaveChangesAsync();
        tweak?.Invoke(h);
        await h.Db.SaveChangesAsync();
        return (h.Tid, new EntitlementResolver(h.Db), h.G[1].Id, h.Alpha.Id, h);
    }

    [Theory]
    [InlineData("2026-10-15", false)]
    [InlineData("2026-11-01", true)]
    [InlineData("2027-10-31", true)]
    [InlineData("2027-11-01", false)]
    public async Task TheResolver_OffersABenefitOrAllowanceOnlyInsideItsWindow(string asOf, bool offered)
    {
        var (tid, resolver, grade, company, h) = await ResolverSetup(flag: true);
        await using var _ = h;

        var lines = await resolver.GradeStandardAsync(tid, grade, company, DateOnly.Parse(asOf), default);

        lines.Single(l => l.ComponentCode == "BEN_PARK").Offered.Should().Be(offered);
        lines.Single(l => l.ComponentCode == "ALW_CAR").Offered.Should().Be(offered);
    }

    [Fact]
    public async Task TheResolver_DoesNotOfferASwitchedOffBenefit()
    {
        var (tid, resolver, grade, company, h) = await ResolverSetup(flag: true, tweak: x =>
            x.Db.PayComponents.Single(p => p.Code == "BEN_PARK").IsActive = false);
        await using var _ = h;

        var lines = await resolver.GradeStandardAsync(tid, grade, company, Dec1, default);

        lines.Single(l => l.ComponentCode == "BEN_PARK").Offered.Should().BeFalse();
        lines.Single(l => l.ComponentCode == "ALW_CAR").Offered.Should().BeTrue();
    }

    [Fact]
    public async Task WithTheFlagOff_TheResolverIgnoresTheWindowAndTheSwitch_AsToday()
    {
        var (tid, resolver, grade, company, h) = await ResolverSetup(flag: false, tweak: x =>
            x.Db.PayComponents.Single(p => p.Code == "BEN_PARK").IsActive = false);
        await using var _ = h;

        var lines = await resolver.GradeStandardAsync(tid, grade, company, new DateOnly(2026, 10, 15), default);

        lines.Select(l => (l.ComponentCode, l.Offered)).Should().BeEquivalentTo([("BEN_PARK", true), ("ALW_CAR", true)]);
    }

    [Fact]
    public async Task TheMatrixServicesGradeStandard_AgreesWithTheResolver()
    {
        var (tid, _, grade, company, h) = await ResolverSetup(flag: true);
        await using var _ = h;

        var before = await h.Service.GradeStandardLinesAsync(tid, grade, company, new DateOnly(2026, 10, 15), default);
        var during = await h.Service.GradeStandardLinesAsync(tid, grade, company, Dec1, default);

        before.Where(l => l.ComponentCode is "BEN_PARK" or "ALW_CAR").Should().OnlyContain(l => !l.Offered);
        during.Where(l => l.ComponentCode is "BEN_PARK" or "ALW_CAR").Should().OnlyContain(l => l.Offered);
    }

    [Fact]
    public async Task AllowanceVersions_AreOfferedWhileAnyLiveVersionIsInEffect()
    {
        var (tid, resolver, grade, company, h) = await ResolverSetup(flag: true, tweak: x =>
        {
            var v1 = x.Db.PayComponents.Single(p => p.Code == "ALW_CAR");
            v1.EffectiveFrom = new DateOnly(2026, 1, 1); v1.EffectiveTo = new DateOnly(2026, 12, 31);
            x.Db.PayComponents.Add(Row(x.Tid, "ALW_CAR", PayComponentTypes.Earning, from: new DateOnly(2027, 1, 1), to: null));
        });
        await using var _ = h;

        (await resolver.GradeStandardAsync(tid, grade, company, new DateOnly(2026, 6, 1), default)).Single(l => l.ComponentCode == "ALW_CAR").Offered.Should().BeTrue();
        (await resolver.GradeStandardAsync(tid, grade, company, new DateOnly(2027, 6, 1), default)).Single(l => l.ComponentCode == "ALW_CAR").Offered.Should().BeTrue();
    }
}
