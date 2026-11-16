using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Leave;
using Zayra.Api.Controllers.Leave;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Every leave refusal the rules produce carries a stable code (and the values its sentence needs) so the web and mobile
/// clients can show it in Arabic; the English message stays byte-for-byte what it was, and the HTTP status stays 400.
/// In memory, no web host.
/// </summary>
public sealed class LeaveRefusalCodesTests
{
    private static LeaveRequest Req(LeaveBatch1World w, DateOnly start, DateOnly? end = null, string dayType = "Full", string reason = "needed", decimal hours = 0m) => new()
    {
        TenantId = w.TenantId, EmployeeId = w.Emp.Id, LeaveTypeId = w.LeaveType.Id, StartDate = start, EndDate = end ?? start,
        DayType = dayType, Reason = reason, HoursRequested = hours,
    };

    /// <summary>The employee filing their own leave: the employee_id claim is what makes the request "self".</summary>
    private static System.Security.Claims.ClaimsPrincipal Self(LeaveBatch1World w)
    {
        var user = w.User(w.EmpUserId, "Employee", "leave.read", "ess.read");
        ((System.Security.Claims.ClaimsIdentity)user.Identity!).AddClaim(new System.Security.Claims.Claim("employee_id", w.Emp.Id.ToString()));
        return user;
    }

    private static async Task<LeaveRefusedException> RefusedAsync(LeaveBatch1World w, LeaveRequest request)
    {
        var act = () => w.Service.SubmitRequestAsync(w.TenantId, request, w.EmpUserId);
        return (await act.Should().ThrowAsync<LeaveRefusedException>()).Which;
    }

    private static async Task<LeavePolicy> AddPolicyAsync(LeaveBatch1World w, Action<LeavePolicy> configure)
    {
        var policy = new LeavePolicy { TenantId = w.TenantId, Name = "Annual", LeaveTypeId = w.LeaveType.Id, Status = "Active", AppliesOnProbation = true };
        configure(policy);
        w.Db.LeavePolicies.Add(policy);
        await w.Db.SaveChangesAsync();
        return policy;
    }

    [Fact]
    public async Task Overlap_carries_the_overlap_code_and_the_same_message()
    {
        var w = await LeaveBatch1World.CreateAsync();
        var day = w.NextWorkday();
        await w.Service.SubmitRequestAsync(w.TenantId, Req(w, day), w.EmpUserId);

        var refused = await RefusedAsync(w, Req(w, day));

        refused.Code.Should().Be(LeaveRefusalCodes.Overlap);
        refused.Message.Should().Be("Employee already has an approved or pending leave for the requested dates.");
    }

    [Fact]
    public async Task EndBeforeStart_is_an_invalid_dates_refusal()
    {
        var w = await LeaveBatch1World.CreateAsync();
        var day = w.NextWorkday();

        var refused = await RefusedAsync(w, Req(w, day, day.AddDays(-1)));

        refused.Code.Should().Be(LeaveRefusalCodes.InvalidDates);
        refused.Message.Should().Be("End date must be after start date.");
    }

    [Fact]
    public async Task HalfDayOverSeveralDates_is_an_invalid_dates_refusal()
    {
        var w = await LeaveBatch1World.CreateAsync();
        var day = w.NextWorkday();

        var refused = await RefusedAsync(w, Req(w, day, day.AddDays(3), dayType: "HalfDay"));

        refused.Code.Should().Be(LeaveRefusalCodes.InvalidDates);
        refused.Message.Should().Be("Half-day leave must be for a single date.");
    }

    [Fact]
    public async Task UnknownLeaveType_is_a_type_unavailable_refusal()
    {
        var w = await LeaveBatch1World.CreateAsync();
        var request = Req(w, w.NextWorkday());
        request.LeaveTypeId = Guid.NewGuid();

        var refused = await RefusedAsync(w, request);

        refused.Code.Should().Be(LeaveRefusalCodes.TypeUnavailable);
        refused.Message.Should().Be("Invalid leave type.");
    }

