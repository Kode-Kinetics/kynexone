using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Contracts;
using Zayra.Api.Application.Entitlements;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Contracts;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// A benefit the customer added in Benefits by grade (<c>BEN_*</c>) reaches the employee's package like a built-in contract
/// benefit: a preview from the grade value until the term is frozen, then fixed for the contract year, named from its own row.
/// </summary>
public sealed class CustomBenefitPackageTests
{
    private static readonly DateOnly Today = new(2026, 10, 6);

    private static async Task<(ZayraDbContext Db, PackageSeed Seed)> SeededAsync()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase($"custom-benefit-{Guid.NewGuid():N}").Options);
        var seed = new PackageSeed();
        seed.Cell("BEN_PARKING", PayEntitlementClasses.Contractual, GradeEntitlementValueTypes.Amount, amount: 1_200m, period: EntitlementLimitPeriods.Annual);
        await seed.SaveAsync(db);
        db.PayComponents.Add(new PayComponent
        {
            TenantId = seed.TenantId, CompanyId = null, Code = "BEN_PARKING", NameEn = "Parking", NameAr = "موقف",
            ComponentType = PayComponentTypes.Benefit, EntitlementClass = PayEntitlementClasses.Contractual, IsOffered = true,
        });
        await db.SaveChangesAsync();
        return (db, seed);
    }

    [Fact]
    public async Task ACustomBenefit_IsAPreviewBeforeTheFreeze_ThenFrozenForTheContractYear()
    {
        var (db, s) = await SeededAsync();
        await using var _ = db;
        var resolver = new EntitlementResolver(db);

        var preview = await resolver.ResolveAsync(s.TenantId, s.Mohammed.Id, Today, default);
        var line = preview.Lines.Single(l => l.ComponentCode == "BEN_PARKING");
        (line.Class, line.Source, line.Eligible, line.Offered, line.Amount, line.LimitPeriod)
            .Should().Be((PayEntitlementClasses.Contractual, PackageLineSources.GradeStandard, true, true, 1_200m, EntitlementLimitPeriods.Annual));
        preview.BlockCodes.Should().BeEmpty();

        var writer = new EntitlementWriter(db, new FixedTenantClock(PackageSeed.FreezeDay), resolver);
        await writer.FreezeTermAsync(s.TenantId, s.Term.Id, default);
        await db.SaveChangesAsync();
        db.EmployeeEntitlements.Single(e => e.PayComponentCode == "BEN_PARKING").Amount.Should().Be(1_200m);

        var frozen = (await resolver.ResolveAsync(s.TenantId, s.Mohammed.Id, Today, default)).Lines.Single(l => l.ComponentCode == "BEN_PARKING");
        (frozen.Source, frozen.Amount).Should().Be((PackageLineSources.ContractFrozen, 1_200m));
    }

    [Fact]
    public async Task ACompanyThatSkipsACustomBenefit_DoesNotOfferIt_AndItIsNotFrozen()
    {
        var (db, s) = await SeededAsync();
        await using var _ = db;
        db.PayComponents.Add(new PayComponent
        {
            TenantId = s.TenantId, CompanyId = s.Company.Id, Code = "BEN_PARKING", NameEn = "Parking", NameAr = "موقف",
            ComponentType = PayComponentTypes.Benefit, EntitlementClass = PayEntitlementClasses.Contractual, IsOffered = false,
        });
        await db.SaveChangesAsync();
        var resolver = new EntitlementResolver(db);

        var line = (await resolver.ResolveAsync(s.TenantId, s.Mohammed.Id, Today, default)).Lines.Single(l => l.ComponentCode == "BEN_PARKING");
        (line.Offered, line.Eligible).Should().Be((false, false));
        (await resolver.ResolveAsync(s.TenantId, s.Colleague.Id, Today, default)).Lines.Single(l => l.ComponentCode == "BEN_PARKING").Offered.Should().BeTrue();

        await new EntitlementWriter(db, new FixedTenantClock(PackageSeed.FreezeDay), resolver).FreezeTermAsync(s.TenantId, s.Term.Id, default);
        db.ChangeTracker.Entries<EmployeeEntitlement>().Select(e => e.Entity.PayComponentCode).Should().NotContain("BEN_PARKING");
    }
}
