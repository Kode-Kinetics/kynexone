using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;
using Xunit;

namespace Zayra.Api.Tests;

/// <summary>
/// Setup → GL &amp; rates → Company Rates accepted thirteen registry keys, but payroll reads only the five
/// proration / arrears keys (ProrationPolicyResolver). The other eight were saved and never applied, so a
/// customer could "set" a wellness allowance or extra end-of-service days and nobody's pay changed.
/// The registry and every row now say whether payroll applies the key; a new value for a recorded-only
/// key is refused with COMPANY_RATE_NOT_APPLIED; existing rows of those keys stay readable and can
/// still be end-dated (cleanup).
/// </summary>
public class CompanyRateAppliedByPayrollTests
{
    private static readonly string[] Applied =
    {
        ProrationRateKeys.Basis, ProrationRateKeys.GosiBase, ProrationRateKeys.ArrearsGosiTreatment,
        ProrationRateKeys.ArrearsMaxLookback, ProrationRateKeys.ProratedComponents,
        // POD-C2 — read at every payroll Lock (PayrollController.BuildEosbProvisionAsync).
        EosbProvisioningRateKeys.Mode,
    };

    public static readonly TheoryData<string, string> RecordedOnly = new()
    {
        { "allowance.wellness.monthly_amount", "500" },
        { "allowance.mobile.monthly_amount", "200" },
        { "allowance.education.annual_amount", "10000" },
        { "deduction.canteen.monthly_amount", "150" },
        { "deduction.parking.monthly_amount", "100" },
        { "eosb.enhancement_days_per_year", "5" },
        { "payparameter.probation_ot_multiplier", "1.5" },
        { "payparameter.notice_period_days", "60" },
    };

    private static readonly string[] Perms = { "payroll.rates.read", "payroll.rates.manage" };