    [Fact]
    public async Task AnotherCompanysType_is_a_type_unavailable_refusal()
    {
        var w = await LeaveBatch1World.CreateAsync();
        w.LeaveType.CompanyId = Guid.NewGuid();
        await w.Db.SaveChangesAsync();

        var refused = await RefusedAsync(w, Req(w, w.NextWorkday()));

        refused.Code.Should().Be(LeaveRefusalCodes.TypeUnavailable);
        refused.Message.Should().Be(LeaveCompanyScope.NotThisCompanysType);
    }

    [Fact]
    public async Task Blackout_carries_the_blackout_code_and_the_same_message()
    {
        var w = await LeaveBatch1World.CreateAsync();
        var day = w.NextWorkday();
        w.Db.LeaveBlackoutDates.Add(new LeaveBlackoutDate
        {
            TenantId = w.TenantId, NameEn = "Year end", StartDate = day, EndDate = day.AddDays(2), IsCompanyWide = true, Reason = "Audit.",
        });
        await w.Db.SaveChangesAsync();

        var refused = await RefusedAsync(w, Req(w, day));

        refused.Code.Should().Be(LeaveRefusalCodes.Blackout);
        refused.Message.Should().Be($"Leave cannot be requested during blackout 'Year end' ({day:yyyy-MM-dd} to {day.AddDays(2):yyyy-MM-dd}). Audit.");
    }

    [Fact]
    public async Task MissingAttachment_is_an_attachment_required_refusal()
    {
        var w = await LeaveBatch1World.CreateAsync(requireAttachment: true);

        var refused = await RefusedAsync(w, Req(w, w.NextWorkday()));

        refused.Code.Should().Be(LeaveRefusalCodes.AttachmentRequired);
        refused.Message.Should().Be("An attachment is required for this leave type.");
    }

    [Fact]
    public async Task MissingReason_is_a_reason_required_refusal()
    {
        var w = await LeaveBatch1World.CreateAsync();
        w.LeaveType.RequiresReason = true;
        await w.Db.SaveChangesAsync();

        var refused = await RefusedAsync(w, Req(w, w.NextWorkday(), reason: ""));

        refused.Code.Should().Be(LeaveRefusalCodes.ReasonRequired);
        refused.Message.Should().Be("A reason is required for this leave type.");
    }

    [Fact]
    public async Task MaxConsecutiveDays_carries_the_limit()
    {
        var w = await LeaveBatch1World.CreateAsync();
        w.LeaveType.MaxConsecutiveDays = 1;
        await w.Db.SaveChangesAsync();
        var day = w.NextWorkday();

        var refused = await RefusedAsync(w, Req(w, day, day.AddDays(6)));

        refused.Code.Should().Be(LeaveRefusalCodes.MaxConsecutiveDays);
        refused.Args.Should().ContainKey("days").WhoseValue.Should().Be("1");
        refused.Message.Should().StartWith("This leave type allows a maximum of 1 consecutive day(s). Requested: ");
    }

    [Fact]
    public async Task HalfDay_on_a_type_that_does_not_allow_it_is_refused_with_its_code()
    {
        var w = await LeaveBatch1World.CreateAsync();
        w.LeaveType.IsHalfDayAllowed = false;
        await w.Db.SaveChangesAsync();

        var refused = await RefusedAsync(w, Req(w, w.NextWorkday(), dayType: "HalfDay"));

        refused.Code.Should().Be(LeaveRefusalCodes.HalfDayNotAllowed);
        refused.Message.Should().Be("Half-day leave is not allowed for this leave type.");
    }

    [Fact]
    public async Task Hourly_on_a_type_that_does_not_allow_it_is_refused_with_its_code()
    {
        var w = await LeaveBatch1World.CreateAsync();
        w.LeaveType.IsHourlyAllowed = false;
        await w.Db.SaveChangesAsync();

        var refused = await RefusedAsync(w, Req(w, w.NextWorkday(), dayType: "Hourly", hours: 2m));

        refused.Code.Should().Be(LeaveRefusalCodes.HourlyNotAllowed);
        refused.Message.Should().Be("Hourly leave is not allowed for this leave type.");
    }

