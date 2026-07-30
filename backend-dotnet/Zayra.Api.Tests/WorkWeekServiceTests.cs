using Microsoft.EntityFrameworkCore;
using Xunit;
using Zayra.Api.Application.WorkWeek;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Infrastructure.WorkWeek;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// FIX 2: proves the single WorkWeekService/parser drives weekend math from configuration,
/// reconciling the four divergent serializations (Program C2), and that changing the tenant/company
/// weekend config changes leave day-counting (the exact regression the four hard-coded literals caused).
/// </summary>
public class WorkWeekServiceTests
{
    private static ZayraDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    // ── Parser: the canonical normalizer across all four formats ────────────────

    [Fact]
    public void Parser_CommaList_FriSat()
    {
        var set = WorkWeekParser.ParseWeekendDays("Fri,Sat");
        Assert.Equal(new HashSet<DayOfWeek> { DayOfWeek.Friday, DayOfWeek.Saturday }, set);
    }

    [Fact]
    public void Parser_HyphenPair_FriSat()
    {
        var set = WorkWeekParser.ParseWeekendDays("Fri-Sat");
        Assert.Equal(new HashSet<DayOfWeek> { DayOfWeek.Friday, DayOfWeek.Saturday }, set);
    }

    [Fact]
    public void Parser_HyphenPair_SatSun_WithWeekWrap()
    {
        var set = WorkWeekParser.ParseWeekendDays("Sat-Sun");
        Assert.Equal(new HashSet<DayOfWeek> { DayOfWeek.Saturday, DayOfWeek.Sunday }, set);
    }

    [Fact]
    public void Parser_DayNameList_ForShiftsOverride()
    {
        var set = WorkWeekParser.ParseWeekendDays(new List<string> { "Friday", "Saturday" });
        Assert.Equal(new HashSet<DayOfWeek> { DayOfWeek.Friday, DayOfWeek.Saturday }, set);
    }

    [Fact]
    public void Parser_WorkWeekRange_SunThu_YieldsFriSatWeekend()
    {
        var set = WorkWeekParser.ParseWorkWeekToWeekend("Sun-Thu");
        Assert.Equal(new HashSet<DayOfWeek> { DayOfWeek.Friday, DayOfWeek.Saturday }, set);
    }

    [Fact]
    public void Parser_WorkWeekRange_MonFri_YieldsSatSunWeekend()
    {
        var set = WorkWeekParser.ParseWorkWeekToWeekend("Mon-Fri");
        Assert.Equal(new HashSet<DayOfWeek> { DayOfWeek.Saturday, DayOfWeek.Sunday }, set);
    }

    [Fact]
    public void Parser_EmptyOrGarbage_ReturnsNull()
    {
        Assert.Null(WorkWeekParser.ParseWeekendDays((string?)null));
        Assert.Null(WorkWeekParser.ParseWeekendDays(""));
        Assert.Null(WorkWeekParser.ParseWeekendDays("not-a-day"));
    }

    // ── Service resolution precedence (T+C) ─────────────────────────────────────

    [Fact]
    public async Task Resolve_NoConfig_FallsBackToGccDefault()
    {
        await using var db = NewDb();
        var svc = new WorkWeekService(db);
        var cfg = await svc.ResolveAsync(Guid.NewGuid(), null);
        Assert.Equal(new HashSet<DayOfWeek> { DayOfWeek.Friday, DayOfWeek.Saturday }, cfg.WeekendDays);
        Assert.Equal("default:gcc-fri-sat", cfg.Source);
    }

    [Fact]
    public async Task Resolve_TenantDefaultGccSetting_Wins()
    {
        await using var db = NewDb();
        var tenantId = Guid.NewGuid();
        db.GCCComplianceSettings.Add(new GCCComplianceSetting
        {
            TenantId = tenantId, CompanyId = null, CountryCode = "AE", WeekendDays = "Sat,Sun",
        });
        await db.SaveChangesAsync();

        var cfg = await new WorkWeekService(db).ResolveAsync(tenantId, null, "AE");
        Assert.Equal(new HashSet<DayOfWeek> { DayOfWeek.Saturday, DayOfWeek.Sunday }, cfg.WeekendDays);
        Assert.Equal("gcc:tenant", cfg.Source);
    }

    [Fact]
    public async Task Resolve_CompanyOverride_BeatsTenantDefault()
    {
        await using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        db.GCCComplianceSettings.AddRange(
            new GCCComplianceSetting { TenantId = tenantId, CompanyId = null, CountryCode = "SA", WeekendDays = "Fri,Sat" },
            new GCCComplianceSetting { TenantId = tenantId, CompanyId = companyId, CountryCode = "SA", WeekendDays = "Thu,Fri" });
        await db.SaveChangesAsync();
        var svc = new WorkWeekService(db);

        var companyCfg = await svc.ResolveAsync(tenantId, companyId, "SA");
        Assert.Equal(new HashSet<DayOfWeek> { DayOfWeek.Thursday, DayOfWeek.Friday }, companyCfg.WeekendDays);
        Assert.Equal("gcc:company", companyCfg.Source);

        var tenantCfg = await svc.ResolveAsync(tenantId, null, "SA");
        Assert.Equal(new HashSet<DayOfWeek> { DayOfWeek.Friday, DayOfWeek.Saturday }, tenantCfg.WeekendDays);
    }

