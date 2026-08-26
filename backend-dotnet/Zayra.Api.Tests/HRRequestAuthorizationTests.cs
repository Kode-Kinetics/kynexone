using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public class HRRequestAuthorizationTests
{
    [Fact]
    public async Task Create_ForbidsEmployeeOutsideCallerScope()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var own = AddEmployee(db, tenantId, "OWN");
        var other = AddEmployee(db, tenantId, "OTHER");
        await db.SaveChangesAsync();
        var controller = Controller(db, tenantId, own.Id);

        var result = await controller.Create(
            new CreateHRRequestBody(other.Id, null, "General", "Subject", "Description", "Normal"),
            CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
        Assert.Empty(await db.HRRequests.ToListAsync());
    }

    [Fact]
    public async Task AddComment_ForbidsTicketOutsideCallerScope()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var own = AddEmployee(db, tenantId, "OWN");
        var other = AddEmployee(db, tenantId, "OTHER");
        var ticket = new HRRequest { TenantId = tenantId, EmployeeId = other.Id, Subject = "Private", Description = "Private" };
        db.HRRequests.Add(ticket);
        await db.SaveChangesAsync();
        var controller = Controller(db, tenantId, own.Id);

        var result = await controller.AddComment(ticket.Id, new AddCommentRequest(own.Id, "spoof"), CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
        Assert.Empty(await db.HRRequestComments.ToListAsync());
    }

    [Fact]
    public async Task AddComment_DerivesEmployeeFromTicket_NotClientPayload()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = AddEmployee(db, tenantId, "EMP");
        var ticket = new HRRequest { TenantId = tenantId, EmployeeId = employee.Id, Subject = "Help", Description = "Help" };
        db.HRRequests.Add(ticket);
        await db.SaveChangesAsync();
        var controller = Controller(db, tenantId, employee.Id);

        var result = await controller.AddComment(ticket.Id, new AddCommentRequest(999999, "response"), CancellationToken.None);

        Assert.IsType<CreatedResult>(result);
        Assert.Equal(employee.Id, (await db.HRRequestComments.SingleAsync()).EmployeeId);
    }

    private static HRRequestCenterController Controller(ZayraDbContext db, Guid tenantId, params int[] ids)
    {
        var controller = new HRRequestCenterController(db, new FixedScope(ids));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", tenantId.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.Role, "HR Officer")
                }, "test"))
            }
        };
        return controller;
    }

    private static Employee AddEmployee(ZayraDbContext db, Guid tenantId, string code)
    {
        var employee = new Employee { TenantId = tenantId, EmployeeCode = code, FullName = code, EnglishName = code, Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1) };
        db.Employees.Add(employee);
        return employee;
    }

    private static ZayraDbContext CreateDb() => new(
        new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class FixedScope(IReadOnlyCollection<int> ids) : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = DataScopeLevel.Team, CallerEmployeeId = ids.FirstOrDefault(), AllowedEmployeeIds = ids });
    }
}