    private static (ZayraDbContext db, SqliteConnection conn) NewDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static RatesController Rates(ZayraDbContext db, Guid tid)
    {
        var reader = new StatutoryRuleReader(db);
        var c = new RatesController(db, reader, new StatutoryRateResolver(db, reader));
        var claims = new List<Claim> { new("tenant_id", tid.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        claims.AddRange(Perms.Select(p => new Claim("permission", p)));
        c.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) },
        };
        return c;
    }

    private static object? Prop(object o, string name) => o.GetType().GetProperty(name)?.GetValue(o);
    private static List<object> Items(object value) => ((System.Collections.IEnumerable)value).Cast<object>().ToList();

    private static CompanyRatePolicy LegacyRow(Guid tid, Guid? company, string key, string value, DateOnly from) => new()
    {
        TenantId = tid, CompanyId = company, RateKey = key, RateCategory = "Allowance", RateValue = value,
        DataType = "decimal", Unit = "amount", EffectiveFrom = from, Status = CompanyPolicyStatuses.Active, Notes = "",
    };

    [Fact]
    public void EveryRegistryKey_IsClassified_AndOnlyTheProrationKeysAreApplied()
    {
        var registry = StatutoryRateGuard.DefaultClientRateDefinitions(Guid.NewGuid()).Select(d => d.RateKey).ToList();
        registry.Where(StatutoryRateGuard.IsAppliedByPayroll).Should().BeEquivalentTo(Applied);
        var recordedOnly = RecordedOnly.Select(r => (string)r[0]).ToList();
        registry.Where(k => !StatutoryRateGuard.IsAppliedByPayroll(k)).Should().BeEquivalentTo(recordedOnly,
            "a new registry key must be consciously classified: applied only once a calculation reads it");
        StatutoryRateGuard.IsAppliedByPayroll("tenant.custom.key").Should().BeFalse("nothing in the code reads a tenant-invented key");
        StatutoryRateGuard.IsAppliedByPayroll(" PayParameter.Proration_Basis ").Should().BeTrue();
    }

    [Fact]
    public async Task Registry_ExposesAppliedByPayroll_AndTheReasonForRecordedOnlyKeys()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var ok = (await Rates(db, Guid.NewGuid()).ListRateRegistry(default)).Should().BeOfType<OkObjectResult>().Subject;
        var items = Items(ok.Value!);
        items.Should().HaveCount(14);
        foreach (var i in items)
        {
            var key = (string)Prop(i, "RateKey")!;
            var applied = (bool)Prop(i, "appliedByPayroll")!;
            applied.Should().Be(Applied.Contains(key), key);
            if (applied) Prop(i, "notAppliedReason").Should().BeNull();
            else ((string)Prop(i, "notAppliedReason")!).Should().StartWith("Payroll does not use '");
        }
    }

    /// <summary>POD-C2 — the provisioning switch takes exactly its two modes; anything else 400s at the write
    /// surface instead of silently resolving Off at every Lock.</summary>
    [Fact]
    public async Task EosbProvisioningSwitch_AcceptsItsModes_AndRefusesAnythingElse()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        (await Rates(db, tid).CreateCompanyRate(Guid.NewGuid(),
                new CompanyRateRequest(EosbProvisioningRateKeys.Mode, "Monthly", new DateOnly(2026, 1, 1)), default))
            .Should().BeOfType<BadRequestObjectResult>();
        (await Rates(db, tid).CreateCompanyRate(Guid.NewGuid(),
                new CompanyRateRequest(EosbProvisioningRateKeys.Mode, EosbProvisioningRateKeys.StatutoryMonthly, new DateOnly(2026, 1, 1)), default))
            .Should().BeOfType<OkObjectResult>();
    }

    [Theory]
    [MemberData(nameof(RecordedOnly))]
    public async Task Create_OfARecordedOnlyKey_Is422_AndWritesNothing(string key, string value)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var refused = (await Rates(db, tid).CreateCompanyRate(Guid.NewGuid(), new CompanyRateRequest(key, value, new DateOnly(2026, 1, 1)), default))
            .Should().BeOfType<UnprocessableEntityObjectResult>().Subject;
        Prop(refused.Value!, "code").Should().Be("COMPANY_RATE_NOT_APPLIED");
        Prop(refused.Value!, "rateKey").Should().Be(key);
        var label = StatutoryRateGuard.DefaultClientRateDefinitions(tid).Single(d => d.RateKey == key).Description;
        ((string)Prop(refused.Value!, "message")!).Should().Contain($"Payroll does not use '{label}' yet").And.Contain("change nobody's pay");
        (await db.CompanyRatePolicies.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Create_OfAnAllowance_PointsToGradeBenefits()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var refused = (UnprocessableEntityObjectResult)await Rates(db, Guid.NewGuid())
            .CreateCompanyRate(null, new CompanyRateRequest("allowance.mobile.monthly_amount", "200", new DateOnly(2026, 1, 1)), default);
        ((string)Prop(refused.Value!, "message")!).Should().Contain("Benefits (Grade benefits)").And.Contain("pay package");
    }

    [Fact]
    public async Task Create_OfAnAppliedKey_StillWorks_AndTheRowSaysSo()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid(); var company = Guid.NewGuid();
        var r = Rates(db, tid);
        var ok = (await r.CreateCompanyRate(company, new CompanyRateRequest(ProrationRateKeys.Basis, "CalendarActual", new DateOnly(2026, 1, 1)), default))
            .Should().BeOfType<OkObjectResult>().Subject;
        Prop(ok.Value!, "appliedByPayroll").Should().Be(true);
        (await new CompanyRatePolicyResolver(db).ResolveAsync(tid, company, ProrationRateKeys.Basis, new DateOnly(2026, 3, 1)))!
            .RateValue.Should().Be("CalendarActual", "payroll resolves exactly what was saved");
    }

    [Fact]
    public async Task ExistingRecordedOnlyRow_IsListedFlagged_CannotBeChanged_ButCanBeEndDated()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid(); var company = Guid.NewGuid();
        var legacy = LegacyRow(tid, company, "allowance.wellness.monthly_amount", "500", new DateOnly(2026, 1, 1));
        db.CompanyRatePolicies.Add(legacy);
        // A locked run after the end date: irrelevant, because payroll never read this key.
        db.PayrollRuns.Add(new PayrollRun { TenantId = tid, CompanyId = company, Year = 2026, Month = 7, Status = "Locked" });
        await db.SaveChangesAsync();
        var r = Rates(db, tid);

        var list = Items(((OkObjectResult)await r.ListCompanyRates(company, default)).Value!);
        list.Should().ContainSingle();
        Prop(list[0], "appliedByPayroll").Should().Be(false);

        // A new value (supersede) is refused: it would change nobody's pay.
        var refused = (await r.UpdateCompanyRate(legacy.Id, new CompanyRateRequest(legacy.RateKey, "750", new DateOnly(2026, 6, 1)), default))
            .Should().BeOfType<UnprocessableEntityObjectResult>().Subject;
        Prop(refused.Value!, "code").Should().Be("COMPANY_RATE_NOT_APPLIED");
        (await db.CompanyRatePolicies.IgnoreQueryFilters().CountAsync()).Should().Be(1, "no superseding row was written");

        // End-dating (same value + start, with an end) is cleanup: allowed, in place.
        var ended = (await r.UpdateCompanyRate(legacy.Id, new CompanyRateRequest(legacy.RateKey, "500", new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 30)), default))
            .Should().BeOfType<OkObjectResult>().Subject;
        Prop(ended.Value!, "appliedByPayroll").Should().Be(false);
        db.ChangeTracker.Clear();
        var rows = await db.CompanyRatePolicies.IgnoreQueryFilters().ToListAsync();
        rows.Should().ContainSingle();
        rows[0].EffectiveTo.Should().Be(new DateOnly(2026, 6, 30));
        rows[0].Status.Should().Be(CompanyPolicyStatuses.Active);
    }

    [Fact]
    public async Task EndDate_ViaPutWithTheSameValue_EndsTheRowInPlace_AndNeverSupersedes()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid(); var company = Guid.NewGuid();
        var r = Rates(db, tid);
        var key = ProrationRateKeys.ArrearsMaxLookback;
        var created = (OkObjectResult)await r.CreateCompanyRate(company, new CompanyRateRequest(key, "24", new DateOnly(2026, 1, 1)), default);
        var id = (Guid)Prop(created.Value!, "Id")!;

        // Exactly what CompanyRatesPanel's End-date sends: the row's own value and start, plus effectiveTo.
        (await r.UpdateCompanyRate(id, new CompanyRateRequest(key, "24", new DateOnly(2026, 1, 1), new DateOnly(2026, 4, 30)), default))
            .Should().BeOfType<OkObjectResult>();

        db.ChangeTracker.Clear();
        var rows = await db.CompanyRatePolicies.IgnoreQueryFilters().Where(p => p.TenantId == tid).ToListAsync();
        rows.Should().ContainSingle("ending a rate must not insert a superseding row");
        rows[0].Id.Should().Be(id);
        rows[0].Status.Should().Be(CompanyPolicyStatuses.Active);
        rows[0].EffectiveTo.Should().Be(new DateOnly(2026, 4, 30));
        var resolver = new CompanyRatePolicyResolver(db);
        (await resolver.ResolveAsync(tid, company, key, new DateOnly(2026, 4, 30)))!.RateValue.Should().Be("24");
        (await resolver.ResolveAsync(tid, company, key, new DateOnly(2026, 5, 1))).Should().BeNull("payroll falls back to the default after the end date");
        (await db.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.Action == "payroll.rate.company.end_dated")).Should().Be(1);
    }
}
