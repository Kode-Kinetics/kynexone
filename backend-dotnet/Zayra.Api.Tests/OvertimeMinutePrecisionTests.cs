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
/// The defect (audit O2): overtime was converted to DECIMAL HOURS, rounded to 2 dp, and only then
/// multiplied by the hourly rate.
///
/// <para><c>OvertimeController.Calculate</c> computed <c>Math.Round(ApprovedMinutes / 60m, 2)</c>
/// and stored it on <c>OvertimePayrollImpact.Hours</c>, a <c>numeric(8,2)</c> column;
/// <c>PayrollController</c> then multiplied THAT by the hourly rate. Any approved minute count that
/// is not a multiple of 0.6 minutes lost money, always downward on the common cases, on every
/// overtime line of every run.</para>
///
/// <para>Measured on this fixture (KSA 60/40 package, basic 18,000 + allowances 12,000 over 240 h,
/// Art. 107 hour = 125.00 + 0.5 × 75.00 = <b>162.50</b>):</para>
/// <list type="bullet">
///   <item>50 approved minutes → stored as <b>0.83 h</b> instead of 0.8333… h, paid
///     <b>SAR 134.88</b> instead of SAR 135.42 — 54 halalas short, −0.40%</item>
///   <item>35 approved minutes → stored as <b>0.58 h</b> instead of 0.5833… h, paid
///     <b>SAR 94.25</b> instead of SAR 94.79</item>
/// </list>
///
/// <para>The fix carries INT MINUTES through to payroll (<c>OvertimePayrollImpact.Minutes</c>,
/// mirroring <c>AttendancePayrollImpact.Minutes</c>) and divides by 60 once, inside the money
/// expression, so rounding happens exactly once — at the final amount.</para>
///
/// <para>Real Postgres, the real <c>OvertimeController.Approve</c> path and a real
/// <c>PayrollController.Process</c> run: the numbers are read out of the two places the product
/// actually shows them.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class OvertimeMinutePrecisionTests
{
    private readonly PostgresFixture _fx;
    public OvertimeMinutePrecisionTests(PostgresFixture fx) => _fx = fx;

    private const decimal Basic = 18_000m;
    private const decimal Housing = 8_000m;
    private const decimal Transport = 4_000m;   // wage 30,000
    private const int MonthlyHours = 240;       // basicHourly 75.00, wageHourly 125.00
    private const decimal Art107Hour = 162.50m; // 125.00 + 0.5 × 75.00
    private static readonly DateOnly WorkDate = new(2026, 6, 10); // a Wednesday: not a KSA rest day

    /// <summary>
    /// 50 minutes is five sixths of an hour. Rounding that to 0.83 before multiplying throws away
    /// 0.0033… h of paid time on every such line.
    /// </summary>
    [Fact]
    public async Task FiftyApprovedMinutes_ArePaidAsFiveSixthsOfAnHour_NotAsTheTwoDecimalRounding()
    {
        var money = await RunOvertimeAsync(50);

        var expected = Math.Round(50m / 60m * Art107Hour, 2);
        expected.Should().Be(135.42m, "sanity: 162.50 × 50/60 = 135.4166…");

        money.ModuleAmount.Should().Be(135.42m,
            "the overtime module must price the approved MINUTES, not a 2-dp hours copy of them");
        money.PayrollAmount.Should().Be(135.42m,
            "payroll must pay the same minutes; 134.88 is the pre-fix figure from 0.83 h × 162.50");
        money.PayrollAmount.Should().NotBe(134.88m,
            "134.88 is what 0.83 h × 162.50 produced — 54 halalas short on one overtime line");

        money.ImpactMinutes.Should().Be(50,
            "the payroll input is int minutes; the hours value is derived, never stored");
        money.ImpactHours.Should().Be(50m / 60m,
            "0.8333… h, at full decimal precision — not the 0.83 a numeric(8,2) column held");
        money.ImpactHours.Should().NotBe(0.83m);
    }

    /// <summary>35 minutes, the audit's second worked example: 0.58 vs 0.5833…</summary>
    [Fact]
    public async Task ThirtyFiveApprovedMinutes_AreNotQuantisedToPointFiveEight()
    {
        var money = await RunOvertimeAsync(35);

        money.ModuleAmount.Should().Be(Math.Round(35m / 60m * Art107Hour, 2)).And.Be(94.79m);
        money.PayrollAmount.Should().Be(94.79m);
        money.PayrollAmount.Should().NotBe(94.25m, "94.25 is 0.58 h × 162.50, the pre-fix figure");
        money.ImpactMinutes.Should().Be(35);
    }

    /// <summary>
    /// A whole hour must be byte-identical to before the change: the fix moves money only where
    /// minutes did not divide evenly into 2-dp hours.
    /// </summary>
    [Fact]
    public async Task OneWholeHour_IsUnchangedByTheFix()
    {
        var money = await RunOvertimeAsync(60);

        money.ModuleAmount.Should().Be(162.50m);
        money.PayrollAmount.Should().Be(162.50m);
        money.ImpactMinutes.Should().Be(60);
        money.ImpactHours.Should().Be(1.00m);
        money.PayslipLabel.Should().StartWith("Overtime (1.00 h",
            "an unaffected run's payslip label must not change");
    }

    /// <summary>
    /// The payslip line must describe the arithmetic that produced the money. Printing "0.83 h"
    /// beside SAR 135.42 would re-create the defect on the page the employee reads.
    /// </summary>
    [Fact]
    public async Task ThePayslipLabel_StatesTheHoursThatActuallyProducedTheAmount()
    {
        var money = await RunOvertimeAsync(50);

        money.PayslipLabel.Should().StartWith("Overtime (0.8333 h",
            "0.83 h × 162.50 does not equal the 135.42 on the same line");
    }

    // ══════════════════════ Fixture ══════════════════════

    private sealed record OvertimeMoney(
        decimal ModuleAmount, decimal PayrollAmount, int ImpactMinutes, decimal ImpactHours, string PayslipLabel);

    private async Task<OvertimeMoney> RunOvertimeAsync(int approvedMinutes)
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);

        var company = new Company
        {
            TenantId = tenantId,
            LegalNameEn = $"OT Minutes Co {Guid.NewGuid():N}",
            TradeName = "OT Minutes Co",
            CountryCode = CountryCodes.Saudi,
            Jurisdiction = Jurisdictions.KsaMainland,
            RegistrationNumber = $"OTM-{Guid.NewGuid():N}",
            DefaultCurrency = "SAR",
            IsActive = true,
            CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Companies.Add(company);

        var employee = new Employee
        {
            TenantId = tenantId,
            CompanyId = company.Id,
            EmployeeCode = $"OTM-{Guid.NewGuid():N}",
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
            HourlyRateBasis = "BasicSalary",
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
            MolId = $"MOL-OTM-{Guid.NewGuid():N}",
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

        var overtimeResult = await OvertimeCtrl(db, tenantId, rules)
            .Approve(request.Id, new OvertimeDecisionRequest(approvedMinutes, "ok"), CancellationToken.None);
        overtimeResult.Should().BeOfType<OkObjectResult>();

        var impact = await db.OvertimePayrollImpacts.AsNoTracking()
            .SingleAsync(x => x.OvertimeRequestId == request.Id);

        var processResult = await PayrollCtrl(db, tenantId, rules).Process(run.Id, CancellationToken.None);
        processResult.Should().BeOfType<OkObjectResult>();

        var otLine = await db.PayrollEarnings.AsNoTracking()
            .SingleOrDefaultAsync(e => e.TenantId == tenantId && e.EmployeeId == employee.Id
                                    && e.ComponentCode == "OVERTIME");
        otLine.Should().NotBeNull("the run must have produced an overtime earning line to compare");

        return new OvertimeMoney(
            impact.Amount, otLine!.Amount, 0, impact.Hours, otLine.ComponentName);
    }

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
            new _OtMinNullNotifications(),
            new _OtMinKsaPackResolver(rules),
            rules,
            new _OtMinNullLetterService(),
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

file sealed class _OtMinNullNotifications : INotificationService
{
    public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string en, string? eid, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
}

file sealed class _OtMinKsaPackResolver : ICountryPackResolver
{
    private readonly StubRuleReader _rules;
    public _OtMinKsaPackResolver(StubRuleReader rules) => _rules = rules;

    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j) => new KsaDeductionCalculator(_rules);
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}

file sealed class _OtMinNullLetterService : ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}
