using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;
using Zayra.Api.Tests.Security;

namespace Zayra.Api.Tests;

/// <summary>
/// The overtime role matrix through the REAL HTTP pipeline (routing, JWT, legacy role gates, the permission they resolve to,
/// the controller's own stage rules), for every seeded role, on the four things overtime lets anyone do: file for someone
/// else, approve, detect from attendance, and read the payroll review. SQLite stands in for Postgres.
///
/// <para>These pin what each seeded role can do TODAY. The menu and the page were aligned to them (nothing offered that the
/// API refuses); no permission was granted or loosened. Proposed grants are an owner decision and are not encoded here.</para>
/// </summary>
public sealed class OvertimeRoleMatrixHttpTests(OvertimeRoleMatrixFixture f) : IClassFixture<OvertimeRoleMatrixFixture>
{
    private static readonly DateOnly Day = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-2);

    private async Task<Guid> PendingRequestAsync(string status)
    {
        using var scope = f.Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        // Seeded requests sit on the day BEFORE the filed ones, one 61-minute slot each, so no two windows ever overlap.
        var seededDay = Day.AddDays(-1);
        var start = seededDay.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddMinutes(Interlocked.Increment(ref _seededSlot) * 61);
        var r = new OvertimeRequest
        {
            TenantId = f.TenantId, CompanyId = f.CompanyId, EmployeeId = f.SubjectId, EmployeeName = "Subject", OvertimePolicyId = f.PolicyId,
            WorkDate = seededDay, StartTimeUtc = start, EndTimeUtc = start.AddMinutes(60), RequestedMinutes = 60, Reason = "Close",
            Status = status, CreatedBy = Guid.NewGuid(),
        };
        db.OvertimeRequests.Add(r);
        await db.SaveChangesAsync();
        return r.Id;
    }

    private static int _seededSlot;
    private static int _filedSlot;

    [Theory]
    [InlineData("Employee", HttpStatusCode.Forbidden)]
    [InlineData("Manager", HttpStatusCode.Forbidden)]
    [InlineData("Supervisor", HttpStatusCode.Forbidden)]
    [InlineData("Payroll Officer", HttpStatusCode.Forbidden)]
    [InlineData("HR Officer", HttpStatusCode.Created)]
    [InlineData("HR Manager", HttpStatusCode.Created)]
    [InlineData("Admin", HttpStatusCode.Created)]
    public async Task FilingOvertimeForSomeoneElse(string role, HttpStatusCode expected)
    {
        var start = Day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddMinutes(Interlocked.Increment(ref _filedSlot) * 61);
        using var r = await f.Send(HttpMethod.Post, "/api/overtime/requests", f.Token(role), new
        {
            employeeId = f.SubjectId, workDate = Day.ToString("yyyy-MM-dd"), startTimeUtc = start, endTimeUtc = start.AddMinutes(60),
            source = "Manual", reason = "Stayed late",
        });
        r.StatusCode.Should().Be(expected, role + " " + await r.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("Employee", "PendingManager", HttpStatusCode.Forbidden)]
    [InlineData("Supervisor", "PendingManager", HttpStatusCode.Forbidden)]
    [InlineData("Payroll Officer", "PendingHR", HttpStatusCode.Forbidden)]
    [InlineData("HR Officer", "PendingHR", HttpStatusCode.Forbidden)]
    [InlineData("Manager", "PendingHR", HttpStatusCode.BadRequest)]      // step 2 is HR's
    [InlineData("HR Manager", "PendingManager", HttpStatusCode.OK)]       // forwards to step 2: overtime_hr_approves_manager_step, default on
    [InlineData("HR Manager", "PendingHR", HttpStatusCode.OK)]
    [InlineData("Admin", "PendingManager", HttpStatusCode.OK)]
    public async Task ApprovingOvertime(string role, string stage, HttpStatusCode expected)
    {
        var id = await PendingRequestAsync(stage);
        using var r = await f.Send(HttpMethod.Post, $"/api/overtime/requests/{id}/approve", f.Token(role), new { approvedMinutes = 0, notes = "ok" });
        r.StatusCode.Should().Be(expected, role + " " + await r.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("Employee", HttpStatusCode.Forbidden)]
    [InlineData("Manager", HttpStatusCode.Forbidden)]
    [InlineData("Supervisor", HttpStatusCode.Forbidden)]
    [InlineData("Payroll Officer", HttpStatusCode.Forbidden)]
    [InlineData("HR Officer", HttpStatusCode.OK)]   // detect resolves to overtime.write, which HR Officer holds to file on others' behalf
    [InlineData("HR Manager", HttpStatusCode.OK)]
    [InlineData("Admin", HttpStatusCode.OK)]
    public async Task DetectingOvertimeFromAttendance(string role, HttpStatusCode expected)
    {
        using var r = await f.Send(HttpMethod.Post, "/api/overtime/detect-from-attendance", f.Token(role),
            new { fromDate = Day.ToString("yyyy-MM-dd"), toDate = Day.ToString("yyyy-MM-dd") });
        r.StatusCode.Should().Be(expected, role + " " + await r.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("Employee", HttpStatusCode.Forbidden)]
    [InlineData("Manager", HttpStatusCode.Forbidden)]
    [InlineData("Supervisor", HttpStatusCode.Forbidden)]
    [InlineData("HR Officer", HttpStatusCode.Forbidden)]
    [InlineData("Payroll Officer", HttpStatusCode.OK)]
    [InlineData("Payroll Manager", HttpStatusCode.OK)]
    [InlineData("HR Manager", HttpStatusCode.OK)]
    [InlineData("Admin", HttpStatusCode.OK)]
    public async Task ReadingThePayrollReview(string role, HttpStatusCode expected)
    {
        using var r = await f.Send(HttpMethod.Get, "/api/overtime/payroll-review", f.Token(role));
        r.StatusCode.Should().Be(expected, role + " " + await r.Content.ReadAsStringAsync());
    }
}

public sealed class OvertimeRoleMatrixFixture : IAsyncLifetime
{
    private SqliteConnection _anchor = null!;
    private readonly Dictionary<string, string> _tokens = new();
    public AuthorizationPipelineHost Host { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public Guid TenantId { get; } = Guid.NewGuid();
    public Guid CompanyId { get; } = Guid.NewGuid();
    public int SubjectId { get; private set; }
    public Guid PolicyId { get; private set; }
    public string Token(string role) => _tokens[role];

    private static readonly string[] Roles =
        { "Admin", "HR Manager", "HR Officer", "Manager", "Supervisor", "Payroll Officer", "Payroll Manager", "Employee" };

    public async Task InitializeAsync()
    {
        var connection = $"Data Source=file:ot-matrix-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _anchor = new SqliteConnection(connection);
        await _anchor.OpenAsync();
        Host = new AuthorizationPipelineHost(connection);
        Client = Host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        await db.Database.EnsureCreatedAsync();
        db.Tenants.Add(new Tenant { Id = TenantId, Name = "Overtime", Slug = $"ot-{TenantId:N}", IsActive = true });
        db.Companies.Add(new Company { Id = CompanyId, TenantId = TenantId, LegalNameEn = "OT Co", CountryCode = "SA", DefaultCurrency = "SAR", IsActive = true });
        var subject = new Employee
        {
            TenantId = TenantId, CompanyId = CompanyId, EmployeeCode = "OT-1", FullName = "Subject", EnglishName = "Subject",
            Status = EmployeeStatuses.Active, JoiningDate = DateTime.UtcNow.AddYears(-2), Salary = 12_000m,
        };
        db.Employees.Add(subject);
        var policy = new OvertimePolicy
        {
            TenantId = TenantId, Code = "STD", Name = "Standard", IsActive = true, RoundingRule = "None",
            MinimumMinutes = 30, MaximumMinutesPerDay = 600, MonthlyCapMinutes = 60_000,
        };
        db.OvertimePolicies.Add(policy);
        await db.SaveChangesAsync();
        SubjectId = subject.Id;
        PolicyId = policy.Id;
        await new AuthSeeder(db).EnsureTenantRolesAsync(TenantId, CancellationToken.None);
        foreach (var role in Roles) await AddUserAsync(db, scope, role);
    }

    private async Task AddUserAsync(ZayraDbContext db, IServiceScope scope, string role)
    {
        var email = $"{role.Replace(' ', '-')}-{TenantId:N}@ot.local";
        var user = new User
        {
            TenantId = TenantId, Email = email, NormalizedEmail = email.ToUpperInvariant(), FullName = role, PasswordHash = "no-login-path",
            Status = "Active", AccessMode = "FullPortal", IsActive = true, IsEmailConfirmed = true, IsGroupScope = true,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var roleRow = await db.Roles.SingleAsync(r => r.TenantId == TenantId && r.Name == role);
        db.Set<UserRole>().Add(new UserRole { UserId = user.Id, RoleId = roleRow.Id });
        await db.SaveChangesAsync();
        var loaded = await db.Users.IgnoreQueryFilters().AsNoTracking().Include(u => u.Tenant)
            .Include(u => u.UserRoles).ThenInclude(r => r.Role).ThenInclude(r => r!.RolePermissions).ThenInclude(p => p.Permission)
            .Include(u => u.PermissionOverrides).Include(u => u.EmployeeUserAccounts).Include(u => u.EntityAccesses)
            .SingleAsync(u => u.Id == user.Id);
        var grants = Array.Empty<EntityAccessGrant>();
        _tokens[role] = scope.ServiceProvider.GetRequiredService<ITokenService>().CreateAccessToken(loaded, AuthService.GetRoles(loaded),
            AuthService.GetPermissions(loaded), loaded.Tenant!, grants, EntityScopeClaims.Resolve(true, grants, Array.Empty<Guid>()), out _);
    }

    public Task<HttpResponseMessage> Send(HttpMethod method, string path, string bearer, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (body is not null) request.Content = JsonContent.Create(body);
        return Client.SendAsync(request);
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (Host is not null) await Host.DisposeAsync();
        if (_anchor is not null) await _anchor.DisposeAsync();
    }
}
