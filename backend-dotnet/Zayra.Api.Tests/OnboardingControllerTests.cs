using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers.Recruitment;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public sealed class OnboardingControllerTests
{
    [Fact]
    public async Task BulkCreate_UsesPersistedChecklistTemplateTasks()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = new Employee
        {
            TenantId = tenantId, EmployeeCode = "EMP-ONBOARD-1",
            FullName = "Onboarding Employee", EnglishName = "Onboarding Employee", Status = EmployeeStatuses.Active,
        };
        var checklist = new OnboardingChecklist
        {
            TenantId = tenantId,
            Code = "NEW-HIRE",
            Name = "New Hire",
            ApplicableTo = "All"
        };
        db.AddRange(checklist, employee);
        await db.SaveChangesAsync();
        var controller = CreateController(db, tenantId);

        var templateResult = await controller.UpsertTemplateTask(checklist.Id, new UpsertOnboardingTemplateTaskRequest(
            "Issue laptop", "Provision standard equipment", "IT", "IT Admin", null, 2, 1, true, true), CancellationToken.None);
        Assert.IsType<OkObjectResult>(templateResult);

        var bulkResult = await controller.CreateBulkFromChecklist(new BulkOnboardingRequest(
            checklist.Id, employee.PublicId, null, new DateOnly(2026, 3, 1), null), CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(bulkResult);

        Assert.NotNull(ok.Value);
        var task = await db.OnboardingTasks.SingleAsync(x => x.TenantId == tenantId && x.EmployeeId == employee.PublicId);
        Assert.Equal("Issue laptop", task.TaskTitle);
        Assert.Equal("IT", task.Category);
        Assert.True(task.IsMandatory);
        Assert.Equal(new DateOnly(2026, 3, 3), task.DueDate);
    }

    private static OnboardingController CreateController(ZayraDbContext db, Guid tenantId)
    {
        var claims = new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, "Admin")
        };
        return new OnboardingController(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))
                }
            }
        };
    }

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}
