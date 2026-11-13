using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The GL Journal tab for a POSTED run. Once a run is settled and remitted the ledger holds several
/// journals for it (accrual, net-pay settlement, remittances, void). The tab used to merge them into one
/// list, so 2100 showed as both debit and credit and the headline total doubled; it also read "cost
/// accounting is off" from the preview plan, which is always Off for a posted run.
/// </summary>
public class GlJournalPostedJourneysTests
{
    private static readonly DateOnly Day = new(2026, 11, 12);

    private static FinanceGlEntry Row(GlPostingKit k, Guid runId, string eventType, string debit, string credit, decimal amount,
        string by = "", Guid? byId = null, Guid? ccId = null, string? ccCode = null, string? ccName = null, string? ccSegment = null) => new()
    {
        TenantId = k.TenantId, CompanyId = k.CompanyId, SourceModule = "Payroll", SourceEntityId = runId, SourceEntityRef = "PR-2026-11",
        EventType = eventType, DebitAccount = debit, CreditAccount = credit, Amount = amount, Currency = "SAR",
        EntryDate = Day, Period = "2026-11", Description = "Payroll earning: BASIC",
        PostedByName = by, PostedBy = byId,
        CostCenterId = ccId, CostCenterCode = ccCode, CostCenterName = ccName, CostCenterGlSegment = ccSegment,
    };

    private static async Task<PayrollRun> LockedRunAsync(GlPostingKit k)
    {
        var run = await k.SeedApprovedRunAsync(new[] { (1, "BASIC", "Salary", 1_000m) }, Array.Empty<(int, string, string, decimal)>(), net: 1_000m);
        run.Status = "Locked";
        await k.Db.SaveChangesAsync();
        return run;
    }

    private static async Task<JsonElement> JournalAsync(GlPostingKit k, Guid runId)
    {
        k.Db.ChangeTracker.Clear();
        var res = await k.Payroll().GlJournal(runId, default);
        return JsonSerializer.SerializeToElement(res.Should().BeOfType<OkObjectResult>().Subject.Value);
    }

    [Fact]
    public async Task PostedRun_ShowsEachJournalSeparately_AndTheHeadlineIsTheAccrual()
    {
        await using var k = await GlPostingKit.CreateAsync();
        var run = await LockedRunAsync(k);
        k.Db.FinanceGlEntries.AddRange(
            Row(k, run.Id, GlEventTypes.Accrual, "5001 - Basic Salary Expense", "", 1_000m, "Layla Hamdan"),
            Row(k, run.Id, GlEventTypes.Accrual, "", "2100 - Salaries Payable", 1_000m, "Layla Hamdan"),
            Row(k, run.Id, GlEventTypes.NetSettlement, "2100 - Salaries Payable", "1000 - Cash/Bank", 800m, "Omar Finance"),
            Row(k, run.Id, GlEventTypes.Remit("GOSI"), "2101 - Social Insurance Payable (Employee)", "1000 - Cash/Bank", 100m, "Omar Finance"),
            Row(k, run.Id, GlEventTypes.Void, "2100 - Salaries Payable", "5001 - Basic Salary Expense", 50m, "Layla Hamdan"));
        await k.Db.SaveChangesAsync();

        var j = await JournalAsync(k, run.Id);

        j.GetProperty("totalDebits").GetDecimal().Should().Be(1_000m, "the headline total is the accrual journal alone");
        j.GetProperty("totalCredits").GetDecimal().Should().Be(1_000m);
        j.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("glAccount").GetString())
            .Should().BeEquivalentTo("5001", "2100");

