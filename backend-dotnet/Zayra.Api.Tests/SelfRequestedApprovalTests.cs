using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Production, 2026-10-03: a sole tenant Admin edited sensitive fields on their own record and the
/// Approval Center showed thirteen identical rows, each marked only "Watching". Maker-checker was
/// right to refuse them; what was wrong was that nobody was told why, nobody else could ever decide,
/// re-saving queued a copy each time, and the requester had no way to take a request back.
/// </summary>
public class SelfRequestedApprovalTests
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

    [Fact]
    public async Task TheRequesterIsToldWhy_AndThatNobodyElseCanApprove_WhenTheyAreTheOnlyAdmin()
    {
        await using var db = CreateDb();
        var (tenant, adminRole) = await SeedTenantAsync(db);
        var requester = await AddUserAsync(db, tenant, adminRole);
        var employee = await AddEmployeeAsync(db, tenant);

        await SubmitAsync(db, tenant, requester, employee.Id, "salary", 15000);
        var item = await SingleListedAsync(db, tenant, requester);

        item.CanDecide.Should().BeFalse("maker-checker is unchanged");
        item.DecisionBlockedReason.Should().Contain("You requested this").And.Contain("No other active user can approve it");
        item.CanWithdraw.Should().BeTrue();
        item.ChangeSummary.Should().Be("salary");
    }

    [Fact]
    public async Task TheRequesterIsToldItIsWaiting_WhenAnotherAdminExists()
    {
        await using var db = CreateDb();
        var (tenant, adminRole) = await SeedTenantAsync(db);
        var requester = await AddUserAsync(db, tenant, adminRole);
        await AddUserAsync(db, tenant, adminRole);
        var employee = await AddEmployeeAsync(db, tenant);

        await SubmitAsync(db, tenant, requester, employee.Id, "salary", 15000);
        var item = await SingleListedAsync(db, tenant, requester);

        item.DecisionBlockedReason.Should().Contain("You requested this").And.Contain("It is waiting for");
    }

    [Fact]
    public async Task AnInactiveSecondAdminDoesNotCountAsSomeoneWhoCanApprove()
    {
        await using var db = CreateDb();
        var (tenant, adminRole) = await SeedTenantAsync(db);
        var requester = await AddUserAsync(db, tenant, adminRole);
        await AddUserAsync(db, tenant, adminRole, isActive: false);
        var employee = await AddEmployeeAsync(db, tenant);

        await SubmitAsync(db, tenant, requester, employee.Id, "salary", 15000);
        var item = await SingleListedAsync(db, tenant, requester);

        item.DecisionBlockedReason.Should().Contain("No other active user can approve it");
    }

    [Fact]
    public async Task AnotherApproverCanDecide_AndSeesNoBlockedReasonOrWithdraw()
    {
        await using var db = CreateDb();
        var (tenant, adminRole) = await SeedTenantAsync(db);
        var requester = await AddUserAsync(db, tenant, adminRole);
        var approver = await AddUserAsync(db, tenant, adminRole);
        var employee = await AddEmployeeAsync(db, tenant);

        await SubmitAsync(db, tenant, requester, employee.Id, "bankIban", "SA0380000000608010167519");
        var item = await SingleListedAsync(db, tenant, approver);

        item.CanDecide.Should().BeTrue();
        item.DecisionBlockedReason.Should().BeNull();
        item.CanWithdraw.Should().BeFalse();
        item.ChangeSummary.Should().Be("IBAN");
    }

    [Fact]
    public async Task ResubmittingTheSameSensitiveValue_ReturnsTheWaitingRequest_InsteadOfQueueingACopy()
    {
        await using var db = CreateDb();
        var (tenant, adminRole) = await SeedTenantAsync(db);
        var requester = await AddUserAsync(db, tenant, adminRole);
        var employee = await AddEmployeeAsync(db, tenant);

        var first = await SubmitAsync(db, tenant, requester, employee.Id, "salary", 15000);
        var second = await SubmitAsync(db, tenant, requester, employee.Id, "salary", 15000);

        (await db.ApprovalRequests.CountAsync()).Should().Be(1);
        (await db.EmployeeChangeRequests.CountAsync()).Should().Be(1);
        Prop(second, "approvalRequestId").Should().Be(Prop(first, "approvalRequestId"));
        Prop(second, "alreadyPending").Should().Be(true);
    }

    [Fact]
    public async Task ADifferentValue_StillRaisesItsOwnRequest()
    {
        await using var db = CreateDb();
        var (tenant, adminRole) = await SeedTenantAsync(db);
        var requester = await AddUserAsync(db, tenant, adminRole);
        var employee = await AddEmployeeAsync(db, tenant);

        await SubmitAsync(db, tenant, requester, employee.Id, "salary", 15000);
        await SubmitAsync(db, tenant, requester, employee.Id, "salary", 16000);

        (await db.ApprovalRequests.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task AWithdrawnRequest_IsNotReturnedForAnIdenticalResubmission()
    {
        await using var db = CreateDb();
        var (tenant, adminRole) = await SeedTenantAsync(db);
        var requester = await AddUserAsync(db, tenant, adminRole);
        var employee = await AddEmployeeAsync(db, tenant);

        await SubmitAsync(db, tenant, requester, employee.Id, "salary", 15000);
        var approval = await db.ApprovalRequests.SingleAsync();
        await Service(db).WithdrawAsync(tenant, approval.Id, null, Context(tenant, requester), CancellationToken.None);
        await SubmitAsync(db, tenant, requester, employee.Id, "salary", 15000);

        (await db.ApprovalRequests.CountAsync(x => x.Status == "Pending")).Should().Be(1);
        (await db.ApprovalRequests.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task TheRequesterCanWithdraw_WhichCancelsBothTheApprovalAndTheChange()
    {
        await using var db = CreateDb();
        var (tenant, adminRole) = await SeedTenantAsync(db);
        var requester = await AddUserAsync(db, tenant, adminRole);
        var employee = await AddEmployeeAsync(db, tenant);

        await SubmitAsync(db, tenant, requester, employee.Id, "salary", 15000);
        var approval = await db.ApprovalRequests.SingleAsync();

        var result = await Service(db).WithdrawAsync(tenant, approval.Id, "Entered by mistake", Context(tenant, requester), CancellationToken.None);

        result!.Status.Should().Be("Cancelled");
        result.CanWithdraw.Should().BeFalse();
        db.ChangeTracker.Clear();
        var change = await db.EmployeeChangeRequests.SingleAsync();
        change.Status.Should().Be(EmployeeChangeStatuses.Cancelled);
        change.RejectionReason.Should().Be("Entered by mistake");
        (await db.Employees.SingleAsync()).Salary.Should().Be(10000m, "a withdrawn change is never applied");
        (await db.AuditLogs.AnyAsync(x => x.Action == "approval.request_withdrawn" && x.EntityId == approval.Id.ToString())).Should().BeTrue();
    }

    [Fact]
    public async Task NobodyButTheRequesterCanWithdraw_AndOnlyWhilePending()
    {
        await using var db = CreateDb();
        var (tenant, adminRole) = await SeedTenantAsync(db);
        var requester = await AddUserAsync(db, tenant, adminRole);
        var other = await AddUserAsync(db, tenant, adminRole);
        var employee = await AddEmployeeAsync(db, tenant);

        await SubmitAsync(db, tenant, requester, employee.Id, "salary", 15000);
        var approval = await db.ApprovalRequests.SingleAsync();
        var service = Service(db);

        var byOther = () => service.WithdrawAsync(tenant, approval.Id, null, Context(tenant, other), CancellationToken.None);
        await byOther.Should().ThrowAsync<InvalidOperationException>().WithMessage("Only the person who requested this*");

        await service.WithdrawAsync(tenant, approval.Id, null, Context(tenant, requester), CancellationToken.None);
        var again = () => service.WithdrawAsync(tenant, approval.Id, null, Context(tenant, requester), CancellationToken.None);
        await again.Should().ThrowAsync<InvalidOperationException>().WithMessage("Only a pending request*");
    }

    [Fact]
    public async Task OnlyEmployeeChangesCanBeWithdrawnHere()
    {
        await using var db = CreateDb();
        var (tenant, adminRole) = await SeedTenantAsync(db);
        var requester = await AddUserAsync(db, tenant, adminRole);
        var approval = new ApprovalRequest
        {
            TenantId = tenant, WorkflowId = Guid.NewGuid(), EntityName = "Timesheet", EntityId = Guid.NewGuid().ToString(),
            Title = "Timesheet", Status = "Pending", CurrentStepOrder = 1, RequestedByUserId = requester,
            CurrentApproverRole = "HR Manager", CurrentApproverType = "Role"
        };
        db.ApprovalRequests.Add(approval);
        await db.SaveChangesAsync();

        var listed = await SingleListedAsync(db, tenant, requester);
        listed.CanWithdraw.Should().BeFalse();
        var act = () => Service(db).WithdrawAsync(tenant, approval.Id, null, Context(tenant, requester), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*its own screen*");
    }

    private static async Task<ApprovalRequestDto> SingleListedAsync(ZayraDbContext db, Guid tenant, Guid caller)
    {
        var page = await Service(db).GetRequestsAsync(tenant, null, null, "all", 1, 25, Context(tenant, caller), CancellationToken.None);
        page.Items.Should().ContainSingle();
        var listed = page.Items.Single();
        // The detail view must say exactly what the list says.
        var detail = await Service(db).GetRequestAsync(tenant, listed.Id, Context(tenant, caller), CancellationToken.None);
        detail!.DecisionBlockedReason.Should().Be(listed.DecisionBlockedReason);
        detail.CanWithdraw.Should().Be(listed.CanWithdraw);
        detail.ChangeSummary.Should().Be(listed.ChangeSummary);
        return listed;
    }

    private static object? Prop(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value);

    private static async Task<object> SubmitAsync(ZayraDbContext db, Guid tenant, Guid user, int employeeId, string field, object value)
    {
        var result = await CreateController(db, tenant, user).UpdateEmployee(
            employeeId,
            new EmployeeUpdateRequest(Today, new() { [field] = JsonSerializer.SerializeToElement(value) }),
            CancellationToken.None);
        return result.Should().BeOfType<AcceptedResult>().Subject.Value!;
    }

    private static ApprovalWorkflowService Service(ZayraDbContext db) => new(db, new AuditService(db));

    private static RequestContext Context(Guid tenant, Guid user) =>
        new("127.0.0.1", "tests", user, tenant, ["Admin"], ["approvals.read", "approvals.decide", "approvals.override"]);

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<(Guid Tenant, Guid AdminRole)> SeedTenantAsync(ZayraDbContext db)
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Solo Admin Co", Slug = $"solo-{Guid.NewGuid():N}" };
        var admin = new Role { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Admin", NormalizedName = "ADMIN", Description = "Admin" };
        var overridePermission = new Permission { Key = "approvals.override", Module = "Approvals", Description = "Override" };
        db.Tenants.Add(tenant);
        db.Roles.Add(admin);
        db.Permissions.Add(overridePermission);
        db.RolePermissions.Add(new RolePermission { RoleId = admin.Id, PermissionId = overridePermission.Id });
        await db.SaveChangesAsync();
        return (tenant.Id, admin.Id);
    }

    private static async Task<Guid> AddUserAsync(ZayraDbContext db, Guid tenant, Guid roleId, bool isActive = true)
    {
        var user = new User { TenantId = tenant, Email = $"{Guid.NewGuid():N}@example.test", FullName = "Admin", IsActive = isActive };
        user.NormalizedEmail = user.Email.ToUpperInvariant();
        db.Users.Add(user);
        db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = roleId });
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<Employee> AddEmployeeAsync(ZayraDbContext db, Guid tenant)
    {
        var employee = new Employee
        {
            TenantId = tenant, EmployeeCode = "EMP-SA-ADMIN-0001", FullName = "Solo Admin", Status = "Active",
            JoiningDate = DateTime.UtcNow.Date, Salary = 10000m
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        return employee;
    }

    private static EmployeesController CreateController(ZayraDbContext db, Guid tenantId, Guid userId)
    {
        var audit = new AuditService(db);
        var controller = new EmployeesController(
            db,
            new Pbkdf2PasswordHasher(),
            audit,
            new NullDocumentStorage(),
            TestNotifications.For(db),
            new SelfApprovalFakeHijri(),
            new Zayra.Api.Infrastructure.Common.DataScopeService(db),
            new SelfApprovalFakeLetters(),
            new ApprovalWorkflowService(db, audit));
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Role, "Admin"),
            new Claim("permission", "employees.read"),
            new Claim("permission", "employees.write"),
            new Claim("permission", "employees.sensitive")
        }, "Test"));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
        return controller;
    }
}

file sealed class SelfApprovalFakeHijri : Zayra.Api.Infrastructure.Localization.IHijriDateService
{
    public Zayra.Api.Infrastructure.Localization.DateConversionDto FromGregorian(DateOnly date) => new(date.ToString("yyyy-MM-dd"), "1447-01-01", 1447, 1, 1);
}

file sealed class SelfApprovalFakeLetters : ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
}
