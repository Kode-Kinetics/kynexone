using System.Net.Http.Headers;
using System.Net.Http.Json;
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
/// One tenant behind the real Program.cs pipeline for the leave Batch 1 fixes (withdraw, cancel, pending-status
/// consumers, attachment keys). Each test CLASS that uses it gets its own instance, hence its own tenant.
/// People: employee (reports to manager), peer (an unrelated employee with ess.write), manager, hr (HR Manager), admin.
/// </summary>
public sealed class LeaveBatch1Fixture : IAsyncLifetime
{
    private SqliteConnection _anchor = null!;
    private readonly Dictionary<string, string> _tokens = new();
    private int _dayCursor = 14;
    private readonly SemaphoreSlim _onceGate = new(1, 1);
    private readonly HashSet<string> _done = new(StringComparer.Ordinal);

    /// <summary>
    /// Runs <paramref name="seed"/> once per fixture. xUnit builds a new test-class instance (and calls its InitializeAsync)
    /// for EVERY test, while the class fixture is shared, so seeding from the test class would pile the rows up test after test.
    /// </summary>
    public async Task OnceAsync(string key, Func<Task> seed)
    {
        await _onceGate.WaitAsync();
        try
        {
            if (_done.Contains(key)) return;
            await seed();
            _done.Add(key);
        }
        finally { _onceGate.Release(); }
    }

    public AuthorizationPipelineHost Host { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public Guid TenantId { get; } = Guid.NewGuid();
    public Guid CompanyId { get; } = Guid.NewGuid();
    public Guid LeaveTypeId { get; private set; }
    public int EmployeeId { get; private set; }
    public int PeerId { get; private set; }
    public int ManagerId { get; private set; }

    public string Token(string who) => _tokens[who];

    public async Task InitializeAsync()
    {
        var connection = $"Data Source=file:leave-batch1-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _anchor = new SqliteConnection(connection);
        await _anchor.OpenAsync();
        Host = new AuthorizationPipelineHost(connection);
        Client = Host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

        using (var scope = Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.Tenants.Add(new Tenant { Id = TenantId, Name = "Leave batch 1", Slug = $"lb1-{TenantId:N}", IsActive = true });
            db.Companies.Add(new Company { Id = CompanyId, TenantId = TenantId, LegalNameEn = "Batch Co", CountryCode = "SA", IsActive = true });
            await db.SaveChangesAsync();
            await new AuthSeeder(db).EnsureTenantRolesAsync(TenantId, CancellationToken.None);

            var managerEmp = NewEmployee("Mona Manager", null);
            var peerEmp = NewEmployee("Pia Peer", null);
            db.Employees.AddRange(managerEmp, peerEmp);
            await db.SaveChangesAsync();
            var employeeEmp = NewEmployee("Cara Colleague", managerEmp.Id);
            db.Employees.Add(employeeEmp);
            await db.SaveChangesAsync();
            EmployeeId = employeeEmp.Id;
            PeerId = peerEmp.Id;
            ManagerId = managerEmp.Id;

            async Task AddUserAsync(string name, string role, Employee? employee, string accessMode, params string[] permissions)
            {
                var email = $"{name}-{TenantId:N}@lb1-http.local";
                var mfa = role is "HR Manager" or "Admin";
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
                    TenantId = TenantId, UserId = user.Id, PermissionKey = p, Effect = "Allow", IsActive = true, Reason = "leave batch 1 test",
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
            await AddUserAsync("employee", "Employee", employeeEmp, AccessModes.EssOnly, "ess.read", "ess.write", "leave.read");
            await AddUserAsync("peer", "Employee", peerEmp, AccessModes.EssOnly, "ess.read", "ess.write");
            await AddUserAsync("hr", "HR Manager", null, "FullPortal",
                "employees.read", "leave.read", "leave.write", "leave.approve", "approvals.decide", "ess.read", "ess.write", "reports.read");
            await AddUserAsync("admin", "Admin", null, "FullPortal",
                "employees.read", "leave.read", "leave.write", "leave.approve", "approvals.decide", "reports.read");
            await AddUserAsync("auditor", "Auditor", null, "FullPortal", "employees.read", "leave.read", "reports.read");
            // Files leave on someone's behalf (leave.write, organisation-wide) without being HR or Admin.
            await AddUserAsync("clerk", "Employee", null, "FullPortal", "employees.read", "leave.read", "leave.write", "ess.read", "ess.write");

            var leaveType = new LeaveType { TenantId = TenantId, Code = "AL", NameEn = "Annual Leave", IsActive = true, IsPaid = true };
            db.LeaveTypes.Add(leaveType);
            await db.SaveChangesAsync();
            LeaveTypeId = leaveType.Id;
            foreach (var year in new[] { DateTime.UtcNow.Year, DateTime.UtcNow.Year + 1 })
                db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
                {
                    TenantId = TenantId, EmployeeId = employeeEmp.Id, EmployeeName = employeeEmp.FullName,
                    LeaveTypeId = leaveType.Id, LeaveTypeName = leaveType.NameEn, Year = year, Entitled = 60,
                });
            await db.SaveChangesAsync();
            // Leave is decided by the employee's direct manager (one final step).
            await TestApprovalConfig.EnsureDefaultLeaveWorkflowAsync(db, TenantId, approverType: "Manager");
        }

        foreach (var name in new[] { "manager", "employee", "peer", "hr", "admin", "auditor", "clerk" })
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

    /// <summary>Runs <paramref name="work"/> against a fresh scoped context (the same database the API uses).</summary>
    public async Task<T> WithDb<T>(Func<ZayraDbContext, Task<T>> work)
    {
        using var scope = Host.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<ZayraDbContext>());
    }

    /// <summary>The next working day (never a Gcc rest day), each call a later one, so requests never overlap.</summary>
    public DateOnly NextWorkday()
    {
        while (true)
        {
            var day = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(Interlocked.Increment(ref _dayCursor)));
            if (!WorkWeekConfig.GccDefault.IsWeekend(day.DayOfWeek)) return day;
        }
    }

