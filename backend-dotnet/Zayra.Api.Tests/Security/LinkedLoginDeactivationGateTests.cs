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
    [Fact]
    public async Task AnHrUser_DeletingAnAdminLinkedEmployeeRecord_DeletesTheRecord_ButLeavesTheLoginActive()
    {
        await using var db = Db();
        var w = await SeedAsync(db, otherAdminExists: true, employeeStatus: EmployeeStatuses.Active);

        var result = await EmployeesControllerAs(db, w, w.HrUser).Delete(w.EmployeeId, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.True((await db.Employees.IgnoreQueryFilters().SingleAsync(e => e.Id == w.EmployeeId)).IsDeleted);
        Assert.True((await db.Users.SingleAsync(u => u.Id == w.LeaverAdmin)).IsActive);
        var held = await db.AuditLogs.SingleAsync(x => x.Action == LinkedLoginDeactivationGate.HeldAuditAction);
        Assert.Contains("employee.deleted", held.Metadata);
        Assert.Contains(LinkedLoginDeactivationGate.Codes.AdminMustDeactivate, held.Metadata);
    }

    [Fact]
    public async Task ALoginAboveTheActor_IsHeldWithTheAboveActorCode_AndTheRightWording()
    {
        await using var db = Db();
        // The leaver is not an Admin but holds payroll.approve, which the HR user does not.
        var w = await SeedAsync(db, otherAdminExists: true, leaverRole: "Payroll Manager");

        Assert.IsType<OkObjectResult>(await Controller(db, w, w.HrUser).Complete(w.OffboardingId, CancellationToken.None));

        Assert.True((await db.Users.SingleAsync(u => u.Id == w.LeaverAdmin)).IsActive);
        var held = await db.AuditLogs.SingleAsync(x => x.Action == LinkedLoginDeactivationGate.HeldAuditAction);
        Assert.Contains(LinkedLoginDeactivationGate.Codes.AboveActor, held.Metadata);
        Assert.Contains("payroll.approve", held.Metadata);
        var notice = await db.Notifications.FirstAsync(n => n.EntityId == w.LeaverAdmin.ToString());
        Assert.Contains("someone with at least this access must deactivate it", notice.Message);
        Assert.DoesNotContain("an Admin must", notice.Message);

        // An Admin closes the same gap.
        await using var db2 = Db();
        var w2 = await SeedAsync(db2, otherAdminExists: true, leaverRole: "Payroll Manager");
        Assert.IsType<OkObjectResult>(await Controller(db2, w2, w2.OtherAdmin).Complete(w2.OffboardingId, CancellationToken.None));
        Assert.False((await db2.Users.SingleAsync(u => u.Id == w2.LeaverAdmin)).IsActive);
    }

    [Fact]
    public async Task SwitchingALoginBackOn_IsHeldWithItsOwnWording_TheLoginStaysOff()
    {
        // The rescind path: re-enabling a login is an access change too. Held back, it stays OFF until an Admin
        // reactivates it, and the notice says so (not "still active").
        await using var db = Db();
        var w = await SeedAsync(db, otherAdminExists: true);
        var at = DateTime.UtcNow;

        var decision = await LinkedLoginDeactivationGate.DecideAsync(db, w.TenantId, w.HrUser, [w.LeaverAdmin], at, CancellationToken.None, restoring: true);
        var held = Assert.Single(decision.Held);
        Assert.Equal(LinkedLoginDeactivationGate.Codes.AdminMustReactivate, held.Code);
        Assert.Contains("stays switched OFF until an Admin reactivates it", held.MessageEn);
        Assert.Empty(decision.Deactivate);

        await LinkedLoginDeactivationGate.StageHeldAsync(db, w.TenantId, new RequestContext(null, null, w.HrUser, w.TenantId),
            "offboarding.cancel_restore", "Employee", w.EmployeeId.ToString(), decision.Held, at, CancellationToken.None);
        await db.SaveChangesAsync();
        var notice = await db.Notifications.FirstAsync(n => n.UserId == w.OtherAdmin);
        Assert.Equal("A reinstated employee's login is still switched off", notice.Title);

        // An Admin may switch it back on.
        var byAdmin = await LinkedLoginDeactivationGate.DecideAsync(db, w.TenantId, w.OtherAdmin, [w.LeaverAdmin], at, CancellationToken.None, restoring: true);
        Assert.Empty(byAdmin.Held);
    }

    internal sealed record World(Guid TenantId, int EmployeeId, Guid LeaverAdmin, Guid OtherAdmin, Guid HrUser, Guid OffboardingId);

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

    internal static async Task<World> SeedAsync(ZayraDbContext db, bool otherAdminExists, string employeeStatus = EmployeeStatuses.Offboarded,
        string leaverRole = "Admin")
    {
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Gate Tenant", Slug = $"gate-{tenantId:N}", IsActive = true });
        var company = new Company { TenantId = tenantId, LegalNameEn = "Gate Co", RegistrationNumber = $"G-{Guid.NewGuid():N}", IsActive = true };
        db.Companies.Add(company);

        async Task<Permission> Perm(string key)
        {
            var existing = await db.Permissions.SingleOrDefaultAsync(x => x.Key == key);
            if (existing is not null) return existing;
            var created = new Permission { Key = key, Module = "Test", Description = key };
            db.Permissions.Add(created);
            return created;
        }
        var approve = await Perm("employees.approve");
        var write = await Perm("employees.write");
        var delete = await Perm("employees.delete");
        var manage = await Perm("security.manage");
        var payroll = await Perm("payroll.approve");
        var adminRole = new Role { TenantId = tenantId, Name = "Admin", NormalizedName = "ADMIN", IsActive = true, IsSystem = true, IsEditable = false };
        var hrRole = new Role { TenantId = tenantId, Name = "HR Manager", NormalizedName = "HR MANAGER", IsActive = true, IsSystem = true, IsEditable = true };
        var payrollRole = new Role { TenantId = tenantId, Name = "Payroll Manager", NormalizedName = "PAYROLL MANAGER", IsActive = true, IsSystem = true, IsEditable = true };
        db.Roles.AddRange(adminRole, hrRole, payrollRole);
        foreach (var p in new[] { approve, write, delete, manage, payroll }) db.RolePermissions.Add(new RolePermission { RoleId = adminRole.Id, PermissionId = p.Id });
        foreach (var p in new[] { approve, write, delete }) db.RolePermissions.Add(new RolePermission { RoleId = hrRole.Id, PermissionId = p.Id });
        db.RolePermissions.Add(new RolePermission { RoleId = payrollRole.Id, PermissionId = payroll.Id });

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

        var leaver = AddUser("leaver", leaverRole == "Admin" ? adminRole : payrollRole);
        var other = otherAdminExists ? AddUser("other-admin", adminRole) : null;
        var hr = AddUser("hr", hrRole);

        var employee = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, UserAccountId = leaver.Id, EmployeeCode = $"EMP-{Guid.NewGuid():N}"[..12],
            FullName = "Leaving Person", Status = employeeStatus, JoiningDate = DateTime.UtcNow.AddYears(-2),
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
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

    internal static OffboardingController Controller(ZayraDbContext db, World w, Guid actor) =>
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

    private static EmployeesController EmployeesControllerAs(ZayraDbContext db, World w, Guid actor)
    {
        var audit = new AuditService(db);
        var controller = new EmployeesController(db, new Pbkdf2PasswordHasher(), audit, new NoDocs(), new NoNotifications(),
            new NoHijri(), new Zayra.Api.Infrastructure.Common.DataScopeService(db), new NoLetters(),
            new Zayra.Api.Infrastructure.Approvals.ApprovalWorkflowService(db, audit),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<EmployeesController>.Instance, new EstablishmentGuardService(db));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", w.TenantId.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, actor.ToString()),
                    new Claim("permission", "employees.delete"),
                    new Claim("is_group_scope", "true"),
                }, "test")),
            },
        };
        return controller;
    }

    private sealed class NoHijri : Zayra.Api.Infrastructure.Localization.IHijriDateService
    {
        public Zayra.Api.Infrastructure.Localization.DateConversionDto FromGregorian(DateOnly date) => new(date.ToString("yyyy-MM-dd"), "1447-01-01", 1447, 1, 1);
    }

    private sealed class NoLetters : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
    {
        public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    }

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

