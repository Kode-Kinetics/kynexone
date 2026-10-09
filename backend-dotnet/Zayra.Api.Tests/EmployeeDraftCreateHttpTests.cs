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
using Zayra.Api.Application.Employees;
using Zayra.Api.Models;
using Zayra.Api.Tests.Security;

namespace Zayra.Api.Tests;

// Exercise JSON binding, automatic model validation, effective permissions, and the real create
// service together: direct controller/service calls would miss the original [Required] failure.
public class EmployeeDraftCreateHttpTests : IClassFixture<EmployeeDraftCreateHttpFixture>
{
    private readonly EmployeeDraftCreateHttpFixture _fx;
    public EmployeeDraftCreateHttpTests(EmployeeDraftCreateHttpFixture fixture) => _fx = fixture;

    [Fact]
    public async Task NameOnlyCreatesPersistedDraftWithGeneratedCodeAndExistingReadinessGates()
    {
        using var created = await _fx.SendAsync(HttpMethod.Post, "/api/employees", new { englishName = "Minimum Draft" });
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetInt32();
        body.GetProperty("status").GetString().Should().Be("Draft");
        body.GetProperty("employeeCode").GetString().Should().NotBeNullOrWhiteSpace();
        body.GetProperty("gender").GetString().Should().BeEmpty();

        using var readiness = await _fx.SendAsync(HttpMethod.Get, $"/api/employees/{id}/readiness");
        readiness.StatusCode.Should().Be(HttpStatusCode.OK, await readiness.Content.ReadAsStringAsync());
        var check = await readiness.Content.ReadFromJsonAsync<JsonElement>();
        check.GetProperty("blocking").EnumerateArray().Select(item => item.GetProperty("key").GetString())
            .Should().Contain(["Gender", "IqamaNumber"]);
        check.GetProperty("payBlocking").EnumerateArray().Select(item => item.GetProperty("key").GetString())
            .Should().Contain("GosiReference");

        using var activate = await _fx.SendAsync(HttpMethod.Patch, $"/api/employees/{id}/status",
            new { status = "Active", effectiveDate = "2026-01-01", reason = "Readiness must still apply" });
        activate.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, await activate.Content.ReadAsStringAsync());
        (await activate.Content.ReadAsStringAsync()).Should().Contain("employee_not_activatable");
        using var scope = _fx.Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var saved = await db.Employees.IgnoreQueryFilters().SingleAsync(e => e.Id == id);
        saved.TenantId.Should().Be(_fx.TenantId);
        saved.CompanyId.Should().Be(_fx.CompanyId);
        saved.Status.Should().Be("Draft");
        saved.Gender.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExplicitBlankGenderCanBeCompletedLater(string? gender)
    {
        using var response = await _fx.SendAsync(HttpMethod.Post, "/api/employees",
            new { englishName = $"Blank Gender {Guid.NewGuid():N}", gender });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("gender").GetString().Should().BeEmpty();
        body.GetProperty("status").GetString().Should().Be("Draft");
    }

    [Theory]
    [InlineData("englishName", "")]
    [InlineData("englishName", "   ")]
    [InlineData("personalEmail", "not-an-email")]
    [InlineData("dateOfBirth", "not-a-date")]
    [InlineData("gender", "This value is longer than the allowed forty characters")]
    public async Task SuppliedMalformedValuesStillFailValidationWithoutCreatingARecord(string field, string value)
    {
        var before = await _fx.EmployeeCountAsync();
        using var response = await _fx.SendAsync(HttpMethod.Post, "/api/employees",
            new Dictionary<string, object?> { ["englishName"] = "Invalid Draft", [field] = value });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("errors", out _).Should().BeTrue("MVC should return its field validation errors");
        (await _fx.EmployeeCountAsync()).Should().Be(before);
    }

    [Fact]
    public async Task SuppliedInvalidIbanStillFailsBusinessValidationWithoutCreatingARecord()
    {
        var before = await _fx.EmployeeCountAsync();
        using var response = await _fx.SendAsync(HttpMethod.Post, "/api/employees",
            new { englishName = "Invalid Bank Draft", payrollProfile = new { iban = "SA123" } });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        (await _fx.EmployeeCountAsync()).Should().Be(before);
    }

    [Theory]
    [InlineData(false, HttpStatusCode.Unauthorized)]
    [InlineData(true, HttpStatusCode.Forbidden)]
    public async Task MinimumDraftStillRequiresAuthenticatedEmployeeWritePermission(bool authenticated, HttpStatusCode expected)
    {
        var before = await _fx.EmployeeCountAsync();
        using var response = await _fx.SendAsync(HttpMethod.Post, "/api/employees",
            new { englishName = "Unauthorized Draft" }, authenticated ? _fx.ReaderToken : null, useWriter: false);
        response.StatusCode.Should().Be(expected, await response.Content.ReadAsStringAsync());
        (await _fx.EmployeeCountAsync()).Should().Be(before);
    }
}

