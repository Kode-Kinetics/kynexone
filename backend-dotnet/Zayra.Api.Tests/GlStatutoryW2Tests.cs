using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Wave-2 statutory hardening (fix/gl-w2-rates). SQLite in-memory + EnsureCreated, so conditional
/// updates and transactions are real relational operations. Each fact names the review finding it
/// closes; every one failed on the code before the fix.
/// </summary>
public class GlStatutoryW2Tests
{
    private const string Jur = "KSA-mainland";
    private static readonly DateTime Since2020 = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly string[] Override = { "payroll.rates.statutory_override" };
    private static readonly string[] RatePerms = { "payroll.rates.read", "payroll.rates.manage" };

    private static (ZayraDbContext db, SqliteConnection conn) NewDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static T Attach<T>(T c, Guid tenantId, IEnumerable<string> perms, Guid? userId = null) where T : ControllerBase
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, (userId ?? Guid.NewGuid()).ToString()),
        };
        claims.AddRange(perms.Select(p => new Claim("permission", p)));
        c.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) },
        };
        return c;
    }

    private static RatesController Rates(ZayraDbContext db, Guid tid, string[] perms, Guid? uid = null)
    {
        var reader = new StatutoryRuleReader(db);
        return Attach(new RatesController(db, reader, new StatutoryRateResolver(db, reader)), tid, perms, uid);
    }

    private static StatutoryRulesController Rules(ZayraDbContext db, Guid tid, Guid? uid = null)
        => Attach(new StatutoryRulesController(db), tid, Override, uid);

    private static object? Prop(object o, string name) => o.GetType().GetProperty(name)?.GetValue(o);
    private static List<object> Items(object value) => ((System.Collections.IEnumerable)value).Cast<object>().ToList();

    private static void PlatformRule(ZayraDbContext db, string key, string value, string dataType = "decimal", string cc = "SAU")
        => db.StatutoryRules.Add(new StatutoryRule
        {
            TenantId = null, CountryCode = cc, Jurisdiction = Jur, RuleKey = key, RuleValue = value,
            DataType = dataType, Description = "KSA Labour Law", EffectiveFrom = Since2020,
        });

    private static DateTime Tomorrow => DateTime.UtcNow.Date.AddDays(1);

    private static CompanyStatutoryOverride Pending(Guid tid, Guid? company, string key, string value, string dataType = "decimal") => new()
    {
        TenantId = tid, CompanyId = company, CountryCode = "SAU", Jurisdiction = Jur, RuleKey = key, OverrideValue = value,
        DataType = dataType, EffectiveFrom = DateOnly.FromDateTime(Tomorrow.AddDays(30)), ReviewBy = new DateOnly(2099, 1, 1),
        Reason = "legacy request", Status = StatutoryOverridePolicy.PendingApproval, CreatedBy = Guid.NewGuid(),
    };

    // ═══ confirmed[2] — ot.hourly_base is a floor ═════════════════════════════════════════════

    [Fact]
    public async Task HourlyBase_TenantChange_WageToBasicRefused_UnknownWordRefused_BasicToWageAllowed()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        PlatformRule(db, "ot.hourly_base", "wage", "string");
        PlatformRule(db, "ot.hourly_base", "basic", "string", cc: "ARE");
        await db.SaveChangesAsync();

        CreateStatutoryRuleRequest Req(string cc, string value) => new(cc, Jur, "ot.hourly_base", value, "string", "board decision", Tomorrow, null);

        var cut = (await Rules(db, tid).Create(Req("SAU", "basic"), default)).Result.Should().BeOfType<UnprocessableEntityObjectResult>().Subject;
        Prop(cut.Value!, "code").Should().Be(StatutoryOverridePolicy.FloorCode, "wage → basic pays KSA overtime ~30% less");

        var unknown = (await Rules(db, tid).Create(Req("SAU", "full"), default)).Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        Prop(unknown.Value!, "code").Should().Be(StatutoryValueUnits.UnitRefusalCode, "anything but 'wage' is paid as basic");

        (await Rules(db, tid).Create(Req("ARE", "wage"), default)).Result.Should().BeOfType<AcceptedResult>("basic → wage only pays more");
        (await Rules(db, tid).Create(Req("SAU", "Wage"), default)).Result.Should().BeOfType<AcceptedResult>("the platform value itself, any case");
    }

    [Fact]
    public async Task HourlyBase_LegacyPendingRequests_AreRefusedAtApprove()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        PlatformRule(db, "ot.hourly_base", "wage", "string");
        var basic = Pending(tid, null, "ot.hourly_base", "basic", "string");
        var gross = Pending(tid, null, "ot.hourly_base", "gross", "string");
        db.AddRange(basic, gross); await db.SaveChangesAsync();

        var r1 = (await Rates(db, tid, Override).ApproveStatutoryOverride(basic.Id, default)).Should().BeOfType<UnprocessableEntityObjectResult>().Subject;
        Prop(r1.Value!, "code").Should().Be(StatutoryOverridePolicy.FloorCode);
        var r2 = (await Rates(db, tid, Override).ApproveStatutoryOverride(gross.Id, default)).Should().BeOfType<BadRequestObjectResult>().Subject;
        Prop(r2.Value!, "code").Should().Be(StatutoryValueUnits.UnitRefusalCode);
        (await db.StatutoryRules.IgnoreQueryFilters().CountAsync(r => r.TenantId == tid)).Should().Be(0, "nothing reached payroll");
    }

    [Fact]
    public async Task HourlyBase_LegacyTenantRowBelowFloor_IsIgnoredByPayrollRead()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        PlatformRule(db, "ot.hourly_base", "wage", "string");
        db.StatutoryRules.Add(new StatutoryRule { TenantId = tid, CountryCode = "SAU", Jurisdiction = Jur, RuleKey = "ot.hourly_base", RuleValue = "basic", DataType = "string", Description = "legacy one-person write", EffectiveFrom = Since2020 });
        await db.SaveChangesAsync();

        var ctx = await new StatutoryRuleReader(db).ResolveAsync("SAU", Jur, new DateOnly(2026, 6, 1), tid, default);
        ctx.BaseIsFullWage.Should().BeTrue("a tenant 'basic' below the KSA floor is never paid");
    }

    // ═══ confirmed[3] — ot.standard_monthly_hours and the LOP divisors have a lower bound ═══════

    [Theory]
    [InlineData("0.5")]  // truncates to 0: no overtime pay at all
    [InlineData("8")]    // hours per DAY: ~30× the hourly rate on every late hour
    [InlineData("48")]   // hours per WEEK
    [InlineData("143")]  // just under 60% of 240
    public async Task StandardMonthlyHours_ImplausiblyLow_IsRefusedAtCreate(string value)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        PlatformRule(db, "ot.standard_monthly_hours", "240"); await db.SaveChangesAsync();
        var result = (await Rates(db, tid, Override).CreateStatutoryOverride(new StatutoryOverrideRequest(
            Guid.NewGuid(), "SAU", Jur, "ot.standard_monthly_hours", value, new DateOnly(2026, 1, 1), "x", new DateOnly(2027, 1, 1)), default))
            .Should().BeOfType<UnprocessableEntityObjectResult>().Subject;
        Prop(result.Value!, "code").Should().Be(StatutoryOverridePolicy.FloorCode);

        var tenant = (await Rules(db, tid).Create(new CreateStatutoryRuleRequest("SAU", Jur, "ot.standard_monthly_hours", value, "decimal", "x", Tomorrow, null), default))
            .Result.Should().BeOfType<UnprocessableEntityObjectResult>("the tenant path has the same bound").Subject;
        Prop(tenant.Value!, "code").Should().Be(StatutoryOverridePolicy.FloorCode);
    }

    [Theory]
    [InlineData("156")] // KSA Ramadan schedule, 36 h a week
    [InlineData("173.33")] // 40-hour week
    [InlineData("240")]
    public async Task StandardMonthlyHours_RealSchedules_AreAccepted(string value)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        PlatformRule(db, "ot.standard_monthly_hours", "240"); await db.SaveChangesAsync();
        (await Rates(db, Guid.NewGuid(), Override).CreateStatutoryOverride(new StatutoryOverrideRequest(
            Guid.NewGuid(), "SAU", Jur, "ot.standard_monthly_hours", value, new DateOnly(2026, 1, 1), "x", new DateOnly(2027, 1, 1)), default))
            .Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task StandardMonthlyHours_LegacyActiveOverride_BelowTheBound_IsIgnoredAtReadAndApprove()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid(); var company = Guid.NewGuid();
        PlatformRule(db, "ot.standard_monthly_hours", "240");
        var legacy = Pending(tid, company, "ot.standard_monthly_hours", "0.5");
        db.Add(legacy); await db.SaveChangesAsync();
        (await Rates(db, tid, Override).ApproveStatutoryOverride(legacy.Id, default)).Should().BeOfType<UnprocessableEntityObjectResult>();

        await db.CompanyStatutoryOverrides.IgnoreQueryFilters().Where(o => o.Id == legacy.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, "Active").SetProperty(o => o.EffectiveFrom, new DateOnly(2026, 1, 1)));
        (await new StatutoryRuleReader(db).GetDecimalAsync("SAU", Jur, "ot.standard_monthly_hours", new DateOnly(2026, 6, 1), tid, default, company))
            .Should().Be(240m);
    }

    [Theory]
    [InlineData("lop.monthly_day_divisor", "10")]
    [InlineData("lop.standard_work_minutes_per_day", "60")]
    public async Task LopDivisors_HaveAPlausibleMinimum_EvenWithNoPlatformValue(string key, string value)
    {
        StatutoryOverridePolicy.DirectionRefusal(key, value, platformValue: null).Should().NotBeNull();
        StatutoryOverridePolicy.DirectionRefusal(key, "0", platformValue: null).Should().NotBeNull("zero is a division by zero");
        StatutoryOverridePolicy.DirectionRefusal("lop.monthly_day_divisor", "26", platformValue: null).Should().BeNull("Kuwait's 26-day divisor is real");
        await Task.CompletedTask;
    }

    // ═══ minor[3] — tenant values below the floor are not paid, and are flagged ═══════════════

    [Fact]
    public async Task TenantValueBelowFloor_IsIgnoredByTheReader_AndFlaggedOnBothStatutoryViews()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tid, Name = "T", Slug = $"t-{tid:N}" });
        var co = new Company { TenantId = tid, LegalNameEn = "Acme KSA", CountryCode = "SAU", Jurisdiction = Jur, DefaultCurrency = "SAR" };
        db.Companies.Add(co);
        PlatformRule(db, "ot.standard_multiplier", "1.5");
        PlatformRule(db, "lop.monthly_day_divisor", "30");
        db.StatutoryRules.Add(new StatutoryRule { TenantId = tid, CountryCode = "SAU", Jurisdiction = Jur, RuleKey = "ot.standard_multiplier", RuleValue = "1.0", DataType = "decimal", Description = "legacy", EffectiveFrom = Since2020 });
        db.StatutoryRules.Add(new StatutoryRule { TenantId = tid, CountryCode = "SAU", Jurisdiction = Jur, RuleKey = "lop.monthly_day_divisor", RuleValue = "31", DataType = "decimal", Description = "more generous", EffectiveFrom = Since2020 });
        await db.SaveChangesAsync();

        var reader = new StatutoryRuleReader(db);
        var june = new DateOnly(2026, 6, 1);
        (await reader.GetDecimalAsync("SAU", Jur, "ot.standard_multiplier", june, tid)).Should().Be(1.5m, "1.0× is below the Art. 107 floor");
        (await reader.GetDecimalAsync("SAU", Jur, "lop.monthly_day_divisor", june, tid)).Should().Be(31m, "a value above the floor is still the tenant's");

        Dictionary<string, object?> Row(IActionResult r, string key) => Items(((OkObjectResult)r).Value!)
            .Where(o => (string)Prop(o, "ruleKey")! == key)
            .Select(o => o.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(o))).Single();

        var company = await Rates(db, tid, Override).ListStatutory(co.Id, null, null, null, default);
        Row(company, "ot.standard_multiplier")["belowFloorIgnored"].Should().Be(true);
        Row(company, "ot.standard_multiplier")["resolvedValue"].Should().Be(1.5m);
        Row(company, "lop.monthly_day_divisor")["belowFloorIgnored"].Should().Be(false);

        var group = await Rates(db, tid, Override).ListStatutory(null, null, null, null, default);
        var g = Row(group, "ot.standard_multiplier");
        g["belowFloorIgnored"].Should().Be(true);
        g["neverApplied"].Should().Be(true);
        ((string?)g["notice"]).Should().Contain("below the legal minimum");
        g["resolvedValue"].Should().Be(1.5m);
    }

    // ═══ minor[0] — unpaid leave uses the company's approved LOP divisor ════════════════════════

    [Fact]
    public async Task UnpaidLeaveDivisor_UsesTheApprovedCompanyOverride_ForThatCompanyOnly()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tid, Name = "T", Slug = $"t-{tid:N}" });
        var a = new Company { TenantId = tid, LegalNameEn = "A", CountryCode = "SAU", Jurisdiction = Jur, DefaultCurrency = "SAR" };
        var b = new Company { TenantId = tid, LegalNameEn = "B", CountryCode = "SAU", Jurisdiction = Jur, DefaultCurrency = "SAR" };
        db.Companies.AddRange(a, b);
        PlatformRule(db, "lop.monthly_day_divisor", "30");
        await db.SaveChangesAsync();
        Employee Emp(Guid companyId) => new()
        {
            TenantId = tid, CompanyId = companyId, EmployeeCode = $"E-{Guid.NewGuid():N}", FullName = "Leave Test", Nationality = "Indian",
            Status = "Active", JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var empA = Emp(a.Id); var empB = Emp(b.Id);
        db.Employees.AddRange(empA, empB);
        await db.SaveChangesAsync();

        var maker = Guid.NewGuid();
        var created = (OkObjectResult)await Rates(db, tid, Override, maker).CreateStatutoryOverride(new StatutoryOverrideRequest(
            a.Id, "SAU", Jur, "lop.monthly_day_divisor", "31", new DateOnly(2026, 1, 1), "Company handbook", new DateOnly(2027, 1, 1)), default);
        (await Rates(db, tid, Override).ApproveStatutoryOverride((Guid)Prop(created.Value!, "Id")!, default)).Should().BeOfType<OkObjectResult>();

        var leave = new LeaveService(db, router: null!);
        (await leave.ResolveLopDayDivisorAsync(tid, empA.Id, new DateOnly(2026, 6, 10), default)).Should().Be(31m,
            "an unpaid-leave day must cost what an absent day costs on the same payslip");
        (await leave.ResolveLopDayDivisorAsync(tid, empB.Id, new DateOnly(2026, 6, 10), default)).Should().Be(30m);
    }

    // ═══ minor[2] — cancelling a scheduled company-rate change restores the current value ═════

    private const string Basis = "payparameter.proration_basis";

    [Fact]
    public async Task DeletingAScheduledChange_ReopensTheValueItWouldHaveReplaced()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid(); var company = Guid.NewGuid();
        var r = Rates(db, tid, RatePerms);
        var current = (OkObjectResult)await r.CreateCompanyRate(company, new CompanyRateRequest(Basis, "CalendarActual", new DateOnly(2026, 1, 1)), default);
        var currentId = (Guid)Prop(current.Value!, "Id")!;
        var scheduled = (OkObjectResult)await Rates(db, tid, RatePerms).UpdateCompanyRate(currentId, new CompanyRateRequest(Basis, "WorkingDays", new DateOnly(2099, 1, 1)), default);
        var scheduledId = (Guid)Prop(scheduled.Value!, "Id")!;

        (await Rates(db, tid, RatePerms).DeleteCompanyRate(scheduledId, default)).Should().BeOfType<NoContentResult>();

        db.ChangeTracker.Clear();
        var prior = await db.CompanyRatePolicies.IgnoreQueryFilters().SingleAsync(p => p.Id == currentId);
        prior.Status.Should().Be(CompanyPolicyStatuses.Active);
        prior.EffectiveTo.Should().BeNull("it was open-ended before the change was scheduled");
        var resolved = await new CompanyRatePolicyResolver(db).ResolveAsync(tid, company, Basis, new DateOnly(2099, 6, 1));
        resolved!.RateValue.Should().Be("CalendarActual", "the cancelled change must not leave the period uncovered");
        // ...and it can be changed again: it is the current version once more.
        (await Rates(db, tid, RatePerms).UpdateCompanyRate(currentId, new CompanyRateRequest(Basis, "None", new DateOnly(2099, 2, 1)), default))
            .Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task DeletingAScheduledChange_RestoresTheOriginalEndDate()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid(); var company = Guid.NewGuid();
        var current = (OkObjectResult)await Rates(db, tid, RatePerms).CreateCompanyRate(company,
            new CompanyRateRequest(Basis, "CalendarActual", new DateOnly(2026, 1, 1), new DateOnly(2100, 12, 31)), default);
        var currentId = (Guid)Prop(current.Value!, "Id")!;
        var scheduled = (OkObjectResult)await Rates(db, tid, RatePerms).UpdateCompanyRate(currentId,
            new CompanyRateRequest(Basis, "WorkingDays", new DateOnly(2099, 1, 1)), default);
        (await Rates(db, tid, RatePerms).DeleteCompanyRate((Guid)Prop(scheduled.Value!, "Id")!, default)).Should().BeOfType<NoContentResult>();

        db.ChangeTracker.Clear();
        var prior = await db.CompanyRatePolicies.IgnoreQueryFilters().SingleAsync(p => p.Id == currentId);
        prior.Status.Should().Be(CompanyPolicyStatuses.Active);
        prior.EffectiveTo.Should().Be(new DateOnly(2100, 12, 31));
        (await db.AuditLogs.CountAsync(a => a.Action == "payroll.rate.company.deleted" && a.Metadata!.Contains("restoredId"))).Should().Be(1);
    }

    // ═══ minor[1] — withdraw is a conditional claim ═══════════════════════════════════════════

    [Fact]
    public async Task Withdraw_OfARequestDecidedMeanwhile_Is409_AndLeavesTheDecisionStanding()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid(); var company = Guid.NewGuid();
        PlatformRule(db, "ot.standard_multiplier", "1.5"); await db.SaveChangesAsync();
        var maker = Guid.NewGuid();
        var created = (OkObjectResult)await Rates(db, tid, Override, maker).CreateStatutoryOverride(new StatutoryOverrideRequest(
            company, "SAU", Jur, "ot.standard_multiplier", "1.75", new DateOnly(2026, 1, 1), "CBA", new DateOnly(2027, 1, 1)), default);
        var id = (Guid)Prop(created.Value!, "Id")!;

        (await Rates(db, tid, Override, maker).RevertStatutoryOverride(id, default)).Should().BeOfType<NoContentResult>("a pending request is withdrawn");
        db.ChangeTracker.Clear();
        (await db.CompanyStatutoryOverrides.IgnoreQueryFilters().SingleAsync(o => o.Id == id)).Status.Should().Be(StatutoryOverridePolicy.Withdrawn);
        (await db.AuditLogs.CountAsync(a => a.Action == "payroll.rate.statutory_override.withdrawn")).Should().Be(1);
        // The concurrent-approval race itself is proven on Postgres: StatutoryWithdrawRacePostgresTests.
    }
}
