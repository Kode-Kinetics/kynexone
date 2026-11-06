using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.CountryPack.Qatar;
using Zayra.Api.Infrastructure.CountryPack.Uae;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// A customer-defined monthly cash allowance (<c>ALW_*</c>) whose definition says it counts in the end-of-service wage is ADDED to the
/// country pack's wage, never folded into the configured wage the pack takes the greater of with its statutory base (where it was lost
/// whenever statute was the larger). KSA adds it; UAE (Art. 51) and Qatar (Art. 54) award on basic by law, so they do not, and say so.
/// The calculation endpoint, the final settlement and the monthly provision all measure the same wage.
/// </summary>
public class CashAllowanceEosbWageTests
{
    private const string Car = "ALW_CAR";

    // ══ the packs ═══════════════════════════════════════════════════════════════════════════════

    private static readonly DateOnly Start = new(2014, 1, 1);
    private static readonly DateOnly End = new(2025, 12, 31);

    private static EndOfServiceInput Input(SalaryBreakdown salary, decimal configured = 0m, decimal additional = 0m, params string[] codes) =>
        new(Guid.NewGuid(), Guid.NewGuid(), salary, Start, End, "Termination", "Indefinite", "SAU")
        {
            ConfiguredEosbWage = configured,
            AdditionalEosbWage = additional,
            AdditionalEosbWageCodes = codes,
        };

    [Fact]
    public async Task Ksa_TheAllowance_IsAddedOnTopOfTheStatutoryBase_WhenStatuteIsTheLarger()
    {
        // The catalogue flags basic only: configured 5,000. Statute is 5,000 + 2,000 + 0 = 7,000 (the larger). The 500 allowance used to vanish.
        var calc = new KsaEndOfServiceCalculator(new StubRuleReader());

        var result = await calc.CalculateAsync(Input(new SalaryBreakdown(5_000m, 2_000m, 0m, 0m), configured: 5_000m, additional: 500m, Car));

        result.AppliedWageBase.Should().Be(7_500m);
        result.Notices.Should().Contain(n => n.Contains(Car) && n.Contains("500.00") && n.Contains("ADDED"));
    }

    [Fact]
    public async Task Ksa_TheAllowance_IsAddedOnTopOfAConfiguredWage_WhenTheConfiguredWageIsTheLarger()
    {
        var calc = new KsaEndOfServiceCalculator(new StubRuleReader());

        var result = await calc.CalculateAsync(Input(new SalaryBreakdown(10_000m, 2_000m, 0m, 0m), configured: 14_000m, additional: 500m, Car));

        result.AppliedWageBase.Should().Be(14_500m);
    }

    [Fact]
    public async Task Ksa_NoCatalogueFlag_TransportSwitchedOff_AddsTheAllowance_NotTheTransport()
    {
        var calc = new KsaEndOfServiceCalculator(new StubRuleReader().Set("eosb.include_transport", "false"));

        var result = await calc.CalculateAsync(Input(new SalaryBreakdown(5_000m, 2_000m, 1_000m, 0m), configured: 0m, additional: 500m, Car));

        result.AppliedWageBase.Should().Be(7_500m, "basic + housing + the allowance; the company switched transport off");
    }

    [Fact]
    public async Task Ksa_WithoutAnAdditionalWage_EveryFigureIsAsBefore_AndNoAllowanceNoticeAppears()
    {
        var calc = new KsaEndOfServiceCalculator(new StubRuleReader());
        var package = new SalaryBreakdown(5_000m, 2_000m, 1_000m, 0m);

        var result = await calc.CalculateAsync(Input(package, configured: 5_000m));

        result.AppliedWageBase.Should().Be(8_000m);
        result.Notices.Should().NotContain(n => n.Contains("allowance(s)", StringComparison.OrdinalIgnoreCase) && n.Contains("end-of-service wage"));
    }

    public static IEnumerable<object[]> BasicOnlyPacks()
    {
        yield return new object[] { "UAE-mainland", new UaeMainlandEndOfServiceCalculator(new StubRuleReader()), "Art. 51" };
        yield return new object[] { "UAE-DIFC", new UaeDifcEndOfServiceCalculator(new StubRuleReader()), "basic" };
        yield return new object[] { "Qatar", new QatarEndOfServiceCalculator(new StubRuleReader()), "Art. 54" };
    }

