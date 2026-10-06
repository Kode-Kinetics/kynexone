using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Two simultaneous Hajj submissions for one employee, on a real PostgreSQL under READ COMMITTED.
/// Without a per-employee lock each would check for an earlier Hajj leave, find none, and both would
/// be stored — Art. 114's "once in the worker's service" broken by a double click. The submission
/// takes a transaction-held advisory lock (the #170 pattern) keyed by tenant and employee, so the
/// second waits for the first to commit, then sees it and is refused.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class KsaStatutoryLeaveConcurrencyPostgresTests
{
    private readonly PostgresFixture _fixture;

    public KsaStatutoryLeaveConcurrencyPostgresTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task TwoSimultaneousHajjSubmissions_ExactlyOneSucceeds()
    {
        Guid tenantId;
        int employeeId;
        Guid hajjTypeId;
        Guid? userId;
        await using (var db = _fixture.CreateRetryingDb())
        {
            tenantId = await PostgresFixture.SeedMinimalTenant(db);
            var company = new Company { TenantId = tenantId, LegalNameEn = "KSA Co", CountryCode = "SA" };
            db.Companies.Add(company);
            var employee = new Employee
            {
                TenantId = tenantId, EmployeeCode = $"HJ-{Guid.NewGuid():N}"[..12], FullName = "Pilgrim", Status = "Active",
                CompanyId = company.Id, JoiningDate = DateTime.UtcNow.AddYears(-5), UserAccountId = Guid.NewGuid(),
            };
            db.Employees.Add(employee);
            var hajj = new LeaveType { TenantId = tenantId, Code = "HAJJ", NameEn = "Hajj Leave", Category = "Religious", IsPaid = true, IsActive = true };
            db.LeaveTypes.Add(hajj);
            db.LeavePolicies.Add(new LeavePolicy
            {
                TenantId = tenantId, Name = "Hajj", LeaveTypeId = hajj.Id, CountryCode = "SA", AnnualEntitlementDays = 10m,
                MinimumDaysPerRequest = 1m, WeekendsIncluded = true, PublicHolidaysIncluded = true, AppliesOnProbation = true,
                AccrualMethod = "Yearly", Status = "Active",
            });
            await db.SaveChangesAsync();
            await TestApprovalConfig.EnsureDefaultLeaveWorkflowAsync(db, tenantId);
            (employeeId, hajjTypeId, userId) = (employee.Id, hajj.Id, employee.UserAccountId);
        }

        var start = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(60);
        async Task<bool> SubmitAsync(DateOnly from)
        {
            await using var db = _fixture.CreateRetryingDb();
            try
            {
                await new LeaveService(db, new ApprovalRouter(db)).SubmitRequestAsync(tenantId, new LeaveRequest
                {
                    EmployeeId = employeeId, LeaveTypeId = hajjTypeId, StartDate = from, EndDate = from.AddDays(9),
                    DayType = "Full", Reason = "Hajj",
                }, userId);
                return true;
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("once in an employee's service"))
            {
                return false;
            }
        }

        // Different dates, so the ordinary overlap check cannot be what stops the second.
        var results = await Task.WhenAll(SubmitAsync(start), SubmitAsync(start.AddDays(365)));

        results.Count(ok => ok).Should().Be(1, "Hajj leave is granted once in an employee's service");
        await using var check = _fixture.CreateRetryingDb();
        (await check.LeaveRequests.CountAsync(r => r.TenantId == tenantId && r.EmployeeId == employeeId)).Should().Be(1);
    }
}
