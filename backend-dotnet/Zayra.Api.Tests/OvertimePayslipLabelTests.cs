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
/// The payslip's overtime line stated an arithmetic that does not produce its own amount.
///
/// <para>It printed <c>Overtime (1.00 h × 125.00/h × 1.50)</c> beside an amount of <b>162.50</b>.
/// Multiply the label out: <b>187.50</b>. The expression actually applied is KSA Art. 107 —
/// <c>baseHourly + basicHourly × (multiplier − 1)</c> = 125.00 + 75.00 × 0.50 = 162.50 — which is
/// not <c>rate × multiplier</c> and never was. An employee checking their own payslip by hand
/// arrived at <b>15% more than they were paid</b>, on every KSA overtime line of every run.</para>
///
/// <para>Every existing assertion on this label used <c>StartWith</c>, so none of them could see
/// it. These tests DO the sum the employee would do — <see cref="OvertimeLabelMath"/> parses the
/// rendered line and multiplies it out — and compare it with the money on the same line. A label
/// that cannot reproduce its own number fails here.</para>
///
/// <para>Real Postgres, a real <c>PayrollController.Process</c> run: the string asserted is the one
/// persisted onto <c>PayrollEarning.ComponentName</c>, which is what ESS serves to the employee and
/// what the ESS payslip PDF and the mobile app display.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class OvertimePayslipLabelTests
{
    private readonly PostgresFixture _fx;
    public OvertimePayslipLabelTests(PostgresFixture fx) => _fx = fx;

    private const decimal Basic = 18_000m;
    private const decimal Housing = 8_000m;
    private const decimal Transport = 4_000m;   // wage 30,000
    private const int MonthlyHours = 240;       // basicHourly 75.00, wageHourly 125.00
    private const decimal Art107Hour = 162.50m; // 125.00 + 0.5 × 75.00
    private static readonly DateOnly WorkDate = new(2026, 6, 10); // a Wednesday: not a KSA rest day

    /// <summary>
    /// THE defect, on the canonical KSA package and one whole hour — the case a support agent would
    /// be asked to explain.
    /// </summary>
    [Fact]
    public async Task TheOvertimeLine_MultipliesOutToTheAmountPrintedBesideIt()
    {
        var line = await RunOvertimeAsync(60);

        line.Amount.Should().Be(Art107Hour, "sanity: Art. 107 pays 125.00 + 0.5 × 75.00 for the hour");
        line.Label.Should().Be("Overtime (1.00 h × (125.00 + 75.00 × 0.50)/h)");

        var stated = OvertimeLabelMath.Parse(line.Label);
        stated.Amount.Should().Be(line.Amount,
            "on a whole hour at printable rates the line reproduces its amount EXACTLY — an employee "
            + "who works it out by hand arrives at what they were paid");
        stated.ShouldReconcileWith(line.Amount);
        stated.Amount.Should().NotBe(187.50m,
            "187.50 is what the old '1.00 h × 125.00/h × 1.50' label multiplied out to — 15% over "
            + "the 162.50 on the same line");
    }

    /// <summary>
    /// The quantity has to survive the check too: 50 minutes is 0.8333 h, and the label already
    /// prints it at that precision. The whole expression must still reconcile.
    /// </summary>
    [Fact]
    public async Task APartHourLine_AlsoMultipliesOutToItsOwnAmount()
    {
        var line = await RunOvertimeAsync(50);

        line.Amount.Should().Be(135.42m, "162.50 × 50/60 = 135.4166…");
        var stated = OvertimeLabelMath.Parse(line.Label);
        stated.Hours.Should().Be(0.8333m);
        // 0.8333 × 162.50 = 135.41, one halala under the 135.42 paid from 0.8333… h. That is the
        // printed hours being 4 dp, not a different formula: the gap is inside what display rounding
        // can account for, where the old label's was 25.00.
        stated.ShouldReconcileWith(line.Amount);
        Math.Abs(stated.Amount - line.Amount).Should().BeLessThanOrEqualTo(0.01m);
    }

    /// <summary>
    /// A weekend hour, where the statutory floor lifts the multiplier to 2.0. The uplift term is a
    /// FULL basic hour rather than half of one, and the label has to say so: 125.00 + 75.00 × 1.00.
    /// </summary>
    [Fact]
    public async Task AHigherMultiplierIsStatedAsTheUpliftItActuallyApplies()
    {
        // 2026-06-12 is a Friday — a KSA rest day, so the 2.0 restday floor applies.
        var line = await RunOvertimeAsync(60, workDate: new DateOnly(2026, 6, 12));

        line.Amount.Should().Be(200.00m, "125.00 + 75.00 × (2.00 − 1) = 200.00");
        line.Label.Should().Be("Overtime (1.00 h × (125.00 + 75.00 × 1.00)/h)");
        OvertimeLabelMath.Parse(line.Label).ShouldReconcileWith(line.Amount)
            .Amount.Should().Be(line.Amount);
    }

    /// <summary>
    /// The engine path and the legacy path emit the SAME label — they used to hold two copies of the
    /// format string byte-identical by convention, which is how the shape drifted from the money in
    /// the first place. Both now call one definition, and this pins that they agree AND that what
    /// they agree on evaluates to the amount handed in.
    /// </summary>
    [Fact]
    public void TheComponentEngineLabel_EvaluatesToTheOvertimeAmountItLabels()
    {
        var baseHourly = 125m;
        var upliftHourly = 75m;
        var multiplier = 1.5m;
        var hours = 3m;
        var pay = Math.Round(hours * OvertimeStatutoryCalculator.HourPay(baseHourly, upliftHourly, multiplier), 2);

        var comps = PayComponentCatalog.SystemComponentSeeds(Guid.NewGuid());
        var result = PayComponentEngine.Compute(comps, new PayComponentContext
        {
            Basic = Basic, Gross = Basic + Housing + Transport,
            OvertimePay = pay, OtHours = hours, HourlyRate = baseHourly,
            OtUpliftHourly = upliftHourly, OtMultiplier = multiplier,
        });

        var otLine = result.Earnings.Single(l => l.Code == "OVERTIME");
        otLine.Name.Should().Be(OvertimeStatutoryCalculator.PayslipLabel(hours, baseHourly, upliftHourly, multiplier),
            "there is one definition of this label and both renderers must call it");
        OvertimeLabelMath.Parse(otLine.Name).ShouldReconcileWith(otLine.Amount)
            .Amount.Should().Be(487.50m, "3 h × (125.00 + 75.00 × 0.50)");
    }

    /// <summary>
    /// The shape itself, asserted directly against <see cref="OvertimeStatutoryCalculator.HourPay"/>
    /// across the rates the three supported packs produce. No database, so a future edit to the
    /// format string is caught without a payroll run.
    /// </summary>
    [Theory]
    [InlineData(1, 125, 75, 1.5, 162.50)]           // KSA Art. 107 ordinary day
    [InlineData(1, 125, 75, 2.0, 200.00)]           // KSA rest day / public holiday
    [InlineData(2, 41.6667, 41.6667, 1.25, 104.17)] // UAE/Qatar: base IS basic, so it collapses
    [InlineData(1, 100, 100, 1.5, 150.00)]          // A honoured FixedHourlyRate policy
    [InlineData(0.8333333333333333, 125, 75, 1.5, 135.42)] // 50 minutes
    public void EveryRenderedLabel_MultipliesOutToTheHourPayExpression(
        double hours, double baseHourly, double upliftHourly, double multiplier, double expected)
    {
        var h = (decimal)hours;
        var b = (decimal)baseHourly;
        var u = (decimal)upliftHourly;
        var m = (decimal)multiplier;

        var amount = Math.Round(h * OvertimeStatutoryCalculator.HourPay(b, u, m), 2);
        amount.Should().Be((decimal)expected);

        // A payslip prints rates to 2 dp and hours to 4, so a halala of display rounding is allowed;
        // a DIFFERENT formula is not. The old label was out by 25.00 on the first row here.
        OvertimeLabelMath.Parse(OvertimeStatutoryCalculator.PayslipLabel(h, b, u, m))
            .ShouldReconcileWith(amount);
    }

    /// <summary>
    /// The guard on the guard: the shape the payslip used to print — hours × rate × multiplier —
    /// must FAIL the reconciliation check. Without this, a tolerance wide enough to swallow display
    /// rounding could quietly swallow the defect too.
    /// </summary>
    [Fact]
    public void TheOldLabelShape_WouldNotReconcile()
    {
        // "Overtime (1.00 h × 125.00/h × 1.50)" rendered in the new grammar states 125.00 + 125.00 ×
        // 0.50 = 187.50 for an hour paid 162.50.
        var asArithmetic = OvertimeLabelMath.Parse(
            OvertimeStatutoryCalculator.PayslipLabel(1m, 125m, 125m, 1.5m));

        asArithmetic.Amount.Should().Be(187.50m);
        var act = () => asArithmetic.ShouldReconcileWith(Art107Hour);
        act.Should().Throw<Xunit.Sdk.XunitException>(
            "a line 25.00 away from the money beside it must fail, tolerance or no tolerance");
    }

    // ══════════════════════ Fixture ══════════════════════

    private sealed record OvertimeLine(string Label, decimal Amount);

    private async Task<OvertimeLine> RunOvertimeAsync(int approvedMinutes, DateOnly? workDate = null)
    {
        var date = workDate ?? WorkDate;
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);

        var company = new Company
        {
            TenantId = tenantId,
            LegalNameEn = $"OT Label Co {Guid.NewGuid():N}",
            TradeName = "OT Label Co",
            CountryCode = CountryCodes.Saudi,
            Jurisdiction = Jurisdictions.KsaMainland,
            RegistrationNumber = $"OTL-{Guid.NewGuid():N}",
            DefaultCurrency = "SAR",
            IsActive = true,
            CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Companies.Add(company);

        var employee = new Employee
        {
            TenantId = tenantId,
            CompanyId = company.Id,
            EmployeeCode = $"OTL-{Guid.NewGuid():N}",
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
            MolId = $"MOL-OTL-{Guid.NewGuid():N}",
            SalaryCurrency = "SAR",
        });
        var request = new OvertimeRequest
        {
            TenantId = tenantId,
            CompanyId = company.Id,
            EmployeeId = employee.Id,
            EmployeeName = employee.FullName,
            OvertimePolicyId = policy.Id,
            WorkDate = date,
            StartTimeUtc = date.ToDateTime(new TimeOnly(17, 0), DateTimeKind.Utc),
            EndTimeUtc = date.ToDateTime(new TimeOnly(18, 0), DateTimeKind.Utc),
            RequestedMinutes = 60,
            Reason = "Quarterly close",
            Status = "PendingManager",
        };
        var run = new PayrollRun
        {
            TenantId = tenantId,
            CompanyId = company.Id,
            Year = date.Year,
            Month = date.Month,
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

        var processResult = await PayrollCtrl(db, tenantId, rules).Process(run.Id, CancellationToken.None);
        processResult.Should().BeOfType<OkObjectResult>();

        var otLine = await db.PayrollEarnings.AsNoTracking()
            .SingleAsync(e => e.TenantId == tenantId && e.EmployeeId == employee.Id
                           && e.ComponentCode == "OVERTIME");
        return new OvertimeLine(otLine.ComponentName, otLine.Amount);
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
            new _OtLabelNullNotifications(),
            new _OtLabelKsaPackResolver(rules),
            rules,
            new _OtLabelNullLetterService(),
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
        new Claim(ClaimTypes.Name, "OT Label Tester"),
        new Claim(ClaimTypes.Role, "Admin"),
    }, "Test"));

    private sealed class UnrestrictedScopeService : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
            => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
    }
}

// ── File-scoped stubs ─────────────────────────────────────────────────────────

file sealed class _OtLabelNullNotifications : INotificationService
{
    public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string en, string? eid, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
}

file sealed class _OtLabelKsaPackResolver : ICountryPackResolver
{
    private readonly StubRuleReader _rules;
    public _OtLabelKsaPackResolver(StubRuleReader rules) => _rules = rules;

    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j) => new KsaDeductionCalculator(_rules);
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}

file sealed class _OtLabelNullLetterService : ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}
