using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// FAIL-BEFORE PROBE, written against develop @ 52e0822 using ONLY pre-existing APIs.
/// Each assertion states the KSA statutory outcome. All three are expected to FAIL on develop;
/// the same three behaviours pass on feat/ksa-leave-hours via KsaStatutoryLeaveAndHoursTests.
/// </summary>
public class KsaGapProbeTests
{
    private static readonly DateOnly RamadanWednesday = new(2026, 3, 4);   // Um al-Qura 1447-09-15

    [Fact]
    public async Task GAP1_EightHoursOnARamadanDay_ShouldProduceTwoHoursOvertime()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        // The EMPLOYING COMPANY is what puts this employee inside Art. 98. This fixture used to set
        // Employee.CountryCode instead, matching the personal-field gate the article was wrongly
        // read from; see KsaStatutoryLeaveAndHoursTests.Art98_* for both failure directions.
        var company = new Company { TenantId = tenantId, LegalNameEn = "KSA Co", CountryCode = "SA" };
        db.Companies.Add(company);
        var employee = new Employee
        {
            TenantId = tenantId, EmployeeCode = "KSA-1", EnglishName = "R", FullName = "R",
            Status = "Active", CompanyId = company.Id, JoiningDate = new DateTime(2020, 1, 1),
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        db.AttendanceRawEvents.Add(new AttendanceRawEvent
        {
            TenantId = tenantId, EmployeeId = employee.Id, PunchDirection = "In",
            PunchTimestampUtc = RamadanWednesday.ToDateTime(new TimeOnly(6, 0), DateTimeKind.Utc),
        });
        db.AttendanceRawEvents.Add(new AttendanceRawEvent
        {
            TenantId = tenantId, EmployeeId = employee.Id, PunchDirection = "Out",
            PunchTimestampUtc = RamadanWednesday.ToDateTime(new TimeOnly(15, 0), DateTimeKind.Utc),
        });
        await db.SaveChangesAsync();

        await new AttendanceService(db, new NullNotifications(), new NullHttpClients()).ProcessAsync(
            tenantId, new ProcessAttendanceRequest(RamadanWednesday, RamadanWednesday, employee.Id),
            new RequestContext(null, null, Guid.NewGuid(), tenantId), CancellationToken.None);

        var daily = await db.AttendanceDailyRecords.SingleAsync();
        daily.TotalWorkedMinutes.Should().Be(480);
        daily.OvertimeMinutes.Should().Be(120,
            "KSA Art. 98 caps Ramadan actual working hours at 6 h/day, so hours 7 and 8 are overtime");
    }

    [Fact]
    public async Task GAP3_AnnualAccrual_ShouldTierTo30DaysAfterFiveYears()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var company = new Company { TenantId = tenantId, LegalNameEn = "KSA Co", CountryCode = "SA" };
        db.Companies.Add(company);
        var veteran = new Employee
        {
            TenantId = tenantId, EmployeeCode = "VET", EnglishName = "V", FullName = "V",
            Status = "Active", CompanyId = company.Id, JoiningDate = DateTime.UtcNow.AddYears(-7),
        };
        db.Employees.Add(veteran);
        var annual = new LeaveType
        {
            TenantId = tenantId, Code = "ANNUAL", NameEn = "Annual Leave", Category = "Annual", IsActive = true,
        };
        db.LeaveTypes.Add(annual);
        db.LeavePolicies.Add(new LeavePolicy
        {
            TenantId = tenantId, Name = "Default Annual Leave", LeaveTypeId = annual.Id,
            AnnualEntitlementDays = 21m, AccrualMethod = "Monthly", Status = "Active",
        });
        await db.SaveChangesAsync();

        await new LeaveService(db, new Zayra.Api.Infrastructure.Approvals.ApprovalRouter(db))
            .AccrueMonthlyAsync(tenantId, CancellationToken.None);

