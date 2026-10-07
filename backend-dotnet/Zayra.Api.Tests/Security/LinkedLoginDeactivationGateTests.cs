using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Employees;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Application.Common;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Ending an employment that is linked to an Admin's login (LinkedLoginDeactivationGate). Offboarding, a
/// termination status and deleting the record are HR's to make (employees.approve / employees.write), so they
/// always go through; but switching off the LOGIN is an access change. An HR user must not be able to turn off an
/// Admin, and nobody may turn off the last operational Admin this way. The login is left active, and that is
/// raised: an audit row and a notification to the tenant's Admins.
/// </summary>
public sealed class LinkedLoginDeactivationGateTests
{
    private sealed record World(Guid TenantId, int EmployeeId, Guid LeaverAdmin, Guid OtherAdmin, Guid HrUser, Guid OffboardingId);

    [Fact]
    public async Task AnHrUser_OffboardsAnAdminLinkedEmployee_TheSeparationCompletes_TheLoginStaysActive_AndItIsRaised()
    {
        await using var db = Db();
        var w = await SeedAsync(db, otherAdminExists: true);

        var result = await Controller(db, w, w.HrUser).Complete(w.OffboardingId, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("Archived", (await db.Employees.SingleAsync(e => e.Id == w.EmployeeId)).Status);
        var offboarding = await db.EmployeeOffboardings.SingleAsync(o => o.Id == w.OffboardingId);
        Assert.Equal("Completed", offboarding.Status);
        Assert.False(offboarding.AccessRevoked);

        var admin = await db.Users.SingleAsync(u => u.Id == w.LeaverAdmin);
        Assert.True(admin.IsActive);
        Assert.Equal("Active", admin.Status);
        Assert.Equal(AccessModes.FullPortal, admin.AccessMode);
        Assert.Null((await db.RefreshTokens.SingleAsync(t => t.UserId == w.LeaverAdmin)).RevokedAtUtc);

        var held = await db.AuditLogs.SingleAsync(x => x.Action == LinkedLoginDeactivationGate.HeldAuditAction);
        Assert.Equal(w.LeaverAdmin.ToString(), held.EntityId);
        Assert.Equal(w.HrUser, held.UserId);
        Assert.Contains(LinkedLoginDeactivationGate.Codes.AdminMustDeactivate, held.Metadata);

        var notified = await db.Notifications.Where(n => n.EntityId == w.LeaverAdmin.ToString()).Select(n => n.UserId).ToListAsync();
        Assert.Contains(w.OtherAdmin, notified);
        Assert.Contains(w.HrUser, notified);
        Assert.DoesNotContain(w.LeaverAdmin, notified);
    }

    [Fact]
    public async Task AnHrUser_RevokingAccess_SeesTheCodedExceptionInTheResponse()
    {
        await using var db = Db();
        var w = await SeedAsync(db, otherAdminExists: true);

        var result = Assert.IsType<OkObjectResult>(await Controller(db, w, w.HrUser).RevokeAccess(w.OffboardingId, CancellationToken.None));

        var body = System.Text.Json.JsonSerializer.SerializeToElement(result.Value);
        var held = Assert.Single(body.GetProperty("linkedLoginHeld").EnumerateArray());
        Assert.Equal(LinkedLoginDeactivationGate.Codes.AdminMustDeactivate, held.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(held.GetProperty("messageAr").GetString()));
        Assert.False(body.GetProperty("AccessRevoked").GetBoolean());
        Assert.True((await db.Users.SingleAsync(u => u.Id == w.LeaverAdmin)).IsActive);
    }

    [Fact]
    public async Task AnAdmin_OffboardingTheSameEmployee_DeactivatesTheLogin()
    {
        await using var db = Db();
        var w = await SeedAsync(db, otherAdminExists: true);

        var result = await Controller(db, w, w.OtherAdmin).Complete(w.OffboardingId, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var admin = await db.Users.SingleAsync(u => u.Id == w.LeaverAdmin);
        Assert.False(admin.IsActive);
        Assert.Equal(AccessModes.NoLogin, admin.AccessMode);
        Assert.NotNull((await db.RefreshTokens.SingleAsync(t => t.UserId == w.LeaverAdmin)).RevokedAtUtc);
        Assert.True((await db.EmployeeOffboardings.SingleAsync(o => o.Id == w.OffboardingId)).AccessRevoked);
        Assert.False(await db.AuditLogs.AnyAsync(x => x.Action == LinkedLoginDeactivationGate.HeldAuditAction));
    }

    [Fact]
    public async Task TheLastOperationalAdmin_IsNeverDeactivated_ButTheSeparationStillCompletes()
    {
        await using var db = Db();
        var w = await SeedAsync(db, otherAdminExists: false);

        // The only Admin closes their own offboarding: the employment ends, the login does not.
        var result = await Controller(db, w, w.LeaverAdmin).Complete(w.OffboardingId, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("Completed", (await db.EmployeeOffboardings.SingleAsync(o => o.Id == w.OffboardingId)).Status);
        Assert.True((await db.Users.SingleAsync(u => u.Id == w.LeaverAdmin)).IsActive);
        var held = await db.AuditLogs.SingleAsync(x => x.Action == LinkedLoginDeactivationGate.HeldAuditAction);
        Assert.Contains(LinkedLoginDeactivationGate.Codes.LastAdmin, held.Metadata);
    }

    [Fact]
    public async Task AnHrUser_TerminatingAnAdminLinkedEmployee_ByStatus_LeavesTheLoginActive_AndRaisesIt()
    {
        await using var db = Db();
        var w = await SeedAsync(db, otherAdminExists: true, employeeStatus: EmployeeStatuses.Active);
        var service = new EmployeeManagementService(db, new AuditService(db), new NoDocs(), new NoNotifications(), new EstablishmentGuardService(db));

        await service.ChangeStatusAsync(w.TenantId, w.EmployeeId,
            new EmployeeStatusChangeRequest(EmployeeStatuses.Suspended, DateOnly.FromDateTime(DateTime.UtcNow), "Disciplinary suspension"),
            new RequestContext("127.0.0.1", "tests", w.HrUser, w.TenantId), CancellationToken.None);

        Assert.Equal(EmployeeStatuses.Suspended, (await db.Employees.SingleAsync(e => e.Id == w.EmployeeId)).Status);
        Assert.True((await db.Users.SingleAsync(u => u.Id == w.LeaverAdmin)).IsActive);
        var held = await db.AuditLogs.SingleAsync(x => x.Action == LinkedLoginDeactivationGate.HeldAuditAction);
        Assert.Contains(LinkedLoginDeactivationGate.Codes.AdminMustDeactivate, held.Metadata);
        Assert.Contains(w.OtherAdmin, await db.Notifications.Where(n => n.EntityId == w.LeaverAdmin.ToString()).Select(n => n.UserId).ToListAsync());
    }

    // ── Harness (the same in-memory shape as LeaverAccessRevocationTests) ─────────────────────────────

    private static ZayraDbContext Db() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<World> SeedAsync(ZayraDbContext db, bool otherAdminExists, string employeeStatus = EmployeeStatuses.Offboarded)
    {
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Gate Tenant", Slug = $"gate-{tenantId:N}", IsActive = true });

        var approve = new Permission { Key = "employees.approve", Module = "Employees", Description = "Approve" };
        var write = new Permission { Key = "employees.write", Module = "Employees", Description = "Write" };
        var manage = new Permission { Key = "security.manage", Module = "Security", Description = "Manage" };
        db.Permissions.AddRange(approve, write, manage);
        var adminRole = new Role { TenantId = tenantId, Name = "Admin", NormalizedName = "ADMIN", IsActive = true, IsSystem = true, IsEditable = false };
        var hrRole = new Role { TenantId = tenantId, Name = "HR Manager", NormalizedName = "HR MANAGER", IsActive = true, IsSystem = true, IsEditable = true };
        db.Roles.AddRange(adminRole, hrRole);
        foreach (var p in new[] { approve, write, manage }) db.RolePermissions.Add(new RolePermission { RoleId = adminRole.Id, PermissionId = p.Id });
        foreach (var p in new[] { approve, write }) db.RolePermissions.Add(new RolePermission { RoleId = hrRole.Id, PermissionId = p.Id });

        User AddUser(string name, Role role)
        {
            var user = new User
            {
                TenantId = tenantId, Email = $"{name}-{Guid.NewGuid():N}@gate.test", FullName = name, PasswordHash = "hash",
                IsActive = true, IsEmailConfirmed = true, Status = "Active", AccessMode = AccessModes.FullPortal,
            };
            user.NormalizedEmail = user.Email.ToUpperInvariant();
            db.Users.Add(user);
            db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
            return user;
        }

        var leaver = AddUser("leaver-admin", adminRole);
        var other = otherAdminExists ? AddUser("other-admin", adminRole) : null;
        var hr = AddUser("hr", hrRole);

        var employee = new Employee
        {
            Id = 7001, TenantId = tenantId, UserAccountId = leaver.Id, EmployeeCode = "EMP-ADM", FullName = "Leaving Admin",
            Status = employeeStatus, JoiningDate = DateTime.UtcNow.AddYears(-2),
        };
        db.Employees.Add(employee);
        db.EmployeeUserAccounts.Add(new EmployeeUserAccount
        {
            TenantId = tenantId, EmployeeId = employee.Id, UserId = leaver.Id, AccessMode = AccessModes.FullPortal, Status = "Active",
            RequiresPasswordSetup = false,
        });
        db.RefreshTokens.Add(new RefreshToken { UserId = leaver.Id, TokenHash = $"live-{Guid.NewGuid():N}", ExpiresAtUtc = DateTime.UtcNow.AddDays(7) });
        var offboarding = new EmployeeOffboarding
        {
            TenantId = tenantId, EmployeeId = employee.Id, EmployeeCode = employee.EmployeeCode, EmployeeName = employee.FullName,
            Status = "InProgress", LastWorkingDay = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)),
            AssetsReturned = true, KnowledgeHandover = true, ExitInterviewStatus = "Waived",
        };
        db.EmployeeOffboardings.Add(offboarding);
        db.EmployeeFinalSettlements.Add(new EmployeeFinalSettlement
        {
            TenantId = tenantId, EmployeeId = employee.Id, EmployeeCode = employee.EmployeeCode, EmployeeName = employee.FullName,
            OffboardingId = offboarding.Id, LastWorkingDay = offboarding.LastWorkingDay,
            ServiceStartDate = DateOnly.FromDateTime(employee.JoiningDate), SettlementDueDate = offboarding.LastWorkingDay,
            Status = FinalSettlementStatuses.Paid,
        });
        await db.SaveChangesAsync();
        return new World(tenantId, employee.Id, leaver.Id, other?.Id ?? Guid.Empty, hr.Id, offboarding.Id);
    }

    private static OffboardingController Controller(ZayraDbContext db, World w, Guid actor) =>
        new(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("tenant_id", w.TenantId.ToString()),
                        new Claim(ClaimTypes.NameIdentifier, actor.ToString()),
                        new Claim("permission", "employees.approve"),
                    }, "test")),
                },
            },
        };

    private sealed class NoDocs : Zayra.Api.Infrastructure.Documents.IDocumentStorage
    {
        public Task<Zayra.Api.Infrastructure.Documents.StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct) =>
            Task.FromResult(new Zayra.Api.Infrastructure.Documents.StoredDocument("f", "t", "u", "p"));
        public string ResolvePath(string storageUrl) => "/tmp";
        public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    }

    private sealed class NoNotifications : INotificationService
    {
        public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
        public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
    }
}
