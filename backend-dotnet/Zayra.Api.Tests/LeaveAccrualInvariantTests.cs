using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public class LeaveAccrualInvariantTests
{
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

        var service = new LeaveService(db, new ApprovalPolicyService(db));
        await service.AccrueMonthlyAsync(tenantId);
        await service.AccrueMonthlyAsync(tenantId);

        var balance = await db.EmployeeLeaveBalances.SingleAsync();
        balance.Accrued.Should().Be(2m);
        (await db.LeaveBalanceTransactions.CountAsync(x => x.TransactionType == "Accrual")).Should().Be(1);
    }
}