    [Fact]
    public async Task MinimumDaysPerRequest_carries_the_minimum()
    {
        var w = await LeaveBatch1World.CreateAsync();
        await AddPolicyAsync(w, p => p.MinimumDaysPerRequest = 5);

        var refused = await RefusedAsync(w, Req(w, w.NextWorkday()));

        refused.Code.Should().Be(LeaveRefusalCodes.MinDaysPerRequest);
        refused.Args.Should().ContainKey("days").WhoseValue.Should().Be("5");
        refused.Message.Should().Be("This policy requires at least 5 day(s) per request.");
    }

    [Fact]
    public async Task MaximumDaysPerRequest_carries_the_maximum()
    {
        var w = await LeaveBatch1World.CreateAsync();
        await AddPolicyAsync(w, p => p.MaximumDaysPerRequest = 1);
        var day = w.NextWorkday();

        var refused = await RefusedAsync(w, Req(w, day, day.AddDays(6)));

        refused.Code.Should().Be(LeaveRefusalCodes.MaxDaysPerRequest);
        refused.Args.Should().ContainKey("days").WhoseValue.Should().Be("1");
        refused.Message.Should().Be("This policy allows at most 1 day(s) per request.");
    }

    [Fact]
    public async Task Notice_carries_the_days_required()
    {
        var w = await LeaveBatch1World.CreateAsync();
        await AddPolicyAsync(w, p => p.NoticeRequiredDays = 400);

        var refused = await RefusedAsync(w, Req(w, w.NextWorkday()));

        refused.Code.Should().Be(LeaveRefusalCodes.NoticeRequired);
        refused.Args.Should().ContainKey("days").WhoseValue.Should().Be("400");
        refused.Message.Should().Be("This policy requires 400 day(s) advance notice.");
    }

    [Fact]
    public async Task Probation_is_refused_with_its_code()
    {
        var w = await LeaveBatch1World.CreateAsync();
        await AddPolicyAsync(w, p => p.AppliesOnProbation = false);
        var day = w.NextWorkday();
        w.Emp.ProbationEndDate = day.AddDays(30);
        await w.Db.SaveChangesAsync();

        var refused = await RefusedAsync(w, Req(w, day));

        refused.Code.Should().Be(LeaveRefusalCodes.Probation);
        refused.Message.Should().Be("This leave policy does not apply during probation.");
    }

    [Fact]
    public async Task MinimumServiceMonths_carries_the_months()
    {
        var w = await LeaveBatch1World.CreateAsync();
        await AddPolicyAsync(w, p => p.MinimumServiceMonths = 120);

        var refused = await RefusedAsync(w, Req(w, w.NextWorkday()));

        refused.Code.Should().Be(LeaveRefusalCodes.MinServiceMonths);
        refused.Args.Should().ContainKey("months").WhoseValue.Should().Be("120");
        refused.Message.Should().StartWith("This leave policy applies after 120 month(s) of service — from ");
    }

    [Fact]
    public async Task InsufficientBalance_carries_the_year()
    {
        var w = await LeaveBatch1World.CreateAsync();
        var day = w.NextWorkday();
        foreach (var balance in w.Db.EmployeeLeaveBalances.Where(b => b.EmployeeId == w.Emp.Id)) balance.Entitled = 0;
        await w.Db.SaveChangesAsync();

        var refused = await RefusedAsync(w, Req(w, day));

        refused.Code.Should().Be(LeaveRefusalCodes.InsufficientBalance);
        refused.Args.Should().ContainKey("year").WhoseValue.Should().Be(day.Year.ToString());
        refused.Message.Should().Be($"Insufficient leave balance for {day.Year}.");
    }

