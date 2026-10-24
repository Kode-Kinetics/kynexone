using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The Border Number window is a RULE with a configurable number: the dated statutory row
/// <c>ksa.border_number_grace_days</c> (90) with a per-company override <c>payparameter.ksa_border_number_grace_days</c>
/// (0..180), and a per-company switch for writing the border number as the wage-file employee ID. The window is
/// measured at the pay period's END; a hire from recruitment carries the number.
/// </summary>
public class BorderNumberConfigurableRuleTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CompanyA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid CompanyB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateOnly Period = new(2026, 6, 30);
    private static readonly DateTime Joined = new(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ZayraDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task SeedRulesAsync(ZayraDbContext db)
        => await StatutoryRuleSeeder.SeedAsync(db, NullLogger.Instance);

    private static void Rate(ZayraDbContext db, string key, string value, Guid? companyId)
    {
        db.CompanyRatePolicies.Add(new CompanyRatePolicy
        {
            TenantId = Tenant, CompanyId = companyId, RateKey = key, RateValue = value,
            RateCategory = "PayParameter", EffectiveFrom = new DateOnly(2026, 1, 1), Status = CompanyPolicyStatuses.Active,
        });
    }

    private static Task<BorderNumberRule.Settings> Resolve(ZayraDbContext db, Guid? companyId, DateOnly? on = null)
        => BorderNumberRule.ResolveAsync(new CompanyRatePolicyResolver(db), new StatutoryRuleReader(db),
            Tenant, companyId, on ?? Period, CancellationToken.None);

    // ══════════════════════════ The window: 0 / 30 / 90 day boundaries ══════════════════════════

    [Theory]
    [InlineData(0, 0, false)]    // 0 = the Iqama is needed from the first payroll, even on the joining date
    [InlineData(0, 5, false)]
    [InlineData(30, 30, true)]   // last day of the window
    [InlineData(30, 31, false)]  // first day after
    [InlineData(90, 90, true)]
    [InlineData(90, 91, false)]
    [InlineData(180, 180, true)]
    [InlineData(180, 181, false)]
    public void Window_BoundariesFollowTheConfiguredDays(int graceDays, int daysAfterJoining, bool within)
    {
        var asOf = DateOnly.FromDateTime(Joined).AddDays(daysAfterJoining);
        BorderNumberRule.IsWithinWindow(Joined, graceDays, asOf).Should().Be(within);
    }

    [Fact]
    public void Window_UnknownJoiningDate_NeverOpens() =>
        BorderNumberRule.IsWithinWindow(default, 90, Period).Should().BeFalse();

    private static EmployeeReadinessSnapshot Snapshot(int graceDays, DateTime joined) => new()
    {
        EmployeeId = 1, CountryCode = "SA", Nationality = "Indian", EnglishName = "New Arrival", FullName = "New Arrival",
        JoiningDate = joined, BorderNumber = "3012345678", BorderNumberGraceDays = graceDays,
        GosiReference = "G", QiwaContractNumber = "Q", MuqeemNumber = "M", SponsorName = "S",
        WorkPermitNumber = "W", VisaNumber = "V", VisaFileNumber = "VF",
    };

    [Theory]
    [InlineData(0, 0, true)]      // 0 days: lapsed from day one
    [InlineData(30, 30, false)]
    [InlineData(30, 31, true)]
    [InlineData(90, 90, false)]
    [InlineData(90, 91, true)]
    public async Task Readiness_UsesTheConfiguredDays(int graceDays, int daysAfterJoining, bool iqamaBlocksPay)
    {
        await using var db = NewDb();
        var policy = await new EmployeeReadinessPolicyResolver(db).ResolveAsync(Tenant, null, "SA", "Indian");
        var asOf = DateOnly.FromDateTime(Joined).AddDays(daysAfterJoining);

        var r = new EmployeeReadinessEvaluator(db).Evaluate(Snapshot(graceDays, Joined), policy, asOf);

        r.PayBlocking.Any(i => i.Key == "IqamaNumber").Should().Be(iqamaBlocksPay);
        r.IsBlocked.Should().BeFalse("activation always accepts the border number");
    }

    // ══════════════════════════ Where the number comes from ══════════════════════════

    [Fact]
    public async Task Default_IsTheSeededDatedStatutoryRow_NinetyDays_WageIdOn()
    {
        await using var db = NewDb();
        await SeedRulesAsync(db);
        var row = await db.StatutoryRules.IgnoreQueryFilters().SingleAsync(r => r.RuleKey == BorderNumberRule.GraceDaysRuleKey);
        row.RuleValue.Should().Be("90");
        row.TenantId.Should().BeNull("a platform default, effective-dated");

        var s = await Resolve(db, CompanyA);
        s.GraceDays.Should().Be(90);
        s.UseAsWageFileId.Should().BeTrue();
    }

    [Fact]
    public async Task NoRowsAtAll_FallsBackToTheCompiledNinety()
    {
        await using var db = NewDb();
        (await Resolve(db, CompanyA)).Should().Be(BorderNumberRule.Settings.Default);
    }

    [Fact]
    public async Task CompanyOverride_BeatsTheDefault_AndOnlyForThatCompany()
    {
        await using var db = NewDb();
        await SeedRulesAsync(db);
        Rate(db, BorderNumberRule.GraceDaysRateKey, "30", CompanyA);
        await db.SaveChangesAsync();

        (await Resolve(db, CompanyA)).GraceDays.Should().Be(30);
        (await Resolve(db, CompanyB)).GraceDays.Should().Be(90, "another company keeps the platform default");
    }

    [Fact]
    public async Task CompanyOverride_BeatsTheTenantDefaultRate()
    {
        await using var db = NewDb();
        await SeedRulesAsync(db);
        Rate(db, BorderNumberRule.GraceDaysRateKey, "120", null);
        Rate(db, BorderNumberRule.GraceDaysRateKey, "0", CompanyA);
        await db.SaveChangesAsync();

        (await Resolve(db, CompanyA)).GraceDays.Should().Be(0);
        (await Resolve(db, CompanyB)).GraceDays.Should().Be(120);
    }

    [Fact]
    public async Task Override_IsClampedToTheSaneRange()
    {
        await using var db = NewDb();
        Rate(db, BorderNumberRule.GraceDaysRateKey, "5000", CompanyA);
        Rate(db, BorderNumberRule.GraceDaysRateKey, "-4", CompanyB);
        await db.SaveChangesAsync();

        (await Resolve(db, CompanyA)).GraceDays.Should().Be(BorderNumberRule.MaxGraceDays);
        (await Resolve(db, CompanyB)).GraceDays.Should().Be(BorderNumberRule.MinGraceDays);
    }

    [Fact]
    public async Task WageIdSwitch_OffPerCompany()
    {
        await using var db = NewDb();
        Rate(db, BorderNumberRule.UseAsWageFileIdRateKey, "Off", CompanyA);
        await db.SaveChangesAsync();

        (await Resolve(db, CompanyA)).UseAsWageFileId.Should().BeFalse();
        (await Resolve(db, CompanyB)).UseAsWageFileId.Should().BeTrue("default ON");
    }

    [Fact]
    public void TheSettingsAreRegisteredAndApplied_SoTheCompanyRatesScreenOffersThem()
    {
        var keys = StatutoryRateGuard.DefaultClientRateDefinitions(Tenant).ToDictionary(d => d.RateKey);
        keys[BorderNumberRule.GraceDaysRateKey].MinValue.Should().Be(0m);
        keys[BorderNumberRule.GraceDaysRateKey].MaxValue.Should().Be(180m);
        keys[BorderNumberRule.UseAsWageFileIdRateKey].DataType.Should().Be("string");
        StatutoryRateGuard.IsAppliedByPayroll(BorderNumberRule.GraceDaysRateKey).Should().BeTrue();
        StatutoryRateGuard.IsAppliedByPayroll(BorderNumberRule.UseAsWageFileIdRateKey).Should().BeTrue();
        StatutoryRateGuard.IsStatutoryKey(BorderNumberRule.GraceDaysRateKey).Should().BeFalse("it is a company rate, not Surface-B");
    }

    // ══════════════════════════ The loader applies the company's days, at the right date ══════════════════════════

    private static Employee SaEmployee(int id, Guid companyId, string iqama = "", string border = "3012345678") => new()
    {
        Id = id, TenantId = Tenant, CompanyId = companyId, EmployeeCode = $"E{id}", FullName = $"Emp {id}", EnglishName = $"Emp {id}",
        CountryCode = "SA", Nationality = "Indian", Status = "Active", JoiningDate = Joined,
        IqamaNumber = iqama, BorderNumber = border,
    };

    [Fact]
    public async Task SnapshotLoader_CarriesEachCompanysDays()
    {
        await using var db = NewDb();
        Rate(db, BorderNumberRule.GraceDaysRateKey, "30", CompanyA);
        db.Employees.AddRange(SaEmployee(1, CompanyA), SaEmployee(2, CompanyB));
        await db.SaveChangesAsync();

        var snaps = await new EmployeeReadinessEvaluator(db).LoadSnapshotsAsync(Tenant, new[] { 1, 2 }, CancellationToken.None, Period);

        snaps[1].BorderNumberGraceDays.Should().Be(30);
        snaps[2].BorderNumberGraceDays.Should().Be(90);
    }

    [Fact]
    public async Task PeriodEnd_NotToday_DecidesTheWindow()
    {
        // Joined 2026-04-01, 90 days ⇒ the window closes 2026-06-30. Asked "today" (any later date) the border
        // number is lapsed; for the June period it is still inside; for the July period it is lapsed.
        await using var db = NewDb();
        var policy = await new EmployeeReadinessPolicyResolver(db).ResolveAsync(Tenant, null, "SA", "Indian");
        var evaluator = new EmployeeReadinessEvaluator(db);
        var snap = Snapshot(90, Joined);

        evaluator.Evaluate(snap, policy, new DateOnly(2026, 6, 30)).PayBlocking.Should().NotContain(i => i.Key == "IqamaNumber");
        evaluator.Evaluate(snap, policy, new DateOnly(2026, 7, 31)).PayBlocking.Should().Contain(i => i.Key == "IqamaNumber");
        evaluator.Evaluate(snap, policy).PayBlocking.Should().Contain(i => i.Key == "IqamaNumber", "no asOf = today, long past the window");
        BorderNumberRule.PeriodEnd(2026, 6).Should().Be(new DateOnly(2026, 6, 30));
        BorderNumberRule.PeriodEnd(2026, 12).Should().Be(new DateOnly(2026, 12, 31));
    }

    // ══════════════════════════ The wage file ID ══════════════════════════

    [Fact]
    public async Task WageFileIds_SettingOn_InsideTheWindow_UsesTheBorderNumber()
    {
        await using var db = NewDb();
        var emp = SaEmployee(1, CompanyA);
        var ids = await BorderNumberRule.ResolveWageFileIdsAsync(db, Tenant, new[] { emp }, Period, CancellationToken.None);

        ids.Should().ContainKey(1);
        ids[1].Allowed.Should().BeTrue();
        ids[1].BorderNumber.Should().Be("3012345678");
    }

    [Fact]
    public async Task WageFileIds_SettingOff_InsideTheWindow_IsNotAllowed()
    {
        await using var db = NewDb();
        Rate(db, BorderNumberRule.UseAsWageFileIdRateKey, "Off", CompanyA);
        await db.SaveChangesAsync();

        var ids = await BorderNumberRule.ResolveWageFileIdsAsync(db, Tenant, new[] { SaEmployee(1, CompanyA) }, Period, CancellationToken.None);

        ids[1].Allowed.Should().BeFalse();
    }

    [Fact]
    public async Task WageFileIds_OutsideTheWindow_ThePeriodEndDecides_NotToday()
    {
        await using var db = NewDb();
        var emp = SaEmployee(1, CompanyA);

        (await BorderNumberRule.ResolveWageFileIdsAsync(db, Tenant, new[] { emp }, new DateOnly(2026, 6, 30), CancellationToken.None))
            .Should().ContainKey(1, "the June period ends on the last day of the window");
        (await BorderNumberRule.ResolveWageFileIdsAsync(db, Tenant, new[] { emp }, new DateOnly(2026, 7, 1), CancellationToken.None))
            .Should().BeEmpty("July is past the window: the Iqama is required, as before");
    }

    [Fact]
    public async Task WageFileIds_IgnoreAnEmployeeWithAnIqama_OrWithoutABorderNumber_OrOutsideKsa()
    {
        await using var db = NewDb();
        var withIqama = SaEmployee(1, CompanyA, iqama: "2123456789");
        var noBorder = SaEmployee(2, CompanyA, border: "");
        var uae = SaEmployee(3, CompanyA); uae.CountryCode = "AE";

        var ids = await BorderNumberRule.ResolveWageFileIdsAsync(db, Tenant, new[] { withIqama, noBorder, uae }, Period, CancellationToken.None);

        ids.Should().BeEmpty();
    }

    private static (PayrollRun, PayrollSlip, EmployeePayrollProfile) WpsInputs(Employee emp) => (
        new PayrollRun { Id = Guid.NewGuid(), TenantId = Tenant, Year = 2026, Month = 6, Status = "Locked", TotalNetSalary = 5000m, TotalGrossSalary = 6000m },
        new PayrollSlip { Id = Guid.NewGuid(), TenantId = Tenant, EmployeeId = emp.Id, EmployeeCode = emp.EmployeeCode, NetSalary = 5000m, GrossSalary = 6000m, Status = "Final" },
        new EmployeePayrollProfile { Id = Guid.NewGuid(), TenantId = Tenant, EmployeeId = emp.Id, Iban = "SA0380000000608010167519" });

    [Fact]
    public void WpsValidator_BorderNumberAllowed_WritesAWarningNamingTheEmployee_NotAnError()
    {
        var emp = SaEmployee(1, CompanyA); emp.IdNumber = string.Empty;
        var (run, slip, profile) = WpsInputs(emp);
        var ids = new Dictionary<int, BorderNumberRule.WageFileBorderId> { [1] = new("3012345678", true, 90) };

        var result = WpsSifValidator.Validate(run, new[] { slip }, new[] { profile }, new[] { emp }, null, ids);

        result.Warnings.Should().Contain(w => w.Code == BorderNumberRule.BorderNumberUsedAsIdCode && w.Message.Contains("E1"));
        result.BlockingErrors.Should().NotContain(e => e.Code == "MISSING_ID_NUMBER");
    }

    [Fact]
    public void WpsValidator_BorderNumberNotAllowed_HoldsTheEmployeeWithAClearMessage()
    {
        var emp = SaEmployee(1, CompanyA); emp.IdNumber = string.Empty;
        var (run, slip, profile) = WpsInputs(emp);
        var ids = new Dictionary<int, BorderNumberRule.WageFileBorderId> { [1] = new("3012345678", false, 90) };

        var result = WpsSifValidator.Validate(run, new[] { slip }, new[] { profile }, new[] { emp }, null, ids);

        result.CanExport.Should().BeFalse();
        result.BlockingErrors.Should().Contain(e => e.Code == BorderNumberRule.BorderNumberNotAllowedAsIdCode && e.Message.Contains("E1")
            && e.Message.Contains("does not use the Border Number"));
    }

    [Fact]
    public void WpsValidator_OutsideTheWindow_StillRequiresTheId()
    {
        var emp = SaEmployee(1, CompanyA); emp.IdNumber = string.Empty;
        var (run, slip, profile) = WpsInputs(emp);

        var result = WpsSifValidator.Validate(run, new[] { slip }, new[] { profile }, new[] { emp }, null,
            new Dictionary<int, BorderNumberRule.WageFileBorderId>());

        result.BlockingErrors.Should().Contain(e => e.Code == "MISSING_ID_NUMBER");
    }

    // ══════════════════════════ Recruitment hire path ══════════════════════════

    [Fact]
    public void DraftToEmployee_CarriesTheBorderNumber()
    {
        var draft = new EmployeeDraft { TenantId = Tenant, EnglishName = "Hire", BorderNumber = "3012345678", Nationality = "Indian" };
        var placementType = typeof(EmployeesController).GetNestedType("DraftPlacement", BindingFlags.NonPublic)!;
        var placement = Activator.CreateInstance(placementType,
            null, string.Empty, null, string.Empty, null, string.Empty, CompanyA, "SA", null, string.Empty);
        var from = typeof(EmployeesController).GetMethod("EmployeeFromDraft", BindingFlags.NonPublic | BindingFlags.Static)!;

        var employee = (Employee)from.Invoke(null, new[] { draft, Tenant, placement, DateTime.UtcNow })!;

        employee.BorderNumber.Should().Be("3012345678");
    }

    [Fact]
    public void DraftRequest_SetsTheBorderNumber_AndAnOmittedOneKeepsIt()
    {
        var controller = (EmployeesController)RuntimeHelpers.GetUninitializedObject(typeof(EmployeesController));
        var apply = typeof(EmployeesController).GetMethod("ApplyDraft", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var draft = new EmployeeDraft { TenantId = Tenant };

        apply.Invoke(controller, new object[] { draft, EmptyRequest() with { BorderNumber = "3012345678" } });
        draft.BorderNumber.Should().Be("3012345678");

        apply.Invoke(controller, new object[] { draft, EmptyRequest() });
        draft.BorderNumber.Should().Be("3012345678", "an update that does not mention it leaves it alone");
    }

    [Fact]
    public void DraftDto_ExposesTheBorderNumber_OnlyToTheSensitiveView()
    {
        var draft = new EmployeeDraft { TenantId = Tenant, BorderNumber = "3012345678" };

        EmployeeDraftDto.Project(draft, includeSensitive: true).BorderNumber.Should().Be("3012345678");
        EmployeeDraftDto.Project(draft, includeSensitive: false).BorderNumber.Should().BeEmpty();
    }

    // Every positional parameter null: an update that mentions nothing.
    private static EmployeeDraftRequest EmptyRequest()
        => (EmployeeDraftRequest)RuntimeHelpers.GetUninitializedObject(typeof(EmployeeDraftRequest));
}
