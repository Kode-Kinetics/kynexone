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
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Who reads a notice, through the REAL Program.cs pipeline (AuthorizationPipelineHost). Leave notices used to be
/// written with no user, which made them broadcasts: every login in the tenant, a plain self-service employee
/// included, read a colleague's leave in the bell. Any login could also mark-read or delete anybody's row, and any
/// login could read the leave AI insights, which name employees and their sick-leave patterns.
/// </summary>
public sealed class NotificationAudienceHttpTests(NotificationAudienceHttpFixture f) : IClassFixture<NotificationAudienceHttpFixture>
{
    [Fact]
    public async Task A_colleagues_leave_notice_reaches_the_colleague_and_the_approver_and_not_a_bystander()
    {
        var start = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(14));
        while (WorkWeekConfig.GccDefault.IsWeekend(start.DayOfWeek)) start = start.AddDays(1);

        using var submit = await f.Send(HttpMethod.Post, "/api/leave/requests", f.Token("colleague"), new
        {
            employeeId = f.ColleagueEmployeeId, leaveTypeId = f.LeaveTypeId, policyId = (Guid?)null,
            startDate = start.ToString("yyyy-MM-dd"), endDate = start.ToString("yyyy-MM-dd"),
            dayType = "Full", reason = "Doctor's appointment", isEmergency = false,
        });
        var body = await submit.Content.ReadAsStringAsync();
        submit.StatusCode.Should().Be(HttpStatusCode.Created, body);
        var leaveId = JsonDocument.Parse(body).RootElement.GetProperty("id").GetString();

        (await BellAsync("bystander")).Should().NotContain(n => n.EntityId == leaveId,
            "a plain self-service employee must not read a colleague's leave");
        (await BellAsync("colleague")).Should().Contain(n => n.EntityId == leaveId && n.Title == "New Leave Request");
        (await BellAsync("manager")).Should().Contain(n => n.EntityId == leaveId && n.Title == "New Leave Request",
            "the named approver of the pending step is told");

