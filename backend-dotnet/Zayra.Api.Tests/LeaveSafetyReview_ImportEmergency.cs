using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Controllers.Leave;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>Review follow-up: ImportEmergency. See <see cref="LeaveSafetyReviewTests"/>.</summary>
public sealed partial class LeaveSafetyReviewTests
{
    // ── 2. CSV import: the emergency flag ─────────────────────────────────────────────────────────

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

    private static async Task<(int Created, int Skipped)> ImportEmergencyRowAsync(LeaveBatch1World w, ClaimsPrincipal user)
    {
        var day = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(2).ToString("yyyy-MM-dd");
        var csv = "EmployeeCode,LeaveTypeCode,StartDate,EndDate,DayType,IsEmergency,Reason\n"
                  + $"{w.Emp.EmployeeCode},{w.LeaveType.Code},{day},{day},Full,true,needed\n";
        var result = await w.Leave(LeaveBatch1World.OrgScope(), user).Import(new ImportLeaveRequestsRequest(csv), CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result.Should().BeOfType<OkObjectResult>().Subject.Value);
        return (json.GetProperty("created").GetInt32(), json.GetProperty("skipped").GetInt32());
    }

    [Fact]
    public async Task Import_honours_the_emergency_flag_for_leave_administration_on_someone_elses_row()
    {
        var w = await LeaveBatch1World.CreateAsync();
        await AddNoticePolicyAsync(w, 14);

        var (created, _) = await ImportEmergencyRowAsync(w, Caller(w, "HR Officer", null, "leave.read", "leave.write"));

        created.Should().Be(1);
        (await w.Db.LeaveRequests.SingleAsync(r => r.EmployeeId == w.Emp.Id)).IsEmergency.Should().BeTrue();
    }

    [Fact]
    public async Task Import_ignores_the_emergency_flag_on_the_importers_own_row()
    {
        var w = await LeaveBatch1World.CreateAsync();
        await AddNoticePolicyAsync(w, 14);

        var (created, skipped) = await ImportEmergencyRowAsync(w, Caller(w, "HR Manager", w.Emp.Id, "leave.read", "leave.write"));

        (created, skipped).Should().Be((0, 1), "the flag is dropped on the caller's own employee, so the notice period applies");
    }

    [Fact]
    public async Task Import_ignores_the_emergency_flag_without_leave_write()
    {
        var w = await LeaveBatch1World.CreateAsync();
        await AddNoticePolicyAsync(w, 14);

        var (created, skipped) = await ImportEmergencyRowAsync(w, Caller(w, "HR Officer", null, "leave.read"));

        (created, skipped).Should().Be((0, 1));
    }
}
