using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.WorkWeek;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// The cancellation of APPROVED leave, end to end through the real Program.cs pipeline: the employee asks, the
/// line manager is told and finds it in the approvals queue, decides it, and the employee is told and can see the
/// outcome on the leave. Before, the request sat "CancellationRequested" for ever: nobody was told and no screen
/// called cancel-approve / cancel-reject.
/// </summary>
public sealed class LeaveCancellationHttpTests(LeaveCancellationHttpFixture f) : IClassFixture<LeaveCancellationHttpFixture>
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed record Bell(Guid Id, string Title, string? EntityId);

    private async Task<List<Bell>> BellAsync(string who)
    {
        using var response = await f.Send(HttpMethod.Get, "/api/notifications", f.Token(who));
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        return JsonSerializer.Deserialize<List<Bell>>(text, Web)!;
    }

    private async Task<string> ApprovedLeaveAsync()
    {
        var day = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(14));
        while (WorkWeekConfig.GccDefault.IsWeekend(day.DayOfWeek)) day = day.AddDays(1);
        using var submit = await f.Send(HttpMethod.Post, "/api/leave/requests", f.Token("employee"), new
        {
            employeeId = f.EmployeeId, leaveTypeId = f.LeaveTypeId, policyId = (Guid?)null,
            startDate = day.ToString("yyyy-MM-dd"), endDate = day.ToString("yyyy-MM-dd"),
            dayType = "Full", reason = "Family", isEmergency = false,
        });
        var created = await submit.Content.ReadAsStringAsync();
        submit.StatusCode.Should().Be(HttpStatusCode.Created, created);
        var id = JsonDocument.Parse(created).RootElement.GetProperty("id").GetString()!;
        using var approve = await f.Send(HttpMethod.Post, $"/api/leave/requests/{id}/approve", f.Token("manager"), new { notes = "ok" });
        approve.StatusCode.Should().Be(HttpStatusCode.OK, await approve.Content.ReadAsStringAsync());
        return id;
    }

    [Fact]
    public async Task A_cancellation_of_approved_leave_reaches_the_manager_the_queue_and_back_to_the_employee()
    {
        var id = await ApprovedLeaveAsync();

        using var ask = await f.Send(HttpMethod.Post, $"/api/leave/requests/{id}/cancel", f.Token("employee"), new { reason = "Plans changed" });
        ask.StatusCode.Should().Be(HttpStatusCode.Accepted, await ask.Content.ReadAsStringAsync());

        // Told: the line manager, and nobody who may not decide it.
        (await BellAsync("manager")).Should().Contain(n => n.EntityId == id && n.Title == "Leave Cancellation Requested");
        (await BellAsync("bystander")).Should().NotContain(n => n.EntityId == id);
        (await BellAsync("othermanager")).Should().NotContain(n => n.EntityId == id && n.Title == "Leave Cancellation Requested",
            "another manager's reporting line does not cover this employee");

        // Findable: the manager's approvals queue lists it as a cancellation; an unrelated manager's does not.
        async Task<List<JsonElement>> QueueAsync(string who)
        {
            using var response = await f.Send(HttpMethod.Get, "/api/leave/requests/awaiting-decision", f.Token(who));
            var text = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, text);
            return JsonDocument.Parse(text).RootElement.EnumerateArray().Select(x => x.Clone()).ToList();
        }
        (await QueueAsync("manager")).Should().Contain(x => x.GetProperty("id").GetString() == id && x.GetProperty("kind").GetString() == "Cancellation"
            && x.GetProperty("cancellationReason").GetString() == "Plans changed");
        (await QueueAsync("othermanager")).Should().BeEmpty();
        (await QueueAsync("employee")).Should().BeEmpty();

        // The employee's leave detail names who holds it.
        using (var detail = await f.Send(HttpMethod.Get, $"/api/leave/requests/{id}", f.Token("employee")))
        {
            var json = JsonDocument.Parse(await detail.Content.ReadAsStringAsync()).RootElement.GetProperty("cancellation");
            json.GetProperty("status").GetString().Should().Be("Pending");
            json.GetProperty("awaiting").EnumerateArray().Select(x => x.GetString()).Should().Contain("manager");
        }

        // Decided: Keep leave first (it stays approved and can be asked again), then Approve cancellation.
        using (var keep = await f.Send(HttpMethod.Post, $"/api/leave/requests/{id}/cancel-reject", f.Token("manager"), new { notes = "Needed on site" }))
            keep.StatusCode.Should().Be(HttpStatusCode.OK, await keep.Content.ReadAsStringAsync());
        (await BellAsync("employee")).Should().Contain(n => n.EntityId == id && n.Title == "Leave Cancellation Declined");

        using (var again = await f.Send(HttpMethod.Post, $"/api/leave/requests/{id}/cancel", f.Token("employee"), new { reason = "Please reconsider" }))
            again.StatusCode.Should().Be(HttpStatusCode.Accepted);
        using (var approve = await f.Send(HttpMethod.Post, $"/api/leave/requests/{id}/cancel-approve", f.Token("manager"), new { notes = "fine" }))
            approve.StatusCode.Should().Be(HttpStatusCode.OK, await approve.Content.ReadAsStringAsync());
        (await BellAsync("employee")).Should().Contain(n => n.EntityId == id && n.Title == "Leave Cancellation Approved");
        (await QueueAsync("manager")).Should().NotContain(x => x.GetProperty("id").GetString() == id);
    }
}

