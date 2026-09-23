using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The defect: <c>OvertimePolicy.HourlyRateBasis = "FixedHourlyRate"</c> was honoured by the
/// overtime module and read by NOTHING in payroll.
///
/// <para><c>OvertimeController.Calculate</c> put the configured fixed rate on both terms of the
/// overtime hour and persisted the resulting money into <c>OvertimePayrollImpact.Amount</c> — which
/// the module's own "payroll amount" summary sums. <c>PayrollController.Process</c> never looked at
/// the policy's basis at all: it always computed the Art. 107 figure from the salary structure. A
/// tenant configuring a fixed OT rate therefore saw one number on screen and was paid another.</para>
///
/// <para><b>The money, on the canonical 60/40 Saudi package</b> (basic 18,000 + housing 8,000 +
/// transport 4,000 = wage 30,000, over 240 standard monthly hours, one ordinary-day overtime
/// hour):</para>
/// <list type="bullet">
///   <item>basic hourly 75.00, wage hourly 125.00</item>
///   <item>Art. 107 entitlement = 125.00 + 0.5 × 75.00 = <b>162.50</b></item>
///   <item>fixed rate 100 → module showed 100 × 1.5 = <b>150.00</b>; payroll paid <b>162.50</b>
///     — the module displayed an UNLAWFUL rate 12.50/h below the statutory floor</item>
///   <item>fixed rate 200 → module showed 200 × 1.5 = <b>300.00</b>; payroll paid <b>162.50</b>
///     — the tenant's own contractual promise, 137.50/h unpaid</item>
/// </list>
///
/// <para><b>The resolution: floor the configured rate at statutory.</b> Saudi Labour Law Art. 107
/// is a minimum an employer cannot contract below, so a fixed rate worth less than the statutory
/// hour is discarded and the statutory hour is shown AND paid. A fixed rate worth more is a
/// contractual promise the product keeps, so it is shown AND paid. Both sides now resolve the hour
/// through the one shared <see cref="OvertimeStatutoryCalculator.ResolveHourRate"/> — the same
/// treatment <see cref="OvertimeStatutoryCalculator.EffectiveMultiplier"/> already gives a
/// configured multiplier, extended to the configured BASE.</para>
///
/// <para>Real Postgres, the real <c>OvertimeController.Approve</c> path and a real
/// <c>PayrollController.Process</c> run — the two numbers are read out of the two places the
/// product actually shows them, not recomputed in the test.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class OvertimeFixedRatePayrollParityTests
{
    private readonly PostgresFixture _fx;
    public OvertimeFixedRatePayrollParityTests(PostgresFixture fx) => _fx = fx;

    private const decimal Basic = 18_000m;
    private const decimal Housing = 8_000m;
    private const decimal Transport = 4_000m;   // wage = 30,000
    private const int MonthlyHours = 240;

    /// <summary>Art. 107 for one ordinary overtime hour on this package: 125.00 + 0.5 × 75.00.</summary>
    private const decimal StatutoryHour = 162.50m;

    private static readonly DateOnly WorkDate = new(2026, 6, 10);   // a Wednesday: not a KSA rest day

    [Fact]
    public async Task FixedRateBelowStatutory_IsFlooredAtArt107_InTheModuleAndInPayroll()
    {
        // 100.00/h × 1.5 = 150.00 — below the 162.50 Art. 107 entitlement, so unlawful to pay.
        var money = await RunOneOvertimeHourAsync(basis: "FixedHourlyRate", fixedHourlyRate: 100m);

        money.ModuleAmount.Should().Be(StatutoryHour,
            "Art. 107 is a floor: a configured rate worth 150.00 an hour cannot be displayed as payable");
        money.ModuleAmount.Should().NotBe(150.00m,
            "150.00 is the pre-fix module figure — the fixed rate honoured straight through the statutory floor");
        money.PayrollAmount.Should().Be(StatutoryHour);
        money.PayrollAmount.Should().Be(money.ModuleAmount,
            "one overtime hour, one number: what the module shows is what payroll pays");
        money.PayslipHourlyRate.Should().Be(125.00m,
            "the payslip line must describe the arithmetic that produced the money — here the Art. 107 wage base");
    }

    [Fact]
    public async Task FixedRateAboveStatutory_IsHonoured_AndPayrollActuallyPaysIt()
    {
        // 200.00/h × 1.5 = 300.00 — above the 162.50 entitlement, so a contractual promise to keep.
        var money = await RunOneOvertimeHourAsync(basis: "FixedHourlyRate", fixedHourlyRate: 200m);

        money.ModuleAmount.Should().Be(300.00m, "the tenant's configured rate beats the statutory floor");
        money.PayrollAmount.Should().Be(300.00m,
            "payroll used to ignore the policy basis entirely and pay the 162.50 statutory figure instead");
        money.PayrollAmount.Should().NotBe(StatutoryHour,
            "162.50 is the pre-fix payroll figure for this tenant — the promise the product did not keep");
        money.PayrollAmount.Should().Be(money.ModuleAmount);
        money.PayslipHourlyRate.Should().Be(200.00m, "the fixed rate is the base the money was computed on");
    }

    [Fact]
    public async Task BasicSalaryPolicy_IsUnaffected_AndStillPaysTheArt107Hour()
    {
        // The regression control: the overwhelming majority of tenants are on BasicSalary, and this
        // change must be inert for them. A configured FixedHourlyRate value that the basis does not
        // select is likewise ignored.
        var money = await RunOneOvertimeHourAsync(basis: "BasicSalary", fixedHourlyRate: 999m);

        money.ModuleAmount.Should().Be(StatutoryHour);
        money.PayrollAmount.Should().Be(StatutoryHour);
        money.PayslipHourlyRate.Should().Be(125.00m);
    }

    [Fact]
    public async Task GrossSalaryPolicy_IsHonouredConsistently_NotShownAndThenIgnored()
    {
        // The sibling of the same defect. On a "wage"-base jurisdiction the GrossSalary basis and the
        // statutory base coincide, so this pins the figure at the Art. 107 hour on both sides rather
        // than at the module's old gross-only reading.
        var money = await RunOneOvertimeHourAsync(basis: "GrossSalary", fixedHourlyRate: 0m);

        money.ModuleAmount.Should().Be(StatutoryHour);
        money.PayrollAmount.Should().Be(money.ModuleAmount);
    }

    // ── The arithmetic, asserted directly against the statute ─────────────────────────────────

    [Fact]
    public void ResolveHourRate_FloorsBelowStatutory_HonoursAbove_AndReportsWhichApplied()
    {
        const decimal basicHourly = 75m, wageHourly = 125m, m = 1.5m;

        var floored = OvertimeStatutoryCalculator.ResolveHourRate(
            "FixedHourlyRate", 100m, wageHourly, wageHourly, basicHourly, m);
        floored.HourPay.Should().Be(162.50m);
        floored.BaseHourly.Should().Be(125.00m);
        floored.PolicyHonoured.Should().BeFalse();

        var honoured = OvertimeStatutoryCalculator.ResolveHourRate(
            "FixedHourlyRate", 200m, wageHourly, wageHourly, basicHourly, m);
        honoured.HourPay.Should().Be(300.00m);
        honoured.BaseHourly.Should().Be(200.00m);
        honoured.PolicyHonoured.Should().BeTrue();

        // An unset fixed rate is not a promise of zero: it falls through to statutory.
        var unset = OvertimeStatutoryCalculator.ResolveHourRate(
            "FixedHourlyRate", 0m, wageHourly, wageHourly, basicHourly, m);
        unset.HourPay.Should().Be(162.50m);
        unset.PolicyHonoured.Should().BeFalse();

        // Basic-base jurisdiction (UAE/Qatar, 1.25): the statutory hour is basicHourly × 1.25 = 93.75,
        // and a GrossSalary policy there is a genuine contractual improvement, so it is honoured.
        var uaeStatutory = OvertimeStatutoryCalculator.ResolveHourRate(
            "BasicSalary", 0m, basicHourly, wageHourly, basicHourly, 1.25m);
        uaeStatutory.HourPay.Should().Be(93.75m);
        var uaeGross = OvertimeStatutoryCalculator.ResolveHourRate(
            "GrossSalary", 0m, basicHourly, wageHourly, basicHourly, 1.25m);
        uaeGross.HourPay.Should().Be(143.75m, "125.00 + 75.00 × 0.25");
        uaeGross.PolicyHonoured.Should().BeTrue();

        // A null/unknown basis is statutory-only — no configured base, nothing to honour.
        OvertimeStatutoryCalculator.ResolveHourRate(null, 500m, wageHourly, wageHourly, basicHourly, m)
            .HourPay.Should().Be(162.50m);
    }

    // ══════════════════════ Fixture ══════════════════════

    private sealed record OvertimeMoney(decimal ModuleAmount, decimal PayrollAmount, decimal PayslipHourlyRate);

    /// <summary>
    /// Seeds one KSA employee on the 60/40 package with the given overtime policy, approves one
    /// 60-minute overtime request through the REAL module endpoint, then runs the REAL payroll
    /// process over it, and reports both numbers.
    /// </summary>
    private async Task<OvertimeMoney> RunOneOvertimeHourAsync(string basis, decimal fixedHourlyRate)
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);

        var company = new Company
        {
            TenantId = tenantId,
            LegalNameEn = $"OT Fixed Rate Co {Guid.NewGuid():N}",
            TradeName = "OT Fixed Rate Co",
            CountryCode = CountryCodes.Saudi,
            Jurisdiction = Jurisdictions.KsaMainland,
            RegistrationNumber = $"OTF-{Guid.NewGuid():N}",
            DefaultCurrency = "SAR",
            IsActive = true,
            CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Companies.Add(company);

        var employee = new Employee
        {
            TenantId = tenantId,
            CompanyId = company.Id,
            EmployeeCode = $"OTF-{Guid.NewGuid():N}",
            FullName = "Ahmed Al-Rashidi",
            Nationality = "Saudi",
            Status = "Active",
            JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CountryCode = CountryCodes.Saudi,
            Salary = Basic,
        };
        var policy = new OvertimePolicy
        {
            TenantId = tenantId,
            Code = $"OT{Guid.NewGuid():N}"[..8],
            Name = "Tenant OT policy",
            HourlyRateBasis = basis,
            FixedHourlyRate = fixedHourlyRate,
            StandardMonthlyHours = MonthlyHours,
            MinimumMinutes = 30,
            MaximumMinutesPerDay = 240,
            MonthlyCapMinutes = 3600,
            RequiresApproval = true,
            IsActive = true,
            CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Employees.Add(employee);
        db.OvertimePolicies.Add(policy);
        await db.SaveChangesAsync();

        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId,
            EmployeeId = employee.Id,
            SalaryStructureId = Guid.NewGuid(),
            BasicSalary = Basic,
            HousingAllowance = Housing,
            TransportAllowance = Transport,
            Currency = "SAR",
            EffectiveDate = new DateOnly(2024, 1, 1),
            IsActive = true,
        });
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
        {
            TenantId = tenantId,
            EmployeeId = employee.Id,
            Iban = "SA4420000001234567891234",
            MolId = $"MOL-OTF-{Guid.NewGuid():N}",
            SalaryCurrency = "SAR",
        });
        var request = new OvertimeRequest
        {
            TenantId = tenantId,
            CompanyId = company.Id,
            EmployeeId = employee.Id,
            EmployeeName = employee.FullName,
            OvertimePolicyId = policy.Id,
            WorkDate = WorkDate,
            StartTimeUtc = new DateTime(2026, 6, 10, 17, 0, 0, DateTimeKind.Utc),
            EndTimeUtc = new DateTime(2026, 6, 10, 18, 0, 0, DateTimeKind.Utc),
            RequestedMinutes = 60,
            Reason = "Quarterly close",
            Status = "PendingManager",
        };
        var run = new PayrollRun
        {
            TenantId = tenantId,
            CompanyId = company.Id,
            Year = 2026,
            Month = 6,
            Status = "Draft",
            CreatedAtUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.OvertimeRequests.Add(request);
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();

        var rules = KsaRules();

        // ── What the MODULE shows ────────────────────────────────────────────────────────────
        var overtimeResult = await OvertimeCtrl(db, tenantId, rules)
            .Approve(request.Id, new OvertimeDecisionRequest(60, "ok"), CancellationToken.None);
        overtimeResult.Should().BeOfType<OkObjectResult>();
        var calc = (OvertimeCalculation)((OkObjectResult)overtimeResult).Value!;
        calc.ApprovedHours.Should().Be(1.00m, "the whole comparison is per overtime hour");

        // The persisted impact is what the module's own summary endpoint sums as "payrollAmount",
        // so that is the number the tenant is actually shown.
        var impact = await db.OvertimePayrollImpacts.AsNoTracking()
            .SingleAsync(x => x.OvertimeRequestId == request.Id);
        impact.Amount.Should().Be(calc.Amount);

        // ── What PAYROLL pays ────────────────────────────────────────────────────────────────
        var processResult = await PayrollCtrl(db, tenantId, rules).Process(run.Id, CancellationToken.None);
        processResult.Should().BeOfType<OkObjectResult>();

        var otLine = await db.PayrollEarnings.AsNoTracking()
            .SingleOrDefaultAsync(e => e.TenantId == tenantId && e.EmployeeId == employee.Id
                                    && e.ComponentCode == "OVERTIME");
        otLine.Should().NotBeNull("the run must have produced an overtime earning line to compare");
        // The payslip LABEL is the tenant-visible description of the arithmetic, so it is read back
        // and parsed rather than trusted. Its shape is now
        // "Overtime (1.00 h \u00d7 (<base> + <uplift base> \u00d7 <m-1>)/h)" \u2014 the expression that
        // actually produces the money, rather than the "<rate>/h \u00d7 <m>" that did not.
        var label = otLine!.ComponentName;
        var displayedRate = OvertimeLabelMath.Parse(label).BaseHourly;
        label.Should().StartWith("Overtime (1.00 h", "one hour was approved");

        return new OvertimeMoney(impact.Amount, otLine.Amount, displayedRate);
    }

    /// <summary>
    /// The KSA statutory inputs, stubbed so the test states them explicitly:
    /// <c>ot.hourly_base = "wage"</c> is Art. 107's base and is exactly what makes the statutory hour
    /// 162.50 rather than 112.50. Both controllers read the SAME reader instance, so no divergence
    /// here can come from the rule source.
    /// </summary>
    private static StubRuleReader KsaRules() => new StubRuleReader()
        .Set("ot.hourly_base", "wage")
        .Set("ot.standard_multiplier", 1.5m)
        .Set("ot.restday_multiplier", 2.0m)
        .Set("ot.holiday_multiplier", 2.0m)
        .Set("ot.standard_monthly_hours", (decimal)MonthlyHours)
        .Set("lop.monthly_day_divisor", 30m)
        .Set("lop.standard_work_minutes_per_day", 480m)
        .Set("gosi.saudi_employee_rate", 0.09m)
        .Set("gosi.saudi_employer_rate", 0.09m)
        .Set("gosi.saned_rate", 0.0075m)
        .Set("gosi.expat_occupational_hazard_rate", 0.02m)
        .Set("gosi.covered_wage_ceiling_sar", 45_000m);

    private static OvertimeController OvertimeCtrl(ZayraDbContext db, Guid tenantId, StubRuleReader rules)
    {
        var ctrl = new OvertimeController(
            db,
            new UnrestrictedScopeService(),
            new HrmHierarchyService(db, new AuditService(db)),
            workWeek: null,
            ruleReader: rules);
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = Principal(tenantId) },
        };
        return ctrl;
    }

    private static PayrollController PayrollCtrl(ZayraDbContext db, Guid tenantId, StubRuleReader rules)
    {
        var ctrl = new PayrollController(
            db,
            new DataScopeService(db),
            new HttpContextAccessor(),
            new _OtFixedNullNotifications(),
            new _OtFixedKsaPackResolver(rules),
            rules,
            new _OtFixedNullLetterService(),
            new NullDocumentStorage(),
            new PdfRenderGate(8));
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = Principal(tenantId) },
        };
        return ctrl;
    }

    private static ClaimsPrincipal Principal(Guid tenantId) => new(new ClaimsIdentity(new[]
    {
        new Claim("tenant_id", tenantId.ToString()),
        new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
        new Claim(ClaimTypes.Name, "OT Tester"),
        new Claim(ClaimTypes.Role, "Admin"),
    }, "Test"));

    private sealed class UnrestrictedScopeService : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
            => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
    }
}

// ── File-scoped stubs ─────────────────────────────────────────────────────────

file sealed class _OtFixedNullNotifications : INotificationService
{
    public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string en, string? eid, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
}

file sealed class _OtFixedKsaPackResolver : ICountryPackResolver
{
    private readonly StubRuleReader _rules;
    public _OtFixedKsaPackResolver(StubRuleReader rules) => _rules = rules;

    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j) => new KsaDeductionCalculator(_rules);
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}

file sealed class _OtFixedNullLetterService : ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}
