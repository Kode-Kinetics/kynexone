using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public class StatutoryRuleSafeguardTests
{
    private static DateTime Tomorrow => DateTime.UtcNow.Date.AddDays(1);
    internal static StatutoryRule Rule(Guid? tenant, string key = "ot.standard_multiplier", string value = "1.5") => new()
    {
        TenantId = tenant, CountryCode = "SAU", Jurisdiction = "KSA-mainland", RuleKey = key,
        RuleValue = value, DataType = "decimal", EffectiveFrom = DateTime.UtcNow.Date.AddDays(-10)
    };
    private static ZayraDbContext Db() => new(new DbContextOptionsBuilder<ZayraDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static DefaultHttpContext Http(Guid tenant) => new()
    {
        User = new(new ClaimsIdentity(new[] { new Claim("tenant_id", tenant.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, "Admin"),
            new Claim("permission", "payroll.rates.statutory_override") }, "test"))
    };
    internal static StatutoryRulesController Controller(ZayraDbContext db, Guid tenant) => new(db)
    { ControllerContext = new() { HttpContext = Http(tenant) } };
    internal static CreateStatutoryRuleRequest Request(DateTime from, DateTime? to = null, string key = "ot.standard_multiplier")
        => new("SAU", "KSA-mainland", key, "1.75", "decimal", "Approved company policy", from, to);

    [Fact]
    public async Task AuthenticatedListIncludesPlatformAndOwnTenantOnly()
    {
        var options = new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var tenant = Guid.NewGuid();
        await using (var seed = new ZayraDbContext(options))
        {
            seed.AddRange(Rule(null), Rule(tenant), Rule(Guid.NewGuid()));
            await seed.SaveChangesAsync();
        }
        await using var db = new ZayraDbContext(options, new HttpContextAccessor { HttpContext = Http(tenant) });
        var result = await Controller(db, tenant).List(null, null, default);
        var rows = Assert.IsAssignableFrom<IReadOnlyList<StatutoryRuleDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(2, rows.Count);
        Assert.Single(rows, r => !r.IsTenantOverride);
    }

    [Theory]
    [InlineData("gpssa.national_employee_rate")]
    [InlineData("grsia.national_employer_rate")]
    [InlineData("dews.tier1_rate")]
    [InlineData("leave.annual_base_days")]
    public async Task PlatformOnlyKeysCannotCreateOrSupersedeIgnoredOverrides(string key)
    {
        await using var db = Db(); var tenant = Guid.NewGuid(); var prior = Rule(tenant, key);
        db.AddRange(Rule(null, key), prior); await db.SaveChangesAsync();
        var controller = Controller(db, tenant);
        Assert.IsType<UnprocessableEntityObjectResult>((await controller.Create(Request(Tomorrow, key: key), default)).Result);
        Assert.IsType<UnprocessableEntityObjectResult>((await controller.Update(prior.Id, new("0.1", "Reviewed", Tomorrow, null), default)).Result);
        Assert.Null(prior.EffectiveTo);
        Assert.Empty(db.AuditLogs);
    }

    [Fact]
    public async Task InvalidDatesAndOverlapsAreRejectedWithoutWrites()
    {
        await using var db = Db(); var tenant = Guid.NewGuid(); db.Add(Rule(null)); await db.SaveChangesAsync();
        var controller = Controller(db, tenant);
        foreach (var req in new[] { Request(default), Request(Tomorrow.AddDays(-1)), Request(Tomorrow, Tomorrow),
            Request(Tomorrow, Tomorrow.AddDays(-1)), Request(Tomorrow.AddHours(1)), Request(DateTime.SpecifyKind(Tomorrow, DateTimeKind.Unspecified)) })
            Assert.IsType<BadRequestObjectResult>((await controller.Create(req, default)).Result);
        Assert.IsType<CreatedAtActionResult>((await controller.Create(Request(Tomorrow, Tomorrow.AddDays(10)), default)).Result);
        Assert.IsType<ConflictObjectResult>((await controller.Create(Request(Tomorrow.AddDays(2)), default)).Result);
        Assert.IsType<CreatedAtActionResult>((await controller.Create(Request(Tomorrow.AddDays(10)), default)).Result);
        Assert.Equal(2, await db.StatutoryRules.CountAsync(r => r.TenantId == tenant));
    }

    [Fact]
    public async Task SupersedePreservesHistoricalResolutionAndRejectsStaleVersion()
    {
        await using var db = Db(); var tenant = Guid.NewGuid(); var prior = Rule(tenant);
        db.AddRange(Rule(null), prior); await db.SaveChangesAsync(); var controller = Controller(db, tenant);
        var request = new UpdateStatutoryRuleRequest("2", "Reviewed enhancement", Tomorrow, null);
        Assert.IsType<OkObjectResult>((await controller.Update(prior.Id, request, default)).Result);
        Assert.IsType<ConflictObjectResult>((await controller.Update(prior.Id, request with { EffectiveFrom = Tomorrow.AddDays(1) }, default)).Result);
        var reader = new StatutoryRuleReader(db);
        Assert.Equal(1.5m, await reader.GetDecimalAsync("SAU", "KSA-mainland", prior.RuleKey, DateOnly.FromDateTime(Tomorrow.AddDays(-1)), tenant));
        Assert.Equal(2m, await reader.GetDecimalAsync("SAU", "KSA-mainland", prior.RuleKey, DateOnly.FromDateTime(Tomorrow), tenant));
    }

    [Fact]
    public async Task FutureAndForeignVersionsCannotBeRetired()
    {
        await using var db = Db(); var tenant = Guid.NewGuid();
        var future = Rule(tenant); future.EffectiveFrom = Tomorrow.AddDays(3);
        var foreign = Rule(Guid.NewGuid());
        db.AddRange(future, foreign); await db.SaveChangesAsync();
        var controller = Controller(db, tenant);
        Assert.IsType<ConflictObjectResult>(await controller.Delete(future.Id, default));
        Assert.IsType<NotFoundResult>(await controller.Delete(foreign.Id, default));
        Assert.Null(future.EffectiveTo); Assert.Null(foreign.EffectiveTo);
        Assert.Empty(db.AuditLogs);
    }

    [Fact]
    public async Task RetirementKeepsHistoryAndIsIdempotent()
    {
        await using var db = Db(); var tenant = Guid.NewGuid(); var prior = Rule(tenant, value: "2");
        db.AddRange(Rule(null), prior); await db.SaveChangesAsync(); var controller = Controller(db, tenant);
        Assert.IsType<NoContentResult>(await controller.Delete(prior.Id, default));
        Assert.IsType<NoContentResult>(await controller.Delete(prior.Id, default));
        Assert.NotNull(await db.StatutoryRules.FindAsync(prior.Id));
        Assert.Equal(Tomorrow, prior.EffectiveTo);
        Assert.Single(db.AuditLogs.Where(a => a.Action == "statutory_rule.override.retired"));
        var reader = new StatutoryRuleReader(db);
        Assert.Equal(2m, await reader.GetDecimalAsync("SAU", "KSA-mainland", prior.RuleKey, DateOnly.FromDateTime(Tomorrow.AddDays(-1)), tenant));
        Assert.Equal(1.5m, await reader.GetDecimalAsync("SAU", "KSA-mainland", prior.RuleKey, DateOnly.FromDateTime(Tomorrow), tenant));
    }
}

[Trait("Category", "Integration")]
[Collection("Integration")]
public class StatutoryRuleSafeguardPostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ConcurrentOverlappingCreatesProduceOneVersionAndOneConflict()
    {
        Guid tenant;
        await using (var seed = fixture.CreateDb())
        {
            tenant = await PostgresFixture.SeedMinimalTenant(seed);
            if (!await seed.StatutoryRules.AnyAsync(r => r.TenantId == null && r.RuleKey == "ot.standard_multiplier" && r.CountryCode == "SAU" && r.Jurisdiction == "KSA-mainland"))
            {
                seed.Add(StatutoryRuleSafeguardTests.Rule(null)); await seed.SaveChangesAsync();
            }
        }
        async Task<ActionResult<StatutoryRuleDto>> Apply()
        {
            await using var db = fixture.CreateDb();
            return await StatutoryRuleSafeguardTests.Controller(db, tenant).Create(
                StatutoryRuleSafeguardTests.Request(DateTime.UtcNow.Date.AddDays(1)), default);
        }
        var results = await Task.WhenAll(Apply(), Apply());
        Assert.Single(results, r => r.Result is CreatedAtActionResult);
        Assert.Single(results, r => r.Result is ConflictObjectResult);
        await using var verify = fixture.CreateDb();
        Assert.Equal(1, await verify.StatutoryRules.CountAsync(r => r.TenantId == tenant));
        Assert.Equal(1, await verify.AuditLogs.CountAsync(r => r.TenantId == tenant && r.Action == "statutory_rule.override.created"));
    }
}
