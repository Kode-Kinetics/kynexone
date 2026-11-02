using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Compliance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Owner decision 2026-10-25 at REQUEST time: a special-leave policy an admin acknowledged below the Saudi minimum is applied as
/// configured (days, per-request cap, working-day counting), exactly as annual accrual and EOSB are. A policy that was never
/// acknowledged still has the statute grant the leave over its caps, as before.
/// </summary>
public class AcknowledgedSpecialLeaveRequestTests
{
    private static readonly DateOnly Base = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30);

    internal static string Acked(string code, string field, decimal configured, decimal statutory) => FloorAckStore.Serialize(
    [
        new StoredFloorAck(code, field, configured, statutory, "Labor Law", "Board approved a shorter policy", "HR", null, DateTime.UtcNow),
    ])!;

    private sealed record Fx(ZayraDbContext Db, Guid TenantId, Employee Employee, LeaveType Type, LeaveService Service);

    private static async Task<Fx> SeedAsync(string code, string name, string category, decimal days, decimal cap = 0m,
        bool calendar = true, string? acks = null)
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenantId = Guid.NewGuid();
        var company = new Company { TenantId = tenantId, LegalNameEn = "KSA Co", CountryCode = "SA" };
        db.Companies.Add(company);
        var employee = new Employee
        {
            TenantId = tenantId, EmployeeCode = $"E-{Guid.NewGuid():N}", FullName = "Employee", EnglishName = "Employee",
            Status = "Active", CompanyId = company.Id, JoiningDate = DateTime.UtcNow.AddYears(-3), UserAccountId = Guid.NewGuid(), Gender = "Female",
        };
        db.Employees.Add(employee);
        var type = new LeaveType { TenantId = tenantId, Code = code, NameEn = name, Category = category, IsPaid = true, IsActive = true };
        db.LeaveTypes.Add(type);
        db.LeavePolicies.Add(new LeavePolicy
        {
            TenantId = tenantId, Name = name, LeaveTypeId = type.Id, CountryCode = "SA", CompanyId = company.Id, AnnualEntitlementDays = days,
            MaximumDaysPerRequest = cap, MinimumDaysPerRequest = 1m, WeekendsIncluded = calendar, PublicHolidaysIncluded = calendar,
            AppliesOnProbation = true, AccrualMethod = "Yearly", Status = "Active", BelowMinimumAcksJson = acks,
        });
        await db.SaveChangesAsync();
        await TestApprovalConfig.EnsureDefaultLeaveWorkflowAsync(db, tenantId);
        return new Fx(db, tenantId, employee, type, new LeaveService(db, new ApprovalRouter(db)));
    }

    private static Task<LeaveRequest> Submit(Fx f, int calendarDays)
        => f.Service.SubmitRequestAsync(f.TenantId, new LeaveRequest
        {
            EmployeeId = f.Employee.Id, EmployeeName = f.Employee.FullName, LeaveTypeId = f.Type.Id,
            StartDate = Base, EndDate = Base.AddDays(calendarDays - 1), DayType = "Full", Reason = "Statutory",
        }, f.Employee.UserAccountId);

    [Fact]
    public async Task AcknowledgedSeventyDayMaternity_IsGrantedAtSeventy_AndEightyFourIsRefused()
    {
        var f = await SeedAsync("MAT", "Maternity Leave", "Parental", 70m, acks: Acked("leave_special_entitlement_below_minimum", "annualEntitlementDays", 70m, 84m));
        (await Submit(f, 70)).TotalDays.Should().Be(70m);
        var again = await SeedAsync("MAT", "Maternity Leave", "Parental", 70m, acks: Acked("leave_special_entitlement_below_minimum", "annualEntitlementDays", 70m, 84m));
        var tooLong = () => Submit(again, 84);
        (await tooLong.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("70");
    }

    [Fact]
    public async Task UnacknowledgedSeventyDayMaternity_IsStillGrantedTheStatutoryEightyFour()
    {
        var f = await SeedAsync("MAT", "Maternity Leave", "Parental", 70m, cap: 70m);
        (await Submit(f, 84)).TotalDays.Should().Be(84m);
    }

    [Fact]
    public async Task AcknowledgedSevenDayHajj_IsGrantedAtSeven_AndTenIsRefused()
    {
        var f = await SeedAsync("HAJJ", "Hajj Leave", "Religious", 7m, acks: Acked("leave_special_entitlement_below_minimum", "annualEntitlementDays", 7m, 10m));
        (await Submit(f, 7)).TotalDays.Should().Be(7m);
    }

    [Fact]
    public async Task AcknowledgedSevenDayHajj_RefusesTenDays()
    {
        var f = await SeedAsync("HAJJ", "Hajj Leave", "Religious", 7m, acks: Acked("leave_special_entitlement_below_minimum", "annualEntitlementDays", 7m, 10m));
        var tooLong = () => Submit(f, 10);
        (await tooLong.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("7");
    }

    [Fact]
    public async Task UnacknowledgedSevenDayHajj_IsStillGrantedTheStatutoryTen()
    {
        var f = await SeedAsync("HAJJ", "Hajj Leave", "Religious", 7m);
        (await Submit(f, 10)).TotalDays.Should().Be(10m);
    }

    [Fact]
    public async Task AcknowledgedCap_IsEnforcedAsConfigured_AUnacknowledgedCapIsNot()
    {
        var capped = await SeedAsync("MAT", "Maternity Leave", "Parental", 84m, cap: 70m, acks: Acked("leave_special_cap_below_minimum", "maximumDaysPerRequest", 70m, 84m));
        var refused = () => Submit(capped, 84);
        (await refused.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("at most 70");

        var legacy = await SeedAsync("MAT", "Maternity Leave", "Parental", 84m, cap: 70m);
        (await Submit(legacy, 84)).TotalDays.Should().Be(84m);
    }

    [Fact]
    public async Task AcknowledgedWorkingDayCounting_IsMeasuredInWorkingDays_Unacknowledged_IsStillTheCalendarStatute()
    {
        // 84 calendar days is 60+ working days: a 50-working-day policy acknowledged below the law refuses it, an unacknowledged one does not.
        var acked = await SeedAsync("MAT", "Maternity Leave", "Parental", 50m, calendar: false, acks: Acked("leave_special_working_days_below_minimum", "annualEntitlementDays", 50m, 84m));
        var refused = () => Submit(acked, 84);
        await refused.Should().ThrowAsync<InvalidOperationException>();

        var legacy = await SeedAsync("MAT", "Maternity Leave", "Parental", 50m, calendar: false);
        (await Submit(legacy, 84)).Should().NotBeNull();
    }

    [Fact]
    public async Task AnAcknowledgementForADifferentValue_DoesNotApply()
    {
        // Acknowledged at 60, later edited to 70 without a new acknowledgement: the statute governs again.
        var f = await SeedAsync("MAT", "Maternity Leave", "Parental", 70m, cap: 70m, acks: Acked("leave_special_entitlement_below_minimum", "annualEntitlementDays", 60m, 84m));
        (await Submit(f, 84)).TotalDays.Should().Be(84m);
    }
}
