// EosbUnificationIndependentTests.cs
// ─────────────────────────────────────────────────────────────────────────────
// POD-A2 — INDEPENDENT verification (SDE Test SME) of the EOSB engine unification.
//
// These tests are deliberately SEPARATE from the implementer's EosbUnificationTests.cs
// and re-prove the acceptance criteria from scratch with independently hand-computed
// golden numbers, so the proof does not rest on the author's own fixtures:
//
//  (a) PARITY — /eosb/calculate and /final-settlement return IDENTICAL EOSB for
//      identical inputs, proven ALSO on a REMAINDER-DAY (mid-month, fractional-year)
//      tenure — not just the whole-month tenures the author's parity theory used —
//      with the exact shared figure pinned.
//  (b) ART.84/85 TIERS, each hand-verified as an EXACT golden on the live controller
//      surface: resignation <2yr = nil, 2–5yr = ⅓, >5–<10yr = ⅔ (exact 33,333.33,
//      not a range), ≥10yr = full; AND employer-termination full two-tier gratuity.
//  (c) SHORT-SERVICE — the task's own example: a 1.5yr TERMINATION now yields
//      pro-rata 7,500 (non-zero) on BOTH endpoints, while a 1.5yr RESIGNATION yields
//      nil on BOTH endpoints (the pack's Art.85 forfeiture, not the deleted gate).
//  (d) SERVICE-PERIOD / LEAP — an exactly-4.0-year tenure that SPANS a leap day
//      returns EXACTLY 4 years of gratuity (20,000), and is shown to differ from the
//      naive (end-start).Days/365.0 figure the old inline formula used — proving the
//      pack's month-based service math eliminated the leap drift.
//
// Assertions are exact (FluentAssertions .Be / xUnit Assert.Equal). Nothing is weakened.
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