        var journals = j.GetProperty("journals").EnumerateArray().ToList();
        journals.Select(x => x.GetProperty("kind").GetString())
            .Should().Equal("accrual", "settlement", "remittance", "void");
        journals.Should().OnlyContain(x => x.GetProperty("isBalanced").GetBoolean());
        journals.Select(x => x.GetProperty("totalDebits").GetDecimal()).Should().Equal(1_000m, 800m, 100m, 50m);
        journals[2].GetProperty("group").GetString().Should().Be("GOSI");
        journals[1].GetProperty("postedByName").GetString().Should().Be("Omar Finance");
        journals[1].GetProperty("entryDate").GetString().Should().Be("2026-11-12");
        journals[1].GetProperty("entries").GetArrayLength().Should().Be(2, "a two-sided row is a debit line and a credit line");
    }

    [Fact]
    public async Task PostedRun_WithCostCentreLines_ReportsItFromThePostedEntries_AndCarriesTheErpSegment()
    {
        await using var k = await GlPostingKit.CreateAsync();
        var run = await LockedRunAsync(k);
        var cc = new CostCenter { TenantId = k.TenantId, CompanyId = k.CompanyId, Code = "CC-ENG", Name = "Engineering", GlSegment = "ENG-01", IsActive = true };
        k.Db.CostCenters.Add(cc);
        await k.Db.SaveChangesAsync();
        k.Db.FinanceGlEntries.AddRange(
            Row(k, run.Id, GlEventTypes.Accrual, "5001 - Basic Salary Expense", "", 1_000m, "Layla Hamdan", null, cc.Id, "CC-ENG", "Engineering", "ENG-01"),
            Row(k, run.Id, GlEventTypes.Accrual, "", "2100 - Salaries Payable", 1_000m, "Layla Hamdan"));
        await k.Db.SaveChangesAsync();

        var j = await JournalAsync(k, run.Id);

        j.GetProperty("costing").GetProperty("enabled").GetBoolean().Should().BeFalse("the preview plan stays off once posted; it is not the evidence");
        j.GetProperty("costing").GetProperty("postedWithCostCentres").GetBoolean().Should().BeTrue();
        var line = j.GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("glAccount").GetString() == "5001");
        line.GetProperty("costCentreCode").GetString().Should().Be("CC-ENG");
        line.GetProperty("costCentreName").GetString().Should().Be("Engineering");
        line.GetProperty("costCentreErpCode").GetString().Should().Be("ENG-01");
        j.GetProperty("journals")[0].GetProperty("hasCostCentres").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task PostedRun_WithoutCostCentreLines_ReportsNone()
    {
        await using var k = await GlPostingKit.CreateAsync();
        var run = await LockedRunAsync(k);
        k.Db.FinanceGlEntries.AddRange(
            Row(k, run.Id, GlEventTypes.Accrual, "5001 - Basic Salary Expense", "", 1_000m),
            Row(k, run.Id, GlEventTypes.Accrual, "", "2100 - Salaries Payable", 1_000m));
        await k.Db.SaveChangesAsync();

        var j = await JournalAsync(k, run.Id);

        j.GetProperty("costing").GetProperty("postedWithCostCentres").GetBoolean().Should().BeFalse();
        j.GetProperty("journals")[0].GetProperty("hasCostCentres").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task PostedRun_ResolvesAnUnknownPostedByNameFromThePostingUser()
    {
        await using var k = await GlPostingKit.CreateAsync();
        var run = await LockedRunAsync(k);
        k.Db.Tenants.Add(new Tenant { Id = k.TenantId, Name = "t", Slug = "t-" + k.TenantId.ToString("N")[..6], IsActive = true });
        var user = new User { TenantId = k.TenantId, Email = "hana@example.test", NormalizedEmail = "HANA@EXAMPLE.TEST", FullName = "Hana Finance" };
        k.Db.Users.Add(user);
        k.Db.FinanceGlEntries.AddRange(
            Row(k, run.Id, GlEventTypes.Accrual, "5001 - Basic Salary Expense", "", 1_000m, "Unknown", user.Id),
            Row(k, run.Id, GlEventTypes.Accrual, "", "2100 - Salaries Payable", 1_000m, "Unknown", user.Id));
        await k.Db.SaveChangesAsync();

        var j = await JournalAsync(k, run.Id);

        j.GetProperty("journals")[0].GetProperty("postedByName").GetString().Should().Be("Hana Finance");
    }
}