    [Fact]
    public async Task BalanceNotSetUp_carries_the_year_and_the_leave_type()
    {
        var w = await LeaveBatch1World.CreateAsync();
        var day = w.NextWorkday();
        w.Db.EmployeeLeaveBalances.RemoveRange(w.Db.EmployeeLeaveBalances.Where(b => b.EmployeeId == w.Emp.Id));
        await w.Db.SaveChangesAsync();

        var refused = await RefusedAsync(w, Req(w, day));

        refused.Code.Should().Be(LeaveRefusalCodes.BalanceNotSetUp);
        refused.Args.Should().ContainKey("year").WhoseValue.Should().Be(day.Year.ToString());
        refused.Args.Should().ContainKey("leaveType").WhoseValue.Should().Be("Annual Leave");
        refused.Message.Should().Be($"Your Annual Leave balance for {day.Year} has not been set up yet. Ask HR to set up your {day.Year} leave balance.");
    }

    [Fact]
    public async Task BalanceNotSetUp_also_carries_the_arabic_leave_type_name_when_the_type_has_one()
    {
        var w = await LeaveBatch1World.CreateAsync();
        var day = w.NextWorkday();
        w.Db.EmployeeLeaveBalances.RemoveRange(w.Db.EmployeeLeaveBalances.Where(b => b.EmployeeId == w.Emp.Id));
        (await w.Db.LeaveTypes.SingleAsync(t => t.Id == w.LeaveType.Id)).NameAr = "إجازة سنوية";
        await w.Db.SaveChangesAsync();

        var refused = await RefusedAsync(w, Req(w, day));

        refused.Args.Should().ContainKey("leaveType").WhoseValue.Should().Be("Annual Leave");
        refused.Args.Should().ContainKey("leaveTypeAr").WhoseValue.Should().Be("إجازة سنوية");
        refused.Message.Should().Be($"Your Annual Leave balance for {day.Year} has not been set up yet. Ask HR to set up your {day.Year} leave balance.");
    }

    [Fact]
    public async Task BalanceNotSetUp_leaves_the_arabic_name_out_when_the_type_has_none()
    {
        var w = await LeaveBatch1World.CreateAsync();
        var day = w.NextWorkday();
        w.Db.EmployeeLeaveBalances.RemoveRange(w.Db.EmployeeLeaveBalances.Where(b => b.EmployeeId == w.Emp.Id));
        await w.Db.SaveChangesAsync();

        var refused = await RefusedAsync(w, Req(w, day));

        refused.Args.Should().NotContainKey("leaveTypeAr");
    }

    [Fact]
    public async Task AnExistingInvalidOperationCatch_still_sees_the_refusal()
    {
        var w = await LeaveBatch1World.CreateAsync();
        var day = w.NextWorkday();
        await w.Service.SubmitRequestAsync(w.TenantId, Req(w, day), w.EmpUserId);

        var act = () => w.Service.SubmitRequestAsync(w.TenantId, Req(w, day), w.EmpUserId);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("Employee already has an approved or pending leave*");
    }

