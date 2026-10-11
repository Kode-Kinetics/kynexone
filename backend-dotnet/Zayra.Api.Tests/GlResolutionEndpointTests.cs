using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// GET /api/finance/gl/resolve — where each posting rule posts for a company, why, and what is wrong
/// with it. Source precedence, problem flags, tenant isolation and company scope.
/// </summary>
public class GlResolutionEndpointTests
{
    private static readonly string[] Read = { "finance.gl.read" };

    private static GlResolutionController Ctrl(GlPostingKit k, Guid tenantId, string[] perms, Guid[]? scopedCompanies = null)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
        };
        claims.AddRange(perms.Select(p => new Claim("permission", p)));
        if (scopedCompanies is not null)
            claims.Add(new Claim("entity_scope", JsonSerializer.Serialize(new { v = 2, m = "companies", c = scopedCompanies })));
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        return new GlResolutionController(k.Db) { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    private static async Task<Dictionary<string, JsonElement>> RowsAsync(GlResolutionController c, Guid? companyId)
    {
        var res = await c.Resolve(companyId, default);
        var body = JsonSerializer.SerializeToElement(res.Should().BeOfType<OkObjectResult>().Subject.Value);
        return body.GetProperty("rows").EnumerateArray().ToDictionary(r => r.GetProperty("driverKey").GetString()!);
    }

    [Fact]
    public async Task UnseededTenant_ResolvesEveryBuiltInRule_FromTheCompiledCatalog()
    {
        await using var k = await GlPostingKit.CreateAsync();
        var rows = await RowsAsync(Ctrl(k, k.TenantId, Read), k.CompanyId);

        rows.Keys.Should().BeEquivalentTo(PayrollGlCatalog.Drivers.Select(d => d.Key));
        rows.Values.Should().OnlyContain(r => r.GetProperty("source").GetString() == "BuiltIn");
        var att = rows["DED:ATTENDANCE"];
        att.GetProperty("account").GetProperty("code").GetString().Should().Be("5008");
        att.GetProperty("side").GetString().Should().Be("CR");
        att.GetProperty("existsInChart").GetBoolean().Should().BeFalse();
        att.GetProperty("problem").GetString().Should().Be("NotInChart");
        att.GetProperty("componentCodes").EnumerateArray().Select(x => x.GetString())
            .Should().Contain(new[] { "ATTENDANCE", "LOP_DEDUCTION" });
        rows["EARN:BASIC"].GetProperty("componentCodes").EnumerateArray().Select(x => x.GetString())
            .Should().Contain(new[] { "BASIC", PayrollArrearsComponents.Basic });
    }

    [Fact]
    public async Task SourcePrecedence_CompanyMapping_GroupMapping_CompanyDefault_GroupDefault()
    {
        await using var k = await GlPostingKit.CreateAsync();
        await k.SeedGlDefaultsAsync();
        var coAcct = new GlAccount { TenantId = k.TenantId, CompanyId = k.CompanyId, Code = "6001", Name = "Co Basic", AccountType = "Expense" };
        k.Db.GlAccounts.Add(coAcct);
        k.Db.GlAccountMappings.Add(new GlAccountMapping { TenantId = k.TenantId, CompanyId = k.CompanyId, DriverKey = "EARN:BASIC", AccountId = coAcct.Id });
        // A company driver row (no mapping) and a group custom driver (no mapping).
        k.Db.GlDrivers.Add(new GlDriver { TenantId = k.TenantId, CompanyId = k.CompanyId, Key = "EARN:WELLNESS", Label = "Wellness", Category = GlDriverCategories.Earning, PostingSide = "DR", DefaultCode = "6001", DefaultName = "Co Basic", MatchMode = "Exact", MatchComponentCode = "WELLNESS", SortOrder = 50 });
        k.Db.GlDrivers.Add(new GlDriver { TenantId = k.TenantId, CompanyId = null, Key = "EARN:GYM", Label = "Gym", Category = GlDriverCategories.Earning, PostingSide = "DR", DefaultCode = "5004", DefaultName = "Other Allowances Expense", MatchMode = "Exact", MatchComponentCode = "GYM", SortOrder = 51 });
        await k.Db.SaveChangesAsync();

        var rows = await RowsAsync(Ctrl(k, k.TenantId, Read), k.CompanyId);

        Source(rows["EARN:BASIC"]).Should().Be("CompanyMapping");
        rows["EARN:BASIC"].GetProperty("account").GetProperty("id").GetGuid().Should().Be(coAcct.Id);
        Source(rows["EARN:HOUSING"]).Should().Be("GroupMapping");
        Source(rows["EARN:WELLNESS"]).Should().Be("CompanyDriverDefault");
        rows["EARN:WELLNESS"].GetProperty("account").GetProperty("id").GetGuid().Should().Be(coAcct.Id);
        rows["EARN:WELLNESS"].GetProperty("componentCodes").EnumerateArray().Select(x => x.GetString()).Should().Equal("WELLNESS");
        Source(rows["EARN:GYM"]).Should().Be("GroupDriverDefault");
        rows.Values.Should().OnlyContain(r => r.GetProperty("problem").ValueKind == JsonValueKind.Null,
            "a freshly seeded tenant has nothing wrong with it");

        // The group view does not see the company's own rows.
        var group = await RowsAsync(Ctrl(k, k.TenantId, Read), null);
        Source(group["EARN:BASIC"]).Should().Be("GroupMapping");
        group.Should().NotContainKey("EARN:WELLNESS");
    }

    [Fact]
    public async Task ProblemFlags_InactiveAccount_InactiveDriver_Unmapped()
    {
        await using var k = await GlPostingKit.CreateAsync();
        await k.SeedGlDefaultsAsync();
        (await k.Db.GlAccounts.SingleAsync(a => a.TenantId == k.TenantId && a.Code == "5002")).IsActive = false;
        (await k.Db.GlDrivers.SingleAsync(d => d.TenantId == k.TenantId && d.Key == "EARN:OVERTIME")).IsActive = false;
        k.Db.GlDrivers.Add(new GlDriver { TenantId = k.TenantId, Key = "EARN:NOWHERE", Label = "Nowhere", Category = GlDriverCategories.Earning, PostingSide = "DR", DefaultCode = "", DefaultName = "", MatchMode = "Exact", MatchComponentCode = "NOWHERE", SortOrder = 60 });
        await k.Db.SaveChangesAsync();

        var rows = await RowsAsync(Ctrl(k, k.TenantId, Read), k.CompanyId);

        var housing = rows["EARN:HOUSING"];
        housing.GetProperty("problem").GetString().Should().Be("InactiveAccount");
        housing.GetProperty("isActive").GetBoolean().Should().BeFalse();
        housing.GetProperty("account").GetProperty("code").GetString().Should().Be("5002", "report the account that was chosen, not a silent fallback");
        rows["EARN:OVERTIME"].GetProperty("problem").GetString().Should().Be("InactiveDriver");
        rows["EARN:NOWHERE"].GetProperty("problem").GetString().Should().Be("Unmapped");
        rows["EARN:NOWHERE"].GetProperty("existsInChart").GetBoolean().Should().BeFalse();
        rows["EARN:BASIC"].GetProperty("problem").ValueKind.Should().Be(JsonValueKind.Null);
        rows["EARN:BASIC"].GetProperty("isActive").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task TenantIsolation_AnotherTenantsMappingsAndCompaniesAreInvisible()
    {
        await using var k = await GlPostingKit.CreateAsync();
        await k.SeedGlDefaultsAsync();
        // A second tenant in the same database remaps EARN:BASIC.
        var otherTenant = Guid.NewGuid();
        var foreign = new GlAccount { TenantId = otherTenant, CompanyId = null, Code = "7777", Name = "Foreign", AccountType = "Expense" };
        k.Db.GlAccounts.Add(foreign);
        k.Db.GlAccountMappings.Add(new GlAccountMapping { TenantId = otherTenant, CompanyId = null, DriverKey = "EARN:BASIC", AccountId = foreign.Id });
        await k.Db.SaveChangesAsync();

        var rows = await RowsAsync(Ctrl(k, k.TenantId, Read), k.CompanyId);
        rows["EARN:BASIC"].GetProperty("account").GetProperty("code").GetString().Should().Be("5001");

        // Our company id asked for under the other tenant reads as not found, not as our data.
        (await Ctrl(k, otherTenant, Read).Resolve(k.CompanyId, default)).Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task CompanyScope_ACompanyUserReadsOnlyTheirOwnCompany()
    {
        await using var k = await GlPostingKit.CreateAsync();
        var other = await k.AddCompanyAsync();
        var scoped = Ctrl(k, k.TenantId, Read, new[] { k.CompanyId });

        (await scoped.Resolve(k.CompanyId, default)).Should().BeOfType<OkObjectResult>();
        (await scoped.Resolve(other, default)).Should().BeOfType<ForbidResult>("company A's user cannot read company B");
        (await scoped.Resolve(null, default)).Should().BeOfType<ForbidResult>("the group view needs a group-level caller");
        (await Ctrl(k, k.TenantId, Array.Empty<string>()).Resolve(k.CompanyId, default)).Should().BeOfType<ForbidResult>();
        (await Ctrl(k, k.TenantId, new[] { "finance.gl.manage" }).Resolve(k.CompanyId, default)).Should().BeOfType<OkObjectResult>();
    }

    private static string? Source(JsonElement row) => row.GetProperty("source").GetString();
}