public class EosbUnificationIndependentTests
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
            new _EosbIndScope(),
            new _EosbIndHttp(httpCtx),
            new _EosbIndNoNotify(),
            new _EosbIndKsaResolver(new StubRuleReader()),
            new StubRuleReader(),
            new _EosbIndNoLetters(),
            new NullDocumentStorage(),
            new Zayra.Api.Infrastructure.Documents.PdfRenderGate(1));
        ctrl.ControllerContext = new ControllerContext { HttpContext = httpCtx };
        return ctrl;
    }

    // Seeds one KSA employee (EOSB enabled) joining on `joiningDate`, basic SAR `basic`.
    private static int SeedKsaEmployee(ZayraDbContext db, Guid tenantId, DateTime joiningDate, decimal basic = Basic)
    {
        var emp = new Employee
        {
            TenantId = tenantId, EmployeeCode = "E900", FullName = "Independent Test Employee",
            Status = "Active", Nationality = "SAU", JoiningDate = joiningDate,
        };
        db.Employees.Add(emp);
        db.GCCComplianceSettings.Add(new GCCComplianceSetting
        {
            TenantId = tenantId, CountryCode = "SA", EosbEnabled = true, EosbMinYears = 1
        });
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = emp.Id,
            BasicSalary = basic, Currency = "SAR",
            EffectiveDate = DateOnly.FromDateTime(joiningDate), IsActive = true,
        });
        db.SaveChanges();
        return emp.Id;
    }

    private static decimal EosbFrom(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var body = ok.Value!;
        return (decimal)body.GetType().GetProperty("eosbAmount")!.GetValue(body)!;
    }

    // ── (a) PARITY on a fractional (remainder-day) tenure ─────────────────────────
    // The author's parity theory only used whole-month tenures ("remainder-day fraction
    // is 0 … line up exactly"). We prove parity ALSO holds with a mid-month join AND a
    // mid-month as-of date, i.e. when the pack's remDays/365 fraction is non-zero — the
    // regime where the OLD inline /365.0 formula diverged from the pack.
    //
    // Join 2020-03-10 → as-of 2023-08-25, basic 10,000, employer Termination.
    //   ServicePeriod: months = (2023-2020)*12 + (8-3) = 41 ; remDays = 25-10 = 15.
    //   serviceYears  = 41/12 + 15/365 = 3.4166666… + 0.0410958… = 3.45776255708…
    //   tier1 (≤5yr)  = 3.45776255708 × 0.5 × 10,000 = 17,288.8127… → 17,288.81.
    [Fact]
    public async Task Parity_FractionalTenure_BothEndpointsReturnIdenticalExactFigure()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var joining = new DateTime(2020, 3, 10, 0, 0, 0, DateTimeKind.Utc);
        var asOf    = new DateTime(2023, 8, 25, 0, 0, 0, DateTimeKind.Utc);
        var empId = SeedKsaEmployee(db, tenantId, joining);
        var ctrl = MakeCtrl(db, tenantId);

        var eosb = await ctrl.CalculateEosb(
            new EosbCalculationRequest(empId, asOf, "Termination"), CancellationToken.None);
        var settlement = await ctrl.FinalSettlement(
            new FinalSettlementRequest(empId, DateOnly.FromDateTime(asOf), 0, "Termination"),
            CancellationToken.None);

        EosbFrom(eosb).Should().Be(17_288.81m, "hand-computed pack figure for 3.45776yr termination");
        EosbFrom(settlement).Should().Be(EosbFrom(eosb),
            "both endpoints share one engine even when the service period has a remainder-day fraction");
    }

    // ── (b) Art.84/85 tiers — EXACT goldens on the live /final-settlement surface ──

    // Resignation < 2yr → nil (Art.85 forfeiture).
    [Fact]
    public async Task Resignation_UnderTwoYears_ForfeitsEntirely()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var empId = SeedKsaEmployee(db, tenantId, new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var ctrl = MakeCtrl(db, tenantId);

        // 1.5yr resignation (join 2022-01 → 2023-07).
        var result = await ctrl.FinalSettlement(
            new FinalSettlementRequest(empId, new DateOnly(2023, 7, 1), 0, "Resignation"),
            CancellationToken.None);

        EosbFrom(result).Should().Be(0m, "Art.85: resignation with < 2 years service earns nil");
    }

    // Resignation 2–5yr → ⅓ (exact).  3.0yr full = 15,000 → ⅓ = 5,000.
    [Fact]
    public async Task Resignation_TwoToFiveYears_TakesOneThird_Exact()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var empId = SeedKsaEmployee(db, tenantId, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var ctrl = MakeCtrl(db, tenantId);

        var result = await ctrl.FinalSettlement(
            new FinalSettlementRequest(empId, new DateOnly(2023, 1, 1), 0, "Resignation"),
            CancellationToken.None);

        EosbFrom(result).Should().Be(5_000m, "⅓ of the 15,000 two-tier award (Art.85, 2–5yr band)");
    }

    // Resignation >5yr & <10yr → ⅔ (EXACT, not a range).
    //   7.5yr: tier1 = 5×0.5×10,000 = 25,000 ; tier2 = 2.5×1×10,000 = 25,000 ; full = 50,000.
    //   ⅔ × 50,000 = 33,333.3333… → 33,333.33.
    [Fact]
    public async Task Resignation_FiveToTenYears_TakesTwoThirds_ExactNotRange()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var empId = SeedKsaEmployee(db, tenantId, new DateTime(2018, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var ctrl = MakeCtrl(db, tenantId);

        // join 2018-01 → 2025-07 = exactly 7.5 years.
        var result = await ctrl.FinalSettlement(
            new FinalSettlementRequest(empId, new DateOnly(2025, 7, 1), 0, "Resignation"),
            CancellationToken.None);

        EosbFrom(result).Should().Be(33_333.33m, "⅔ of the 50,000 two-tier award (Art.85, >5–<10yr band)");
    }

    // Resignation ≥ 10yr → full award (no reduction).
    //   12yr: tier1 = 25,000 ; tier2 = 7×1×10,000 = 70,000 ; full = 95,000.
    [Fact]
    public async Task Resignation_TenYearsOrMore_ReceivesFullAward()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var empId = SeedKsaEmployee(db, tenantId, new DateTime(2011, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var ctrl = MakeCtrl(db, tenantId);

        var result = await ctrl.FinalSettlement(
            new FinalSettlementRequest(empId, new DateOnly(2023, 1, 1), 0, "Resignation"),
            CancellationToken.None);

        EosbFrom(result).Should().Be(95_000m, "Art.85: a resignation at ≥ 10 years takes the full award");
    }

    // Employer termination spanning BOTH tiers → full two-tier gratuity (no Art.85 haircut).
    //   7.5yr Termination → 50,000 (contrast the 33,333.33 a resignation of the same tenure gets).
    [Fact]
    public async Task Termination_SevenAndHalfYears_PaysFullTwoTierGratuity()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var empId = SeedKsaEmployee(db, tenantId, new DateTime(2018, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var ctrl = MakeCtrl(db, tenantId);

        var result = await ctrl.FinalSettlement(
            new FinalSettlementRequest(empId, new DateOnly(2025, 7, 1), 0, "Termination"),
            CancellationToken.None);

        EosbFrom(result).Should().Be(50_000m, "employer termination pays the full ½-then-1 month award (Art.84)");
    }

    // ── (c) Short-service 1.5yr: TERMINATION pro-rata (non-zero) vs RESIGNATION nil ─
    // The task's explicit example. Both endpoints must agree on each.
    //   1.5yr termination: tier1 = 1.5 × 0.5 × 10,000 = 7,500 (pre-gate removed → non-zero).
    [Fact]
    public async Task ShortService_OneAndHalfYear_TerminationProRata_ResignationNil_OnBothEndpoints()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        // join 2022-07 → 2024-01 = 1.5 years.
        var empId = SeedKsaEmployee(db, tenantId, new DateTime(2022, 7, 1, 0, 0, 0, DateTimeKind.Utc));
        var ctrl = MakeCtrl(db, tenantId);

        var asOf = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var termEosb = await ctrl.CalculateEosb(
            new EosbCalculationRequest(empId, asOf, "Termination"), CancellationToken.None);
        var termSettle = await ctrl.FinalSettlement(
            new FinalSettlementRequest(empId, DateOnly.FromDateTime(asOf), 0, "Termination"), CancellationToken.None);
        var resignEosb = await ctrl.CalculateEosb(
            new EosbCalculationRequest(empId, asOf, "Resignation"), CancellationToken.None);
        var resignSettle = await ctrl.FinalSettlement(
            new FinalSettlementRequest(empId, DateOnly.FromDateTime(asOf), 0, "Resignation"), CancellationToken.None);

        EosbFrom(termEosb).Should().Be(7_500m, "Art.84 pays a short-service TERMINATION pro-rata from day one");
        EosbFrom(termSettle).Should().Be(7_500m, "/final-settlement agrees with /eosb for the same termination");
        EosbFrom(resignEosb).Should().Be(0m, "Art.85 forfeits a < 2yr RESIGNATION");
        EosbFrom(resignSettle).Should().Be(0m, "/final-settlement agrees with /eosb for the same resignation");
    }

    // ── (d) Service-period / leap correctness ─────────────────────────────────────
    // A tenure of EXACTLY 4 years by the calendar (same month+day) that SPANS the
    // 2020-02-29 leap day must be valued as exactly 4.0 years — the pack derives service
    // length from whole months (48/12 = 4.0, remDays = 0), so gratuity = 4 × 0.5 × 10,000
    // = 20,000. The old inline formula used (end-start).Days/365.0, which for this span is
    // 1461/365 = 4.00274 → 20,013.70. We assert the pack value AND that it differs from the
    // naive /365.0 value, proving the leap drift was eliminated.
    [Fact]
    public async Task LeapCorrectness_ExactFourYearsSpanningLeapDay_IsNotDriftedBy365Divisor()
    {
        var start = new DateOnly(2019, 3, 1);
        var end   = new DateOnly(2023, 3, 1); // spans 2020-02-29
        var calc = new KsaEndOfServiceCalculator(new StubRuleReader());
        var input = new EndOfServiceInput(
            Guid.NewGuid(), Guid.NewGuid(),
            new SalaryBreakdown(10_000m, 0m, 0m, 0m),
            start, end, "Termination", "Unlimited", "SAU");

        var result = await calc.CalculateAsync(input);

        // The naive divisor the deleted inline formula used.
        double naiveYears = (end.ToDateTime(TimeOnly.MinValue) - start.ToDateTime(TimeOnly.MinValue)).TotalDays / 365.0;
        decimal naiveTier1 = Math.Round((decimal)naiveYears * 0.5m * 10_000m, 2);

        result.TotalGratuity.Should().Be(20_000m, "exactly 4 calendar years → 4 × ½ month × 10,000");
        naiveYears.Should().BeGreaterThan(4.0, "the /365.0 divisor over-counts the leap day");
        naiveTier1.Should().Be(20_013.70m, "the deleted inline formula would have over-paid");
        result.TotalGratuity.Should().NotBe(naiveTier1,
            "the pack's whole-month service math eliminates the leap drift the inline /365.0 formula had");
    }

    // Remainder-day precision at the pack boundary: a whole-month + partial-month tenure
    // is valued as fullMonths/12 + remDays/365, hand-verified independently of the controller.
    //   Join 2021-06-15 → 2024-09-30: months = 39, remDays = 15.
    //   serviceYears = 39/12 + 15/365 = 3.25 + 0.0410958… = 3.29109589…
    //   tier1 = 3.29109589 × 0.5 × 10,000 = 16,455.4795 → 16,455.48.
    [Fact]
    public async Task ServicePeriod_RemainderDays_UsesMonthPlus365Fraction_Exact()
    {
        var calc = new KsaEndOfServiceCalculator(new StubRuleReader());
        var input = new EndOfServiceInput(
            Guid.NewGuid(), Guid.NewGuid(),
            new SalaryBreakdown(10_000m, 0m, 0m, 0m),
            new DateOnly(2021, 6, 15), new DateOnly(2024, 9, 30),
            "Termination", "Unlimited", "SAU");

        var result = await calc.CalculateAsync(input);

        result.TotalGratuity.Should().Be(16_455.48m,
            "serviceYears = 39/12 + 15/365 = 3.291096, tier-1 = ½ month × basic");
    }
}

// ── File-scoped test doubles (independent of the author's EosbUnificationTests.cs) ──

file sealed class _EosbIndScope : IDataScopeService
{
    public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization, AllowedEmployeeIds = null });
}

file sealed class _EosbIndHttp : IHttpContextAccessor
{
    public _EosbIndHttp(HttpContext ctx) => HttpContext = ctx;
    public HttpContext? HttpContext { get; set; }
}

file sealed class _EosbIndNoNotify : Zayra.Api.Infrastructure.Notifications.INotificationService
{
    public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string entity, string? entityId, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
}

file sealed class _EosbIndNoLetters : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}

file sealed class _EosbIndKsaResolver : ICountryPackResolver
{
    private readonly StubRuleReader _rules;
    public _EosbIndKsaResolver(StubRuleReader rules) => _rules = rules;

    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j)
        => cc is "SAU" or "SA" ? new KsaDeductionCalculator(_rules) : new DefaultStatutoryDeductionCalculator();
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j)
        => cc is "SAU" or "SA" ? new KsaEndOfServiceCalculator(_rules) : new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}
