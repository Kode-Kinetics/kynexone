using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public class DomainPermanentRepairTests
{
    [Theory]
    [InlineData("SAU")]
    [InlineData("SA")]
    public void KsaRegularOvertime_DefaultsToOnePointFive(string countryCode) =>
        OvertimeController.DefaultRegularDayMultiplier(countryCode).Should().Be(1.5m);

    [Theory]
    [InlineData("ARE")]
    [InlineData("US")]
    [InlineData(null)]
    public void NonKsaRegularOvertime_PreservesOnePointTwoFiveDefault(string? countryCode) =>
        OvertimeController.DefaultRegularDayMultiplier(countryCode).Should().Be(1.25m);

    [Fact]
    public async Task LeaveSubmission_RejectsApplicableDepartmentBlackout()
    {
        await using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var leaveType = new LeaveType
        {
            TenantId = tenantId, Code = "AL", NameEn = "Annual", IsActive = true
        };
        var employee = new Employee
        {
            TenantId = tenantId, EmployeeCode = "BLACKOUT-1", FullName = "Demo Employee",
            Department = "Operations", Status = EmployeeStatuses.Active,
            JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        db.AddRange(leaveType, employee);
        db.LeaveBlackoutDates.Add(new LeaveBlackoutDate
        {
            TenantId = tenantId, NameEn = "Year-end close", DepartmentName = "Operations",
            StartDate = new DateOnly(2026, 12, 20), EndDate = new DateOnly(2026, 12, 31),
            Reason = "Critical staffing period", IsCompanyWide = false
        });
        await db.SaveChangesAsync();

        var service = new LeaveService(db, new ApprovalPolicyService(db));
        var action = () => service.SubmitRequestAsync(tenantId, new LeaveRequest
        {
            TenantId = tenantId, EmployeeId = employee.Id, LeaveTypeId = leaveType.Id,
            StartDate = new DateOnly(2026, 12, 24), EndDate = new DateOnly(2026, 12, 24),
            DayType = "Full", Status = "Draft"
        }, CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Year-end close*2026-12-20*2026-12-31*");
        (await db.LeaveRequests.CountAsync()).Should().Be(0);
    }

    [Fact]
    public void EssProfile_ProjectsGccIdentityExpiryDatesForMobileClients()
    {
        var employee = new Employee
        {
            IqamaNumber = "IQ-1", IqamaExpiryDate = new DateOnly(2027, 1, 2),
            EmiratesId = "EID-1", EmiratesIdExpiryDate = new DateOnly(2027, 2, 3),
            Qid = "QID-1", QidExpiryDate = new DateOnly(2027, 3, 4),
            CivilId = "CID-1", CivilIdExpiryDate = new DateOnly(2027, 4, 5)
        };

        var dto = EssEmployeeProfileDto.Project(employee);

        dto.IqamaExpiryDate.Should().Be(new DateOnly(2027, 1, 2));
        dto.EmiratesIdExpiryDate.Should().Be(new DateOnly(2027, 2, 3));
        dto.QidExpiryDate.Should().Be(new DateOnly(2027, 3, 4));
        dto.CivilIdExpiryDate.Should().Be(new DateOnly(2027, 4, 5));
    }

    private static ZayraDbContext NewDb() => new(
        new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}
