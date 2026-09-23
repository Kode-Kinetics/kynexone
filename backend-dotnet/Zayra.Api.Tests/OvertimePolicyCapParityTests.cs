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
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// There are two doors into overtime pay and only one of them obeyed the policy.
///
/// <para><c>POST /api/overtime/requests</c> rounded the measured minutes by the policy's
/// <c>RoundingRule</c>, refused anything under <c>MinimumMinutes</c>, refused anything over
/// <c>MaximumMinutesPerDay</c>, and refused anything that would take the month past
/// <c>MonthlyCapMinutes</c>. <c>POST /api/overtime/detect-from-attendance</c> wrote
/// <c>RequestedMinutes = record.OvertimeMinutes</c> raw and applied NONE of them — it did not even
/// load the policy.</para>
///
/// <para>Measured here on a policy configured at <b>240 min/day</b> and <b>600 min/month</b>: a
/// 14-hour attendance day (840 min) was raised for 840 min — <b>3.5× the configured daily
/// maximum</b> — and approved and paid in full, while the same 840 minutes keyed by hand were
/// refused outright. Unbounded, and always in the employee's favour.</para>
///
/// <para>The cap is not a licence to truncate: the minutes the policy will not pay are worked time,
/// so every assertion below also checks that they reach a row a human resolves.</para>
///
/// <para>Real Postgres, the real controller actions both doors are served by.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class OvertimePolicyCapParityTests
{
    private readonly PostgresFixture _fx;
    public OvertimePolicyCapParityTests(PostgresFixture fx) => _fx = fx;

    private const int MaxPerDay = 240;        // 4 h
    private const int MonthlyCap = 600;       // 10 h
    private const int MinimumMinutes = 30;
    private static readonly DateOnly WorkDate = new(2026, 6, 10);   // a Wednesday: not a KSA rest day

    /// <summary>
    /// THE defect. 840 minutes of attendance overtime on a policy that caps a day at 240.
    /// The manual door refuses it; the attendance door used to raise all 840.
    /// </summary>
    [Fact]
    public async Task AttendanceDerivedOvertime_IsCappedAtTheDailyMaximum_JustAsTheManualDoorIs()
    {
        await using var db = _fx.CreateDb();
        var f = await SeedAsync(db);

        // ── The manual door, with the same 840 minutes ────────────────────────────────────────
        var manual = await Ctrl(db, f.TenantId).CreateRequest(new OvertimeRequestCreate(
            f.EmployeeId, f.PolicyId, null, WorkDate,
            new DateTime(2026, 6, 10, 10, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 6, 11, 0, 0, 0, DateTimeKind.Utc),   // 840 minutes
            "Manual", "Stock count"), CancellationToken.None);

        manual.Result.Should().BeOfType<BadRequestObjectResult>(
            "the manual door has always refused a day over the configured maximum");
        Message(manual.Result!).Should().Be($"Maximum overtime is {MaxPerDay} minutes per day.");
        (await db.OvertimeRequests.CountAsync(x => x.TenantId == f.TenantId)).Should().Be(0,
            "a refused request must not be persisted");

        // ── The attendance door, with the same 840 minutes ────────────────────────────────────
        await AddAttendanceDayAsync(db, f, WorkDate, overtimeMinutes: 840);

        var detected = await DetectAsync(db, f, WorkDate, WorkDate);

        detected.Created.Should().HaveCount(1);
        var request = detected.Created.Single();
        request.RequestedMinutes.Should().Be(MaxPerDay,
            "attendance-derived overtime must obey the same configured daily maximum as a hand-keyed "
            + "request; 840 is the pre-fix figure — 3.5× the policy, paid in full");
        request.RequestedMinutes.Should().NotBe(840);
        request.OvertimePolicyId.Should().Be(f.PolicyId,
            "the policy is resolved and stamped, not taken on trust from the caller");

        // ── The 600 minutes the policy will not pay are VISIBLE, not discarded ────────────────
        detected.Capped.Should().HaveCount(1, "the response tells the operator what was not paid");
        var persisted = await db.AttendanceExceptions.AsNoTracking()
            .SingleAsync(x => x.TenantId == f.TenantId && x.EmployeeId == f.EmployeeId);
        persisted.ExceptionType.Should().Be(OvertimePolicyLimits.OvertimeAbovePolicyExceptionType);
        persisted.Severity.Should().Be("High", "unpaid worked time is not an informational notice");
        persisted.IsResolved.Should().BeFalse("it is open until a human decides what to do with it");
        persisted.WorkDate.Should().Be(WorkDate);
        persisted.Details.Should().Contain("840").And.Contain("240").And.Contain("600",
            "the row must state what was measured, what is payable and what is not");

        // The request's own start/end window describes the minutes it actually claims.
        (request.EndTimeUtc - request.StartTimeUtc).TotalMinutes.Should().Be(MaxPerDay,
            "a request whose times span more than it pays is its own defect");
    }

    /// <summary>
    /// The monthly cap, the limit an unbounded batch walks past one day at a time. Three 240-minute
    /// days against a 600-minute month: 240 + 240 + 120, then nothing.
    /// </summary>
    [Fact]
    public async Task AttendanceDetection_ConsumesTheMonthlyCapAcrossTheBatch_AndStopsAtIt()
    {
        await using var db = _fx.CreateDb();
        var f = await SeedAsync(db);

        foreach (var day in new[] { 8, 9, 10, 11 })
            await AddAttendanceDayAsync(db, f, new DateOnly(2026, 6, day), overtimeMinutes: 240);

        var detected = await DetectAsync(db, f, new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30));

        detected.Created.Sum(x => x.RequestedMinutes).Should().Be(MonthlyCap,
            "the batch must not walk past the monthly cap one day at a time; 960 is the pre-fix total");
        detected.Created.OrderBy(x => x.WorkDate).Select(x => x.RequestedMinutes)
            .Should().Equal(240, 240, 120);

        var persistedTotal = await db.OvertimeRequests.AsNoTracking()
            .Where(x => x.TenantId == f.TenantId).SumAsync(x => x.RequestedMinutes);
        persistedTotal.Should().Be(MonthlyCap, "what was stored is what will be paid");

        // Day 3 lost 120 minutes to the cap and day 4 lost all 240 — both are on the record.
        var capped = await db.AttendanceExceptions.AsNoTracking()
            .Where(x => x.TenantId == f.TenantId
                     && x.ExceptionType == OvertimePolicyLimits.OvertimeAbovePolicyExceptionType)
            .OrderBy(x => x.WorkDate).ToListAsync();
        capped.Should().HaveCount(2);
        capped[0].WorkDate.Should().Be(new DateOnly(2026, 6, 10));
        capped[1].WorkDate.Should().Be(new DateOnly(2026, 6, 11));
        capped.Should().OnlyContain(x => !x.IsResolved);
    }

    /// <summary>
    /// The rounding rule and the minimum. 22 minutes rounds to 15 under Down15 and then falls under
    /// the 30-minute minimum, so the policy pays nothing for the day — which is exactly what the
    /// manual door does, and is still recorded rather than vanishing.
    /// </summary>
    [Fact]
    public async Task AttendanceDerivedOvertime_AppliesTheRoundingRuleAndTheMinimum()
    {
        await using var db = _fx.CreateDb();
        var f = await SeedAsync(db, roundingRule: "Down15");
        await AddAttendanceDayAsync(db, f, WorkDate, overtimeMinutes: 22);

        var detected = await DetectAsync(db, f, WorkDate, WorkDate);

        detected.Created.Should().BeEmpty(
            "22 min rounds down to 15, which is under the 30-minute policy minimum the manual door enforces");
        var below = await db.AttendanceExceptions.AsNoTracking()
            .SingleAsync(x => x.TenantId == f.TenantId);
        below.ExceptionType.Should().Be(OvertimePolicyLimits.OvertimeBelowMinimumExceptionType);
        below.Severity.Should().Be("Low", "the policy working as configured is logged, not escalated");
        below.Details.Should().Contain("22").And.Contain("15").And.Contain("30");
    }

    /// <summary>
    /// A second detection over the same range must neither double the pay nor stack duplicate
    /// exception rows. Detection is run repeatedly by operators; it has to be idempotent.
    /// </summary>
    [Fact]
    public async Task ReRunningDetection_AddsNeitherPayNorDuplicateExceptions()
    {
        await using var db = _fx.CreateDb();
        var f = await SeedAsync(db);
        await AddAttendanceDayAsync(db, f, WorkDate, overtimeMinutes: 840);

        await DetectAsync(db, f, WorkDate, WorkDate);
        var second = await DetectAsync(db, f, WorkDate, WorkDate);

        second.Created.Should().BeEmpty("the day already has an attendance-sourced request");
        (await db.OvertimeRequests.CountAsync(x => x.TenantId == f.TenantId)).Should().Be(1);
        (await db.OvertimeRequests.Where(x => x.TenantId == f.TenantId).SumAsync(x => x.RequestedMinutes))
            .Should().Be(MaxPerDay);
        (await db.AttendanceExceptions.CountAsync(x => x.TenantId == f.TenantId)).Should().Be(1,
            "an unresolved row for the same day and reason is not raised twice");
    }

    /// <summary>
    /// Detection cannot proceed without a policy to apply — the whole point of the fix. Previously
    /// the caller's policy id was stamped on the request without ever being looked up, so an id
    /// belonging to nothing (or to another tenant) was accepted silently.
    /// </summary>
    [Fact]
    public async Task Detection_RefusesWhenTheCallerNamesAPolicyThisTenantDoesNotHave()
    {
        await using var db = _fx.CreateDb();
        var f = await SeedAsync(db);
        await AddAttendanceDayAsync(db, f, WorkDate, overtimeMinutes: 840);

        var result = await Ctrl(db, f.TenantId).DetectFromAttendance(
            new DetectOvertimeRequest(WorkDate, WorkDate, Guid.NewGuid()), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        Message(result.Result!).Should().Be("An active overtime policy is required.");
        (await db.OvertimeRequests.CountAsync(x => x.TenantId == f.TenantId)).Should().Be(0,
            "no overtime may be raised against a policy that was never resolved");
    }

    /// <summary>
    /// The limits are a pure function, so the arithmetic is pinned directly as well as through the
    /// controller — including the case where both caps bind and the first one is the one reported.
    /// </summary>
    [Theory]
    // raw, monthToDate, expected payable, expected excess, expected limit
    [InlineData(120, 0, 120, 0, OvertimeLimitKind.None)]
    [InlineData(840, 0, 240, 600, OvertimeLimitKind.AboveDailyMaximum)]
    [InlineData(240, 480, 120, 120, OvertimeLimitKind.AboveMonthlyCap)]
    [InlineData(840, 480, 120, 720, OvertimeLimitKind.AboveDailyMaximum)]
    [InlineData(240, 600, 0, 240, OvertimeLimitKind.AboveMonthlyCap)]
    [InlineData(20, 0, 0, 20, OvertimeLimitKind.BelowMinimum)]
    // Only 20 minutes of cap are left, which is under the policy minimum: the manual door would
    // refuse such a request outright, so none is raised here either.
    [InlineData(240, 580, 0, 240, OvertimeLimitKind.AboveMonthlyCap)]
    public void PolicyLimits_CapWithoutLosingTrackOfTheExcess(
        int raw, int monthToDate, int expectedPayable, int expectedExcess, OvertimeLimitKind expectedLimit)
    {
        var policy = new OvertimePolicy
        {
            MinimumMinutes = MinimumMinutes,
            MaximumMinutesPerDay = MaxPerDay,
            MonthlyCapMinutes = MonthlyCap,
            RoundingRule = "None",
        };

        var outcome = OvertimePolicyLimits.Apply(raw, policy, monthToDate);

        outcome.PayableMinutes.Should().Be(expectedPayable);
        outcome.ExcessMinutes.Should().Be(expectedExcess);
        outcome.Limit.Should().Be(expectedLimit);
        (outcome.PayableMinutes + outcome.ExcessMinutes).Should().Be(outcome.RoundedMinutes,
            "every rounded minute is either payable or accounted for as excess — none may be dropped");
        outcome.PayableMinutes.Should().BeLessThanOrEqualTo(MaxPerDay);
        (monthToDate + outcome.PayableMinutes).Should().BeLessThanOrEqualTo(MonthlyCap);
    }

    // ══════════════════════ Fixture ══════════════════════

    private sealed record Fixture(Guid TenantId, Guid CompanyId, int EmployeeId, Guid PolicyId);

    private async Task<Fixture> SeedAsync(ZayraDbContext db, string roundingRule = "None")
    {
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var company = new Company
        {
            TenantId = tenantId,
            LegalNameEn = $"OT Caps Co {Guid.NewGuid():N}",
            TradeName = "OT Caps Co",
            CountryCode = CountryCodes.Saudi,
            Jurisdiction = Jurisdictions.KsaMainland,
            RegistrationNumber = $"OTC-{Guid.NewGuid():N}",
            DefaultCurrency = "SAR",
            IsActive = true,
            CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Companies.Add(company);
        var employee = new Employee
        {
            TenantId = tenantId,
            CompanyId = company.Id,
            EmployeeCode = $"OTC-{Guid.NewGuid():N}",
            FullName = "Ahmed Al-Rashidi",
            Nationality = "Saudi",
            Status = "Active",
            JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CountryCode = CountryCodes.Saudi,
            Salary = 18_000m,
        };
        var policy = new OvertimePolicy
        {
            TenantId = tenantId,
            Code = $"OT{Guid.NewGuid():N}"[..8],
            Name = "Capped OT policy",
            HourlyRateBasis = "BasicSalary",
            StandardMonthlyHours = 240,
            MinimumMinutes = MinimumMinutes,
            MaximumMinutesPerDay = MaxPerDay,
            MonthlyCapMinutes = MonthlyCap,
            RoundingRule = roundingRule,
            RequiresApproval = true,
            IsActive = true,
            CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Employees.Add(employee);
        db.OvertimePolicies.Add(policy);
        await db.SaveChangesAsync();
        return new Fixture(tenantId, company.Id, employee.Id, policy.Id);
    }

    private static async Task AddAttendanceDayAsync(
        ZayraDbContext db, Fixture f, DateOnly workDate, int overtimeMinutes)
    {
        db.AttendanceDailyRecords.Add(new AttendanceDailyRecord
        {
            TenantId = f.TenantId,
            EmployeeId = f.EmployeeId,
            EmployeeName = "Ahmed Al-Rashidi",
            WorkDate = workDate,
            FirstInUtc = workDate.ToDateTime(new TimeOnly(6, 0), DateTimeKind.Utc),
            LastOutUtc = workDate.ToDateTime(new TimeOnly(20, 0), DateTimeKind.Utc),
            OvertimeMinutes = overtimeMinutes,
            Status = "Present",
        });
        await db.SaveChangesAsync();
    }

    private async Task<DetectOvertimeResult> DetectAsync(
        ZayraDbContext db, Fixture f, DateOnly from, DateOnly to)
    {
        var result = await Ctrl(db, f.TenantId).DetectFromAttendance(
            new DetectOvertimeRequest(from, to, f.PolicyId), CancellationToken.None);
        result.Result.Should().BeOfType<OkObjectResult>();
        return (DetectOvertimeResult)((OkObjectResult)result.Result!).Value!;
    }

    private static string Message(IActionResult result) =>
        (string)result.GetType().GetProperty("Value")!.GetValue(result)!
            .GetType().GetProperty("message")!
            .GetValue(((ObjectResult)result).Value!)!;

    private static OvertimeController Ctrl(ZayraDbContext db, Guid tenantId)
    {
        var ctrl = new OvertimeController(
            db,
            new OrganizationScopeService(),
            new HrmHierarchyService(db, new AuditService(db)));
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
        new Claim(ClaimTypes.Name, "OT Caps Tester"),
        new Claim(ClaimTypes.Role, "Admin"),
    }, "Test"));

    private sealed class OrganizationScopeService : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
            => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
    }
}
