using FluentAssertions;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers.Leave;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public class LeaveAccrualInvariantTests
{
    [Theory]
    [InlineData(2026, 4, 1, "2.00")]
    [InlineData(2026, 4, 16, "1.00")]
    [InlineData(2026, 4, 30, "0.07")]
    [InlineData(2026, 7, 16, "1.03")]
    [InlineData(2024, 2, 15, "1.03")]
    [InlineData(2025, 2, 15, "1.00")]
    public async Task MonthlyAccrual_ProrationUsesInclusiveCalendarDays(int year, int month, int joiningDay, string expected)
    {
        await using var db = CreateDb();
        var (tenantId, _, _) = await SeedAsync(db, Utc(year, month, joiningDay), prorate: true);
        await new LeaveService(db, new ApprovalRouter(db)).AccrueMonthlyAsync(tenantId, MonthEnd(year, month));

        var balance = await db.EmployeeLeaveBalances.SingleAsync();
        balance.Accrued.Should().Be(decimal.Parse(expected, CultureInfo.InvariantCulture));
        balance.Entitled.Should().Be(0m, "monthly accrual must not also grant the full annual entitlement");
        var posted = await db.LeaveBalanceTransactions.SingleAsync();
        posted.Amount.Should().Be(balance.Accrued);
        posted.BalanceBefore.Should().Be(0m);
        posted.BalanceAfter.Should().Be(balance.Accrued);
        if (joiningDay > 1) posted.Reason.Should().Contain("calendar days of service");
    }

    [Theory]
    [InlineData(2026, 5, 1)]
    [InlineData(2027, 1, 1)]
    [InlineData(2026, 4, 20)]
    public async Task MonthlyAccrual_DoesNotGrantBeforeJoining(int year, int month, int day)
    {
        await using var db = CreateDb();
        var (tenantId, _, _) = await SeedAsync(db, Utc(year, month, day), prorate: false);
        await new LeaveService(db, new ApprovalRouter(db)).AccrueMonthlyAsync(tenantId, Utc(2026, 4, 15));

        (await db.EmployeeLeaveBalances.CountAsync()).Should().Be(0);
        (await db.LeaveBalanceTransactions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task MonthlyAccrual_MidYearHireReceivesOnlyServiceMonths_AndReplaysDoNotChangePostedAmounts()
    {
        await using var db = CreateDb();
        var (tenantId, _, policy) = await SeedAsync(db, Utc(2026, 7, 16), prorate: true);
        var service = new LeaveService(db, new ApprovalRouter(db));
        for (var month = 1; month <= 12; month++)
        {
            await service.AccrueMonthlyAsync(tenantId, MonthEnd(2026, month));
            await service.AccrueMonthlyAsync(tenantId, MonthEnd(2026, month));
        }

        (await db.EmployeeLeaveBalances.SingleAsync()).Accrued.Should().Be(11.03m, "July earns 16/31 of 2 days, then five complete months earn 10 days");
        (await db.LeaveBalanceTransactions.CountAsync()).Should().Be(6);
        policy.ProratePartialMonths = false;
        policy.AnnualEntitlementDays = 36;
        await db.SaveChangesAsync();
        await service.AccrueMonthlyAsync(tenantId, MonthEnd(2026, 7));
        (await db.EmployeeLeaveBalances.SingleAsync()).Accrued.Should().Be(11.03m, "a changed policy must not rewrite an already posted month");
        (await db.LeaveBalanceTransactions.CountAsync()).Should().Be(6);
    }

    [Fact]
    public async Task MonthlyAccrual_ExistingPolicyRetainsFullJoiningMonthWhenProrationIsOff()
    {
        await using var db = CreateDb();
        var (tenantId, _, policy) = await SeedAsync(db, Utc(2026, 4, 16), prorate: false);
        new LeavePolicy().ProratePartialMonths.Should().BeFalse();
        await new LeaveService(db, new ApprovalRouter(db)).AccrueMonthlyAsync(tenantId, MonthEnd(2026, 4));

        policy.ProratePartialMonths.Should().BeFalse();
        (await db.EmployeeLeaveBalances.SingleAsync()).Accrued.Should().Be(2m);
    }

    [Theory]
    [InlineData("Active", true, 15, "1.00")]
    [InlineData("Offboarded", true, 15, "1.00")]
    [InlineData("Offboarded", false, 15, "2.00")]
    [InlineData("Offboarded", true, 30, "2.00")]
    public async Task MonthlyAccrual_ServesNoticeThroughLastWorkingDay_AndStopsAfterwards(string status, bool prorate, int leavingDay, string expected)
    {
        await using var db = CreateDb();
        var (tenantId, employee, _) = await SeedAsync(db, Utc(2025, 1, 1), prorate, status: status);
        db.EmployeeOffboardings.Add(new EmployeeOffboarding
        {
            TenantId = tenantId, EmployeeId = employee.Id, LastWorkingDay = new DateOnly(2026, 4, leavingDay), Status = "InProgress"
        });
        await db.SaveChangesAsync();
        var service = new LeaveService(db, new ApprovalRouter(db));
        await service.AccrueMonthlyAsync(tenantId, MonthEnd(2026, 4));
        await service.AccrueMonthlyAsync(tenantId, MonthEnd(2026, 5));

        (await db.EmployeeLeaveBalances.SingleAsync()).Accrued.Should().Be(decimal.Parse(expected, CultureInfo.InvariantCulture));
        (await db.LeaveBalanceTransactions.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task MonthlyAccrual_JoiningAndLeavingInSameMonthUsesOnlyTheirOverlap()
    {
        await using var db = CreateDb();
        var (tenantId, employee, _) = await SeedAsync(db, Utc(2026, 4, 11), prorate: true, status: "Offboarded");
        db.EmployeeOffboardings.Add(new EmployeeOffboarding
        {
            TenantId = tenantId, EmployeeId = employee.Id, LastWorkingDay = new DateOnly(2026, 4, 20), Status = "InProgress"
        });
        await db.SaveChangesAsync();
        await new LeaveService(db, new ApprovalRouter(db)).AccrueMonthlyAsync(tenantId, MonthEnd(2026, 4));

        (await db.EmployeeLeaveBalances.SingleAsync()).Accrued.Should().Be(0.67m);
        (await db.LeaveBalanceTransactions.SingleAsync()).Reason.Should().Contain("10/30");
    }

    [Fact]
    public async Task MonthlyAccrual_CancelledSeparationDoesNotReduceAnActiveEmployeesCredit()
    {
        await using var db = CreateDb();
        var (tenantId, employee, _) = await SeedAsync(db, Utc(2025, 1, 1), prorate: true);
        db.EmployeeOffboardings.Add(new EmployeeOffboarding
        {
            TenantId = tenantId, EmployeeId = employee.Id, LastWorkingDay = new DateOnly(2026, 4, 15), Status = "Cancelled"
        });
        await db.SaveChangesAsync();
        await new LeaveService(db, new ApprovalRouter(db)).AccrueMonthlyAsync(tenantId, MonthEnd(2026, 4));

        (await db.EmployeeLeaveBalances.SingleAsync()).Accrued.Should().Be(2m);
    }

    [Theory]
    [InlineData(2026, 4, 16, 12, "0.88")]
    [InlineData(2019, 4, 1, 21, "1.25")]
    [InlineData(2019, 4, 1, 36, "1.50")]
    public async Task MonthlyAccrual_ResolvesKsaAnnualFloorAndMoreGenerousPolicyBeforeProration(int year, int month, int day, int annualDays, string expected)
    {
        await using var db = CreateDb();
        var veteran = year < 2026;
        var (tenantId, employee, _) = await SeedAsync(db, Utc(year, month, day), prorate: true,
            annualDays: annualDays, country: "SA", status: veteran ? "Offboarded" : "Active");
        if (veteran)
        {
            db.EmployeeOffboardings.Add(new EmployeeOffboarding
            {
                TenantId = tenantId, EmployeeId = employee.Id, LastWorkingDay = new DateOnly(2026, 4, 15), Status = "InProgress"
            });
            await db.SaveChangesAsync();
        }
        await new LeaveService(db, new ApprovalRouter(db)).AccrueMonthlyAsync(tenantId, MonthEnd(2026, 4));

        (await db.EmployeeLeaveBalances.SingleAsync()).Accrued.Should().Be(decimal.Parse(expected, CultureInfo.InvariantCulture));
        var reason = (await db.LeaveBalanceTransactions.SingleAsync()).Reason;
        reason.Should().Contain("prorated");
        if (annualDays < 30) reason.Should().Contain("statutory floor");
    }

    [Fact]
    public async Task PolicyMaintenance_PersistsProration_LeavesItUnchangedWhenOmitted_AndAuditsChanges()
    {
        await using var db = CreateDb();
        var (tenantId, _, policy) = await SeedAsync(db, Utc(2025, 1, 1), prorate: false);
        var controller = new LeavePoliciesController(db, new LeaveService(db, new ApprovalRouter(db)))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("tenant_id", tenantId.ToString()), new Claim(ClaimTypes.Name, "Policy Owner") }, "Test"))
            } }
        };
        var create = JsonSerializer.Deserialize<CreateLeavePolicyRequest>(JsonSerializer.Serialize(new
        {
            Name = "Prorated monthly", policy.LeaveTypeId, AnnualEntitlementDays = 24, AccrualMethod = "Monthly", Status = "Active", ProratePartialMonths = true
        }))!;
        var created = (CreatedResult)await controller.Create(create, default);
        var saved = (LeavePolicy)created.Value!;
        saved.ProratePartialMonths.Should().BeTrue();
        await controller.Update(saved.Id, JsonSerializer.Deserialize<UpdateLeavePolicyRequest>("{\"Name\":\"Renamed policy\"}")!, default);
        saved.ProratePartialMonths.Should().BeTrue("older clients omitting the field must preserve the configured value");
        await controller.Update(saved.Id, JsonSerializer.Deserialize<UpdateLeavePolicyRequest>("{\"ProratePartialMonths\":false}")!, default);
        saved.ProratePartialMonths.Should().BeFalse();
        (await db.LeaveAuditLogs.CountAsync(a => a.EntityId == saved.Id.ToString() && a.Action == "PartialMonthProrationChanged")).Should().Be(2);
    }

    private static ZayraDbContext CreateDb() => new(new DbContextOptionsBuilder<ZayraDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static DateTime Utc(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);
    private static DateTime MonthEnd(int year, int month) => Utc(year, month, DateTime.DaysInMonth(year, month));

    private static async Task<(Guid TenantId, Employee Employee, LeavePolicy Policy)> SeedAsync(
        ZayraDbContext db, DateTime joiningDate, bool prorate, decimal annualDays = 24m, string country = "AE", string status = "Active")
    {
        var tenantId = Guid.NewGuid();
        var company = new Company { TenantId = tenantId, LegalNameEn = "Proration fixture", CountryCode = country };
        var type = new LeaveType { TenantId = tenantId, Code = "AL", NameEn = "Annual leave", Category = "Annual", IsActive = true };
        var employee = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "PRORATION-1", FullName = "Proration Employee",
            Status = status, JoiningDate = joiningDate
        };
        var policy = new LeavePolicy
        {
            TenantId = tenantId, LeaveTypeId = type.Id, Name = "Monthly annual leave", Status = "Active", AccrualMethod = "Monthly",
            AnnualEntitlementDays = annualDays, ProratePartialMonths = prorate
        };
        db.AddRange(company, type, employee, policy);
        await db.SaveChangesAsync();
        return (tenantId, employee, policy);
    }

    [Fact]
    public async Task MonthlyAccrual_MoreSpecificYearlyPolicySuppressesMonthlyDefaultForEligibleEmployeeOnly()
    {
        await using var db = CreateDb();
        var (tenantId, employee, monthly) = await SeedAsync(db, Utc(2025, 1, 1), prorate: true);
        employee.GradeId = Guid.NewGuid();
        var yearly = new LeavePolicy
        {
            TenantId = tenantId, LeaveTypeId = monthly.LeaveTypeId, Name = "Grade yearly override",
            Status = "Active", AccrualMethod = "Yearly", AnnualEntitlementDays = 30
        };
        var otherEmployee = new Employee
        {
            TenantId = tenantId, CompanyId = employee.CompanyId, EmployeeCode = "PRORATION-2",
            FullName = "Monthly Employee", Status = "Active", JoiningDate = Utc(2025, 1, 1)
        };
        db.AddRange(yearly, otherEmployee, new LeavePolicyEligibility
        {
            TenantId = tenantId, LeavePolicyId = yearly.Id, GradeId = employee.GradeId, IsActive = true
        });
        await db.SaveChangesAsync();

        await new LeaveService(db, new ApprovalRouter(db)).AccrueMonthlyAsync(tenantId, MonthEnd(2026, 4));

        (await db.EmployeeLeaveBalances.AnyAsync(b => b.EmployeeId == employee.Id)).Should().BeFalse();
        (await db.LeaveBalanceTransactions.AnyAsync(t => t.EmployeeId == employee.Id)).Should().BeFalse();
        var credited = await db.EmployeeLeaveBalances.SingleAsync();
        credited.EmployeeId.Should().Be(otherEmployee.Id);
        credited.Accrued.Should().Be(2m);
    }

    [Fact]
    public async Task MonthlyAccrual_UsesMostSpecificOverlappingPolicy_AndIsReplaySafe()
    {
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenantId = Guid.NewGuid();
        var company = new Company { TenantId = tenantId, LegalNameEn = "Acme", CountryCode = "SA" };
        var type = new LeaveType { TenantId = tenantId, Code = "AL", NameEn = "Annual", IsActive = true };
        db.AddRange(company, type);
        await db.SaveChangesAsync();
        var employee = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "ACC-1", FullName = "Accrual Employee",
            Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-2)
        };
        db.Employees.Add(employee);
        db.LeavePolicies.AddRange(
            new LeavePolicy { TenantId = tenantId, LeaveTypeId = type.Id, Name = "Default", Status = "Active", AccrualMethod = "Monthly", AnnualEntitlementDays = 12, UpdatedAtUtc = DateTime.UtcNow.AddDays(-1) },
            new LeavePolicy { TenantId = tenantId, LeaveTypeId = type.Id, CompanyId = company.Id, Name = "Company override", Status = "Active", AccrualMethod = "Monthly", AnnualEntitlementDays = 24, UpdatedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var service = new LeaveService(db, new ApprovalRouter(db));
        await service.AccrueMonthlyAsync(tenantId);
        await service.AccrueMonthlyAsync(tenantId);

        var balance = await db.EmployeeLeaveBalances.SingleAsync();
        balance.Accrued.Should().Be(2m);
        (await db.LeaveBalanceTransactions.CountAsync(x => x.TransactionType == "Accrual")).Should().Be(1);
    }
}
