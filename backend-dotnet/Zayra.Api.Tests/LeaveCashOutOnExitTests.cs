using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;
using Xunit;

namespace Zayra.Api.Tests;

/// <summary>
/// Untaken annual leave is paid out when employment ends (Art. 111), whether or not the tenant's leave policy
/// allows encashment, unless the company turns the setting off.
/// </summary>
public class LeaveCashOutOnExitTests
{
    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<(Guid Tenant, int Employee)> SeedAsync(ZayraDbContext db, bool? policyEncashmentAllowed)
    {
        var tenant = Guid.NewGuid();
        var type = new LeaveType { TenantId = tenant, Code = "ANNUAL", NameEn = "Annual Leave", Category = "Annual", IsActive = true };
        var other = new LeaveType { TenantId = tenant, Code = "STUDY", NameEn = "Study Leave", Category = "Other", IsActive = true };
        db.LeaveTypes.AddRange(type, other);
        var emp = new Employee { TenantId = tenant, EmployeeCode = "E1", FullName = "Leaver", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-4) };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();
        if (policyEncashmentAllowed is bool allowed)
            db.LeavePolicies.Add(new LeavePolicy { TenantId = tenant, CompanyId = null, LeaveTypeId = type.Id, Name = "Annual", Status = "Active", EncashmentAllowed = allowed });
        // 12 untaken annual days, and 5 untaken days of a non-annual type with no policy.
        db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance { TenantId = tenant, EmployeeId = emp.Id, LeaveTypeId = type.Id, LeaveTypeName = "Annual Leave", Year = 2026, Entitled = 30m, Used = 18m });
        db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance { TenantId = tenant, EmployeeId = emp.Id, LeaveTypeId = other.Id, LeaveTypeName = "Study Leave", Year = 2026, Entitled = 5m });
        await db.SaveChangesAsync();
        return (tenant, emp.Id);
    }

    [Theory]
    [InlineData(false)]   // the policy says no encashment
    [InlineData(null)]    // no policy at all
    public async Task SettingOn_PaysAnnualLeave_EvenWhenThePolicyDoesNot(bool? policy)
    {
        await using var db = CreateDb();
        var (tenant, emp) = await SeedAsync(db, policy);

        var withSetting = await LeaveEncashmentCalculator.ComputeAsync(
            db, tenant, emp, null, new DateOnly(2026, 9, 30), 13_500m, default, statutoryAnnualLeaveCashOut: true);

        // 12 days x 13,500 / 30 = 5,400. The non-annual type with no policy is still not paid.
        withSetting.TotalDays.Should().Be(12m);
        withSetting.TotalAmount.Should().Be(5_400m);
        withSetting.Lines.Should().ContainSingle().Which.LeaveTypeName.Should().Be("Annual Leave");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task SettingOff_KeepsThePolicyAnswer_NothingPaid(bool? policy)
    {
        await using var db = CreateDb();
        var (tenant, emp) = await SeedAsync(db, policy);

        var result = await LeaveEncashmentCalculator.ComputeAsync(
            db, tenant, emp, null, new DateOnly(2026, 9, 30), 13_500m, default, statutoryAnnualLeaveCashOut: false);

        result.TotalAmount.Should().Be(0m, "before the setting existed this paid 0; turning it off restores that");
    }

    [Fact]
    public async Task ChosenBasis_ChangesTheDayRate()
    {
        await using var db = CreateDb();
        var (tenant, emp) = await SeedAsync(db, false);

        // The caller passes the wage the company chose to cash out on: basic 10,000 instead of gross 13,500.
        var onBasic = await LeaveEncashmentCalculator.ComputeAsync(
            db, tenant, emp, null, new DateOnly(2026, 9, 30), 10_000m, default,
            statutoryAnnualLeaveCashOut: true, basisLabel: "basic/30");

        onBasic.TotalAmount.Should().Be(4_000m, "12 x 10,000 / 30");
        onBasic.Lines.Single().Basis.Should().Be("basic/30");
    }

    [Fact]
    public async Task APolicyThatAllowsEncashment_KeepsItsOwnCap()
    {
        await using var db = CreateDb();
        var (tenant, emp) = await SeedAsync(db, true);
        var policy = await db.LeavePolicies.SingleAsync();
        policy.EncashmentMaxDays = 10m;
        await db.SaveChangesAsync();

        var result = await LeaveEncashmentCalculator.ComputeAsync(
            db, tenant, emp, null, new DateOnly(2026, 9, 30), 13_500m, default, statutoryAnnualLeaveCashOut: true);

        result.TotalDays.Should().Be(10m, "a company that allows encashment up to 10 days chose that cap");
    }

    [Fact]
    public async Task NoAnnualTypeAmongTheBalances_SaysSo_InsteadOfUnderpayingInSilence()
    {
        await using var db = CreateDb();
        var tenant = Guid.NewGuid();
        var vacation = new LeaveType { TenantId = tenant, Code = "VAC", NameEn = "Vacation", Category = "Other", IsActive = true };
        db.LeaveTypes.Add(vacation);
        var emp = new Employee { TenantId = tenant, EmployeeCode = "E2", FullName = "Leaver", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-4) };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();
        db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance { TenantId = tenant, EmployeeId = emp.Id, LeaveTypeId = vacation.Id, LeaveTypeName = "Vacation", Year = 2026, Entitled = 20m });
        await db.SaveChangesAsync();

        var result = await LeaveEncashmentCalculator.ComputeAsync(
            db, tenant, emp.Id, null, new DateOnly(2026, 9, 30), 13_500m, default, statutoryAnnualLeaveCashOut: true);

        result.TotalAmount.Should().Be(0m);
        result.Warnings.Should().Contain(w => w.Contains("annual-leave type"));
    }

    [Fact]
    public async Task APolicyCap_StillLimitsTheAnnualPayout_WhenThePolicyAllowsEncashment()
    {
        await using var db = CreateDb();
        var (tenant, emp) = await SeedAsync(db, true);
        var policy = await db.LeavePolicies.SingleAsync();
        policy.EncashmentMaxDays = 5m;
        await db.SaveChangesAsync();

        var result = await LeaveEncashmentCalculator.ComputeAsync(
            db, tenant, emp, null, new DateOnly(2026, 9, 30), 13_500m, default, statutoryAnnualLeaveCashOut: true);

        result.Lines.Should().ContainSingle(l => l.LeaveTypeName == "Annual Leave").Which.EncashableDays.Should().Be(5m,
            "a company's own encashment cap on its policy still limits the payout; the setting only lifts the no-policy / not-allowed case");
    }
}
