using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;
using static Zayra.Api.Tests.OvertimeFixKit;

namespace Zayra.Api.Tests;

/// <summary>
/// OVT-07 (P1): the policy and type endpoints stored whatever they were given - a 900x multiplier, a
/// negative fixed rate, a 100,000-hour month, a free-text rounding rule, a null code (a 500), a duplicate
/// type code (a database error), and no audit trail. They now validate, answer 400/409, and audit.
/// (There is no policy update endpoint in this controller, so "update" auditing has nothing to attach to.)
/// </summary>
public class OvertimePolicyValidationTests
{
    private static readonly string[] Adm = { "Admin" };

    private static OvertimePolicyRequest P(
        string? code = "P-1", string? name = "Policy", string? basis = "BasicSalary", decimal fixedRate = 0m,
        int monthlyHours = 240, int min = 30, int maxDay = 240, int cap = 3600, string? rounding = "Nearest15",
        decimal regular = 1.5m, decimal weekend = 2.0m, decimal holiday = 2.0m) =>
        new(code!, name!, basis, fixedRate, monthlyHours, min, maxDay, cap, rounding, true, true, regular, weekend, holiday);

    private static async Task<(World W, OvertimeController C)> NewAsync()
    {
        var w = await SeedAsync();
        w.Db.OvertimePolicies.RemoveRange(w.Db.OvertimePolicies); // the seeded policy would only add noise to the counts
        await w.Db.SaveChangesAsync();
        return (w, Controller(w, Guid.NewGuid(), Adm));
    }

    [Fact]
    public async Task AValidPolicy_IsCreated_WithOneAuditRowCarryingOldAndNewValues()
    {
        var (w, c) = await NewAsync();

        var result = await c.CreatePolicy(P(), CancellationToken.None);

        result.Result.Should().BeOfType<CreatedResult>();
        var audit = await w.Db.OvertimeAuditLogs.SingleAsync(x => x.Action == "overtime.policy.created");
        audit.MetadataJson.Should().Contain("\"old\":null").And.Contain("\"new\"").And.Contain("P-1").And.Contain("Nearest15");
        audit.UserId.Should().NotBeNull();
    }

