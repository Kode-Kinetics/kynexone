using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Employee forms now store reference-list CODES (FULL_TIME, LIMITED, FEMALE); older rows and leave policies carry the
/// English labels ("Full-Time"). A leave policy scoped to a label must still match an employee stored by code, and the
/// reverse, for employment type, contract type and gender — or the employee silently falls through to no policy.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class LeavePolicyReferenceCodePostgresTests
{
    private static readonly DateOnly Day = new(2026, 11, 18); // a Wednesday
    private readonly PostgresFixture _fx;
    public LeavePolicyReferenceCodePostgresTests(PostgresFixture fx) => _fx = fx;

    [Theory]
    [InlineData("EmploymentType", "Full-Time", "FULL_TIME")]
    [InlineData("EmploymentType", "FULL_TIME", "Full-Time")]
    [InlineData("ContractType", "Limited Term", "LIMITED")]
    [InlineData("ContractType", "LIMITED", "Limited Term")]
    [InlineData("Gender", "Female", "FEMALE")]
    [InlineData("Gender", "FEMALE", "Female")]
    public async Task APolicyScopedByLabelOrCode_MatchesAnEmployeeStoredTheOtherWay(string field, string policyValue, string employeeValue)
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var type = new LeaveType { TenantId = tenantId, Code = "ANNUAL", NameEn = "Annual Leave", Category = "Annual", IsActive = true, IsPaid = true };
        db.LeaveTypes.Add(type);
        var employee = new Employee
        {
            TenantId = tenantId, EmployeeCode = $"RL-{Guid.NewGuid():N}"[..14], FullName = "Reference Person", UserAccountId = Guid.NewGuid(),
            Status = "Active", JoiningDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), WorkEmail = $"{Guid.NewGuid():N}@p.test",
        };
        Set(employee, field, employeeValue);
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
        {
            TenantId = tenantId, EmployeeId = employee.Id, EmployeeName = employee.FullName, LeaveTypeId = type.Id,
            LeaveTypeName = type.NameEn, Year = Day.Year, Entitled = 30,
        });
        var policy = new LeavePolicy { TenantId = tenantId, LeaveTypeId = type.Id, Name = "Scoped", Status = "Active", AnnualEntitlementDays = 30 };
        Set(policy, field, policyValue);
        db.LeavePolicies.Add(policy);
        await db.SaveChangesAsync();

        var service = await TestApprovalConfig.LeaveServiceAsync(db, tenantId, "HR");
        var submitted = await service.SubmitRequestAsync(tenantId, new LeaveRequest
        {
            TenantId = tenantId, EmployeeId = employee.Id, LeaveTypeId = type.Id, StartDate = Day, EndDate = Day, DayType = "Full",
        });

        submitted.PolicyId.Should().Be(policy.Id, $"a policy scoped to {field} '{policyValue}' is the same value as '{employeeValue}'");
    }

    /// <summary>
    /// Employment Type is a list: a policy for "Full-Time,Part-Time" covers a part-timer (stored by code), and an
    /// employment type it does not list falls through to the all-staff policy.
    /// </summary>
    [Theory]
    [InlineData("PART_TIME", true)]
    [InlineData("Full-Time", true)]
    [InlineData("TEMPORARY", false)]
    public async Task APolicyForSeveralEmploymentTypes_MatchesAnyOfThem(string employeeType, bool expectScoped)
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var type = new LeaveType { TenantId = tenantId, Code = "ANNUAL", NameEn = "Annual Leave", Category = "Annual", IsActive = true, IsPaid = true };
        db.LeaveTypes.Add(type);
        var employee = new Employee
        {
            TenantId = tenantId, EmployeeCode = $"ET-{Guid.NewGuid():N}"[..14], FullName = "Listed Person", UserAccountId = Guid.NewGuid(),
            Status = "Active", JoiningDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), WorkEmail = $"{Guid.NewGuid():N}@p.test",
            EmploymentType = employeeType,
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
        {
            TenantId = tenantId, EmployeeId = employee.Id, EmployeeName = employee.FullName, LeaveTypeId = type.Id,
            LeaveTypeName = type.NameEn, Year = Day.Year, Entitled = 30,
        });
        var everyone = new LeavePolicy { TenantId = tenantId, LeaveTypeId = type.Id, Name = "All staff", Status = "Active", AnnualEntitlementDays = 21 };
        var scoped = new LeavePolicy { TenantId = tenantId, LeaveTypeId = type.Id, Name = "Full and part time", Status = "Active", AnnualEntitlementDays = 30, EmploymentType = "Full-Time,Part-Time" };
        db.LeavePolicies.AddRange(everyone, scoped);
        await db.SaveChangesAsync();

        var service = await TestApprovalConfig.LeaveServiceAsync(db, tenantId, "HR");
        var submitted = await service.SubmitRequestAsync(tenantId, new LeaveRequest
        {
            TenantId = tenantId, EmployeeId = employee.Id, LeaveTypeId = type.Id, StartDate = Day, EndDate = Day, DayType = "Full",
        });

        submitted.PolicyId.Should().Be(expectScoped ? scoped.Id : everyone.Id);
    }

    private static void Set(object target, string field, string value) =>
        target.GetType().GetProperty(field)!.SetValue(target, value);
}
