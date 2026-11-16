using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// A real pipeline host (real JWT, role and permission gates, company query filters) over SQLite, with one group tenant, two companies,
/// the seeded tenant roles and named personas. Tests add the rows they need through <see cref="SeedAsync"/> and talk to the API through
/// <see cref="Send"/>, so they exercise the same code a browser does.
/// </summary>
public sealed class LaunchSecurityFixture : IAsyncLifetime
{
    private SqliteConnection _anchor = null!;
    private readonly Dictionary<string, string> _tokens = new();
    private readonly Dictionary<string, Guid> _userIds = new();
    private readonly ErrorCapture _errors = new();

    public AuthorizationPipelineHost Host { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;

    public Guid TenantId { get; } = Guid.NewGuid();
    public Guid OtherTenantId { get; } = Guid.NewGuid();
    public Guid CompanyA { get; } = Guid.NewGuid();
    public Guid CompanyB { get; } = Guid.NewGuid();
    public Guid OtherTenantCompany { get; } = Guid.NewGuid();

    public string Token(string key) => _tokens[key];
    public Guid UserId(string key) => _userIds[key];

    public async Task InitializeAsync()
    {
        var connection = $"Data Source=file:launch-sec-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _anchor = new SqliteConnection(connection);
        await _anchor.OpenAsync();
        Host = new AuthorizationPipelineHost(connection, services => services.AddLogging(b => b.AddProvider(_errors)),
            new Dictionary<string, string?> { ["RateLimit:LoginPermitLimit"] = "1000", ["RateLimit:LoginWindowSeconds"] = "60" });
        Client = Host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        await db.Database.EnsureCreatedAsync();

        db.Tenants.Add(new Tenant { Id = TenantId, Name = "Launch", Slug = $"launch-{TenantId:N}", IsActive = true, AccountType = TenantAccountTypes.Group });
        db.Tenants.Add(new Tenant { Id = OtherTenantId, Name = "Other", Slug = $"other-{OtherTenantId:N}", IsActive = true });
        db.Companies.Add(new Company { Id = CompanyA, TenantId = TenantId, LegalNameEn = "Alpha", CountryCode = "SA", IsActive = true });
        db.Companies.Add(new Company { Id = CompanyB, TenantId = TenantId, LegalNameEn = "Beta", CountryCode = "SA", IsActive = true });
        db.Companies.Add(new Company { Id = OtherTenantCompany, TenantId = OtherTenantId, LegalNameEn = "Gamma", CountryCode = "SA", IsActive = true });
        await db.SaveChangesAsync();
        await new AuthSeeder(db).EnsureTenantRolesAsync(TenantId, CancellationToken.None);

        await AddUserAsync("AdminGroup", "Admin", groupScope: true, null);
        await AddUserAsync("AdminA", "Admin", groupScope: false, CompanyA);
        await AddUserAsync("HrOfficerA", "HR Officer", groupScope: false, CompanyA);
        await AddUserAsync("HrManagerA", "HR Manager", groupScope: false, CompanyA);
        await AddUserAsync("HrManagerGroup", "HR Manager", groupScope: true, null);
        await AddUserAsync("EmployeeA", "Employee", groupScope: false, CompanyA);
    }

    private readonly Dictionary<string, object> _cache = new();
    private readonly SemaphoreSlim _cacheGate = new(1, 1);

    /// <summary>Builds shared seed data once per fixture (the first test to ask pays for it).</summary>
    public async Task<T> OnceAsync<T>(string key, Func<Task<T>> build) where T : notnull
    {
        await _cacheGate.WaitAsync();
        try
        {
            if (_cache.TryGetValue(key, out var known)) return (T)known;
            var made = await build();
            _cache[key] = made;
            return made;
        }
        finally { _cacheGate.Release(); }
    }

    /// <summary>Adds a persona unless it already exists.</summary>
    public async Task EnsureUserAsync(string key, string role, bool groupScope, Guid? company, string[]? allow = null)
    {
        if (!_tokens.ContainsKey(key)) await AddUserAsync(key, role, groupScope, company, allow: allow);
    }

    /// <summary>Runs <paramref name="seed"/> against a fresh scope and saves.</summary>
    public async Task SeedAsync(Func<ZayraDbContext, Task> seed)
    {
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        await seed(db);
        await db.SaveChangesAsync();
    }

    public async Task<T> ReadAsync<T>(Func<ZayraDbContext, Task<T>> read)
    {
        using var scope = Host.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<ZayraDbContext>());
    }