public sealed class EmployeeDraftCreateHttpFixture : IAsyncLifetime
{
    private SqliteConnection _anchor = null!;
    private HttpClient _client = null!;
    private string _writerToken = null!;
    public AuthorizationPipelineHost Host { get; private set; } = null!;
    public Guid TenantId { get; private set; }
    public Guid CompanyId { get; private set; }
    public string ReaderToken { get; private set; } = null!;
    public string EditorToken { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connection = $"Data Source=file:employee-draft-create-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _anchor = new SqliteConnection(connection);
        await _anchor.OpenAsync();
        Host = new AuthorizationPipelineHost(connection);
        _client = Host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using (var scope = Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            await db.Database.EnsureCreatedAsync();
            var tenant = new Tenant { Name = "Draft create HTTP", Slug = $"draft-create-{Guid.NewGuid():N}", IsActive = true };
            db.Tenants.Add(tenant);
            TenantId = tenant.Id;
            var company = new Company { TenantId = TenantId, LegalNameEn = "Draft Company", TradeName = "Draft Company",
                CountryCode = "SA", IsActive = true, DefaultCurrency = "SAR" };
            db.Companies.Add(company);
            CompanyId = company.Id;
            db.TenantSubscriptions.Add(new TenantSubscription { TenantId = TenantId, Plan = "Enterprise", Status = "Active", MaxEmployees = 1000 });
            db.CompanyComplianceProfiles.Add(new CompanyComplianceProfile
            {
                TenantId = TenantId, CompanyId = CompanyId, CountryCode = "SA", Status = "Active",
                EffectiveFrom = new DateOnly(2020, 1, 1),
                RequiredFieldsJson = """[{"key":"Gender","failClosed":true,"gate":"activate"}]""",
            });
            var role = new Role { TenantId = TenantId, Name = "HR Officer", NormalizedName = "HR OFFICER" };
            db.Roles.Add(role);
            var writer = User("writer");
            var reader = User("reader");
            var editor = User("editor");
            db.Users.AddRange(writer, reader, editor);
            db.Set<UserRole>().Add(new UserRole { UserId = writer.Id, RoleId = role.Id });
            db.Set<UserRole>().Add(new UserRole { UserId = editor.Id, RoleId = role.Id });
            db.Set<UserPermissionOverride>().AddRange(
                Grant(writer.Id, "employees.write"), Grant(writer.Id, "employees.read"), Grant(writer.Id, "employees.sensitive"),
                Grant(editor.Id, "employees.write"), Grant(editor.Id, "employees.read"), Grant(reader.Id, "employees.read"));
            await db.SaveChangesAsync();
        }
        using (var scope = Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            var tokens = scope.ServiceProvider.GetRequiredService<ITokenService>();
            _writerToken = await MintAsync(db, tokens, "writer");
            ReaderToken = await MintAsync(db, tokens, "reader");
            EditorToken = await MintAsync(db, tokens, "editor");
        }
    }

    private User User(string name) => new()
    {
        TenantId = TenantId, Email = $"{name}@draft-create.local", NormalizedEmail = $"{name}@draft-create.local".ToUpperInvariant(),
        FullName = name, PasswordHash = "no-login-path", Status = "Active", AccessMode = "FullPortal",
        IsActive = true, IsEmailConfirmed = true, IsGroupScope = true,
    };

    private UserPermissionOverride Grant(Guid userId, string permission) => new()
    {
        TenantId = TenantId, UserId = userId, PermissionKey = permission, Effect = "Allow", IsActive = true, Reason = "HTTP create regression",
    };

    private async Task<string> MintAsync(ZayraDbContext db, ITokenService tokens, string name)
    {
        var user = await db.Users.IgnoreQueryFilters().AsNoTracking()
            .Include(u => u.Tenant)
            .Include(u => u.UserRoles).ThenInclude(r => r.Role).ThenInclude(r => r!.RolePermissions).ThenInclude(p => p.Permission)
            .Include(u => u.PermissionOverrides).Include(u => u.EmployeeUserAccounts).Include(u => u.EntityAccesses)
            .SingleAsync(u => u.TenantId == TenantId && u.Email == $"{name}@draft-create.local");
        var grants = Array.Empty<EntityAccessGrant>();
        var entityScope = EntityScopeClaims.Resolve(user.IsGroupScope, grants, Array.Empty<Guid>());
        return tokens.CreateAccessToken(user, AuthService.GetRoles(user), AuthService.GetPermissions(user), user.Tenant!, grants, entityScope, out _);
    }

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null, string? token = null, bool useWriter = true)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        var accessToken = useWriter ? _writerToken : token;
        if (accessToken is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return _client.SendAsync(request);
    }

    public async Task<int> EmployeeCountAsync()
    {
        using var scope = Host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ZayraDbContext>().Employees.IgnoreQueryFilters().CountAsync(e => e.TenantId == TenantId);
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (Host is not null) await Host.DisposeAsync();
        if (_anchor is not null) await _anchor.DisposeAsync();
    }
}
