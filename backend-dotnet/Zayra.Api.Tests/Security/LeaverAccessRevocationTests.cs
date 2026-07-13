using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

public class LeaverAccessRevocationTests
{
    [Fact]
    public async Task OffboardingComplete_RevokesLinkedUserAccessAndRefreshTokens()
    {
        var options = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new ZayraDbContext(options);

        var tenantId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var employee = new Employee
        {
            Id = 1001,
            TenantId = tenantId,
            UserAccountId = userId,
            EmployeeCode = "EMP-001",
            FullName = "Leaver User",
            Status = EmployeeStatuses.Offboarded,
            JoiningDate = DateTime.UtcNow.AddYears(-1)
        };
        var user = new User
        {
            Id = userId,
            TenantId = tenantId,
            Email = "leaver@test.local",
            NormalizedEmail = "LEAVER@TEST.LOCAL",
            FullName = "Leaver User",
            PasswordHash = "hash",
            IsActive = true,
            Status = "Active",
            AccessMode = AccessModes.FullPortal
        };
        var offboarding = new EmployeeOffboarding
        {
            TenantId = tenantId,
            EmployeeId = employee.Id,
            EmployeeCode = employee.EmployeeCode,
            EmployeeName = employee.FullName,
            Status = "InProgress"
        };

        db.Employees.Add(employee);
        db.Users.Add(user);
        db.EmployeeUserAccounts.Add(new EmployeeUserAccount
        {
            TenantId = tenantId,
            EmployeeId = employee.Id,
            UserId = userId,
            AccessMode = AccessModes.FullPortal,
            Status = "Active",
            RequiresPasswordSetup = false
        });
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = userId,
            TokenHash = "active-token",
            ExpiresAtUtc = DateTime.UtcNow.AddDays(7)
        });
        db.EmployeeOffboardings.Add(offboarding);
        await db.SaveChangesAsync();

        var controller = new OffboardingController(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("tenant_id", tenantId.ToString()),
                        new Claim(ClaimTypes.NameIdentifier, actorId.ToString()),
                        new Claim(ClaimTypes.Role, "Admin")
                    }, "test"))
                }
            }
        };

        var result = await controller.Complete(offboarding.Id, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var storedUser = await db.Users.SingleAsync(u => u.Id == userId);
        var storedEmployee = await db.Employees.SingleAsync(e => e.Id == employee.Id);
        var link = await db.EmployeeUserAccounts.SingleAsync(l => l.UserId == userId);
        var token = await db.RefreshTokens.SingleAsync(t => t.UserId == userId);
        var storedOffboarding = await db.EmployeeOffboardings.SingleAsync(o => o.Id == offboarding.Id);

        Assert.False(storedUser.IsActive);
        Assert.Equal("Deactivated", storedUser.Status);
        Assert.Equal(AccessModes.NoLogin, storedUser.AccessMode);
        Assert.Equal(AccessModes.NoLogin, link.AccessMode);
        Assert.Equal("NoLogin", link.Status);
        Assert.NotNull(token.RevokedAtUtc);
        Assert.Null(storedEmployee.UserAccountId);
        Assert.True(storedOffboarding.AccessRevoked);
    }
}
