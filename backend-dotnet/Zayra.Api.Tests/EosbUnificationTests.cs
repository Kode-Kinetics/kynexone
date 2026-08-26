// EosbUnificationTests.cs
// ─────────────────────────────────────────────────────────────────────────────
// POD-A2 — EOSB engine unification.
//
// Proves /eosb/calculate and /final-settlement now share ONE authoritative engine
// (the country-pack IEndOfServiceCalculator) and are Art.80/84/85-correct:
//
//  1. PARITY   — identical (basic, dates, reason, country) → identical eosbAmount on
//                both endpoints, parametrized over reasons × tenures.
//  2. KSA GOLD — 3yr termination = 15,000; 3yr resignation = 5,000 (⅓); Art.80 = 0.
//  3. SHORT    — 0.5yr TERMINATION = 2,500 (pro-rata, pre-gate removed); 0.5yr
//                RESIGNATION = 0 (pack Art.85 forfeiture, not the deleted controller gate).
//  4. REASON   — /final-settlement sources the reason from EmployeeOffboarding.SeparationType
//                when none is passed (a recorded resignation is discounted WITHOUT an
//                explicit override); an explicit request reason overrides it.
//  5. COMPAT   — no reason + no offboarding record → "Termination" (full award); the full
//                JSON contract (proRataSalary/leaveEncashment/noticePeriodDeduction/breakdown)
//                is unchanged.
//  6. BOUNDARY — Art.85 exactly-5.0-year resignation → ⅓ (not ⅔).
// ─────────────────────────────────────────────────────────────────────────────

