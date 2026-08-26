using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Shifts;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public class ShiftRosterConstraintTests
{
    [Fact]
    public async Task AutoPlan_EnforcesConsecutiveDayLimitWithinItsOwnBatch()
    {
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenantId = Guid.NewGuid();
        var employee = new Employee { TenantId = tenantId, EmployeeCode = "RST-1", FullName = "Roster Employee", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1) };
        var shift = new ShiftDefinition { TenantId = tenantId, Code = "DAY", Name = "Day", StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(16, 0) };
        db.AddRange(employee, shift, new ShiftPolicy { TenantId = tenantId, MinRestHours = 8, MaxConsecutiveDays = 2 });
        await db.SaveChangesAsync();

        var controller = new ShiftsController(db, new UnrestrictedScope(), new UnusedPlanner())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("tenant_id", tenantId.ToString()) }, "Test"))
                }
            }
        };
        var from = new DateOnly(2026, 8, 17);
        var result = await controller.AutoPlan(new AutoPlanRequest(from, from.AddDays(2), new List<Guid> { shift.Id }, "fixed", false, false, new List<int> { employee.Id }), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        (await db.ShiftAssignments.CountAsync()).Should().Be(2);
    }

    private sealed class UnrestrictedScope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = DataScopeLevel.Organization, AllowedEmployeeIds = null });
    }

    private sealed class UnusedPlanner : IRosterPlannerService
    {
        public Task<RosterPlanResult> PlanAsync(RosterPlanInput input, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
