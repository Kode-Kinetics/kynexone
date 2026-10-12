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
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Succession;
using Zayra.Api.Models;
using Zayra.Api.Tests.Security;

namespace Zayra.Api.Tests.Succession;

/// <summary>
/// Succession through the REAL Program.cs pipeline (AuthorizationPipelineHost, F9): [HasPermission] any-of semantics on
/// every endpoint, the opt-in flag guard, the error mapping, the masked nominator response, and the audit-log viewer as
/// Admin showing succession rows with ids only. The Postgres suites cover behaviour; this one covers the wiring.
/// </summary>
public sealed class SuccessionHttpTests(SuccessionHttpFixture f) : IClassFixture<SuccessionHttpFixture>
{
    private const string Missing = "00000000-0000-0000-0000-00000000abcd";

    public static TheoryData<string, string, string, HttpStatusCode> Matrix => new()
    {
        { "GET", "/api/succession/positions", "anonymous", HttpStatusCode.Unauthorized },
        { "GET", "/api/succession/positions", "plain", HttpStatusCode.Forbidden },
        { "GET", "/api/succession/positions", "admin", HttpStatusCode.OK },
        { "GET", "/api/succession/pools", "admin", HttpStatusCode.OK },
        { "GET", "/api/succession/nominations", "admin", HttpStatusCode.Forbidden },
        { "GET", "/api/succession/nine-box", "admin", HttpStatusCode.Forbidden },
        { "POST", "/api/succession/assessments", "admin", HttpStatusCode.Forbidden },
        { "POST", "/api/succession/nominations", "admin", HttpStatusCode.Forbidden },
        { "GET", "/api/succession/reports/summary", "admin", HttpStatusCode.Forbidden },
        { "GET", "/api/succession/development-plans", "admin", HttpStatusCode.Forbidden },
        { "GET", "/api/ess/development-plan", "admin", HttpStatusCode.Forbidden },
        { "GET", "/api/succession/positions", "employee", HttpStatusCode.Forbidden },
        { "GET", "/api/succession/positions/mine", "employee", HttpStatusCode.OK },
        { "GET", $"/api/succession/positions/{Missing}", "employee", HttpStatusCode.NotFound },
        { "GET", "/api/ess/development-plan", "employee", HttpStatusCode.OK },
        { "GET", "/api/ess/development-plan", "plain", HttpStatusCode.Forbidden },
        { "GET", "/api/succession/nomination-targets", "manager", HttpStatusCode.OK },
        { "GET", "/api/succession/nominations", "manager", HttpStatusCode.Forbidden },
        { "GET", "/api/succession/nine-box", "manager", HttpStatusCode.Forbidden },
        { "GET", "/api/succession/development-plans", "manager", HttpStatusCode.OK },
        { "GET", "/api/succession/nine-box", "hr", HttpStatusCode.OK },
        { "GET", "/api/succession/reports/summary", "hr", HttpStatusCode.OK },
        { "GET", $"/api/succession/slates/{Missing}", "hr", HttpStatusCode.NotFound },
        { "GET", $"/api/succession/slates/{Missing}", "employee", HttpStatusCode.NotFound },
        { "GET", $"/api/succession/slates/{Missing}", "admin", HttpStatusCode.NotFound },
        { "GET", $"/api/succession/slates/{Missing}", "plain", HttpStatusCode.Forbidden },
        { "PUT", $"/api/succession/slates/{Missing}", "admin", HttpStatusCode.Forbidden },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task EveryEndpoint_AnswersOnlyToItsPermissions_AnyOfThem(string method, string path, string who, HttpStatusCode expected)
    {
        using var response = await f.Send(new HttpMethod(method), path, f.Token(who), method is "POST" or "PUT" ? new { } : null);
        response.StatusCode.Should().Be(expected, $"{who} {method} {path}: {await response.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task ATenantWithoutTheOptIn_IsRefusedEverywhere()
    {
        foreach (var path in new[] { "/api/succession/positions", "/api/succession/reports/summary", "/api/ess/development-plan" })
        {
            using var response = await f.Send(HttpMethod.Get, path, f.Token("hrB"));
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, path);
            (await Json(response)).GetProperty("error").GetString().Should().Be("feature_not_enabled");
        }
    }

    [Fact]
    public async Task Errors_MapToStableCodes()
    {
        using (var missing = await f.Send(HttpMethod.Get, $"/api/succession/nominations/{Missing}", f.Token("hr")))
        {
            missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await Json(missing)).GetProperty("error").GetString().Should().Be("not_found");
        }
        using (var invalid = await f.Send(HttpMethod.Post, "/api/succession/nominations", f.Token("hr"), new { employeeId = f.EmployeeId }))
        {
            invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest, await invalid.Content.ReadAsStringAsync());
            (await Json(invalid)).GetProperty("error").GetString().Should().Be("invalid_request");
        }
        await f.DesignateAsync();
        using var twice = await f.Send(HttpMethod.Post, "/api/succession/positions", f.Token("hr"), f.DesignateBody());
        twice.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Json(twice)).GetProperty("error").GetString().Should().Be("already_critical");
    }

    [Fact]
    public async Task TheManagersNominationResponseIsMasked_AndTheAuditViewerAsAdminShowsIdsOnly()
    {
        var pool = await f.PoolAsync();
        using var nominate = await f.Send(HttpMethod.Post, "/api/succession/nominations", f.Token("manager"),
            new { employeeId = f.EmployeeId, talentPoolId = pool, evidence = "Ran the regional close this year without a single late entry." });
        var text = await nominate.Content.ReadAsStringAsync();
        nominate.StatusCode.Should().Be(HttpStatusCode.OK, text);
        text.Should().Contain("\"stage\"").And.NotContain("currentAssessment").And.NotContain("readyNowOnApprovedSlates").And.NotContain("readiness");

        using var logs = await f.Send(HttpMethod.Get, "/api/audit-logs?limit=500", f.Token("admin"));
        var body = await logs.Content.ReadAsStringAsync();
        logs.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var rows = JsonDocument.Parse(body).RootElement.EnumerateArray()
            .Where(r => r.GetProperty("action").GetString()!.StartsWith("succession.", StringComparison.Ordinal)).ToList();
        rows.Should().Contain(r => r.GetProperty("action").GetString() == "succession.nomination.created");
        var metadata = string.Join("\n", rows.Select(r => r.GetProperty("metadata").ToString()));
        metadata.Should().NotContain("mployeeId").And.NotContain(f.EmployeeName).And.NotContain("evidence").And.NotContain("readiness");
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
}

