using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Leave;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Leave;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Leave access rules decided for the release: only HR and leave administrators may mark a request as an emergency
/// (which waives the notice period), and a leave type is offered and accepted only to employees it is meant for
/// (maternity / paternity by gender, and a type whose every policy is restricted to another gender). In memory, no web host.
/// </summary>
public sealed class LeaveEmergencyAndEligibilityTests
{
    // ── Harness ───────────────────────────────────────────────────────────────────────────────────

    private static ClaimsPrincipal Caller(LeaveBatch1World w, string role, int? employeeId, params string[] permissions)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", w.TenantId.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "Test User"), new(ClaimTypes.Role, role),
        };
        if (employeeId is int id) claims.Add(new Claim("employee_id", id.ToString()));
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static ClaimsPrincipal EmployeeCaller(LeaveBatch1World w) => Caller(w, "Employee", w.Emp.Id, "ess.read", "ess.write");
    private static ClaimsPrincipal HrOfficerCaller(LeaveBatch1World w) => Caller(w, "HR Officer", null, "leave.read", "leave.write");
    private static ClaimsPrincipal HrManagerOwnCaller(LeaveBatch1World w) => Caller(w, "HR Manager", w.Emp.Id, "leave.read", "leave.write");

    private static async Task AddNoticePolicyAsync(LeaveBatch1World w, int noticeDays)
    {
        w.Db.LeavePolicies.Add(new LeavePolicy
        {
            TenantId = w.TenantId, Name = "Annual", LeaveTypeId = w.LeaveType.Id, AnnualEntitlementDays = 30m, MinimumDaysPerRequest = 1m,
            WeekendsIncluded = true, PublicHolidaysIncluded = true, AppliesOnProbation = true, AccrualMethod = "Yearly", Status = "Active",
            NoticeRequiredDays = noticeDays,
        });
        await w.Db.SaveChangesAsync();
    }

    /// <summary>A start date well inside a 14-day notice period (the day after tomorrow).</summary>
    private static DateOnly ShortNoticeDay() => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(2);

    private static Task<IActionResult> SubmitAsync(LeaveBatch1World w, ClaimsPrincipal user, LeaveType type, bool emergency, DateOnly day)
    {
        var scope = user.IsInRole("Employee") ? w.OwnScope(w.Emp) : LeaveBatch1World.OrgScope();
        return w.Leave(scope, user).Submit(
            new SubmitLeaveRequestRequest(w.Emp.Id, type.Id, null, day, day, "Full", null, "needed", emergency, null), CancellationToken.None);
    }

    private static LeaveTypesController Types(LeaveBatch1World w, ClaimsPrincipal user) => new(w.Db, w.Service)
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } },
    };

    private static async Task<List<LeaveType>> ListTypesAsync(LeaveBatch1World w, ClaimsPrincipal user)
    {
        var result = await Types(w, user).List(null, null, null, null, CancellationToken.None);
        return result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeAssignableTo<IEnumerable<LeaveType>>().Subject.ToList();
    }

    private static async Task<LeaveType> AddTypeAsync(LeaveBatch1World w, string code, string name, string category)
    {
        var type = new LeaveType { TenantId = w.TenantId, Code = code, NameEn = name, Category = category, IsActive = true, IsPaid = true };
        w.Db.LeaveTypes.Add(type);
        foreach (var year in new[] { DateTime.UtcNow.Year, DateTime.UtcNow.Year + 1 })
            w.Db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
            {
                TenantId = w.TenantId, EmployeeId = w.Emp.Id, EmployeeName = w.Emp.FullName, LeaveTypeId = type.Id, LeaveTypeName = type.NameEn,
                Year = year, Entitled = 60,
            });
        await w.Db.SaveChangesAsync();
        return type;
    }

    private static async Task SetGenderAsync(LeaveBatch1World w, string gender)
    {
        var tracked = await w.Db.Employees.SingleAsync(e => e.Id == w.Emp.Id);
        tracked.Gender = gender;
        await w.Db.SaveChangesAsync();
    }

    private static async Task MakeCompanyKsaAsync(LeaveBatch1World w, string countryCode)
    {
        w.Db.Companies.Add(new Company { Id = w.CompanyId, TenantId = w.TenantId, LegalNameEn = "Co", CountryCode = countryCode });
        await w.Db.SaveChangesAsync();
    }

    private static string RefusalOf(IActionResult result)
    {
        var body = result.Should().BeOfType<BadRequestObjectResult>().Subject.Value!;
        return body.GetType().GetProperty("message")!.GetValue(body)!.ToString()!;
    }

    // ── (a) Emergency flag ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_employees_own_request_marked_emergency_still_needs_the_notice_period()
    {
        var w = await LeaveBatch1World.CreateAsync();
        await AddNoticePolicyAsync(w, 14);

        var result = await SubmitAsync(w, EmployeeCaller(w), w.LeaveType, emergency: true, ShortNoticeDay());

        RefusalOf(result).Should().Contain("advance notice");
        (await w.Db.LeaveRequests.AnyAsync(r => r.EmployeeId == w.Emp.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task Hr_filing_for_an_employee_as_an_emergency_skips_the_notice_period()
    {
        var w = await LeaveBatch1World.CreateAsync();
        await AddNoticePolicyAsync(w, 14);

        var result = await SubmitAsync(w, HrOfficerCaller(w), w.LeaveType, emergency: true, ShortNoticeDay());

        var created = result.Should().BeOfType<CreatedResult>().Subject.Value.Should().BeOfType<LeaveRequest>().Subject;
        created.IsEmergency.Should().BeTrue();
    }

    [Fact]
    public async Task Hr_filing_for_an_employee_without_the_flag_still_needs_the_notice_period()
    {
        var w = await LeaveBatch1World.CreateAsync();
        await AddNoticePolicyAsync(w, 14);

        var result = await SubmitAsync(w, HrOfficerCaller(w), w.LeaveType, emergency: false, ShortNoticeDay());

        RefusalOf(result).Should().Contain("advance notice");
    }

    [Fact]
    public async Task A_leave_administrators_emergency_flag_on_their_own_request_is_ignored_and_the_notice_period_applies()
    {
        // Separation of duties: the subject never decides about their own request, so holding leave.write does not
        // let HR waive the notice period for their own leave.
        var w = await LeaveBatch1World.CreateAsync();
        await AddNoticePolicyAsync(w, 14);

        var result = await SubmitAsync(w, HrManagerOwnCaller(w), w.LeaveType, emergency: true, ShortNoticeDay());

        RefusalOf(result).Should().Contain("advance notice");
        (await w.Db.LeaveRequests.AnyAsync(r => r.EmployeeId == w.Emp.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task A_leave_administrators_own_request_with_notice_is_stored_as_not_an_emergency()
    {
        var w = await LeaveBatch1World.CreateAsync();
        await AddNoticePolicyAsync(w, 1);
        var day = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(20);
        while (day.DayOfWeek is DayOfWeek.Friday or DayOfWeek.Saturday) day = day.AddDays(1);

        var result = await SubmitAsync(w, HrManagerOwnCaller(w), w.LeaveType, emergency: true, day);

        result.Should().BeOfType<CreatedResult>().Subject.Value.Should().BeOfType<LeaveRequest>().Subject.IsEmergency.Should().BeFalse();
    }

    [Fact]
    public async Task An_employees_own_request_marked_emergency_is_stored_as_not_an_emergency_when_notice_is_given()
    {
        var w = await LeaveBatch1World.CreateAsync();
        await AddNoticePolicyAsync(w, 1);
        var day = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(20);
        while (day.DayOfWeek is DayOfWeek.Friday or DayOfWeek.Saturday) day = day.AddDays(1);

        var result = await SubmitAsync(w, EmployeeCaller(w), w.LeaveType, emergency: true, day);

        result.Should().BeOfType<CreatedResult>().Subject.Value.Should().BeOfType<LeaveRequest>().Subject.IsEmergency.Should().BeFalse();
    }

    // ── (b) Self-service submit response ──────────────────────────────────────────────────────────

    [Fact]
    public void The_self_service_leave_submit_response_goes_through_the_leave_file_key_rule()
    {
        // The reader rule hands the attachment key back only to the leave's own employee and HR; the ESS caller is
        // always the owner, so behaviour is unchanged. This pins the response to the one shared exposure rule.
        var source = File.ReadAllText(Path.Combine(ResolveApiRoot(), "Controllers", "EmployeeSelfServiceController.cs"));
        var line = source.Split('\n').Single(l => l.Contains("Created($\"/api/ess/leave/request/{leave.Id}\""));
        line.Should().Contain("LeaveFileKeyExposure.ForReader(");
    }

    private static string ResolveApiRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir?.Parent is not null; i++)
        {
            dir = dir.Parent;
            var candidate = Path.Combine(dir.FullName, "Zayra.Api");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new InvalidOperationException($"Could not locate the Zayra.Api source root from {AppContext.BaseDirectory}.");
    }

    // ── (c) Leave types offered and accepted by eligibility ───────────────────────────────────────

    [Theory]
    [InlineData("MALE", false, true)]
    [InlineData("Male", false, true)]
    [InlineData("FEMALE", true, false)]
    [InlineData("F", true, false)]
    [InlineData("", true, true)]
    public async Task The_self_service_type_list_offers_maternity_and_paternity_by_gender(string gender, bool maternityListed, bool paternityListed)
    {
        var w = await LeaveBatch1World.CreateAsync();
        await AddTypeAsync(w, "MATERNITY", "Maternity Leave", "Maternity");
        await AddTypeAsync(w, "PATERNITY", "Paternity Leave", "Paternity");
        await SetGenderAsync(w, gender);

        var codes = (await ListTypesAsync(w, EmployeeCaller(w))).Select(t => t.Code).ToList();

        codes.Should().Contain("AL");
        codes.Contains("MATERNITY").Should().Be(maternityListed);
        codes.Contains("PATERNITY").Should().Be(paternityListed);
    }

    [Fact]
    public async Task An_administrator_listing_leave_types_still_sees_every_type()
    {
        var w = await LeaveBatch1World.CreateAsync();
        await AddTypeAsync(w, "MATERNITY", "Maternity Leave", "Maternity");
        await AddTypeAsync(w, "PATERNITY", "Paternity Leave", "Paternity");
        await SetGenderAsync(w, "MALE");

        var codes = (await ListTypesAsync(w, Caller(w, "HR Manager", w.Emp.Id, "leave.read", "leave.write"))).Select(t => t.Code).ToList();

        codes.Should().Contain(new[] { "AL", "MATERNITY", "PATERNITY" });
    }

    [Fact]
    public async Task A_type_whose_every_policy_is_restricted_to_another_gender_is_not_listed_but_a_mixed_one_is()
    {
        var w = await LeaveBatch1World.CreateAsync();
        var femaleOnly = await AddTypeAsync(w, "FONLY", "Women's Welfare Leave", "Special");
        var mixed = await AddTypeAsync(w, "MIXED", "Mixed Leave", "Special");
        var noPolicy = await AddTypeAsync(w, "NOPOL", "No Policy Leave", "Special");
        LeavePolicy Policy(LeaveType t, string gender) => new()
        {
            TenantId = w.TenantId, Name = $"{t.Code}-{gender}", LeaveTypeId = t.Id, Gender = gender, Status = "Active", AnnualEntitlementDays = 10m,
        };
        w.Db.LeavePolicies.AddRange(Policy(femaleOnly, "Female"), Policy(mixed, "Female"), Policy(mixed, ""));
        await w.Db.SaveChangesAsync();
        await SetGenderAsync(w, "MALE");

        var codes = (await ListTypesAsync(w, EmployeeCaller(w))).Select(t => t.Code).ToList();

        codes.Should().NotContain("FONLY");
        codes.Should().Contain(new[] { "MIXED", "NOPOL" });
        await SetGenderAsync(w, "FEMALE");
        (await ListTypesAsync(w, EmployeeCaller(w))).Select(t => t.Code).Should().Contain("FONLY");
    }

    [Theory]
    [InlineData("SA")]
    [InlineData("AE")]
    public async Task A_male_employees_maternity_request_is_refused_on_a_ksa_and_on_a_non_ksa_entity(string country)
    {
        var w = await LeaveBatch1World.CreateAsync();
        await MakeCompanyKsaAsync(w, country);
        var maternity = await AddTypeAsync(w, "MATERNITY", "Maternity Leave", "Maternity");
        await SetGenderAsync(w, "MALE");

        var day = w.NextWorkday();
        var act = () => w.Service.SubmitRequestAsync(w.TenantId, new LeaveRequest
        {
            TenantId = w.TenantId, EmployeeId = w.Emp.Id, LeaveTypeId = maternity.Id, StartDate = day, EndDate = day, DayType = "Full",
        }, w.EmpUserId);

        var refused = (await act.Should().ThrowAsync<LeaveRefusedException>()).Which;
        refused.Code.Should().Be(LeaveRefusalCodes.NotEligible);
        refused.Message.Should().Be("Maternity Leave is not available to you.");
        refused.Args["leaveType"].Should().Be("Maternity Leave");
        (await w.Db.LeaveRequests.AnyAsync(r => r.LeaveTypeId == maternity.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task A_male_employees_request_is_refused_for_a_type_every_policy_of_which_is_for_women()
    {
        var w = await LeaveBatch1World.CreateAsync();
        var type = await AddTypeAsync(w, "FONLY", "Women's Welfare Leave", "Special");
        w.Db.LeavePolicies.Add(new LeavePolicy { TenantId = w.TenantId, Name = "F", LeaveTypeId = type.Id, Gender = "Female", Status = "Active", AnnualEntitlementDays = 10m });
        await w.Db.SaveChangesAsync();
        await SetGenderAsync(w, "MALE");

        var day = w.NextWorkday();
        var act = () => w.Service.SubmitRequestAsync(w.TenantId, new LeaveRequest
        {
            TenantId = w.TenantId, EmployeeId = w.Emp.Id, LeaveTypeId = type.Id, StartDate = day, EndDate = day, DayType = "Full",
        }, w.EmpUserId);

        (await act.Should().ThrowAsync<LeaveRefusedException>()).Which.Code.Should().Be(LeaveRefusalCodes.NotEligible);
    }

    [Fact]
    public async Task Hr_filing_a_maternity_request_for_a_male_employee_is_refused_too()
    {
        var w = await LeaveBatch1World.CreateAsync();
        var maternity = await AddTypeAsync(w, "MATERNITY", "Maternity Leave", "Maternity");
        await SetGenderAsync(w, "MALE");

        var result = await SubmitAsync(w, HrOfficerCaller(w), maternity, emergency: false, w.NextWorkday());

        RefusalOf(result).Should().Be("Maternity Leave is not available to you.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("FEMALE")]
    public async Task An_employee_of_unknown_or_matching_gender_can_submit_maternity(string gender)
    {
        var w = await LeaveBatch1World.CreateAsync();
        var maternity = await AddTypeAsync(w, "MATERNITY", "Maternity Leave", "Maternity");
        await SetGenderAsync(w, gender);
        var day = w.NextWorkday();

        var submitted = await w.Service.SubmitRequestAsync(w.TenantId, new LeaveRequest
        {
            TenantId = w.TenantId, EmployeeId = w.Emp.Id, LeaveTypeId = maternity.Id, StartDate = day, EndDate = day, DayType = "Full",
        }, w.EmpUserId);

        submitted.Status.Should().StartWith("Pending");
    }
}