    [Theory]
    [InlineData(6.0, 2.0, 2.0)]      // regular above the 5x cap
    [InlineData(1.5, 900.0, 2.0)]    // weekend absurd
    [InlineData(1.5, 2.0, 5.01)]     // holiday just over
    [InlineData(-1.5, 2.0, 2.0)]     // negative
    [InlineData(0.5, 2.0, 2.0)]      // below ordinary pay
    public async Task MultipliersOutsideTheSaneBand_AreRefused(double regular, double weekend, double holiday)
    {
        var (w, c) = await NewAsync();

        var result = await c.CreatePolicy(P(regular: (decimal)regular, weekend: (decimal)weekend, holiday: (decimal)holiday), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        (await w.Db.OvertimePolicies.CountAsync()).Should().Be(0);
        (await w.Db.OvertimeMultipliers.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task AMultiplierBelowTheJurisdictionsStatutoryFloor_IsRefused_AndAtTheFloorIsAccepted()
    {
        var w = await SeedAsync();
        w.Db.OvertimePolicies.RemoveRange(w.Db.OvertimePolicies);
        await StatutoryRuleSeeder.SeedAsync(w.Db, NullLogger.Instance);
        w.Db.Companies.Add(new Company { TenantId = w.TenantId, LegalNameEn = "KSA Co", TradeName = "KSA Co", CountryCode = Zayra.Api.Application.CountryPack.CountryCodes.Saudi, Jurisdiction = Zayra.Api.Application.CountryPack.Jurisdictions.KsaMainland, DefaultCurrency = "SAR", IsActive = true });
        await w.Db.SaveChangesAsync();
        var c = Controller(w, Guid.NewGuid(), Adm);

        var below = await c.CreatePolicy(P(weekend: 1.5m), CancellationToken.None);
        below.Result.Should().BeOfType<BadRequestObjectResult>("KSA's seeded rest-day floor is 2.0");
        Message(below.Result).Should().Contain("2");

        var atFloor = await c.CreatePolicy(P(weekend: 2.0m), CancellationToken.None);
        atFloor.Result.Should().BeOfType<CreatedResult>();
    }

    [Theory]
    [InlineData(-1, 240, 30, 240, 3600)]
    [InlineData(1000000, 240, 30, 240, 3600)]
    public async Task FixedHourlyRate_IsBounded(int fixedRate, int hours, int min, int maxDay, int cap)
    {
        var (w, c) = await NewAsync();
        (await c.CreatePolicy(P(fixedRate: fixedRate, monthlyHours: hours, min: min, maxDay: maxDay, cap: cap), CancellationToken.None))
            .Result.Should().BeOfType<BadRequestObjectResult>();
        (await w.Db.OvertimePolicies.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(745, 30, 240, 3600)]      // monthly hours above a 31-day month
    [InlineData(-5, 30, 240, 3600)]
    [InlineData(240, -1, 240, 3600)]      // negative minimum
    [InlineData(240, 30, 1441, 3600)]     // more than a day
    [InlineData(240, 30, -240, 3600)]
    [InlineData(240, 30, 240, 44641)]     // more than 744 h of overtime a month
    [InlineData(240, 30, 240, -3600)]
    [InlineData(240, 300, 240, 3600)]     // minimum above the daily maximum
    public async Task QuantityLimits_AreBounded(int monthlyHours, int min, int maxDay, int cap)
    {
        var (w, c) = await NewAsync();
        (await c.CreatePolicy(P(monthlyHours: monthlyHours, min: min, maxDay: maxDay, cap: cap), CancellationToken.None))
            .Result.Should().BeOfType<BadRequestObjectResult>();
        (await w.Db.OvertimePolicies.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task TheUpperBounds_Themselves_AreAccepted()
    {
        var (_, c) = await NewAsync();
        (await c.CreatePolicy(P(monthlyHours: 744, maxDay: 1440, cap: 44640, min: 15, regular: 5m, weekend: 5m, holiday: 5m, fixedRate: 10000m), CancellationToken.None))
            .Result.Should().BeOfType<CreatedResult>();
    }

    [Theory]
    [InlineData("Bogus", null)]
    [InlineData(null, "Whatever")]
    [InlineData("nearest15", null)]
    public async Task RoundingRule_AndHourlyRateBasis_MustComeFromTheAllowedLists(string? rounding, string? basis)
    {
        var (w, c) = await NewAsync();
        (await c.CreatePolicy(P(rounding: rounding ?? "Nearest15", basis: basis ?? "BasicSalary"), CancellationToken.None))
            .Result.Should().BeOfType<BadRequestObjectResult>();
        (await w.Db.OvertimePolicies.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("None")]
    [InlineData("Nearest15")]
    [InlineData("Up15")]
    [InlineData("Down15")]
    [InlineData("Nearest30")]
    public async Task EveryDocumentedRoundingRule_IsAccepted(string rule)
    {
        var (_, c) = await NewAsync();
        (await c.CreatePolicy(P(rounding: rule), CancellationToken.None)).Result.Should().BeOfType<CreatedResult>();
    }

    [Theory]
    [InlineData("BasicSalary")]
    [InlineData("GrossSalary")]
    [InlineData("FixedHourlyRate")]
    public async Task EveryDocumentedHourlyRateBasis_IsAccepted(string basis)
    {
        var (_, c) = await NewAsync();
        (await c.CreatePolicy(P(basis: basis, fixedRate: basis == "FixedHourlyRate" ? 50m : 0m), CancellationToken.None)).Result.Should().BeOfType<CreatedResult>();
    }

    [Theory]
    [InlineData(null, "Name")]
    [InlineData("", "Name")]
    [InlineData("  ", "Name")]
    [InlineData("CODE", null)]
    [InlineData("CODE", "   ")]
    public async Task APolicy_WithoutACodeOrName_Is400_NotAnException(string? code, string? name)
    {
        var (w, c) = await NewAsync();
        (await c.CreatePolicy(P(code: code, name: name), CancellationToken.None)).Result.Should().BeOfType<BadRequestObjectResult>();
        (await w.Db.OvertimePolicies.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ADuplicatePolicyCode_Is409()
    {
        var (_, c) = await NewAsync();
        (await c.CreatePolicy(P(), CancellationToken.None)).Result.Should().BeOfType<CreatedResult>();
        (await c.CreatePolicy(P(), CancellationToken.None)).Result.Should().BeOfType<ConflictObjectResult>();
    }

    // -- types -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, "Name")]
    [InlineData("", "Name")]
    [InlineData("   ", "Name")]
    [InlineData("CODE", null)]
    [InlineData("CODE", "")]
    public async Task AType_WithoutACodeOrName_Is400_NotAnException(string? code, string? name)
    {
        var (w, c) = await NewAsync();
        (await c.CreateType(new OvertimeTypeRequest(code!, name!, null), CancellationToken.None)).Result.Should().BeOfType<BadRequestObjectResult>();
        (await w.Db.OvertimeTypes.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ADuplicateTypeCode_Is409_EvenWithDifferentCase()
    {
        var (w, c) = await NewAsync();
        (await c.CreateType(new OvertimeTypeRequest("HOL", "Holiday work", "Holiday"), CancellationToken.None)).Result.Should().BeOfType<CreatedResult>();

        (await c.CreateType(new OvertimeTypeRequest("hol", "Again", null), CancellationToken.None)).Result.Should().BeOfType<ConflictObjectResult>();
        (await w.Db.OvertimeTypes.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task CreatingAType_WritesAnAuditRow()
    {
        var (w, c) = await NewAsync();
        (await c.CreateType(new OvertimeTypeRequest("HOL", "Holiday work", "Holiday"), CancellationToken.None)).Result.Should().BeOfType<CreatedResult>();

        var audit = await w.Db.OvertimeAuditLogs.SingleAsync(x => x.Action == "overtime.type.created");
        audit.MetadataJson.Should().Contain("\"old\":null").And.Contain("HOL");
    }
}