public sealed class SuccessionHttpFixture : IAsyncLifetime
{
    private SqliteConnection _anchor = null!;
    private readonly Dictionary<string, string> _tokens = new();

    public AuthorizationPipelineHost Host { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public Guid TenantA { get; } = Guid.NewGuid();
    public Guid TenantB { get; } = Guid.NewGuid();
    public Guid CompanyA { get; } = Guid.NewGuid();
    public Guid PositionId { get; } = Guid.NewGuid();
    public int EmployeeId { get; private set; }
    public string EmployeeName => "Salma Successor";

    private static readonly string[] Hr = [.. SuccessionPermissions.All, "employees.read", "employees.write"];

    public async Task InitializeAsync()
    {
        var connection = $"Data Source=file:succession-http-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _anchor = new SqliteConnection(connection);
        await _anchor.OpenAsync();
        Host = new AuthorizationPipelineHost(connection);
        Client = Host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using (var scope = Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.Tenants.Add(new Tenant { Id = TenantA, Name = "Succession A", Slug = $"suc-a-{TenantA:N}", IsActive = true });
            db.Tenants.Add(new Tenant { Id = TenantB, Name = "Succession B", Slug = $"suc-b-{TenantB:N}", IsActive = true });
            db.Companies.Add(new Company { Id = CompanyA, TenantId = TenantA, LegalNameEn = "Succession Co", CountryCode = "SA", IsActive = true });
            db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = TenantA, FeatureKey = FeatureKeys.Succession, IsEnabled = true });
            db.Positions.Add(new Position { Id = PositionId, TenantId = TenantA, CompanyId = CompanyA, Code = "CFO", Title = "Chief Financial Officer" });
            var managerEmployee = NewEmployee("Mona Manager", null);
            var hrEmployee = NewEmployee("Huda HR", null); // HR reads candidates as an employee: conflicts are checked against it
            db.Employees.AddRange(managerEmployee, hrEmployee);
            await db.SaveChangesAsync();
            var employee = NewEmployee(EmployeeName, managerEmployee.Id);
            db.Employees.Add(employee);
            var adminRole = new Role { TenantId = TenantA, Name = "Admin", NormalizedName = "ADMIN", Description = "Admin" };
            db.Roles.Add(adminRole);
            var users = new Dictionary<string, (User User, string[] Permissions)>
            {
                ["hr"] = (NewUser(TenantA, "hr", mfa: true), Hr),
                ["admin"] = (NewUser(TenantA, "admin", mfa: true), [SuccessionPermissions.Read, SuccessionPermissions.PositionsManage,
                    SuccessionPermissions.PoolsManage, "approvals.decide", "audit.read", "security.manage", "employees.read", "employees.write"]),
                ["manager"] = (NewUser(TenantA, "manager", mfa: false), ["employees.read", "manager.read", "succession.nominate",
                    "succession.devplans.read", "succession.devplans.manage", "succession.self"]),
                ["employee"] = (NewUser(TenantA, "employee", mfa: false), ["ess.read", "ess.write", "succession.self"]),
                ["plain"] = (NewUser(TenantA, "plain", mfa: false), ["employees.read"]),
                ["hrB"] = (NewUser(TenantB, "hrB", mfa: true), Hr.Append("ess.read").ToArray()),
            };
            db.Users.AddRange(users.Values.Select(u => u.User));
            await db.SaveChangesAsync();
            EmployeeId = employee.Id;
            db.UserRoles.Add(new UserRole { UserId = users["admin"].User.Id, RoleId = adminRole.Id });
            foreach (var (user, permissions) in users.Values)
                db.Set<UserPermissionOverride>().AddRange(permissions.Select(p => new UserPermissionOverride
                {
                    TenantId = user.TenantId, UserId = user.Id, PermissionKey = p, Effect = "Allow", IsActive = true, Reason = "succession http test",
                }));
            db.Set<EmployeeUserAccount>().AddRange(
                new EmployeeUserAccount { TenantId = TenantA, EmployeeId = employee.Id, UserId = users["employee"].User.Id, AccessMode = "FullPortal", Status = "Active", RequiresPasswordSetup = false },
                new EmployeeUserAccount { TenantId = TenantA, EmployeeId = managerEmployee.Id, UserId = users["manager"].User.Id, AccessMode = "FullPortal", Status = "Active", RequiresPasswordSetup = false },
                new EmployeeUserAccount { TenantId = TenantA, EmployeeId = hrEmployee.Id, UserId = users["hr"].User.Id, AccessMode = "FullPortal", Status = "Active", RequiresPasswordSetup = false });
            await db.SaveChangesAsync();
        }
        foreach (var (name, tenant) in new[] { ("hr", TenantA), ("admin", TenantA), ("manager", TenantA), ("employee", TenantA), ("plain", TenantA), ("hrB", TenantB) })
            _tokens[name] = await MintAsync(name, tenant);
    }

    private Employee NewEmployee(string name, int? manager) => new()
    {
        TenantId = TenantA, EmployeeCode = $"SUC-{Guid.NewGuid():N}"[..12], FullName = name, EnglishName = name, Status = EmployeeStatuses.Active,
        CompanyId = CompanyA, ManagerEmployeeId = manager, EmploymentType = "Full Time", Nationality = "Saudi",
        JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    public string? Token(string who) => who == "anonymous" ? null : _tokens[who];

    public object DesignateBody() => new
    {
        positionId = PositionId, riskOfLoss = "High", impactOfVacancy = "Critical", rationale = "Signs the statutory accounts; no cover.",
    };

    private bool _designated;

    public async Task DesignateAsync()
    {
        if (_designated) return;
        using var response = await Send(HttpMethod.Post, "/api/succession/positions", Token("hr"), DesignateBody());
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        _designated = true;
    }

    public async Task<Guid> PoolAsync()
    {
        using var response = await Send(HttpMethod.Post, "/api/succession/pools", Token("hr"), new
        {
            companyId = CompanyA, code = "LEAD" + Guid.NewGuid().ToString("N")[..4], nameEn = "Future leaders", purpose = "Leaders for the next three years.",
        });
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        return JsonDocument.Parse(text).RootElement.GetProperty("id").GetGuid();
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

    private static User NewUser(Guid tenantId, string name, bool mfa) => new()
    {
        TenantId = tenantId, Email = $"{name}-{tenantId:N}@suc-http.local", NormalizedEmail = $"{name}-{tenantId:N}@suc-http.local".ToUpperInvariant(),
        FullName = name, PasswordHash = "no-login-path", Status = "Active", AccessMode = "FullPortal", IsActive = true, IsEmailConfirmed = true,
        IsGroupScope = true, MFAEnabled = mfa, MfaSecretEncrypted = mfa ? "enrolled-for-test" : null,
    };

    private async Task<string> MintAsync(string name, Guid tenantId)
    {
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var email = $"{name}-{tenantId:N}@suc-http.local";
        var user = await db.Users.IgnoreQueryFilters().AsNoTracking().Include(u => u.Tenant)
            .Include(u => u.UserRoles).ThenInclude(r => r.Role).ThenInclude(r => r!.RolePermissions).ThenInclude(p => p.Permission)
            .Include(u => u.PermissionOverrides).Include(u => u.EmployeeUserAccounts).Include(u => u.EntityAccesses)
            .SingleAsync(u => u.TenantId == tenantId && u.Email == email);
        return scope.ServiceProvider.GetRequiredService<ITokenService>().CreateAccessToken(user, AuthService.GetRoles(user),
            AuthService.GetPermissions(user), user.Tenant!, Array.Empty<EntityAccessGrant>(),
            EntityScopeClaims.Resolve(true, Array.Empty<EntityAccessGrant>(), Array.Empty<Guid>()), out _);
    }
}
