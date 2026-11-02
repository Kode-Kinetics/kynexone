using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>Configuration entities (ICompanyScoped, not operational) are protected on the write side by the
/// DbContext, so a controller that forgets a company check cannot leak across companies.</summary>
public class ConfigCompanyWriteGuardTests
{
    private static LeaveType Type(ScopeSodWorld w, Guid? company, string code) =>
        new() { TenantId = w.Tenant, CompanyId = company, Code = code, NameEn = code, IsActive = true };

    [Fact]
    public async Task CompanyAdmin_CannotCreateConfigInAnotherCompany()
    {
        var w = await ScopeSodWorld.CreateAsync();
        await using var db = w.DbFor(w.CompanyAdmin(w.CompanyA));
        db.LeaveTypes.Add(Type(w, w.CompanyB, "XB"));
        await FluentActions.Awaiting(() => db.SaveChangesAsync()).Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*company_scope_denied*");
    }

    [Fact]
    public async Task CompanyAdmin_NewConfigWithNoCompany_IsStampedToTheirCompany()
    {
        var w = await ScopeSodWorld.CreateAsync();
        var t = Type(w, null, "XN");
        await using (var db = w.DbFor(w.CompanyAdmin(w.CompanyA))) { db.LeaveTypes.Add(t); await db.SaveChangesAsync(); }
        await using var check = w.Plain();
        (await check.LeaveTypes.AsNoTracking().SingleAsync(x => x.Id == t.Id)).CompanyId.Should().Be(w.CompanyA);
    }

    [Fact]
    public async Task CompanyAdmin_CannotEditOrDeleteATenantWideConfigRow()
    {
        var w = await ScopeSodWorld.CreateAsync();
        var shared = Type(w, null, "SH");
        await using (var seed = w.Plain()) { seed.LeaveTypes.Add(shared); await seed.SaveChangesAsync(); }

        await using (var db = w.DbFor(w.CompanyAdmin(w.CompanyA)))
        {
            var row = await db.LeaveTypes.SingleAsync(x => x.Id == shared.Id); // visible: tenant-wide rows are readable
            row.NameEn = "hijacked";
            await FluentActions.Awaiting(() => db.SaveChangesAsync()).Should().ThrowAsync<UnauthorizedAccessException>();
        }
        await using (var db = w.DbFor(w.CompanyAdmin(w.CompanyA)))
        {
            var row = await db.LeaveTypes.SingleAsync(x => x.Id == shared.Id);
            db.LeaveTypes.Remove(row);
            await FluentActions.Awaiting(() => db.SaveChangesAsync()).Should().ThrowAsync<UnauthorizedAccessException>();
        }
    }

    [Fact]
    public async Task CompanyAdmin_CannotReassignTheirRowToAnotherCompany()
    {
        var w = await ScopeSodWorld.CreateAsync();
        var own = Type(w, w.CompanyA, "OW");
        await using (var seed = w.Plain()) { seed.LeaveTypes.Add(own); await seed.SaveChangesAsync(); }
        await using var db = w.DbFor(w.CompanyAdmin(w.CompanyA));
        var row = await db.LeaveTypes.SingleAsync(x => x.Id == own.Id);
        row.CompanyId = w.CompanyB;
        await FluentActions.Awaiting(() => db.SaveChangesAsync()).Should().ThrowAsync<InvalidOperationException>().WithMessage("*company_reassignment_blocked*");
    }

    [Fact]
    public async Task CompanyAdmin_CanEditTheirOwnCompanysConfig()
    {
        var w = await ScopeSodWorld.CreateAsync();
        var own = Type(w, w.CompanyA, "OK");
        await using (var seed = w.Plain()) { seed.LeaveTypes.Add(own); await seed.SaveChangesAsync(); }
        await using var db = w.DbFor(w.CompanyAdmin(w.CompanyA));
        (await db.LeaveTypes.SingleAsync(x => x.Id == own.Id)).NameEn = "renamed";
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task GroupAdmin_CanEditTenantWideAndAnyCompanyConfig()
    {
        var w = await ScopeSodWorld.CreateAsync();
        var shared = Type(w, null, "G1"); var b = Type(w, w.CompanyB, "G2");
        await using (var seed = w.Plain()) { seed.LeaveTypes.AddRange(shared, b); await seed.SaveChangesAsync(); }
        await using var db = w.DbFor(w.GroupAdmin());
        foreach (var t in await db.LeaveTypes.ToListAsync()) t.NameEn += "!";
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task SingleCompanyTenant_IsUnaffected()
    {
        var w = await ScopeSodWorld.CreateAsync();
        await using (var seed = w.Plain())
        {
            seed.Companies.Remove(await seed.Companies.SingleAsync(c => c.Id == w.CompanyB));
            await seed.SaveChangesAsync();
        }
        var shared = Type(w, null, "S1");
        await using (var seed = w.Plain()) { seed.LeaveTypes.Add(shared); await seed.SaveChangesAsync(); }
        await using var db = w.DbFor(w.CompanyAdmin(w.CompanyA));
        (await db.LeaveTypes.SingleAsync(x => x.Id == shared.Id)).NameEn = "fine";
        await db.SaveChangesAsync();
    }

    [Fact]
    public void TheExemptListIsExactlyTheReviewedLogsAndSystemOutput()
    {
        ZayraDbContext.ConfigScopeGuardExempt.Select(t => t.Name).Should().BeEquivalentTo(new[] { "AuditLog", "AdminAuditLog", "AIInsight" },
            "adding a config entity to the exempt list switches off its write-side company guard; that needs a deliberate, reviewed change here");
        ZayraDbContext.ConfigScopeGuardExempt.Should().OnlyContain(t => typeof(ICompanyScoped).IsAssignableFrom(t));
    }

    [Fact]
    public void EveryCompanyScopedConfigEntityIsCoveredByTheGuard()
    {
        var covered = typeof(ZayraDbContext).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ICompanyScoped).IsAssignableFrom(t))
            .Where(t => !typeof(ICompanyScopedOperational).IsAssignableFrom(t))
            .Where(t => !ZayraDbContext.ConfigScopeGuardExempt.Contains(t))
            .Select(t => t.Name).ToList();
        covered.Should().Contain(new[] { "LeaveType", "LeavePolicy", "PayComponent", "CompanyTaxPolicy", "GlAccount", "SalaryStructure", "BenefitPlan" });
    }
}