using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public class EosbUnificationTests
{
    private const decimal Basic = 10_000m;

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static PayrollController MakeCtrl(ZayraDbContext db, Guid tenantId)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "Test User"),
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var httpCtx = new DefaultHttpContext { User = principal };

        var ctrl = new PayrollController(
            db,
            new _EosbUnrestrictedScope(),
            new _EosbHttpAccessor(httpCtx),
            new _EosbNullNotifications(),
            new _EosbKsaPackResolver(new StubRuleReader()),
            new StubRuleReader(),
            new _EosbNullLetterService(),
            new NullDocumentStorage(),
            new Zayra.Api.Infrastructure.Documents.PdfRenderGate(1));
        ctrl.ControllerContext = new ControllerContext { HttpContext = httpCtx };
        return ctrl;
    }

    // Seeds one KSA employee (EOSB enabled) joining on `joiningDate` with basic SAR 10,000.
    private static int SeedKsaEmployee(ZayraDbContext db, Guid tenantId, DateTime joiningDate)
    {
        // POD-C1 refuses to guess a settlement's legal entity (it decides which chart-of-accounts
        // overrides apply and which GL period-close row binds), so the fixture now states one
        // explicitly rather than leaving the employee entity-less.
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = "Test KSA Entity", CountryCode = "SA",
            DefaultCurrency = "SAR", IsActive = true,
        };
        db.Companies.Add(company);
        var emp = new Employee
        {
            TenantId = tenantId, EmployeeCode = "E001", FullName = "Ahmed Al-Rashidi",
            Status = "Active", Nationality = "SAU", JoiningDate = joiningDate,
            CompanyId = company.Id,
        };
        db.Employees.Add(emp);
        db.GCCComplianceSettings.Add(new GCCComplianceSetting
        {
            TenantId = tenantId, CountryCode = "SA", EosbEnabled = true, EosbMinYears = 1
        });
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = emp.Id,
            BasicSalary = Basic, Currency = "SAR",
            EffectiveDate = DateOnly.FromDateTime(joiningDate), IsActive = true,
        });
        db.SaveChanges();
        return emp.Id;
    }

    // POD-C1 hardened /final-settlement so it can only settle a real leaver: the last working day is
    // now read from the offboarding record and never from the request body ("not_a_leaver" otherwise).
    // These POD-A2 tests predate that rule, so each records the separation it was always implying.
    // The EOSB assertions below are unchanged — this only supplies the leaver context the endpoint
    // now (correctly) insists on before it will accrue a payable.
    private static void SeedLeaver(ZayraDbContext db, Guid tenantId, int empId, DateOnly lastWorkingDay,
        string separationType = "Termination")
    {
        db.EmployeeOffboardings.Add(new EmployeeOffboarding
        {
            TenantId = tenantId, EmployeeId = empId, SeparationType = separationType,
            Status = "InProgress", LastWorkingDay = lastWorkingDay, CreatedAtUtc = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    private static decimal EosbFrom(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var body = ok.Value!;
        return (decimal)body.GetType().GetProperty("eosbAmount")!.GetValue(body)!;
    }

    private static T? Prop<T>(IActionResult result, string name)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var body = ok.Value!;
        return (T?)body.GetType().GetProperty(name)?.GetValue(body);
    }

    // ── 1. PARITY: both endpoints → identical eosbAmount for identical inputs ──────

    [Theory]
    // (joinYear, joinMonth, asOfYear, asOfMonth, reason) — whole-month tenures so the
    // ServicePeriod remainder-day fraction is 0 and the two surfaces line up exactly.
    [InlineData(2022, 7, 2024, 1, "Termination")]  // 1.5 yr
    [InlineData(2022, 7, 2024, 1, "Resignation")]  // 1.5 yr → <2yr resignation = 0
    [InlineData(2020, 1, 2023, 1, "Termination")]  // 3 yr
    [InlineData(2020, 1, 2023, 1, "Resignation")]  // 3 yr → ⅓
    [InlineData(2018, 1, 2025, 7, "Termination")]  // 7.5 yr
    [InlineData(2018, 1, 2025, 7, "Resignation")]  // 7.5 yr → ⅔
    [InlineData(2011, 1, 2023, 1, "Termination")]  // 12 yr
    [InlineData(2011, 1, 2023, 1, "Resignation")]  // 12 yr → full
    public async Task BothEndpoints_ProduceIdenticalEosb_ForIdenticalInputs(
        int jy, int jm, int ay, int am, string reason)
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var joining = new DateTime(jy, jm, 1, 0, 0, 0, DateTimeKind.Utc);
        var asOf    = new DateTime(ay, am, 1, 0, 0, 0, DateTimeKind.Utc);
        var empId = SeedKsaEmployee(db, tenantId, joining);
        SeedLeaver(db, tenantId, empId, DateOnly.FromDateTime(asOf), reason);
        var ctrl = MakeCtrl(db, tenantId);

        var eosbResult = await ctrl.CalculateEosb(
            new EosbCalculationRequest(empId, asOf, reason), CancellationToken.None);
        var settlementResult = await ctrl.FinalSettlement(
            new FinalSettlementRequest(empId, DateOnly.FromDateTime(asOf), 0, reason), CancellationToken.None);

        EosbFrom(settlementResult).Should().Be(EosbFrom(eosbResult),
            "both endpoints route through the same country-pack engine");
    }

    // ── 2. KSA golden numbers on the newly-unified /final-settlement surface ───────

    [Fact]
    public async Task FinalSettlement_ThreeYearTermination_Returns15000()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var empId = SeedKsaEmployee(db, tenantId, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        SeedLeaver(db, tenantId, empId, new DateOnly(2023, 1, 1), "Termination");
        var ctrl = MakeCtrl(db, tenantId);

        var result = await ctrl.FinalSettlement(
            new FinalSettlementRequest(empId, new DateOnly(2023, 1, 1), 0, "Termination"),
            CancellationToken.None);

        EosbFrom(result).Should().Be(15_000m, "3 × 0.5 × 10,000 (Art.84 tier-1); kills the old ~10,000 inline underpay");
    }

    [Fact]
    public async Task FinalSettlement_ThreeYearResignation_AppliesArt85Discount_Returns5000()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var empId = SeedKsaEmployee(db, tenantId, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        SeedLeaver(db, tenantId, empId, new DateOnly(2023, 1, 1), "Resignation");
        var ctrl = MakeCtrl(db, tenantId);

        var result = await ctrl.FinalSettlement(
            new FinalSettlementRequest(empId, new DateOnly(2023, 1, 1), 0, "Resignation"),
            CancellationToken.None);

        EosbFrom(result).Should().Be(5_000m, "⅓ of 15,000 (Art.85) — previously impossible on final-settlement");
    }

    [Fact]
    public async Task FinalSettlement_Article80Dismissal_ForfeitsGratuity_ReturnsZero()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var empId = SeedKsaEmployee(db, tenantId, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        SeedLeaver(db, tenantId, empId, new DateOnly(2023, 1, 1), "Article80");
        var ctrl = MakeCtrl(db, tenantId);

        var result = await ctrl.FinalSettlement(
            new FinalSettlementRequest(empId, new DateOnly(2023, 1, 1), 0, "Article80"),
            CancellationToken.None);

        EosbFrom(result).Should().Be(0m, "Art.80 grave-fault dismissal forfeits EOSB entirely");
    }

    // ── 3. Short-service TERMINATION pro-rata (controller pre-gate removed) ────────

    [Fact]
    public async Task FinalSettlement_HalfYearTermination_IsProRated_NotZeroed()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var empId = SeedKsaEmployee(db, tenantId, new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        SeedLeaver(db, tenantId, empId, new DateOnly(2023, 7, 1), "Termination");
        var ctrl = MakeCtrl(db, tenantId);

        var result = await ctrl.FinalSettlement(
            new FinalSettlementRequest(empId, new DateOnly(2023, 7, 1), 0, "Termination"),
            CancellationToken.None);

        EosbFrom(result).Should().Be(2_500m,
            "0.5 × 0.5 × 10,000 — Art.84 pays pro-rata from day one for employer termination");
    }

    [Fact]
    public async Task FinalSettlement_HalfYearResignation_IsForfeitedByPack_ReturnsZero()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var empId = SeedKsaEmployee(db, tenantId, new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        SeedLeaver(db, tenantId, empId, new DateOnly(2023, 7, 1), "Resignation");
        var ctrl = MakeCtrl(db, tenantId);

        var result = await ctrl.FinalSettlement(
            new FinalSettlementRequest(empId, new DateOnly(2023, 7, 1), 0, "Resignation"),
            CancellationToken.None);

        EosbFrom(result).Should().Be(0m, "Art.85 <2yr resignation forfeiture is owned by the pack, not the deleted gate");
    }

    // Same short-service pro-rata must now also hold on the live /eosb surface.
    [Fact]
    public async Task Eosb_HalfYearTermination_NoLongerZeroedByMinYearsGate()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var empId = SeedKsaEmployee(db, tenantId, new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var ctrl = MakeCtrl(db, tenantId);

        var result = await ctrl.CalculateEosb(
            new EosbCalculationRequest(empId, new DateTime(2023, 7, 1, 0, 0, 0, DateTimeKind.Utc), "Termination"),
            CancellationToken.None);

        EosbFrom(result).Should().Be(2_500m, "pre-gate removed; short-service termination is pro-rated");
    }

    // ── 4. Reason sourced from EmployeeOffboarding when none is passed (MF-1) ──────

    [Fact]
    public async Task FinalSettlement_NoExplicitReason_SourcesResignationFromOffboardingRecord()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var empId = SeedKsaEmployee(db, tenantId, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        db.EmployeeOffboardings.Add(new EmployeeOffboarding
        {
            TenantId = tenantId, EmployeeId = empId, SeparationType = "Resignation",
            Status = "InProgress", LastWorkingDay = new DateOnly(2023, 1, 1), CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var ctrl = MakeCtrl(db, tenantId);

        // No TerminationReason on the request → must pick up "Resignation" from the record.
        var result = await ctrl.FinalSettlement(
            new FinalSettlementRequest(empId, new DateOnly(2023, 1, 1)),
            CancellationToken.None);

        EosbFrom(result).Should().Be(5_000m, "recorded resignation is discounted per Art.85 without an explicit override");
        Prop<string>(result, "terminationReason").Should().Be("Resignation");
    }

    [Fact]
    public async Task FinalSettlement_ExplicitReason_OverridesOffboardingRecord()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var empId = SeedKsaEmployee(db, tenantId, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        db.EmployeeOffboardings.Add(new EmployeeOffboarding
        {
            TenantId = tenantId, EmployeeId = empId, SeparationType = "Resignation",
            Status = "InProgress", LastWorkingDay = new DateOnly(2023, 1, 1), CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var ctrl = MakeCtrl(db, tenantId);

        var result = await ctrl.FinalSettlement(
            new FinalSettlementRequest(empId, new DateOnly(2023, 1, 1), 0, "Termination"),
            CancellationToken.None);

        EosbFrom(result).Should().Be(15_000m, "explicit request reason takes precedence over the offboarding record");
        Prop<string>(result, "terminationReason").Should().Be("Termination");
    }

    // ── 5. Leaver gate + response-contract compatibility ──────────────────────────
    //
    // CONTRACT CHANGE OF RECORD (POD-C1, deliberate — supersedes the POD-A2 expectation):
    // /final-settlement used to settle ANY employee, including a fully Active one with no separation
    // on record, defaulting the reason to "Termination" and awarding the full gratuity. Once the
    // endpoint began accruing a real payable and posting a journal, settling a non-leaver stopped
    // being a harmless display default and became an unbacked liability. The endpoint now refuses,
    // and the last working day is sourced from the offboarding record rather than the request body
    // (so a caller cannot invent one). The two tests below pin BOTH halves of that rule.

    [Fact]
    public async Task FinalSettlement_NoOffboardingRecord_IsRefused_RatherThanSettlingANonLeaver()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var empId = SeedKsaEmployee(db, tenantId, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var ctrl = MakeCtrl(db, tenantId);

        var result = await ctrl.FinalSettlement(
            new FinalSettlementRequest(empId, new DateOnly(2023, 1, 1)),
            CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        bad.Value!.GetType().GetProperty("error")!.GetValue(bad.Value)
            .Should().Be("not_a_leaver", "accruing a payable for someone who is not leaving is an unbacked liability");
        db.EmployeeFinalSettlements.Should().BeEmpty("a refused settlement must persist nothing");
    }

    [Fact]
    public async Task FinalSettlement_NoExplicitReason_DefaultsToRecordedTermination_ContractUnchanged()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var empId = SeedKsaEmployee(db, tenantId, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        SeedLeaver(db, tenantId, empId, new DateOnly(2023, 1, 1), "Termination");
        var ctrl = MakeCtrl(db, tenantId);

        var result = await ctrl.FinalSettlement(
            new FinalSettlementRequest(empId, new DateOnly(2023, 1, 1)),
            CancellationToken.None);

        EosbFrom(result).Should().Be(15_000m, "recorded termination takes the full Art.84 award");
        Prop<string>(result, "terminationReason").Should().Be("Termination");

        // Pre-C1 response contract still present at the SAME paths — this is what downstream callers bind to.
        var ok = Assert.IsType<OkObjectResult>(result);
        var body = ok.Value!;
        var t = body.GetType();
        t.GetProperty("proRataSalary").Should().NotBeNull();
        t.GetProperty("leaveEncashment").Should().NotBeNull();
        t.GetProperty("noticePeriodDeduction").Should().NotBeNull();
        t.GetProperty("totalYears").Should().NotBeNull();
        var breakdown = ((System.Collections.IEnumerable)t.GetProperty("breakdown")!.GetValue(body)!)
            .Cast<object>().ToList();
        breakdown.Should().NotBeEmpty("the settlement must itemise what it is paying");
        breakdown.Select(b => (string)b.GetType().GetProperty("component")!.GetValue(b)!)
            .Should().Contain(c => c.Contains("Service", StringComparison.OrdinalIgnoreCase)
                                || c.Contains("Gratuity", StringComparison.OrdinalIgnoreCase),
                "the gratuity must appear as its own itemised line");
    }

    // ── 6. Art.85 exactly-5.0-year boundary (SF-4): 5.0yr resignation → ⅓, not ⅔ ──

    [Fact]
    public async Task KsaPack_FivePointZeroYearResignation_TakesOneThirdBand()
    {
        var calc = new KsaEndOfServiceCalculator(new StubRuleReader());
        var input = new EndOfServiceInput(
            Guid.NewGuid(), Guid.NewGuid(),
            new SalaryBreakdown(10_000m, 0m, 0m, 0m),
            new DateOnly(2018, 1, 1), new DateOnly(2023, 1, 1), // exactly 60 months = 5.0 yrs
            "Resignation", "Unlimited", "SAU");

        var result = await calc.CalculateAsync(input);

        // Full award = 5 × 0.5 × 10,000 = 25,000; Art.85 "2–5 yrs inclusive" → ⅓ = 8,333.33
        result.TotalGratuity.Should().Be(8_333.33m, "Art.85 grants ⅓ up to and including 5.0 years");
    }
}

// ── File-scoped test doubles (isolated from other test files) ─────────────────

file sealed class _EosbUnrestrictedScope : IDataScopeService
{
    public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization, AllowedEmployeeIds = null });
}

file sealed class _EosbHttpAccessor : IHttpContextAccessor
{
    public _EosbHttpAccessor(HttpContext ctx) => HttpContext = ctx;
    public HttpContext? HttpContext { get; set; }
}

file sealed class _EosbNullNotifications : Zayra.Api.Infrastructure.Notifications.INotificationService
{
    public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string entity, string? entityId, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
}

file sealed class _EosbNullLetterService : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}

file sealed class _EosbKsaPackResolver : ICountryPackResolver
{
    private readonly StubRuleReader _rules;
    public _EosbKsaPackResolver(StubRuleReader rules) => _rules = rules;

    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j)
        => cc is "SAU" or "SA" ? new KsaDeductionCalculator(_rules) : new DefaultStatutoryDeductionCalculator();
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j)
        => cc is "SAU" or "SA" ? new KsaEndOfServiceCalculator(_rules) : new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}