    /// <summary>The employee submits a one-day annual leave through the API; returns its id.</summary>
    public async Task<Guid> SubmitAsync(string who = "employee")
    {
        var day = NextWorkday();
        using var submit = await Send(HttpMethod.Post, "/api/leave/requests", Token(who), new
        {
            employeeId = EmployeeId, leaveTypeId = LeaveTypeId, policyId = (Guid?)null,
            startDate = day.ToString("yyyy-MM-dd"), endDate = day.ToString("yyyy-MM-dd"),
            dayType = "Full", reason = "Family", isEmergency = false,
        });
        var text = await submit.Content.ReadAsStringAsync();
        if (submit.StatusCode != System.Net.HttpStatusCode.Created) throw new InvalidOperationException($"Submit failed {submit.StatusCode}: {text}");
        return Guid.Parse(System.Text.Json.JsonDocument.Parse(text).RootElement.GetProperty("id").GetString()!);
    }

    /// <summary>Inserts a leave row directly in a given status (the API never writes some of these).</summary>
    public async Task<Guid> SeedLeaveAsync(string status, DateOnly start, DateOnly end, int? employeeId = null, string attachmentPath = "")
    {
        var employee = employeeId ?? EmployeeId;
        return await WithDb(async db =>
        {
            var row = new LeaveRequest
            {
                TenantId = TenantId, CompanyId = CompanyId, EmployeeId = employee, EmployeeName = "Seeded", LeaveTypeId = LeaveTypeId,
                LeaveTypeName = "Annual Leave", StartDate = start, EndDate = end, TotalDays = 1, DayType = "Full", Status = status,
                Reason = $"seed-{status}", AttachmentPath = attachmentPath, SubmittedAtUtc = DateTime.UtcNow,
            };
            db.LeaveRequests.Add(row);
            await db.SaveChangesAsync();
            return row.Id;
        });
    }

    private Employee NewEmployee(string name, int? manager) => new()
    {
        TenantId = TenantId, EmployeeCode = $"LB-{Guid.NewGuid():N}"[..12], FullName = name, EnglishName = name,
        Status = EmployeeStatuses.Active, CompanyId = CompanyId, ManagerEmployeeId = manager,
        EmploymentType = "Full Time", Nationality = "Saudi", JoiningDate = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private async Task<string> MintAsync(string name)
    {
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var email = $"{name}-{TenantId:N}@lb1-http.local";
        var user = await db.Users.IgnoreQueryFilters().AsNoTracking().Include(u => u.Tenant)
            .Include(u => u.UserRoles).ThenInclude(r => r.Role).ThenInclude(r => r!.RolePermissions).ThenInclude(p => p.Permission)
            .Include(u => u.PermissionOverrides).Include(u => u.EmployeeUserAccounts).Include(u => u.EntityAccesses)
            .SingleAsync(u => u.TenantId == TenantId && u.Email == email);
        return scope.ServiceProvider.GetRequiredService<ITokenService>().CreateAccessToken(user, AuthService.GetRoles(user),
            AuthService.GetPermissions(user), user.Tenant!, Array.Empty<EntityAccessGrant>(),
            EntityScopeClaims.Resolve(true, Array.Empty<EntityAccessGrant>(), Array.Empty<Guid>()), out _);
    }
}