    public async Task AddUserAsync(string key, string role, bool groupScope, Guid? company, int? employeeId = null, string[]? allow = null)
    {
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var email = $"{key.ToLowerInvariant()}-{TenantId:N}@launch.local";
        var user = new User
        {
            TenantId = TenantId, Email = email, NormalizedEmail = email.ToUpperInvariant(), FullName = key, PasswordHash = "no-login-path",
            Status = "Active", AccessMode = "FullPortal", IsActive = true, IsEmailConfirmed = true, IsGroupScope = groupScope,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var roleRow = await db.Roles.SingleAsync(r => r.TenantId == TenantId && r.Name == role);
        db.Set<UserRole>().Add(new UserRole { UserId = user.Id, RoleId = roleRow.Id });
        if (company is Guid c) db.UserEntityAccesses.Add(new UserEntityAccess { TenantId = TenantId, UserId = user.Id, CompanyId = c, Role = "HR", IsActive = true });
        foreach (var permission in allow ?? [])
            db.Set<UserPermissionOverride>().Add(new UserPermissionOverride { TenantId = TenantId, UserId = user.Id, PermissionKey = permission, Effect = "Allow", Reason = "test" });
        if (employeeId is int e)
            db.EmployeeUserAccounts.Add(new EmployeeUserAccount { TenantId = TenantId, EmployeeId = e, UserId = user.Id, AccessMode = AccessModes.FullPortal, Status = "Active", RequiresPasswordSetup = false });
        await db.SaveChangesAsync();
        _userIds[key] = user.Id;
        await IssueTokenAsync(db, scope, key, email);
    }

    /// <summary>Signs a fresh token for the persona (after its roles or access changed).</summary>
    public async Task RefreshTokenAsync(string key)
    {
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var email = $"{key.ToLowerInvariant()}-{TenantId:N}@launch.local";
        await IssueTokenAsync(db, scope, key, email);
    }

    private async Task IssueTokenAsync(ZayraDbContext db, IServiceScope scope, string key, string email)
    {
        var loaded = await db.Users.IgnoreQueryFilters().AsNoTracking().Include(u => u.Tenant)
            .Include(u => u.UserRoles).ThenInclude(r => r.Role).ThenInclude(r => r!.RolePermissions).ThenInclude(p => p.Permission)
            .Include(u => u.PermissionOverrides).Include(u => u.EmployeeUserAccounts).Include(u => u.EntityAccesses)
            .SingleAsync(u => u.TenantId == TenantId && u.Email == email);
        var grants = loaded.EntityAccesses.Where(a => a.IsActive).Select(a => new EntityAccessGrant(a.CompanyId, a.Role, a.GrantMode)).ToList();
        _tokens[key] = scope.ServiceProvider.GetRequiredService<ITokenService>().CreateAccessToken(loaded, AuthService.GetRoles(loaded),
            AuthService.GetPermissions(loaded), loaded.Tenant!, grants,
            EntityScopeClaims.Resolve(loaded.IsGroupScope, grants, Array.Empty<Guid>()), out _);
    }

    public async Task<HttpResponseMessage> Send(HttpMethod method, string path, string bearer, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (body is not null) request.Content = JsonContent.Create(body);
        var response = await Client.SendAsync(request);
        if ((int)response.StatusCode >= 500)
            throw new Xunit.Sdk.XunitException($"{method} {path} answered {(int)response.StatusCode}: {_errors.Last()}");
        return response;
    }

    public async Task<HttpStatusCode> Status(HttpMethod method, string path, string bearer, object? body = null)
    {
        using var r = await Send(method, path, bearer, body);
        return r.StatusCode;
    }

    public async Task<JsonElement> Json(HttpMethod method, string path, string bearer, object? body = null)
    {
        using var r = await Send(method, path, bearer, body);
        var text = await r.Content.ReadAsStringAsync();
        r.StatusCode.Should().Be(HttpStatusCode.OK, $"{method} {path}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (Host is not null) await Host.DisposeAsync();
        if (_anchor is not null) await _anchor.DisposeAsync();
    }
}
