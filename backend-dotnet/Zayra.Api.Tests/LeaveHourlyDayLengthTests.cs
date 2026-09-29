using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The defect (audit F12): hourly leave divided by a hardcoded 8-hour day and then rounded to 4 dp
/// into a <c>numeric(6,2)</c> column.
///
/// <list type="number">
///   <item><b>The 8 was hardcoded.</b> The tenant's real day is
///     <c>AttendancePolicy.StandardWorkMinutes</c>, and for a KSA employer during Ramadan it is the
///     Art. 98 six-hour baseline that <c>AttendanceService</c> already resolves through
///     <c>KsaWorkingHoursBaselineService</c>. Leave ignored both: a 9-hour-day tenant deducted a
///     FULL day for 8 hours of leave instead of 0.89, and a 6-hour Ramadan day of hourly leave
///     deducted 0.75 days instead of 1.00.</item>
///   <item><b>The rounding was to 4 dp into a 2 dp column.</b> One hour on an 8-hour day is 0.125
///     days: the create response returned <b>0.125</b>, Postgres silently stored <b>0.13</b>, and
///     every later GET showed 0.13. The record disagreed with the response that created it.</item>
/// </list>
///
/// <para>Real Postgres, because the second half of the defect only exists at the column.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class LeaveHourlyDayLengthTests
{
    private readonly PostgresFixture _fx;
    public LeaveHourlyDayLengthTests(PostgresFixture fx) => _fx = fx;

    /// <summary>
    /// A tenant configured on a 9-hour day. Eight hours of leave is 8/9 = 0.8889 of a day, not a
    /// whole one. Pre-fix this deducted 1.00 day — 12.5% more leave than the employee took.
    /// </summary>
    [Fact]
    public async Task HourlyLeave_DividesByTheTenantsConfiguredWorkingDay_NotByAHardcodedEight()
    {
        await using var db = _fx.CreateDb();
        var (tenantId, employee, leaveType) = await SeedAsync(db, standardWorkMinutes: 540);

        var service = await TestApprovalConfig.LeaveServiceAsync(db, tenantId);
        var submitted = await service.SubmitRequestAsync(tenantId, NewHourlyRequest(employee, leaveType, hours: 8m));

        submitted.TotalDays.Should().Be(0.89m,
            "8 hours of a 9-hour working day is 0.8889 of a day, rounded to the column's 2 dp");
        submitted.TotalDays.Should().NotBe(1.00m,
            "1.00 is what dividing by a hardcoded 8 produced — a 12.5% over-deduction");
    }

    /// <summary>
    /// The number the create response returns and the number every later read returns must be the
    /// same number. One hour of an 8-hour day is the canonical case: 0.125 in memory, 0.13 in the
    /// numeric(6,2) column.
    /// </summary>
    [Fact]
    public async Task TheCreateResponseAndTheStoredRow_AgreeOnTheSameNumber()
    {
        await using var db = _fx.CreateDb();
        var (tenantId, employee, leaveType) = await SeedAsync(db, standardWorkMinutes: 480);

        var service = await TestApprovalConfig.LeaveServiceAsync(db, tenantId);
        var submitted = await service.SubmitRequestAsync(tenantId, NewHourlyRequest(employee, leaveType, hours: 1m));
        var returned = submitted.TotalDays;

        await using var reader = _fx.CreateDb();
        var stored = await reader.LeaveRequests.AsNoTracking().SingleAsync(x => x.Id == submitted.Id);

        stored.TotalDays.Should().Be(returned,
            "the create response said {0} and the row says {1}; pre-fix these were 0.125 and 0.13",
            returned, stored.TotalDays);
        returned.Should().Be(0.13m, "0.125 rounded to the column's precision, once, before the write");
        returned.Should().NotBe(0.125m, "a 4-dp value cannot survive a numeric(6,2) column");
    }

    /// <summary>
    /// An 8-hour-day tenant is the overwhelmingly common case and the fix must be inert for it:
    /// 4 hours is still half a day.
    /// </summary>
    [Fact]
    public async Task AnEightHourDayTenant_IsUnaffected()
    {
        await using var db = _fx.CreateDb();
        var (tenantId, employee, leaveType) = await SeedAsync(db, standardWorkMinutes: 480);

        var service = await TestApprovalConfig.LeaveServiceAsync(db, tenantId);
        var submitted = await service.SubmitRequestAsync(tenantId, NewHourlyRequest(employee, leaveType, hours: 4m));

        submitted.TotalDays.Should().Be(0.50m);
    }

    /// <summary>
    /// The upper bound on an hourly request was also a hardcoded 8, so a 6-hour-day tenant could
    /// book 8 hours — 1.33 days — of "hourly" leave in one request.
    /// </summary>
    [Fact]
    public async Task AnHourlyRequestCannotExceedTheTenantsWorkingDay()
    {
        await using var db = _fx.CreateDb();
        var (tenantId, employee, leaveType) = await SeedAsync(db, standardWorkMinutes: 360);

        var service = await TestApprovalConfig.LeaveServiceAsync(db, tenantId);
        var submit = () => service.SubmitRequestAsync(tenantId, NewHourlyRequest(employee, leaveType, hours: 8m));

        await submit.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*6*", "the tenant's day is 6 hours, so 8 hours is more than one day");
    }

    // ── fixture ──────────────────────────────────────────────────────────────

    private static LeaveRequest NewHourlyRequest(Employee employee, LeaveType leaveType, decimal hours)
    {
        var day = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(7));
        return new LeaveRequest
        {
            EmployeeId = employee.Id,
            EmployeeName = employee.FullName,
            LeaveTypeId = leaveType.Id,
            StartDate = day,
            EndDate = day,
            DayType = "Hourly",
            HoursRequested = hours,
            Reason = "Clinic appointment",
        };
    }

    private static async Task<(Guid TenantId, Employee Employee, LeaveType LeaveType)> SeedAsync(
        ZayraDbContext db, int standardWorkMinutes)
    {
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var company = new Company
        {
            TenantId = tenantId,
            LegalNameEn = $"Leave Hours Co {Guid.NewGuid():N}",
            TradeName = "Leave Hours Co",
            CountryCode = CountryCodes.Saudi,
            Jurisdiction = Jurisdictions.KsaMainland,
            RegistrationNumber = $"LH-{Guid.NewGuid():N}",
            DefaultCurrency = "SAR",
            IsActive = true,
        };
        db.Companies.Add(company);
        db.AttendancePolicies.Add(new AttendancePolicy
        {
            TenantId = tenantId,
            Code = $"AP{Guid.NewGuid():N}"[..8],
            Name = "Tenant working day",
            StandardWorkMinutes = standardWorkMinutes,
            IsActive = true,
        });
        var leaveType = new LeaveType
        {
            TenantId = tenantId,
            Code = $"CAS{Guid.NewGuid():N}"[..8],
            NameEn = "Casual Leave",
            IsHourlyAllowed = true,
            IsActive = true,
        };
        db.LeaveTypes.Add(leaveType);
        await db.SaveChangesAsync();

        var employee = new Employee
        {
            TenantId = tenantId,
            CompanyId = company.Id,
            EmployeeCode = $"LH-{Guid.NewGuid():N}",
            FullName = "Hourly Leave Taker",
            Status = "Active",
            JoiningDate = DateTime.UtcNow.AddYears(-3),
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
        {
            TenantId = tenantId,
            EmployeeId = employee.Id,
            EmployeeName = employee.FullName,
            LeaveTypeId = leaveType.Id,
            LeaveTypeName = leaveType.NameEn,
            Year = DateTime.UtcNow.Date.AddDays(7).Year,
            Entitled = 21,
        });
        await db.SaveChangesAsync();

        return (tenantId, employee, leaveType);
    }
}
