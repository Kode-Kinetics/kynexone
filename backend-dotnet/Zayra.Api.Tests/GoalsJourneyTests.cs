using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers.Performance;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// R05 — the goal journey: create → approve (activates) → progress → complete.
///
/// <para>Three breaks on the way: <c>Create</c> never copied the entered weight, so a 10% goal was
/// stored at the entity default of 100%; <c>Approve</c> set <c>ManagerApproved</c> but left the goal in
/// <c>Draft</c>, and the screen only offers progress on <c>Active</c> goals, so no goal could ever be
/// progressed from the UI; and <c>UpdateProgress</c> accepted any goal, so the API could progress (and
/// complete) a goal nobody had approved.</para>
/// </summary>
public sealed class GoalsJourneyTests
{
    private static ZayraDbContext Db() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static GoalsController Controller(ZayraDbContext db, Guid tenantId) =>
        new(db, new DataScopeService(db), new HrmHierarchyService(db, new AuditService(db)))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("tenant_id", tenantId.ToString()),
                        new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                        new Claim(ClaimTypes.Role, "Admin"),
                        new Claim("permission", "employees.read"),
                        new Claim("permission", "performance.read"),
                        new Claim("permission", "performance.write"),
                    }, "Test")),
                },
            },
        };

    private static async Task<(Guid TenantId, Employee Employee)> SeedAsync(ZayraDbContext db)
    {
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Goal Tenant", Slug = tenantId.ToString("N") });
        var employee = new Employee
        {
            TenantId = tenantId, EmployeeCode = "GOAL-001", FullName = "Goal Owner",
            Status = "Active", JoiningDate = DateTime.UtcNow.Date,
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        return (tenantId, employee);
    }

    private static GoalRequest Request(int employeeId, decimal weight = 10) => new(
        employeeId, "Client-supplied name", null, "Close support cases", "Quarterly target", "Individual",
        "Quantitative", "cases", TargetValue: 100, ActualValue: 25, Weight: weight, DueDate: new DateOnly(2026, 12, 31));

    private static async Task<EmployeeGoal> CreateAsync(GoalsController controller, int employeeId) =>
        Assert.IsType<EmployeeGoal>(Assert.IsType<CreatedResult>(
            await controller.Create(Request(employeeId), CancellationToken.None)).Value);

    [Fact]
    public async Task Create_StoresTheEnteredWeight_AndTheResolvedEmployeeName()
    {
        await using var db = Db();
        var (tenantId, employee) = await SeedAsync(db);

        var goal = await CreateAsync(Controller(db, tenantId), employee.Id);

        Assert.Equal(10m, goal.Weight);
        Assert.Equal(10m, (await db.EmployeeGoals.AsNoTracking().SingleAsync()).Weight);
        Assert.Equal(employee.FullName, goal.EmployeeName);
        Assert.Equal("Draft", goal.Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(100.01)]
    [InlineData(150)]
    public async Task Create_RefusesAWeightOutsideOneToOneHundredPercent(double weight)
    {
        await using var db = Db();
        var (tenantId, employee) = await SeedAsync(db);

        var result = await Controller(db, tenantId).Create(Request(employee.Id, (decimal)weight), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.False(await db.EmployeeGoals.AnyAsync());
    }

    [Fact]
    public async Task Approve_ActivatesTheDraft_ThenProgressCompletesIt()
    {
        await using var db = Db();
        var (tenantId, employee) = await SeedAsync(db);
        var controller = Controller(db, tenantId);
        var goal = await CreateAsync(controller, employee.Id);

        Assert.IsType<OkObjectResult>(await controller.Approve(goal.Id, CancellationToken.None));
        Assert.True(goal.ManagerApproved);
        Assert.Equal("Active", goal.Status);

        Assert.IsType<OkObjectResult>(await controller.UpdateProgress(goal.Id,
            new ProgressUpdateRequest(100, "Target met", null), CancellationToken.None));
        Assert.Equal("Completed", goal.Status);
        Assert.Equal(100m, goal.AchievementPct);
        Assert.Equal(1, await db.GoalProgressUpdates.CountAsync());
    }

    [Fact]
    public async Task Approve_IsIdempotentOnAnActiveGoal_AndRefusesAClosedOne()
    {
        await using var db = Db();
        var (tenantId, employee) = await SeedAsync(db);
        var controller = Controller(db, tenantId);
        var goal = await CreateAsync(controller, employee.Id);

        Assert.IsType<OkObjectResult>(await controller.Approve(goal.Id, CancellationToken.None));
        Assert.IsType<OkObjectResult>(await controller.Approve(goal.Id, CancellationToken.None));
        Assert.Equal("Active", goal.Status);

        goal.Status = "Completed";
        await db.SaveChangesAsync();
        Assert.IsType<ConflictObjectResult>(await controller.Approve(goal.Id, CancellationToken.None));
        Assert.Equal("Completed", goal.Status);
    }

    [Fact]
    public async Task Progress_OnAnUnapprovedDraft_IsRefused()
    {
        await using var db = Db();
        var (tenantId, employee) = await SeedAsync(db);
        var controller = Controller(db, tenantId);
        var goal = await CreateAsync(controller, employee.Id);

        Assert.IsType<ConflictObjectResult>(await controller.UpdateProgress(goal.Id,
            new ProgressUpdateRequest(100, "Must not apply", null), CancellationToken.None));

        Assert.Equal(25m, goal.ActualValue);
        Assert.Equal("Draft", goal.Status);
        Assert.False(await db.GoalProgressUpdates.AnyAsync());
    }

    [Fact]
    public async Task Progress_OnACompletedGoal_IsRefused()
    {
        await using var db = Db();
        var (tenantId, employee) = await SeedAsync(db);
        var controller = Controller(db, tenantId);
        var goal = await CreateAsync(controller, employee.Id);
        await controller.Approve(goal.Id, CancellationToken.None);
        await controller.UpdateProgress(goal.Id, new ProgressUpdateRequest(100, "Done", null), CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(await controller.UpdateProgress(goal.Id,
            new ProgressUpdateRequest(40, "Reopen by accident", null), CancellationToken.None));
        Assert.Equal(100m, goal.ActualValue);
        Assert.Equal(1, await db.GoalProgressUpdates.CountAsync());
    }

    [Fact]
    public async Task AnotherTenant_CannotApproveOrProgressTheGoal()
    {
        await using var db = Db();
        var (tenantId, employee) = await SeedAsync(db);
        var goal = await CreateAsync(Controller(db, tenantId), employee.Id);
        var foreign = Controller(db, Guid.NewGuid());

        Assert.IsType<NotFoundResult>(await foreign.Approve(goal.Id, CancellationToken.None));
        Assert.IsType<NotFoundResult>(await foreign.UpdateProgress(goal.Id,
            new ProgressUpdateRequest(100, "forbidden", null), CancellationToken.None));
        Assert.False(goal.ManagerApproved);
        Assert.Equal("Draft", goal.Status);
    }
}