        var balance = await db.EmployeeLeaveBalances.SingleAsync(b => b.EmployeeId == veteran.Id);
        balance.Accrued.Should().Be(2.5m,
            "KSA Art. 109 raises annual leave to 30 days after five consecutive years — 30/12 = 2.5/month");
    }

    [Fact]
    public async Task GAP2_OneHundredAndTwentySickDays_ShouldDeduct18000()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var company = new Company { TenantId = tenantId, LegalNameEn = "KSA Co", CountryCode = "SA" };
        db.Companies.Add(company);
        var emp = new Employee
        {
            TenantId = tenantId, EmployeeCode = "SICK1", EnglishName = "S", FullName = "S",
            Status = "Active", CompanyId = company.Id, JoiningDate = DateTime.UtcNow.AddYears(-3),
        };
        db.Employees.Add(emp);
        var sick = new LeaveType
        {
            TenantId = tenantId, Code = "SICK", NameEn = "Sick Leave", Category = "Sick",
            IsPaid = true, IsActive = true,
        };
        db.LeaveTypes.Add(sick);
        await db.SaveChangesAsync();

        // Art. 117 counts sick leave in CALENDAR days — "during a single year, whether such leaves
        // are continuous or intermittent" — so the sick-leave policy includes weekends and public
        // holidays. This used to be implicit: the fixture seeded no policy at all and the day-count
        // fell back to raw calendar days for want of one. That fallback now resolves the tenant's
        // configured working week (a Fri–Sat rest week here), which is right for annual leave and
        // wrong for Art. 117 — so the statute is stated on the policy, where it belongs, instead of
        // being inherited from a missing one.
        db.LeavePolicies.Add(new LeavePolicy
        {
            TenantId = tenantId, CompanyId = company.Id, CountryCode = "SA", LeaveTypeId = sick.Id,
            Name = "KSA Sick Leave (Art. 117)", Status = "Active",
            WeekendsIncluded = true, PublicHolidaysIncluded = true,
        });
        // The sick-leave requests below are backdated certified illness, which is what sick leave
        // is: they carry IsEmergency so the policy's notice period — a rule for PLANNED absence —
        // does not reject them. With no policy at all there was no notice period to satisfy.

        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = emp.Id, SalaryStructureId = Guid.NewGuid(),
            BasicSalary = 12_000m, EffectiveDate = new DateOnly(2020, 1, 1), IsActive = true,
        });
        db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
        {
            TenantId = tenantId, EmployeeId = emp.Id, LeaveTypeId = sick.Id,
            LeaveTypeName = sick.NameEn, EmployeeName = emp.FullName,
            Year = 2026, Entitled = 400, Accrued = 0,
        });
        await db.SaveChangesAsync();

        var svc = await TestApprovalConfig.LeaveServiceAsync(db, tenantId);
        var start = new DateOnly(2026, 1, 5);
        var submitted = await svc.SubmitRequestAsync(tenantId, new LeaveRequest
        {
            TenantId = tenantId, CompanyId = emp.CompanyId, EmployeeId = emp.Id, EmployeeName = emp.FullName,
            LeaveTypeId = sick.Id, StartDate = start, EndDate = start.AddDays(119), DayType = "Full", IsEmergency = true,
            Reason = "Certified illness",
        }, CancellationToken.None);
        await svc.ApproveRequestAsync(tenantId, submitted.Id, Guid.NewGuid(), "Manager", null, CancellationToken.None);

        var impact = await db.LeavePayrollImpacts.FirstOrDefaultAsync(x => x.LeaveRequestId == submitted.Id);
        impact.Should().NotBeNull(
            "KSA Art. 117 pays only 30 days at full wage, 60 at three quarters and 30 unpaid");
        impact!.Amount.Should().Be(18_000m, "45 unpaid-equivalent days x (12,000/30) = 18,000");
    }

    private static ZayraDbContext CreateDb() => new(
        new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class NullNotifications : INotificationService
    {
        public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
        public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NullHttpClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
