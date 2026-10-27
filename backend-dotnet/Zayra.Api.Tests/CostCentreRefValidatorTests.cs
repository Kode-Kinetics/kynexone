using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Cost-centre accounting v1, WP-A (contract 2.9): the one validator behind every door that writes a CostCenterId.
/// Exists, same tenant, not deleted, same company as the owner, and active when it is a NEW assignment.
/// </summary>
public class CostCentreRefValidatorTests
{
    private static ZayraDbContext Db() => new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed record World(ZayraDbContext Db, Guid Tenant, Guid CompanyA, Guid CompanyB, CostCenter Own, CostCenter Inactive, CostCenter Deleted, CostCenter OtherCompany, CostCenter OtherTenant, CostCenter TenantWide);

    private static async Task<World> Seed()
    {
        var db = Db();
        var tenant = Guid.NewGuid(); var other = Guid.NewGuid();
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var own = new CostCenter { TenantId = tenant, CompanyId = a, Code = "OWN", Name = "Own" };
        var inactive = new CostCenter { TenantId = tenant, CompanyId = a, Code = "OFF", Name = "Off", IsActive = false };
        var deleted = new CostCenter { TenantId = tenant, CompanyId = a, Code = "DEL", Name = "Del", IsDeleted = true };
        var otherCompany = new CostCenter { TenantId = tenant, CompanyId = b, Code = "OC", Name = "Other co" };
        var otherTenant = new CostCenter { TenantId = other, CompanyId = a, Code = "OT", Name = "Other tenant" };
        var wide = new CostCenter { TenantId = tenant, CompanyId = null, Code = "WIDE", Name = "Tenant wide" };
        db.AddRange(own, inactive, deleted, otherCompany, otherTenant, wide);
        await db.SaveChangesAsync();
        return new World(db, tenant, a, b, own, inactive, deleted, otherCompany, otherTenant, wide);
    }

    [Fact]
    public async Task AValidCostCentreOfTheOwnersCompany_Passes()
    {
        var w = await Seed();
        var r = await new CostCentreRefValidator(w.Db).ValidateAsync(w.Tenant, w.CompanyA, w.Own.Id, forNewAssignment: true, default);
        r.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("random", "cost_centre_not_found")]
    [InlineData("other-tenant", "cost_centre_not_found")]
    [InlineData("deleted", "cost_centre_not_found")]
    [InlineData("other-company", "cost_centre_other_company")]
    [InlineData("tenant-wide", "cost_centre_other_company")]
    [InlineData("inactive", "cost_centre_inactive")]
    public async Task T008_EveryBadReference_IsRefused_WithItsOwnCode(string kind, string code)
    {
        var w = await Seed();
        var id = kind switch
        {
            "random" => Guid.NewGuid(), "other-tenant" => w.OtherTenant.Id, "deleted" => w.Deleted.Id,
            "other-company" => w.OtherCompany.Id, "tenant-wide" => w.TenantWide.Id, _ => w.Inactive.Id,
        };
        var r = await new CostCentreRefValidator(w.Db).ValidateAsync(w.Tenant, w.CompanyA, id, forNewAssignment: true, default);
        (r.IsValid, r.Code).Should().Be((false, code));
        r.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task AnInactiveCostCentre_AlreadyOnTheRecord_IsAcceptedWhenTheAssignmentIsNotNew()
    {
        var w = await Seed();
        var v = new CostCentreRefValidator(w.Db);
        (await v.ValidateAsync(w.Tenant, w.CompanyA, w.Inactive.Id, forNewAssignment: false, default)).IsValid.Should().BeTrue();
        (await v.ValidateAsync(w.Tenant, w.CompanyA, w.Inactive.Id, forNewAssignment: true, default)).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task ALegacyOwnerWithNoCompany_MayOnlyUseAnUnassignedCostCentre()
    {
        var w = await Seed();
        var v = new CostCentreRefValidator(w.Db);
        (await v.ValidateAsync(w.Tenant, null, w.TenantWide.Id, true, default)).IsValid.Should().BeTrue();
        (await v.ValidateAsync(w.Tenant, null, w.Own.Id, true, default)).Code.Should().Be("cost_centre_other_company");
    }

    [Fact]
    public async Task Ensure_ThrowsAnInvalidOperationException_CarryingTheCode()
    {
        var w = await Seed();
        var act = () => new CostCentreRefValidator(w.Db).EnsureAsync(w.Tenant, w.CompanyA, w.OtherCompany.Id, true, default);
        var ex = (await act.Should().ThrowAsync<CostCentreRefException>()).Which;
        ex.Code.Should().Be("cost_centre_other_company");
        ex.Should().BeAssignableTo<InvalidOperationException>("every existing door already turns that into a 4xx");
    }
}
