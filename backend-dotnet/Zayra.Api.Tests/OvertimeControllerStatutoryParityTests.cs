using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The defect: <c>OvertimeController</c> reported 112.50 for the same overtime hour the payroll
/// run paid at 162.50.
///
/// <para>S1 (<c>fix/s1-statutory-payroll</c>) moved the payroll run onto KSA Labour Law Art. 107 —
/// "an additional amount equal to the hourly WAGE plus 50% of his BASIC wage" — and made the
/// seeded <c>ot.restday_multiplier</c> / <c>ot.holiday_multiplier</c> rates live. It did not move
/// <c>OvertimeController.Calculate</c>, which kept computing
/// <c>basic ÷ StandardMonthlyHours × policy-multiplier</c> off the <c>OvertimeMultipliers</c>
/// table and never read a statutory rule at all.</para>
///
/// <para>On the canonical 60/40 Saudi package (basic 18,000 + allowances 12,000 over 240 h):</para>
/// <list type="bullet">
///   <item>basic hourly = 18,000 ÷ 240 = 75.00</item>
///   <item>wage  hourly = 30,000 ÷ 240 = 125.00</item>
///   <item>Art. 107 (payroll, correct) = 125.00 + 0.5 × 75.00 = <b>162.50</b></item>
///   <item>pre-fix controller = 75.00 × 1.5 = <b>112.50</b> — 30.8% short</item>
/// </list>
/// The payroll side of that arithmetic is asserted directly in
/// <see cref="StatutoryOvertimeTests.Ksa_OvertimeHour_IsWageHourlyPlusHalfOfBasicHourly"/>; these
/// tests assert the controller now lands on the same number, through the real
/// <see cref="StatutoryRuleSeeder"/> rows and the real <see cref="StatutoryRuleReader"/> against
/// Postgres — the reader's platform-default lookup turns on <c>TenantId IS NULL</c>, which is
/// exactly the kind of NULL semantics an in-memory provider gets wrong.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class OvertimeControllerStatutoryParityTests
{
    private readonly PostgresFixture _fx;
    public OvertimeControllerStatutoryParityTests(PostgresFixture fx) => _fx = fx;

    private const decimal Basic = 18_000m;
    private const decimal Housing = 8_000m;
    private const decimal Transport = 4_000m;   // basic 18,000 + allowances 12,000 = wage 30,000
    private const int MonthlyHours = 240;

    [Fact]
    public async Task KsaRegularDayOvertime_MatchesTheArt107FigurePayrollPays_Not_TheStaleBasicOnlyOne()
    {
        await using var db = _fx.CreateDb();
        var (tenantId, employee, policy) = await SeedKsaAsync(db);

        // A Wednesday: not a KSA rest day (Fri/Sat) and not a public holiday.
        var workDate = NextWeekday(DayOfWeek.Wednesday);
        var request = await SeedPendingRequestAsync(db, tenantId, employee, policy, workDate);

        var ctrl = CreateController(db, tenantId);
        var result = await ctrl.Approve(request.Id, new OvertimeDecisionRequest(60, "ok"), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        var calc = (OvertimeCalculation)((OkObjectResult)result).Value!;

        calc.ApprovedHours.Should().Be(1.00m);
        calc.Amount.Should().Be(162.50m,
            "Art. 107 pays wage-hourly 125.00 + 50% of basic-hourly 75.00 for one KSA overtime hour");
        calc.Amount.Should().NotBe(112.50m,
            "112.50 is the pre-S1 basic-only figure the controller used to report while payroll paid 162.50");

        // The persisted impact is what the payroll run consumes, so it must carry the same money
        // and a DAY multiplier (not an effective rate ratio, which payroll would re-uplift).
        var impact = await db.OvertimePayrollImpacts.AsNoTracking()
            .SingleAsync(x => x.OvertimeRequestId == request.Id);
        impact.Amount.Should().Be(162.50m);
        impact.Hours.Should().Be(1.00m);
        impact.ApprovedMultiplier.Should().Be(1.5m, "Art. 107's regular-day rate is the DAY multiplier");
    }

    /// <summary>
    /// The genuine parity assertion: the controller's amount is recomputed here with the payroll
    /// run's own expression and inputs (PayrollController → OvertimeStatutoryCalculator.HourPay on
    /// fullWage ÷ standardMonthlyHours and fullBasic ÷ standardMonthlyHours). If either side ever
    /// drifts again, this fails without anybody having to notice a hard-coded figure changed.
    /// </summary>
    [Fact]
    public async Task ControllerAmount_EqualsWhatThePayrollRunWouldPayForTheSameHour()
    {
        await using var db = _fx.CreateDb();
        var (tenantId, employee, policy) = await SeedKsaAsync(db);
        var workDate = NextWeekday(DayOfWeek.Wednesday);
        var request = await SeedPendingRequestAsync(db, tenantId, employee, policy, workDate);

        var ctrl = CreateController(db, tenantId);
        var result = await ctrl.Approve(request.Id, new OvertimeDecisionRequest(120, "ok"), CancellationToken.None);
        var calc = (OvertimeCalculation)((OkObjectResult)result).Value!;

        // Exactly what PayrollController does for this employee/hour, resolved from the same rows.
        var reader = new StatutoryRuleReader(db);
        var ctx = await reader.ResolveAsync(
            CountryCodes.Saudi, Jurisdictions.KsaMainland, workDate, tenantId, CancellationToken.None);
        var hours = ctx.StandardMonthlyHoursOverride ?? MonthlyHours;
        var basicHourly = Basic / hours;
        var wageHourly = (Basic + Housing + Transport) / hours;
        var payrollAmount = Math.Round(
            calc.ApprovedHours * OvertimeStatutoryCalculator.HourPay(
                ctx.BaseIsFullWage ? wageHourly : basicHourly,
                basicHourly,
                ctx.EffectiveMultiplier(1.5m, OvertimeStatutoryCalculator.DayCategoryRegular)),
            2);

        payrollAmount.Should().Be(325.00m, "sanity: two Art. 107 hours on this package");
        calc.Amount.Should().Be(payrollAmount,
            "the controller and the payroll run must produce one number for one overtime hour");
    }

    /// <summary>
    /// The statutory floor, from the controller's side. KSA's seeded
    /// <c>ot.restday_multiplier</c> is 2.0, and a tenant policy configured at 1.5 for weekends
    /// cannot authorise less. Pre-fix the controller read only the policy table and reported the
    /// 1.5 rate for a rest-day hour that payroll pays at 2.0.
    /// </summary>
    [Fact]
    public async Task KsaRestDayOvertime_IsFlooredAtTheSeededStatutoryRestDayRate_NotThePolicyRate()
    {
        await using var db = _fx.CreateDb();
        var (tenantId, employee, policy) = await SeedKsaAsync(db);

        var friday = NextWeekday(DayOfWeek.Friday);   // default KSA rest day
        var request = await SeedPendingRequestAsync(db, tenantId, employee, policy, friday);

        var ctrl = CreateController(db, tenantId);
        var result = await ctrl.Approve(request.Id, new OvertimeDecisionRequest(60, "ok"), CancellationToken.None);
        var calc = (OvertimeCalculation)((OkObjectResult)result).Value!;

        calc.Multiplier.Should().Be(2.0m, "the seeded ot.restday_multiplier floors the policy's 1.5");
        // 125.00 + 75.00 × (2 − 1) = 200.00
        calc.Amount.Should().Be(200.00m);
    }

    // ── fixture ────────────────────────────────────────────────────────────────

    private async Task<(Guid TenantId, Employee Employee, OvertimePolicy Policy)> SeedKsaAsync(ZayraDbContext db)
    {
        // The real platform-default rules, not a stub: ot.hourly_base = "wage",
        // ot.standard_multiplier = 1.5, ot.restday_multiplier = 2.0, ot.standard_monthly_hours = 240.
        await StatutoryRuleSeeder.SeedAsync(db, NullLogger.Instance);

        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var company = new Company
        {
            TenantId = tenantId,
            LegalNameEn = "KSA Co",
            TradeName = "KSA Co",
            CountryCode = CountryCodes.Saudi,
            Jurisdiction = Jurisdictions.KsaMainland,
            DefaultCurrency = "SAR",
            IsActive = true,
        };
        db.Companies.Add(company);
        await db.SaveChangesAsync();

        var employee = new Employee
        {
            TenantId = tenantId,
            CompanyId = company.Id,
            EmployeeCode = $"OT-{Guid.NewGuid():N}".Substring(0, 12),
            FullName = "Ahmed Al-Rashidi",
            Status = "Active",
            JoiningDate = DateTime.UtcNow.AddYears(-3),
            CountryCode = CountryCodes.Saudi,
            Salary = Basic,
        };
        var policy = new OvertimePolicy
        {
            TenantId = tenantId,
            Code = $"OT-{Guid.NewGuid():N}".Substring(0, 10),
            Name = "Standard OT",
            HourlyRateBasis = "BasicSalary",
            StandardMonthlyHours = MonthlyHours,
            MinimumMinutes = 30,
            MaximumMinutesPerDay = 240,
            MonthlyCapMinutes = 3600,
            RequiresApproval = true,
            IsActive = true,
        };
        db.Employees.Add(employee);
        db.OvertimePolicies.Add(policy);
        await db.SaveChangesAsync();

        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId,
            EmployeeId = employee.Id,
            BasicSalary = Basic,
            HousingAllowance = Housing,
            TransportAllowance = Transport,
            Currency = "SAR",
            EffectiveDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-1)),
            IsActive = true,
        });
        // The tenant's own configured rates. RegularDay sits exactly on the statutory rate;
        // Weekend is deliberately BELOW KSA's seeded 2.0 rest-day rate so the floor is visible.
        db.OvertimeMultipliers.AddRange(
            new OvertimeMultiplier { TenantId = tenantId, OvertimePolicyId = policy.Id, DayCategory = "RegularDay", Multiplier = 1.5m },
            new OvertimeMultiplier { TenantId = tenantId, OvertimePolicyId = policy.Id, DayCategory = "Weekend", Multiplier = 1.5m },
            new OvertimeMultiplier { TenantId = tenantId, OvertimePolicyId = policy.Id, DayCategory = "PublicHoliday", Multiplier = 2.0m });
        await db.SaveChangesAsync();

        return (tenantId, employee, policy);
    }

    private static async Task<OvertimeRequest> SeedPendingRequestAsync(
        ZayraDbContext db, Guid tenantId, Employee employee, OvertimePolicy policy, DateOnly workDate)
    {
        var request = new OvertimeRequest
        {
            TenantId = tenantId,
            CompanyId = employee.CompanyId,
            EmployeeId = employee.Id,
            EmployeeName = employee.FullName,
            OvertimePolicyId = policy.Id,
            WorkDate = workDate,
            StartTimeUtc = DateTime.UtcNow.Date.AddHours(18),
            EndTimeUtc = DateTime.UtcNow.Date.AddHours(20),
            RequestedMinutes = 120,
            Reason = "Quarterly close",
            Status = "PendingManager",
        };
        db.OvertimeRequests.Add(request);
        await db.SaveChangesAsync();
        return request;
    }

    private static OvertimeController CreateController(ZayraDbContext db, Guid tenantId)
    {
        var ctrl = new OvertimeController(
            db,
            new UnrestrictedScopeService(),
            new HrmHierarchyService(db, new AuditService(db)));
        var identity = new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, "Admin"),
        }, "Test");
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return ctrl;
    }

    private static DateOnly NextWeekday(DayOfWeek day)
    {
        var d = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        while (d.DayOfWeek != day) d = d.AddDays(1);
        return d;
    }

    private sealed class UnrestrictedScopeService : Zayra.Api.Application.Common.IDataScopeService
    {
        public Task<Zayra.Api.Application.Common.DataScope> ResolveAsync(
            ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
            => Task.FromResult(new Zayra.Api.Application.Common.DataScope
            {
                Level = Zayra.Api.Application.Common.DataScopeLevel.Organization
            });
    }
}
