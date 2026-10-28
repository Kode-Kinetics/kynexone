using FluentAssertions;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>The request-time rule on real PostgreSQL: an acknowledged lower special-leave policy is applied as configured; one never acknowledged keeps the statute.</summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class AcknowledgedSpecialLeaveRequestPostgresTests(PostgresFixture fixture)
{
    private static readonly DateOnly Base = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30);

    private async Task<(Guid Tenant, Employee Employee, LeaveType Type)> SeedAsync(string code, string name, string category, decimal days, string? acks)
    {
        await using var db = fixture.CreateDb();
        var tenant = Guid.NewGuid();
        var company = new Company { TenantId = tenant, LegalNameEn = "KSA Co", RegistrationNumber = $"K-{tenant:N}", CountryCode = "SA", DefaultCurrency = "SAR", IsActive = true };
        db.Companies.Add(company);
        var type = new LeaveType { TenantId = tenant, CompanyId = company.Id, Code = code, NameEn = name, Category = category, IsPaid = true, IsActive = true };
        var employee = new Employee
        {
            TenantId = tenant, EmployeeCode = $"E-{Guid.NewGuid():N}", FullName = "Employee", EnglishName = "Employee", Status = "Active",
            CompanyId = company.Id, JoiningDate = DateTime.UtcNow.AddYears(-3), UserAccountId = Guid.NewGuid(), Gender = "Female",
        };
        db.AddRange(type, employee, new LeavePolicy
        {
            TenantId = tenant, Name = name, LeaveTypeId = type.Id, CountryCode = "SA", CompanyId = company.Id, AnnualEntitlementDays = days,
            MinimumDaysPerRequest = 1m, WeekendsIncluded = true, PublicHolidaysIncluded = true, AppliesOnProbation = true,
            AccrualMethod = "Yearly", Status = "Active", BelowMinimumAcksJson = acks,
        });
        await db.SaveChangesAsync();
        await TestApprovalConfig.EnsureDefaultLeaveWorkflowAsync(db, tenant);
        return (tenant, employee, type);
    }

    private async Task<LeaveRequest> SubmitAsync((Guid Tenant, Employee Employee, LeaveType Type) f, int calendarDays)
    {
        await using var db = fixture.CreateDb();
        return await new LeaveService(db, new ApprovalRouter(db)).SubmitRequestAsync(f.Tenant, new LeaveRequest
        {
            EmployeeId = f.Employee.Id, EmployeeName = f.Employee.FullName, LeaveTypeId = f.Type.Id,
            StartDate = Base, EndDate = Base.AddDays(calendarDays - 1), DayType = "Full", Reason = "Statutory",
        }, f.Employee.UserAccountId);
    }

    [Fact]
    public async Task AcknowledgedLowerMaternity_IsGrantedAtThePolicyValue_AndTheStatuteDoesNotOverrideIt()
    {
        var acks = AcknowledgedSpecialLeaveRequestTests.Acked("leave_special_entitlement_below_minimum", "annualEntitlementDays", 70m, 84m);
        var f = await SeedAsync("MAT", "Maternity Leave", "Parental", 70m, acks);
        (await SubmitAsync(f, 70)).TotalDays.Should().Be(70m);
        var longer = await SeedAsync("MAT", "Maternity Leave", "Parental", 70m, acks);
        var refused = () => SubmitAsync(longer, 84);
        await refused.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task AcknowledgedLowerHajj_IsGrantedAtSeven_AndTenIsRefused()
    {
        var acks = AcknowledgedSpecialLeaveRequestTests.Acked("leave_special_entitlement_below_minimum", "annualEntitlementDays", 7m, 10m);
        (await SubmitAsync(await SeedAsync("HAJJ", "Hajj Leave", "Religious", 7m, acks), 7)).TotalDays.Should().Be(7m);
        var refused = () => SubmitAsync(SeedAsync("HAJJ", "Hajj Leave", "Religious", 7m, acks).GetAwaiter().GetResult(), 10);
        await refused.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task UnacknowledgedLowerPolicies_KeepTheStatute()
    {
        (await SubmitAsync(await SeedAsync("MAT", "Maternity Leave", "Parental", 70m, null), 84)).TotalDays.Should().Be(84m);
        (await SubmitAsync(await SeedAsync("HAJJ", "Hajj Leave", "Religious", 7m, null), 10)).TotalDays.Should().Be(10m);
    }
}