public sealed class LeaveCancellationHttpFixture : IAsyncLifetime
{
    private SqliteConnection _anchor = null!;
    private readonly Dictionary<string, string> _tokens = new();

    public AuthorizationPipelineHost Host { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public Guid TenantId { get; } = Guid.NewGuid();
    public Guid CompanyId { get; } = Guid.NewGuid();
    public Guid LeaveTypeId { get; private set; }
    public int EmployeeId { get; private set; }

    public string Token(string who) => _tokens[who];

    public async Task InitializeAsync()
    {
        var connection = $"Data Source=file:leave-cancellation-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _anchor = new SqliteConnection(connection);
        await _anchor.OpenAsync();
        Host = new AuthorizationPipelineHost(connection);
        Client = Host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using (var scope = Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.Tenants.Add(new Tenant { Id = TenantId, Name = "Leave cancellation", Slug = $"lc-{TenantId:N}", IsActive = true });
            db.Companies.Add(new Company { Id = CompanyId, TenantId = TenantId, LegalNameEn = "Cancel Co", CountryCode = "SA", IsActive = true });
            await db.SaveChangesAsync();
            await new AuthSeeder(db).EnsureTenantRolesAsync(TenantId, CancellationToken.None);

            var managerEmp = NewEmployee("Mona Manager", null);
            var otherManagerEmp = NewEmployee("Omar Other", null);
            var bystanderEmp = NewEmployee("Ben Bystander", null);
            db.Employees.AddRange(managerEmp, otherManagerEmp, bystanderEmp);
            await db.SaveChangesAsync();
            var employeeEmp = NewEmployee("Cara Colleague", managerEmp.Id);
            db.Employees.Add(employeeEmp);
            await db.SaveChangesAsync();
            EmployeeId = employeeEmp.Id;

            async Task AddUserAsync(string name, string role, Employee? employee, string accessMode, params string[] permissions)
            {
                var email = $"{name}-{TenantId:N}@lc-http.local";
                var mfa = role == "HR Manager";
                var user = new User
                {
                    TenantId = TenantId, Email = email, NormalizedEmail = email.ToUpperInvariant(), FullName = name, PasswordHash = "no-login-path",
                    Status = "Active", AccessMode = accessMode, IsActive = true, IsEmailConfirmed = true, IsGroupScope = true,
                    MFAEnabled = mfa, MfaSecretEncrypted = mfa ? "enrolled-for-test" : null,
                };
                db.Users.Add(user);
                var roleRow = await db.Roles.SingleAsync(r => r.TenantId == TenantId && r.Name == role);
                db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = roleRow.Id });
                db.Set<UserPermissionOverride>().AddRange(permissions.Select(p => new UserPermissionOverride
                {
                    TenantId = TenantId, UserId = user.Id, PermissionKey = p, Effect = "Allow", IsActive = true, Reason = "leave cancellation test",
                }));
                if (employee is not null)
                {
                    employee.UserAccountId = user.Id;
                    db.Set<EmployeeUserAccount>().Add(new EmployeeUserAccount
                    {
                        TenantId = TenantId, EmployeeId = employee.Id, UserId = user.Id, AccessMode = accessMode, Status = "Active", RequiresPasswordSetup = false,
                    });
                }
                await db.SaveChangesAsync();
            }
            string[] manager = ["employees.read", "manager.read", "leave.read", "leave.approve", "approvals.decide", "ess.read", "ess.write"];
            await AddUserAsync("manager", "Manager", managerEmp, "FullPortal", manager);
            await AddUserAsync("othermanager", "Manager", otherManagerEmp, "FullPortal", manager);
            await AddUserAsync("employee", "Employee", employeeEmp, AccessModes.EssOnly, "ess.read", "ess.write", "leave.read");
            await AddUserAsync("bystander", "Employee", bystanderEmp, AccessModes.EssOnly, "ess.read", "ess.write");

            var leaveType = new LeaveType { TenantId = TenantId, Code = "AL", NameEn = "Annual Leave", IsActive = true, IsPaid = true };
            db.LeaveTypes.Add(leaveType);
            await db.SaveChangesAsync();
            LeaveTypeId = leaveType.Id;
            foreach (var year in new[] { DateTime.UtcNow.Year, DateTime.UtcNow.Year + 1 })
                db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
                {
                    TenantId = TenantId, EmployeeId = employeeEmp.Id, EmployeeName = employeeEmp.FullName,
                    LeaveTypeId = leaveType.Id, LeaveTypeName = leaveType.NameEn, Year = year, Entitled = 21,
                });
            await db.SaveChangesAsync();
            // Leave is decided by the employee's direct manager (one final step).
            await TestApprovalConfig.EnsureDefaultLeaveWorkflowAsync(db, TenantId, approverType: "Manager");
        }

        foreach (var name in new[] { "manager", "othermanager", "employee", "bystander" })
            _tokens[name] = await MintAsync(name);
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (Host is not null) await Host.DisposeAsync();
        if (_anchor is not null) await _anchor.DisposeAsync();
    }

    public async Task<HttpResponseMessage> Send(HttpMethod method, string path, string? bearer, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await Client.SendAsync(request);
    }

    private Employee NewEmployee(string name, int? manager) => new()
    {
        TenantId = TenantId, EmployeeCode = $"LC-{Guid.NewGuid():N}"[..12], FullName = name, EnglishName = name,
        Status = EmployeeStatuses.Active, CompanyId = CompanyId, ManagerEmployeeId = manager,
        EmploymentType = "Full Time", Nationality = "Saudi", JoiningDate = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private async Task<string> MintAsync(string name)
    {
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var email = $"{name}-{TenantId:N}@lc-http.local";
        var user = await db.Users.IgnoreQueryFilters().AsNoTracking().Include(u => u.Tenant)
            .Include(u => u.UserRoles).ThenInclude(r => r.Role).ThenInclude(r => r!.RolePermissions).ThenInclude(p => p.Permission)
            .Include(u => u.PermissionOverrides).Include(u => u.EmployeeUserAccounts).Include(u => u.EntityAccesses)
            .SingleAsync(u => u.TenantId == TenantId && u.Email == email);
        return scope.ServiceProvider.GetRequiredService<ITokenService>().CreateAccessToken(user, AuthService.GetRoles(user),
            AuthService.GetPermissions(user), user.Tenant!, Array.Empty<EntityAccessGrant>(),
            EntityScopeClaims.Resolve(true, Array.Empty<EntityAccessGrant>(), Array.Empty<Guid>()), out _);
    }
}