    // ── KSA statutory limits ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AStatutoryEventOverTheStatute_is_a_statutory_limit_refusal_with_the_days()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenantId = Guid.NewGuid();
        var company = new Company { TenantId = tenantId, LegalNameEn = "KSA Co", CountryCode = "SA" };
        db.Companies.Add(company);
        var employee = new Employee
        {
            TenantId = tenantId, EmployeeCode = $"E-{Guid.NewGuid():N}", FullName = "Employee", EnglishName = "Employee", Status = "Active",
            CompanyId = company.Id, JoiningDate = DateTime.UtcNow.AddYears(-3), UserAccountId = Guid.NewGuid(), Gender = "Female",
        };
        db.Employees.Add(employee);
        var type = new LeaveType { TenantId = tenantId, Code = "MARRIAGE", NameEn = "Marriage Leave", Category = "Marriage", IsPaid = true, IsActive = true };
        db.LeaveTypes.Add(type);
        db.LeavePolicies.Add(new LeavePolicy
        {
            TenantId = tenantId, Name = "Marriage", LeaveTypeId = type.Id, CountryCode = "SA", MinimumDaysPerRequest = 1m,
            WeekendsIncluded = true, PublicHolidaysIncluded = true, AppliesOnProbation = true, AccrualMethod = "Yearly", Status = "Active",
        });
        await db.SaveChangesAsync();
        await TestApprovalConfig.EnsureDefaultLeaveWorkflowAsync(db, tenantId);
        var start = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30);

        var act = () => new LeaveService(db, new ApprovalRouter(db)).SubmitRequestAsync(tenantId, new LeaveRequest
        {
            EmployeeId = employee.Id, EmployeeName = employee.FullName, LeaveTypeId = type.Id, StartDate = start, EndDate = start.AddDays(9),
            DayType = "Full", Reason = "Statutory",
        }, employee.UserAccountId);

        var refused = (await act.Should().ThrowAsync<LeaveRefusedException>()).Which;
        refused.Code.Should().Be(LeaveRefusalCodes.StatutoryLimit);
        refused.Args.Should().ContainKey("days").WhoseValue.Should().Be("5");
        refused.Message.Should().Contain("per event");
    }

    // ── What the controller returns ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheSubmitEndpoint_returns_code_args_and_the_unchanged_message_as_a_400()
    {
        var w = await LeaveBatch1World.CreateAsync();
        var day = w.NextWorkday();
        await w.Service.SubmitRequestAsync(w.TenantId, Req(w, day), w.EmpUserId);
        var controller = w.Leave(w.OwnScope(w.Emp), Self(w));

        var result = await controller.Submit(
            new SubmitLeaveRequestRequest(w.Emp.Id, w.LeaveType.Id, null, day, day, "Full", null, "needed", false, null), CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.StatusCode.Should().Be(400);
        var body = JsonSerializer.SerializeToElement(bad.Value);
        body.GetProperty("code").GetString().Should().Be(LeaveRefusalCodes.Overlap);
        body.GetProperty("message").GetString().Should().Be("Employee already has an approved or pending leave for the requested dates.");
        body.GetProperty("args").ValueKind.Should().Be(JsonValueKind.Object);
    }

    [Fact]
    public async Task TheSubmitEndpoint_carries_the_args_of_a_refusal_that_has_them()
    {
        var w = await LeaveBatch1World.CreateAsync();
        await AddPolicyAsync(w, p => p.NoticeRequiredDays = 400);
        var day = w.NextWorkday();
        var controller = w.Leave(w.OwnScope(w.Emp), Self(w));

        var result = await controller.Submit(
            new SubmitLeaveRequestRequest(w.Emp.Id, w.LeaveType.Id, null, day, day, "Full", null, "needed", false, null), CancellationToken.None);

        var body = JsonSerializer.SerializeToElement(result.Should().BeOfType<BadRequestObjectResult>().Subject.Value);
        body.GetProperty("code").GetString().Should().Be(LeaveRefusalCodes.NoticeRequired);
        body.GetProperty("args").GetProperty("days").GetString().Should().Be("400");
    }

    [Fact]
    public async Task TheSubmitEndpoint_names_a_missing_attachment_document_with_its_code()
    {
        var w = await LeaveBatch1World.CreateAsync();
        var day = w.NextWorkday();
        var controller = w.Leave(w.OwnScope(w.Emp), Self(w));

        var result = await controller.Submit(
            new SubmitLeaveRequestRequest(w.Emp.Id, w.LeaveType.Id, null, day, day, "Full", null, "needed", false, null, AttachmentDocumentId: Guid.NewGuid()),
            CancellationToken.None);

        var body = JsonSerializer.SerializeToElement(result.Should().BeOfType<BadRequestObjectResult>().Subject.Value);
        body.GetProperty("code").GetString().Should().Be(LeaveRefusalCodes.AttachmentNotFound);
        body.GetProperty("message").GetString().Should().Be("The attachment was not found among the employee's documents.");
    }
}