    [Theory]
    [MemberData(nameof(BasicOnlyPacks))]
    public async Task BasicOnlyPacks_DoNotCountTheAllowance_AndSayWhy(string name, IEndOfServiceCalculator calc, string law)
    {
        var result = await calc.CalculateAsync(Input(new SalaryBreakdown(10_000m, 2_000m, 1_000m, 0m), configured: 10_000m, additional: 500m, Car));

        result.AppliedWageBase.Should().Be(10_000m, $"{name} awards on basic by law");
        result.Notices.Should().Contain(n => n.Contains(Car) && n.Contains("NOT counted") && n.Contains(law));
    }

    [Theory]
    [MemberData(nameof(BasicOnlyPacks))]
    public async Task BasicOnlyPacks_WithoutAnAllowance_AddNoNotice(string name, IEndOfServiceCalculator calc, string law)
    {
        _ = law;
        var result = await calc.CalculateAsync(Input(new SalaryBreakdown(10_000m, 2_000m, 1_000m, 0m), configured: 10_000m));

        result.Notices.Should().NotContain(n => n.Contains("NOT counted"), name);
    }

    // ══ the three consumers, end to end ═════════════════════════════════════════════════════════

    private sealed class World : IAsyncDisposable
    {
        public ZayraDbContext Db = null!;
        public SqliteConnection Conn = null!;
        public Guid Tid;
        public Guid CompanyId;
        public Employee A = null!;
        public PayrollController Ctrl = null!;
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await Conn.DisposeAsync(); }
    }

    /// <summary>Resolves the real KSA, UAE and Qatar end-of-service packs (the shared test controller only ever has the no-op one).</summary>
    private sealed class RealEosbPacks : ICountryPackResolver
    {
        private readonly StubRuleReader _rules;
        public RealEosbPacks(StubRuleReader rules) => _rules = rules;
        public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j) => new DefaultStatutoryDeductionCalculator();
        public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => cc switch
        {
            "SAU" or "SA" => new KsaEndOfServiceCalculator(_rules),
            "ARE" => new UaeMainlandEndOfServiceCalculator(_rules),
            "QAT" => new QatarEndOfServiceCalculator(_rules),
            _ => new DefaultEndOfServiceCalculator(),
        };
        public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
        public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
        public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
        public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
    }

    /// <summary>
    /// A KSA (or other) company, one employee on 10,000 basic + 2,000 housing + 1,000 transport who joined 2023-01-01, and a catalogue that
    /// flags only the BASIC component into the end-of-service wage (so the statutory base, 13,000, is the larger of the pair).
    /// </summary>
    private static async Task<World> NewWorld(string country = "SAU", bool eosbAllowance = true, bool giveAllowance = true,
        bool transportOff = false, bool catalogueFlagsAnything = true)
    {
        var w = new World { Tid = Guid.NewGuid() };
        (w.Db, w.Conn) = PayrollProrationArrearsTests.CreateDb();
        (w.CompanyId, w.A, _) = await PayrollProrationArrearsTests.SeedAsync(w.Db, w.Tid);
        var company = await w.Db.Companies.SingleAsync(c => c.Id == w.CompanyId);
        company.CountryCode = country;
        company.Jurisdiction = country == "SAU" ? "KSA-mainland" : country == "ARE" ? "UAE-mainland" : "QAT-mainland";

        w.Db.GlDrivers.Add(new GlDriver
        {
            TenantId = w.Tid, CompanyId = null, Key = "EARN:CAR", Label = "Car allowance", Category = GlDriverCategories.Earning, PostingSide = "DR",
            DefaultCode = "5210", DefaultName = "Car allowance Expense", MatchMode = "Exact", MatchComponentCode = Car, SortOrder = 60,
        });
        w.Db.PayComponents.Add(new PayComponent
        {
            TenantId = w.Tid, CompanyId = null, Code = Car, NameEn = "Car allowance", NameAr = "car", ComponentType = PayComponentTypes.Earning,
            CalcMethod = PayComponentCalcMethods.GradePackage, FinanceConfirmedAtUtc = DateTime.UtcNow, EntitlementClass = PayEntitlementClasses.QiwaWage, StatutoryFloor = PayStatutoryFloors.None,
            GlDriverKey = "EARN:CAR", GosiSubject = false, WpsIncluded = true, IsTaxable = false, EosbIncluded = eosbAllowance, DisplayOrder = 9100,
        });
        // The shipped catalogue flags BASIC and HOUSING; this company flags BASIC only (or, when asked, nothing at all).
        foreach (var seed in PayComponentCatalog.SystemComponentSeeds(w.Tid).Where(c => c.Code is "BASIC" or "HOUSING" or "TRANSPORT"))
        {
            seed.EosbIncluded = catalogueFlagsAnything && seed.Code == "BASIC";
            w.Db.PayComponents.Add(seed);
        }
        w.Db.GCCComplianceSettings.Add(new GCCComplianceSetting { TenantId = w.Tid, CountryCode = country, EosbEnabled = true, EosbMinYears = 1 });
        await w.Db.SaveChangesAsync();

        if (giveAllowance)
        {
            var salary = await w.Db.EmployeeSalaryStructures.OrderByDescending(x => x.EffectiveDate).FirstAsync(x => x.EmployeeId == w.A.Id);
            w.Db.EmployeeSalaryAllowances.Add(new EmployeeSalaryAllowance
            {
                TenantId = w.Tid, SalaryStructureId = salary.Id, Code = Car, Amount = 500m, Basis = AllowanceBases.Amount,
            });
            await w.Db.SaveChangesAsync();
        }

        var rules = new StubRuleReader();
        if (transportOff) rules.Set("eosb.include_transport", "false");
        w.Ctrl = PayrollProrationArrearsTests.Ctrl(w.Db, w.Tid);
        typeof(PayrollController).GetField("_packResolver", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(w.Ctrl, new RealEosbPacks(rules));
        return w;
    }

    private static T? Prop<T>(IActionResult result, string name)
    {
        var ok = result.Should().BeOfType<OkObjectResult>(result is ObjectResult o ? $"{o.StatusCode}: {o.Value}" : result.GetType().Name).Subject;
        return (T?)ok.Value!.GetType().GetProperty(name)?.GetValue(ok.Value);
    }

    private static Task<IActionResult> CalcAsync(World w) =>
        w.Ctrl.CalculateEosb(new EosbCalculationRequest(w.A.Id, new DateTime(2026, 6, 30), "Termination"), CancellationToken.None);

    private static async Task<IActionResult> SettleAsync(World w)
    {
        w.Db.EmployeeOffboardings.Add(new EmployeeOffboarding
        {
            TenantId = w.Tid, EmployeeId = w.A.Id, SeparationType = "Termination", Status = "InProgress",
            LastWorkingDay = new DateOnly(2026, 6, 30), CreatedAtUtc = DateTime.UtcNow,
        });
        await w.Db.SaveChangesAsync();
        return await w.Ctrl.FinalSettlement(new FinalSettlementRequest(w.A.Id, new DateOnly(2026, 6, 30), 0, "Termination"), CancellationToken.None);
    }

    /// <summary>The wage the settlement's own persisted snapshot says the pack awarded on.</summary>
    private static async Task<decimal> SettledWageBaseAsync(World w)
    {
        var json = await w.Db.EmployeeFinalSettlements.AsNoTracking().Where(x => x.EmployeeId == w.A.Id).Select(x => x.InputsSnapshotJson).SingleAsync();
        return System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("appliedWageBase").GetDecimal();
    }

    private static async Task<decimal> ProvisionWageBaseAsync(World w)
    {
        (await w.Db.Employees.SingleAsync(e => e.Id == w.A.Id)).JoiningDate = new DateTime(2023, 7, 1);
        w.Db.CompanyRatePolicies.Add(new CompanyRatePolicy
        {
            TenantId = w.Tid, CompanyId = w.CompanyId, RateKey = EosbProvisioningRateKeys.Mode, RateCategory = "PayParameter",
            RateValue = EosbProvisioningRateKeys.StatutoryMonthly, DataType = "string", Unit = "enum",
            EffectiveFrom = new DateOnly(2026, 6, 1), Status = CompanyPolicyStatuses.Active,
        });
        var run = PayrollProrationArrearsTests.AddRun(w.Db, w.Tid, w.CompanyId, 2026, 6);
        await w.Db.SaveChangesAsync();
        await BonusLifecycleGlTests.LockRun(w.Db, w.Ctrl, run);
        return (await w.Db.EosbProvisionSnapshots.AsNoTracking().SingleAsync(s => s.EmployeeId == w.A.Id)).WageBase;
    }

    [Fact]
    public async Task Ksa_AllThreeConsumers_MeasureStatutoryPlusTheAllowance_WhenStatuteIsTheLarger()
    {
        await using var w = await NewWorld();

        var calc = await CalcAsync(w);
        var settlement = await SettleAsync(w);

        // Statute: 10,000 + 2,000 + 1,000 = 13,000, which beats the configured 10,000 (basic only). The 500 allowance is added: 13,500.
        Prop<decimal>(calc, "eligibleSalary").Should().Be(13_500m);
        (await SettledWageBaseAsync(w)).Should().Be(13_500m, "the settlement counts the allowance once");
        Prop<decimal>(settlement, "eosbAmount").Should().Be(Prop<decimal>(calc, "eosbAmount"), "one engine, one wage base");
        Prop<IReadOnlyList<string>>(calc, "wageBasis")!.Should().Contain(Car);
        (await ProvisionWageBaseAsync(w)).Should().Be(13_500m);
    }

    [Fact]
    public async Task Ksa_AnAllowanceTheDefinitionDoesNotCount_IsNotAdded()
    {
        await using var w = await NewWorld(eosbAllowance: false);

        var calc = await CalcAsync(w);
        var settlement = await SettleAsync(w);

        Prop<decimal>(calc, "eligibleSalary").Should().Be(13_000m);
        (await SettledWageBaseAsync(w)).Should().Be(13_000m);
        Prop<IReadOnlyList<string>>(calc, "wageBasis")!.Should().NotContain(Car);
        (await ProvisionWageBaseAsync(w)).Should().Be(13_000m);
    }

    [Fact]
    public async Task Ksa_AnEmployeeWithoutAnAllowance_IsMeasuredExactlyAsBefore()
    {
        await using var w = await NewWorld(giveAllowance: false);

        var calc = await CalcAsync(w);
        var settlement = await SettleAsync(w);

        Prop<decimal>(calc, "eligibleSalary").Should().Be(13_000m);
        (await SettledWageBaseAsync(w)).Should().Be(13_000m);
        Prop<decimal>(settlement, "eosbAmount").Should().Be(Prop<decimal>(calc, "eosbAmount"));
        Prop<IReadOnlyList<string>>(calc, "wageBasis")!.Should().Equal("BASIC");
        Prop<IReadOnlyList<string>>(calc, "statutoryNotices")!.Should().NotContain(n => n.Contains(Car));
        (await ProvisionWageBaseAsync(w)).Should().Be(13_000m);
    }

    [Fact]
    public async Task Ksa_NoCatalogueFlag_TransportOff_TheFallbackAddsTheAllowance_NotTheTransport()
    {
        await using var w = await NewWorld(transportOff: true, catalogueFlagsAnything: false);

        var calc = await CalcAsync(w);
        var settlement = await SettleAsync(w);

        // Statute with transport off: 12,000; the allowance: +500. Not 13,500 (transport must not come back through the fallback).
        Prop<decimal>(calc, "eligibleSalary").Should().Be(12_500m);
        (await SettledWageBaseAsync(w)).Should().Be(12_500m);
        Prop<IReadOnlyList<string>>(calc, "statutoryNotices")!.Should().Contain(n => n.Contains("No active pay component"), "the missing catalogue flag is still announced");
        (await ProvisionWageBaseAsync(w)).Should().Be(12_500m);
    }

    [Fact]
    public async Task Uae_TheAllowanceIsNotCounted_AndTheCalculationAndSettlementSayWhy()
    {
        await using var w = await NewWorld(country: "ARE");

        var calc = await CalcAsync(w);
        var settlement = await SettleAsync(w);

        Prop<decimal>(calc, "eligibleSalary").Should().Be(10_000m);
        (await SettledWageBaseAsync(w)).Should().Be(10_000m);
        Prop<decimal>(settlement, "eosbAmount").Should().Be(Prop<decimal>(calc, "eosbAmount"));
        Prop<IReadOnlyList<string>>(calc, "statutoryNotices")!.Should().Contain(n => n.Contains(Car) && n.Contains("NOT counted"));
        Prop<List<string>>(settlement, "warnings")!.Should().Contain(n => n.Contains(Car) && n.Contains("NOT counted"));
        // (The monthly provision is not exercised here: the test harness has no UAE payroll pack, so Process refuses the run before the provision is measured.
        // The provision reads the same AppliedWageBase, which the UAE pack sets to basic.)
    }
}