    [Fact]
    public async Task Resolve_CountryPayrollRule_WhenNoGccSetting()
    {
        await using var db = NewDb();
        var tenantId = Guid.NewGuid();
        db.CountryPayrollRules.Add(new CountryPayrollRule
        {
            TenantId = tenantId, CountryCode = "SA", RuleKey = "weekend_days", RuleValue = "Fri-Sat", DataType = "string",
        });
        await db.SaveChangesAsync();

        var cfg = await new WorkWeekService(db).ResolveAsync(tenantId, null, "SA");
        Assert.Equal(new HashSet<DayOfWeek> { DayOfWeek.Friday, DayOfWeek.Saturday }, cfg.WeekendDays);
        Assert.Equal("country_payroll_rule", cfg.Source);
    }

    // ── Per-country matrix from the seeded packs (UAE corrected to Sat-Sun) ──────

    [Theory]
    [InlineData("SA", DayOfWeek.Friday, DayOfWeek.Saturday)]
    [InlineData("AE", DayOfWeek.Saturday, DayOfWeek.Sunday)]   // UAE post-2022 reform (was the Fri-Sat defect)
    [InlineData("US", DayOfWeek.Saturday, DayOfWeek.Sunday)]
    public async Task Resolve_CountryMatrix(string country, DayOfWeek d1, DayOfWeek d2)
    {
        await using var db = NewDb();
        var tenantId = Guid.NewGuid();
        // Seed the canonical weekend the provisioning bundle would install for this country.
        var weekend = country switch { "AE" => "Sat-Sun", "SA" => "Fri-Sat", _ => "Sat-Sun" };
        db.CountryPayrollRules.Add(new CountryPayrollRule
        {
            TenantId = tenantId, CountryCode = country, RuleKey = "weekend_days", RuleValue = weekend, DataType = "string",
        });
        await db.SaveChangesAsync();

        var cfg = await new WorkWeekService(db).ResolveAsync(tenantId, null, country);
        Assert.Equal(new HashSet<DayOfWeek> { d1, d2 }, cfg.WeekendDays);
    }

    // ── THE regression: weekend config drives leave day-counting ────────────────
    //
    // A single Friday: with a GCC weekend (Fri,Sat) it is a REST day (0 working days deducted from
    // balance is wrong — it is EXCLUDED from the leave span → 0 working days); with a Western
    // weekend (Sat,Sun) that same Friday is a WORKING day → 1. The hard-coded Sat/Sun literal made
    // every GCC tenant compute the Western answer. This proves the config now drives the math.

    [Fact]
    public async Task LeaveWorkingDays_DrivenByWeekendConfig_GccVsWestern()
    {
        var friday = new DateOnly(2026, 1, 2);
        Assert.Equal(DayOfWeek.Friday, friday.DayOfWeek); // guard: the date really is a Friday

        // Case A — GCC weekend Fri/Sat: the Friday is a rest day → 0 working days.
        await using (var db = NewDb())
        {
            var tenantId = Guid.NewGuid();
            var policyId = await SeedLeavePolicyAndWeekend(db, tenantId, "Fri,Sat");
            var svc = new LeaveService(db, null!, new WorkWeekService(db));
            var working = await svc.CalculateWorkingDaysAsync(tenantId, friday, friday, policyId);
            Assert.Equal(0m, working);
        }

        // Case B — Western weekend Sat/Sun: the same Friday is a working day → 1 working day.
        await using (var db = NewDb())
        {
            var tenantId = Guid.NewGuid();
            var policyId = await SeedLeavePolicyAndWeekend(db, tenantId, "Sat,Sun");
            var svc = new LeaveService(db, null!, new WorkWeekService(db));
            var working = await svc.CalculateWorkingDaysAsync(tenantId, friday, friday, policyId);
            Assert.Equal(1m, working);
        }
    }

    private static async Task<Guid> SeedLeavePolicyAndWeekend(ZayraDbContext db, Guid tenantId, string weekendDays)
    {
        db.GCCComplianceSettings.Add(new GCCComplianceSetting
        {
            TenantId = tenantId, CompanyId = null, CountryCode = "SA", WeekendDays = weekendDays,
        });
        var policy = new LeavePolicy
        {
            TenantId = tenantId, Name = "Annual", LeaveTypeId = Guid.NewGuid(), CountryCode = "SA",
            WeekendsIncluded = false, PublicHolidaysIncluded = true, Status = "Active",
        };
        db.LeavePolicies.Add(policy);
        await db.SaveChangesAsync();
        return policy.Id;
    }
}
