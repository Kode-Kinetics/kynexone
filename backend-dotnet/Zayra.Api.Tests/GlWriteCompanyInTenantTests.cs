using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using static Zayra.Api.Tests.CostAccountingKit;

namespace Zayra.Api.Tests;

/// <summary>A group-scope caller writing chart-of-accounts data must name a company of their own tenant; anything else is not found and nothing is written.</summary>
public class GlWriteCompanyInTenantTests
{
    private static readonly string[] Manage = { "finance.gl.read", "finance.gl.manage" };

    [Fact]
    public async Task CreatingAnAccount_ForACompanyOutsideTheTenant_IsNotFound_AndWritesNothing()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var foreign = Guid.NewGuid();

        var result = await Gl(db, tid, Manage).CreateAccount(foreign, new GlAccountRequest("9990", "Orphan", "Expense"), default);

        Status(result).Should().Be(404);
        (await db.GlAccounts.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CreatingAnAccount_ForACompanyOfTheTenant_StillWorks()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid);

        var result = await Gl(db, tid, Manage).CreateAccount(co.Id, new GlAccountRequest("9990", "Mine", "Expense"), default);

        Status(result).Should().Be(200);
    }

    [Fact]
    public async Task SavingMappings_ForACompanyOutsideTheTenant_IsNotFound()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;

        var result = await Gl(db, Guid.NewGuid(), Manage).SetMappings(Guid.NewGuid(), new GlMappingsBody { Mappings = new List<GlMappingRequest>() }, default);

        Status(result).Should().Be(404);
    }

    [Fact]
    public async Task ClosingAPeriod_ForACompanyOutsideTheTenant_IsNotFound_AndWritesNothing()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;

        var result = await Gl(db, Guid.NewGuid(), Manage).ClosePeriod("2026-07", Guid.NewGuid(), null, default);

        Status(result).Should().Be(404);
        (await db.GlPeriodCloses.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }
}