/// <summary>The same gate on real Postgres: the relational offboarding path (tenant anchor, row locks, the
/// execution-strategy transaction and its audit marker).</summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class LinkedLoginDeactivationGatePostgresTests
{
    private readonly PostgresFixture _fixture;
    public LinkedLoginDeactivationGatePostgresTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task OnPostgres_AnHrUser_CompletesAnAdminLinkedOffboarding_TheLoginStaysActive_AndItIsRaised()
    {
        LinkedLoginDeactivationGateTests.World w;
        await using (var seed = _fixture.CreateRetryingDb())
            w = await LinkedLoginDeactivationGateTests.SeedAsync(seed, otherAdminExists: true);

        await using (var db = _fixture.CreateRetryingDb())
            Assert.IsType<OkObjectResult>(await LinkedLoginDeactivationGateTests.Controller(db, w, w.HrUser).Complete(w.OffboardingId, CancellationToken.None));

        await using var verify = _fixture.CreateRetryingDb();
        Assert.Equal("Completed", (await verify.EmployeeOffboardings.IgnoreQueryFilters().SingleAsync(o => o.Id == w.OffboardingId)).Status);
        Assert.True((await verify.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == w.LeaverAdmin)).IsActive);
        var held = await verify.AuditLogs.IgnoreQueryFilters().SingleAsync(x => x.TenantId == w.TenantId && x.Action == LinkedLoginDeactivationGate.HeldAuditAction);
        Assert.Contains(LinkedLoginDeactivationGate.Codes.AdminMustDeactivate, held.Metadata);
        Assert.True(await verify.Notifications.IgnoreQueryFilters().AnyAsync(n => n.TenantId == w.TenantId && n.UserId == w.OtherAdmin));
    }
}
