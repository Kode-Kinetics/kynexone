using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Country payroll keys: every create/remove is audited, and a key payroll depends on
/// (weekend_days feeds the work week) is not removed while a payroll run is still open.
/// </summary>
public class SetupRemovalSafetyCountryRuleTests
{
    private static ZayraDbContext Db() => new(new DbContextOptionsBuilder<ZayraDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static TenantAdminController Ctl(ZayraDbContext db, Guid tenant, bool group = true)
    {
        var claims = new List<Claim> { new("tenant_id", tenant.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        if (group) claims.Add(new Claim("is_group_scope", "true"));
        else claims.Add(new Claim("entity_access", System.Text.Json.JsonSerializer.Serialize(new { c = Guid.NewGuid(), r = "Admin" })));
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        return new TenantAdminController(db) { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    private static CountryPayrollRule Rule(Guid t, string key, string cc = "SA") =>
        new() { TenantId = t, CountryCode = cc, RuleKey = key, RuleValue = "5,6" };

    private static string? Code(IActionResult r) => r is ConflictObjectResult c
        ? c.Value!.GetType().GetProperty("code")!.GetValue(c.Value)?.ToString() : null;

    [Fact]
    public async Task Delete_WeekendDays_WithOpenPayrollRun_IsRefused()
    {
        await using var db = Db(); var t = Guid.NewGuid();
        var r = Rule(t, "weekend_days"); db.CountryPayrollRules.Add(r);
        db.PayrollRuns.Add(new PayrollRun { TenantId = t, Year = 2026, Month = 10, Status = "Draft" });
        await db.SaveChangesAsync();

        Code(await Ctl(db, t).DeleteCountryRule(r.Id, default)).Should().Be("COUNTRY_RULE_IN_USE");
        (await db.CountryPayrollRules.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Delete_WeekendDays_WithOnlyLockedRuns_Deletes_AndAudits()
    {
        await using var db = Db(); var t = Guid.NewGuid();
        var r = Rule(t, "weekend_days"); db.CountryPayrollRules.Add(r);
        db.PayrollRuns.Add(new PayrollRun { TenantId = t, Year = 2026, Month = 9, Status = "Locked" });
        await db.SaveChangesAsync();

        (await Ctl(db, t).DeleteCountryRule(r.Id, default)).Should().BeOfType<NoContentResult>();

        (await db.CountryPayrollRules.CountAsync()).Should().Be(0);
        var a = await db.AdminAuditLogs.SingleAsync();
        a.EntityType.Should().Be("CountryPayrollRule"); a.Action.Should().Be("Deleted");
        a.OldValuesJson.Should().Contain("weekend_days");
    }

    [Fact]
    public async Task Delete_OtherKey_IsAllowedEvenWithOpenRun_AndAudited()
    {
        await using var db = Db(); var t = Guid.NewGuid();
        var r = Rule(t, "eosb_rate"); db.CountryPayrollRules.Add(r);
        db.PayrollRuns.Add(new PayrollRun { TenantId = t, Year = 2026, Month = 10, Status = "Draft" });
        await db.SaveChangesAsync();

        (await Ctl(db, t).DeleteCountryRule(r.Id, default)).Should().BeOfType<NoContentResult>();
        (await db.AdminAuditLogs.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task DeletePack_IncludingWeekendDays_WithOpenRun_IsRefused()
    {
        await using var db = Db(); var t = Guid.NewGuid();
        db.CountryPayrollRules.AddRange(Rule(t, "weekend_days"), Rule(t, "eosb_rate"));
        db.PayrollRuns.Add(new PayrollRun { TenantId = t, Year = 2026, Month = 10, Status = "Processed" });
        await db.SaveChangesAsync();

        Code(await Ctl(db, t).DeleteCountryPack("sa", default)).Should().Be("COUNTRY_RULE_IN_USE");
        (await db.CountryPayrollRules.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task DeletePack_Audits_OneEntryNamingTheKeys()
    {
        await using var db = Db(); var t = Guid.NewGuid();
        db.CountryPayrollRules.AddRange(Rule(t, "weekend_days"), Rule(t, "eosb_rate"), Rule(t, "eosb_rate", "AE"));
        await db.SaveChangesAsync();

        (await Ctl(db, t).DeleteCountryPack("sa", default)).Should().BeOfType<OkObjectResult>();

        (await db.CountryPayrollRules.CountAsync()).Should().Be(1);
        var a = await db.AdminAuditLogs.SingleAsync();
        a.Action.Should().Be("PackDeleted"); a.OldValuesJson.Should().Contain("weekend_days").And.Contain("eosb_rate");
    }

    [Fact]
    public async Task Delete_WithoutGroupScope_IsForbidden_AndOtherTenantNotFound()
    {
        await using var db = Db(); var t = Guid.NewGuid();
        var mine = Rule(t, "eosb_rate"); var foreign = Rule(Guid.NewGuid(), "eosb_rate");
        db.CountryPayrollRules.AddRange(mine, foreign); await db.SaveChangesAsync();

        (await Ctl(db, t, group: false).DeleteCountryRule(mine.Id, default)).Should().BeOfType<ForbidResult>();
        (await Ctl(db, t).DeleteCountryRule(foreign.Id, default)).Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Create_Audits()
    {
        await using var db = Db(); var t = Guid.NewGuid();
        var r = await Ctl(db, t).CreateCountryRule(new CreateCountryRuleRequest("sa", "work_note", "hello", "string", null, null, null, null), default);
        r.Should().BeOfType<CreatedResult>();
        (await db.AdminAuditLogs.SingleAsync()).Action.Should().Be("Created");
    }
}
