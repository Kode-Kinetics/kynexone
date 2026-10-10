using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Companies hold the canonical ISO-2 country ("SA") and, unless someone typed one, no jurisdiction.
/// Statutory rules (and the overrides written against them) are keyed "SAU"/"KSA-mainland". Found
/// live on 2026-10-10: a Saudi company's Setup → GL &amp; rates → Statutory Rates tab said "No statutory
/// rules are published for this country yet", an override could not be requested, and payroll's
/// overtime/LOP reads silently fell back to hard-coded defaults — so an approved 1.75× override for
/// that company could never reach a payslip.
/// </summary>
public class StatutoryPackKeyTests
{
    [Theory]
    [InlineData("SA", "", "SAU", "KSA-mainland")]
    [InlineData("sa", null, "SAU", "KSA-mainland")]
    [InlineData("SAU", "", "SAU", "KSA-mainland")]
    [InlineData("SAU", "KSA-mainland", "SAU", "KSA-mainland")]
    [InlineData("AE", "UAE-DIFC", "ARE", "UAE-DIFC")]
    [InlineData("AE", "", "ARE", "UAE-mainland")]
    [InlineData("IN", "", "IND", "")]
    [InlineData("SAU", "OVR-test", "SAU", "OVR-test")]
    public void Normalize_MapsACompanyCountryToTheRulePackKey(string cc, string? jur, string expectedCc, string expectedJur)
        => Assert.Equal((expectedCc, expectedJur), StatutoryPackKey.Normalize(cc, jur));

    [Fact]
    public async Task ACompanyStoredAsSaWithNoJurisdiction_SeesItsRules_AndItsApprovedOverrideReachesPayrollReads()
    {
        var options = new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var tenant = Guid.NewGuid();
        var company = new Company
        {
            TenantId = tenant, LegalNameEn = "Dairy KSA", CountryCode = "SA", Jurisdiction = "",
            RegistrationNumber = "CR-1", DefaultCurrency = "SAR", IsActive = true,
        };
        var sibling = new Company
        {
            TenantId = tenant, LegalNameEn = "Bakery KSA", CountryCode = "SA", Jurisdiction = "",
            RegistrationNumber = "CR-2", DefaultCurrency = "SAR", IsActive = true,
        };
        await using (var seed = new ZayraDbContext(options))
        {
            seed.Add(StatutoryRuleSafeguardTests.Rule(null));
            seed.AddRange(company, sibling);
            await seed.SaveChangesAsync();
        }
        await using var db = new ZayraDbContext(options, new HttpContextAccessor { HttpContext = Http(tenant) });
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // Payroll passes the company's own values; the platform rule is found.
        Assert.Equal(1.5m, await new StatutoryRuleReader(db).GetDecimalAsync("SA", "", "ot.standard_multiplier", today, tenant));

        // The company's Statutory Rates tab lists the rule.
        var listed = Assert.IsType<OkObjectResult>(await Rates(db, tenant).ListStatutory(company.Id, null, null, null, default));
        Assert.Contains("ot.standard_multiplier", JsonSerializer.Serialize(listed.Value));

        // And the group view, which asks for the company's own country code, lists it too.
        var group = Assert.IsType<OkObjectResult>(await Rates(db, tenant).ListStatutory(null, "SA", null, null, default));
        Assert.Contains("ot.standard_multiplier", JsonSerializer.Serialize(group.Value));

        // The panel requests the override with the company's own country and no jurisdiction.
        var created = Assert.IsType<OkObjectResult>(await Rates(db, tenant).CreateStatutoryOverride(new StatutoryOverrideRequest(
            company.Id, "SA", "", "ot.standard_multiplier", "1.75", today, "Collective agreement", today.AddMonths(6)), default));
        var id = (Guid)created.Value!.GetType().GetProperty("Id")!.GetValue(created.Value)!;
        var stored = await db.CompanyStatutoryOverrides.IgnoreQueryFilters().SingleAsync(o => o.Id == id);
        Assert.Equal(("SAU", "KSA-mainland"), (stored.CountryCode, stored.Jurisdiction));

        Assert.IsType<OkObjectResult>(await StatutoryRuleSafeguardTests.ApproveAsSecondPerson(db, tenant, id));

        // Payroll's company-aware read now pays 1.75 for that company only.
        Assert.Equal(1.75m, await new StatutoryRuleReader(db).GetDecimalAsync("SA", "", "ot.standard_multiplier", today, tenant, default, company.Id));
        Assert.Equal(1.5m, await new StatutoryRuleReader(db).GetDecimalAsync("SA", "", "ot.standard_multiplier", today, tenant, default, sibling.Id));
    }

    private static DefaultHttpContext Http(Guid tenant) => new()
    {
        User = new(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenant.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, "Admin"), new Claim("permission", "payroll.rates.statutory_override"),
        }, "test")),
    };

    private static RatesController Rates(ZayraDbContext db, Guid tenant)
    {
        var reader = new StatutoryRuleReader(db);
        return new RatesController(db, reader, new StatutoryRateResolver(db, reader)) { ControllerContext = new() { HttpContext = Http(tenant) } };
    }
}
