using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

public class MobileMutationSecurityTests
{
    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task MarkRead_IsCallerScoped_AndCannotMutateAColleaguesNotification()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var owner = AddEmployee(db, tenantId, "OWNER");
        var attacker = AddEmployee(db, tenantId, "ATTACKER");
        var ownersNotification = new EmployeeNotification
        {
            TenantId = tenantId, EmployeeId = owner.Id, Title = "Private", Body = "Owner only"
        };
        var attackersNotification = new EmployeeNotification
        {
            TenantId = tenantId, EmployeeId = attacker.Id, Title = "Mine", Body = "Attacker"
        };
        db.EmployeeNotifications.AddRange(ownersNotification, attackersNotification);
        await db.SaveChangesAsync();
        var controller = Controller(db, tenantId, attacker.Id);

        (await controller.MarkRead(ownersNotification.Id, CancellationToken.None))
            .Should().BeOfType<NotFoundResult>();
        (await db.EmployeeNotifications.AsNoTracking().SingleAsync(x => x.Id == ownersNotification.Id))
            .IsRead.Should().BeFalse("a guessed notification id must not mutate its owner record");

        (await controller.MarkRead(attackersNotification.Id, CancellationToken.None))
            .Should().BeOfType<NoContentResult>();
        (await db.EmployeeNotifications.AsNoTracking().SingleAsync(x => x.Id == attackersNotification.Id))
            .IsRead.Should().BeTrue();
    }

    [Fact]
    public async Task MarkRead_UnlinkedEmployeeClaim_IsForbiddenWithoutMutation()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var owner = AddEmployee(db, tenantId, "OWNER");
        var notification = new EmployeeNotification
        {
            TenantId = tenantId, EmployeeId = owner.Id, Title = "Private", Body = "Owner only"
        };
        db.EmployeeNotifications.Add(notification);
        await db.SaveChangesAsync();

        var result = await Controller(db, tenantId, 999_999).MarkRead(notification.Id, CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
        (await db.EmployeeNotifications.AsNoTracking().SingleAsync()).IsRead.Should().BeFalse();
    }

    [Fact]
    public async Task ForeignTenantEmployeeClaim_IsForbiddenWithoutMutation()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var foreignTenantId = Guid.NewGuid();
        var owner = AddEmployee(db, tenantId, "OWNER");
        var foreign = AddEmployee(db, foreignTenantId, "FOREIGN");
        var notification = new EmployeeNotification
        {
            TenantId = tenantId, EmployeeId = owner.Id, Title = "Private", Body = "Owner only"
        };
        db.EmployeeNotifications.Add(notification);
        await db.SaveChangesAsync();

        var result = await Controller(db, tenantId, foreign.Id).MarkRead(notification.Id, CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
        (await db.EmployeeNotifications.AsNoTracking().SingleAsync()).IsRead.Should().BeFalse();
    }

    [Fact]
    public async Task UnregisterDevice_IsOwnedAndIdempotent_WithoutCrossEmployeeDeletion()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var owner = AddEmployee(db, tenantId, "OWNER");
        var attacker = AddEmployee(db, tenantId, "ATTACKER");
        db.EmployeeMobileDevices.AddRange(
            new EmployeeMobileDevice { TenantId = tenantId, EmployeeId = owner.Id, DeviceIdentifier = "owner-device" },
            new EmployeeMobileDevice { TenantId = tenantId, EmployeeId = attacker.Id, DeviceIdentifier = "attacker-device" });
        await db.SaveChangesAsync();
        var controller = Controller(db, tenantId, attacker.Id);

        (await controller.UnregisterDevice("owner-device", CancellationToken.None))
            .Should().BeOfType<NoContentResult>("absence and someone else's identifier are deliberately indistinguishable");
        (await db.EmployeeMobileDevices.AsNoTracking().CountAsync()).Should().Be(2);

        (await controller.UnregisterDevice("attacker-device", CancellationToken.None)).Should().BeOfType<NoContentResult>();
        (await controller.UnregisterDevice("attacker-device", CancellationToken.None)).Should().BeOfType<NoContentResult>();
        (await db.EmployeeMobileDevices.AsNoTracking().SingleAsync()).DeviceIdentifier.Should().Be("owner-device");
    }

    [Fact]
    public async Task Punch_IgnoresClientEmployeeId_AndWritesOnlyForCaller()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var victim = AddEmployee(db, tenantId, "VICTIM");
        var caller = AddEmployee(db, tenantId, "CALLER");
        var controller = Controller(db, tenantId, caller.Id);

        (await controller.Punch(new MobilePunchRequest(victim.Id, "In", null), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();

        var row = await db.AttendanceDailyRecords.AsNoTracking().SingleAsync();
        row.EmployeeId.Should().Be(caller.Id);
        row.EmployeeId.Should().NotBe(victim.Id);
    }

    private static Employee AddEmployee(ZayraDbContext db, Guid tenantId, string code)
    {
        var employee = new Employee
        {
            TenantId = tenantId,
            EmployeeCode = code,
            FullName = code,
            EnglishName = code,
            Status = EmployeeStatuses.Active,
            JoiningDate = DateTime.UtcNow.AddDays(-30)
        };
        db.Employees.Add(employee);
        db.SaveChanges();
        return employee;
    }

    private static MobileController Controller(ZayraDbContext db, Guid tenantId, int employeeId)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("tenant_id", tenantId.ToString()),
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim("employee_id", employeeId.ToString())
            ], "Test"))
        };
        return new MobileController(db)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }
}
