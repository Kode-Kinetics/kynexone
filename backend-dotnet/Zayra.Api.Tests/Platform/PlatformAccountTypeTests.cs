using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Platform;

/// <summary>Phase 1B (A): platform-admin control over the tenant product account type.</summary>
public class PlatformAccountTypeTests : PlatformTestBase
{
    [Fact]
    public async Task SetAccountType_UpgradesToGroup_AndIsAudited()
    {
        await using var db = CreateDb();
        var controller = CreateController(db);
        var tenant = new Tenant { Name = "AT Corp", Slug = "at-corp" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var result = await controller.SetAccountType(tenant.Id, new SetAccountTypeRequest(TenantAccountTypes.Group), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        (await db.Tenants.FindAsync(tenant.Id))!.AccountType.Should().Be(TenantAccountTypes.Group);
        db.AdminAuditLogs.Should().Contain(a => a.Action == "AccountTypeChanged" && a.TenantId == tenant.Id);
    }

    [Fact]
    public async Task SetAccountType_RejectsInvalidValue()
    {
        await using var db = CreateDb();
        var controller = CreateController(db);
        var tenant = new Tenant { Name = "AT Bad", Slug = "at-bad" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var result = await controller.SetAccountType(tenant.Id, new SetAccountTypeRequest("HoldingMegaCorp"), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task SetAccountType_DowngradeBlocked_WhileMultipleActiveCompaniesExist()
    {
        await using var db = CreateDb();
        var controller = CreateController(db);
        var tenant = new Tenant { Name = "AT Group", Slug = "at-group", AccountType = TenantAccountTypes.Group };
        db.Tenants.Add(tenant);
        db.Companies.AddRange(
            new Company { TenantId = tenant.Id, LegalNameEn = "AT One", IsActive = true },
            new Company { TenantId = tenant.Id, LegalNameEn = "AT Two", IsActive = true });
        await db.SaveChangesAsync();

        var result = await controller.SetAccountType(tenant.Id, new SetAccountTypeRequest(TenantAccountTypes.SingleCompany), CancellationToken.None);

        var conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
        conflict.Value!.ToString().Should().Contain("multiple_active_companies");
        (await db.Tenants.FindAsync(tenant.Id))!.AccountType.Should().Be(TenantAccountTypes.Group,
            "the downgrade must not have been applied");
    }

    [Fact]
    public async Task CreateTenant_AcceptsAccountType_AndDefaultsToSingleCompany()
    {
        await using var db = CreateDb();
        var controller = CreateController(db);

        var groupReq = new CreateTenantRequest("Group Co", "group-co", "admin@group.co", null, "Password123!x",
            null, null, null, null, null, null, null, null, AccountType: TenantAccountTypes.Group);
        (await controller.CreateTenant(groupReq, CancellationToken.None)).Should().BeOfType<CreatedAtActionResult>();
        (await db.Tenants.SingleAsync(t => t.Slug == "group-co")).AccountType.Should().Be(TenantAccountTypes.Group);

        var plainReq = new CreateTenantRequest("Plain Co", "plain-co", "admin@plain.co", null, "Password123!x",
            null, null, null, null, null, null, null, null);
        (await controller.CreateTenant(plainReq, CancellationToken.None)).Should().BeOfType<CreatedAtActionResult>();
        (await db.Tenants.SingleAsync(t => t.Slug == "plain-co")).AccountType.Should().Be(TenantAccountTypes.SingleCompany);
    }
}