        using var scope = f.Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        (await db.Notifications.IgnoreQueryFilters().AnyAsync(n => n.EntityId == leaveId && n.UserId == null))
            .Should().BeFalse("no broadcast row is written for a leave request");
    }

    [Fact]
    public async Task A_legacy_broadcast_of_a_person_specific_type_is_not_returned_but_a_genuine_broadcast_is()
    {
        var legacy = await f.SeedNotificationAsync(null, "LeaveRequest", "Legacy leave broadcast");
        var workspace = await f.SeedNotificationAsync(null, "ReportSchedule", "Workspace notice");

        var bell = await BellAsync("bystander");
        bell.Should().NotContain(n => n.Id == legacy);
        bell.Should().Contain(n => n.Id == workspace);
    }

    [Fact]
    public async Task One_user_cannot_mark_read_or_dismiss_another_users_notification_or_a_broadcast()
    {
        var mine = await f.SeedNotificationAsync(f.UserId("colleague"), "PayrollRun", "Your payslip is ready");
        var broadcast = await f.SeedNotificationAsync(null, "ReportSchedule", "Everyone's notice");

        foreach (var id in new[] { mine, broadcast })
        {
            using (var read = await f.Send(HttpMethod.Post, $"/api/notifications/{id}/read", f.Token("bystander")))
                read.StatusCode.Should().Be(HttpStatusCode.NotFound);
            using (var dismiss = await f.Send(HttpMethod.Delete, $"/api/notifications/{id}", f.Token("bystander")))
                dismiss.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        using (var all = await f.Send(HttpMethod.Post, "/api/notifications/read-all", f.Token("bystander")))
            all.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await f.AssertUnreadAsync(mine);
        await f.AssertUnreadAsync(broadcast);

        // The addressee still can.
        using (var own = await f.Send(HttpMethod.Post, $"/api/notifications/{mine}/read", f.Token("colleague")))
            own.StatusCode.Should().Be(HttpStatusCode.NoContent);
        using (var gone = await f.Send(HttpMethod.Delete, $"/api/notifications/{mine}", f.Token("colleague")))
            gone.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Leave_insights_are_refused_to_a_self_service_employee_and_cut_to_a_scoped_readers_reporting_line()
    {
        using (var list = await f.Send(HttpMethod.Get, "/api/leave/ai-insights", f.Token("bystander")))
            list.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using (var ack = await f.Send(HttpMethod.Post, $"/api/leave/ai-insights/{f.ColleagueInsightId}/acknowledge", f.Token("bystander")))
            ack.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var managerSees = await InsightIdsAsync("manager");
        managerSees.Should().Contain(f.ColleagueInsightId).And.NotContain(f.BystanderInsightId,
            "the bystander is outside the manager's reporting line");
        using (var outOfScope = await f.Send(HttpMethod.Post, $"/api/leave/ai-insights/{f.BystanderInsightId}/acknowledge", f.Token("manager")))
            outOfScope.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using (var inScope = await f.Send(HttpMethod.Post, $"/api/leave/ai-insights/{f.ColleagueInsightId}/acknowledge", f.Token("manager")))
            inScope.StatusCode.Should().Be(HttpStatusCode.OK, await inScope.Content.ReadAsStringAsync());

        (await InsightIdsAsync("hr")).Should().Contain(f.ColleagueInsightId).And.Contain(f.BystanderInsightId,
            "an organisation-wide HR reader sees every insight");
    }

    private sealed record BellRow(Guid Id, Guid? UserId, string Title, string EntityName, string? EntityId);

    private async Task<List<BellRow>> BellAsync(string who)
    {
        using var response = await f.Send(HttpMethod.Get, "/api/notifications", f.Token(who));
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        return JsonSerializer.Deserialize<List<BellRow>>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private async Task<List<Guid>> InsightIdsAsync(string who)
    {
        using var response = await f.Send(HttpMethod.Get, "/api/leave/ai-insights?pageSize=100", f.Token(who));
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        return JsonDocument.Parse(text).RootElement.GetProperty("items").EnumerateArray()
            .Select(x => x.GetProperty("id").GetGuid()).ToList();
    }
}

public sealed class NotificationAudienceHttpFixture : IAsyncLifetime
{
    private SqliteConnection _anchor = null!;
    private readonly Dictionary<string, (string Token, Guid UserId)> _users = new();

    public AuthorizationPipelineHost Host { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public Guid TenantId { get; } = Guid.NewGuid();
    public Guid CompanyId { get; } = Guid.NewGuid();
    public Guid LeaveTypeId { get; private set; }
    public int ColleagueEmployeeId { get; private set; }
    public Guid ColleagueInsightId { get; private set; }
    public Guid BystanderInsightId { get; private set; }

    public async Task InitializeAsync()
    {
        var connection = $"Data Source=file:notification-audience-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _anchor = new SqliteConnection(connection);
        await _anchor.OpenAsync();
        Host = new AuthorizationPipelineHost(connection);
        Client = Host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using (var scope = Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.Tenants.Add(new Tenant { Id = TenantId, Name = "Notification audience", Slug = $"notify-{TenantId:N}", IsActive = true });
            db.Companies.Add(new Company { Id = CompanyId, TenantId = TenantId, LegalNameEn = "Audience Co", CountryCode = "SA", IsActive = true });
            await db.SaveChangesAsync();

            var users = new Dictionary<string, (User User, string[] Permissions)>
            {
                ["colleague"] = (NewUser("colleague", AccessModes.EssOnly, mfa: false), []),
                ["bystander"] = (NewUser("bystander", AccessModes.EssOnly, mfa: false), []),
                // A line manager: their own reporting line, and decides its leave.
                ["manager"] = (NewUser("manager", "FullPortal", mfa: false),
                    ["employees.read", "manager.read", "leave.read", "leave.approve", "approvals.decide", "ess.read", "ess.write"]),
                // Organisation-wide HR (employees.write).
                ["hr"] = (NewUser("hr", "FullPortal", mfa: true), ["employees.read", "employees.write", "leave.read", "leave.approve"]),
            };
            db.Users.AddRange(users.Values.Select(u => u.User));
            await db.SaveChangesAsync();
            foreach (var (user, permissions) in users.Values)
                db.Set<UserPermissionOverride>().AddRange(permissions.Select(p => new UserPermissionOverride
                {
                    TenantId = TenantId, UserId = user.Id, PermissionKey = p, Effect = "Allow", IsActive = true, Reason = "notification audience test",
                }));

            var manager = NewEmployee("Mona Manager", null, users["manager"].User.Id);
            var bystander = NewEmployee("Ben Bystander", null, users["bystander"].User.Id);
            db.Employees.AddRange(manager, bystander);
            await db.SaveChangesAsync();
            var colleague = NewEmployee("Cara Colleague", manager.Id, users["colleague"].User.Id);
            db.Employees.Add(colleague);
            await db.SaveChangesAsync();
            ColleagueEmployeeId = colleague.Id;

            db.Set<EmployeeUserAccount>().AddRange(
                Link(colleague.Id, users["colleague"].User.Id, AccessModes.EssOnly),
                Link(bystander.Id, users["bystander"].User.Id, AccessModes.EssOnly),
                Link(manager.Id, users["manager"].User.Id, "FullPortal"));

            var leaveType = new LeaveType { TenantId = TenantId, Code = "AL", NameEn = "Annual Leave", IsActive = true, IsPaid = true };
            db.LeaveTypes.Add(leaveType);
            await db.SaveChangesAsync();
            LeaveTypeId = leaveType.Id;
            foreach (var year in new[] { DateTime.UtcNow.Year, DateTime.UtcNow.Year + 1 })
                db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
                {
                    TenantId = TenantId, EmployeeId = colleague.Id, EmployeeName = colleague.FullName,
                    LeaveTypeId = leaveType.Id, LeaveTypeName = leaveType.NameEn, Year = year, Entitled = 21,
                });

            var colleagueInsight = new LeaveAIInsight
            {
                TenantId = TenantId, InsightType = "AbsencePattern", Severity = "Warning", Title = "Monday/Friday absence pattern detected",
                Summary = $"Employee {colleague.FullName} has taken sick leave on Monday or Friday more than 3 times.",
                AffectedEmployeeId = colleague.Id,
            };
            var bystanderInsight = new LeaveAIInsight
            {
                TenantId = TenantId, InsightType = "AbsencePattern", Severity = "Warning", Title = "Monday/Friday absence pattern detected",
                Summary = $"Employee {bystander.FullName} has taken sick leave on Monday or Friday more than 3 times.",
                AffectedEmployeeId = bystander.Id,
            };
            db.LeaveAIInsights.AddRange(colleagueInsight, bystanderInsight);
            await db.SaveChangesAsync();
            ColleagueInsightId = colleagueInsight.Id;
            BystanderInsightId = bystanderInsight.Id;

            // Leave is decided by the employee's direct manager (a named approver).
            await TestApprovalConfig.EnsureDefaultLeaveWorkflowAsync(db, TenantId, approverType: "Manager");
        }

        foreach (var name in new[] { "colleague", "bystander", "manager", "hr" })
            _users[name] = await MintAsync(name);
    }

    public string Token(string who) => _users[who].Token;
    public Guid UserId(string who) => _users[who].UserId;

    public async Task<Guid> SeedNotificationAsync(Guid? userId, string entityName, string title)
    {
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var row = new Notification
        {
            TenantId = TenantId, UserId = userId, Title = title, Message = title, EntityName = entityName,
            EntityId = Guid.NewGuid().ToString(),
        };
        db.Notifications.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    public async Task AssertUnreadAsync(Guid id)
    {
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var row = await db.Notifications.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(n => n.Id == id);
        row.Should().NotBeNull("another user must not be able to delete it");
        row!.Status.Should().Be("Unread", "another user must not be able to mark it read");
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

    private Employee NewEmployee(string name, int? manager, Guid userId) => new()
    {
        TenantId = TenantId, EmployeeCode = $"AUD-{Guid.NewGuid():N}"[..12], FullName = name, EnglishName = name,
        Status = EmployeeStatuses.Active, CompanyId = CompanyId, ManagerEmployeeId = manager, UserAccountId = userId,
        EmploymentType = "Full Time", Nationality = "Saudi", JoiningDate = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private EmployeeUserAccount Link(int employeeId, Guid userId, string accessMode) => new()
    {
        TenantId = TenantId, EmployeeId = employeeId, UserId = userId, AccessMode = accessMode, Status = "Active", RequiresPasswordSetup = false,
    };

    private User NewUser(string name, string accessMode, bool mfa) => new()
    {
        TenantId = TenantId, Email = $"{name}-{TenantId:N}@notify-http.local", NormalizedEmail = $"{name}-{TenantId:N}@notify-http.local".ToUpperInvariant(),
        FullName = name, PasswordHash = "no-login-path", Status = "Active", AccessMode = accessMode, IsActive = true, IsEmailConfirmed = true,
        IsGroupScope = true, MFAEnabled = mfa, MfaSecretEncrypted = mfa ? "enrolled-for-test" : null,
    };

    private async Task<(string Token, Guid UserId)> MintAsync(string name)
    {
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var email = $"{name}-{TenantId:N}@notify-http.local";
        var user = await db.Users.IgnoreQueryFilters().AsNoTracking().Include(u => u.Tenant)
            .Include(u => u.UserRoles).ThenInclude(r => r.Role).ThenInclude(r => r!.RolePermissions).ThenInclude(p => p.Permission)
            .Include(u => u.PermissionOverrides).Include(u => u.EmployeeUserAccounts).Include(u => u.EntityAccesses)
            .SingleAsync(u => u.TenantId == TenantId && u.Email == email);
        var token = scope.ServiceProvider.GetRequiredService<ITokenService>().CreateAccessToken(user, AuthService.GetRoles(user),
            AuthService.GetPermissions(user), user.Tenant!, Array.Empty<EntityAccessGrant>(),
            EntityScopeClaims.Resolve(true, Array.Empty<EntityAccessGrant>(), Array.Empty<Guid>()), out _);
        return (token, user.Id);
    }
}
