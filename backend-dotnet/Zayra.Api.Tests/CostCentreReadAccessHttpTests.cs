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
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Tests.Security;

namespace Zayra.Api.Tests;

/// <summary>
/// P2-3: through the REAL pipeline, with users holding the seeded role bundles, the cost-centre list and get admit anyone
/// holding organization.read OR finance.gl.read (finance users load cost centres for mappings and readiness), while every
/// write keeps its HR/Admin gate.
/// </summary>
public sealed class CostCentreReadAccessHttpTests(CostCentreReadAccessHttpFixture f) : IClassFixture<CostCentreReadAccessHttpFixture>
{
    [Theory]
    [InlineData("Payroll Manager")]
    [InlineData("Finance")]
    [InlineData("Finance Approver")]
    [InlineData("HR Officer")]
    [InlineData("HR Manager")]
    [InlineData("Admin")]
    [InlineData("Auditor")]
    public async Task ListAndGet_AdmitFinanceReadersAndHr(string role)
    {
        using var list = await f.Send(HttpMethod.Get, "/api/cost-centers", f.Token(role));
        list.StatusCode.Should().Be(HttpStatusCode.OK, role + " " + await list.Content.ReadAsStringAsync());
        using var one = await f.Send(HttpMethod.Get, $"/api/cost-centers/{f.CostCentreId}", f.Token(role));
        one.StatusCode.Should().Be(HttpStatusCode.OK, role + " " + await one.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("Payroll Manager")]
    [InlineData("Finance")]
    [InlineData("Finance Approver")]
    [InlineData("HR Officer")]
    public async Task EveryWrite_StaysRefused_ForRolesWithoutTheHrGate(string role)
    {
        var id = f.CostCentreId;
        var body = new { companyId = f.CompanyId, code = "X1", name = "X" };
        var calls = new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Post, "/api/cost-centers", body),
            (HttpMethod.Put, $"/api/cost-centers/{id}", body),
            (HttpMethod.Delete, $"/api/cost-centers/{id}?reason=because", null),
            (HttpMethod.Post, "/api/cost-centers/import", new { csv = "Code,Name\nA,B\n", companyId = f.CompanyId }),
            (HttpMethod.Post, "/api/cost-centers/import-preview", new { csv = "Code,Name\nA,B\n", companyId = f.CompanyId }),
        };
        foreach (var (method, path, payload) in calls)
        {
            using var r = await f.Send(method, path, f.Token(role), payload);
            r.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{role} {method} {path}");
        }
    }

    [Fact]
    public void TheReadsCarryNoRoleGate_OnlyThePermissionAnyOf()
    {
        typeof(CostCentersController).GetCustomAttributes<AuthorizeAttribute>(true).Should().OnlyContain(a => string.IsNullOrWhiteSpace(a.Roles), "a class-level role list ANDs with every method's own gate");
        foreach (var m in new[] { typeof(CostCentersController).GetMethod(nameof(CostCentersController.Search))!, typeof(CostCentersController).GetMethod(nameof(CostCentersController.Get))!, typeof(OrganizationController).GetMethod(nameof(OrganizationController.CostCenters))! })
        {
            m.GetCustomAttributes<AuthorizeAttribute>(true).Where(a => !string.IsNullOrWhiteSpace(a.Roles)).Should().BeEmpty(m.Name);
            m.GetCustomAttributes<HasPermissionAttribute>().SelectMany(a => a.Permissions).Should().BeEquivalentTo(new[] { "organization.read", "finance.gl.read" }, m.Name);
        }
        foreach (var name in new[] { "Create", "Update", "Delete", "Export", "ImportTemplate", "ImportPreview", "Import" })
            typeof(CostCentersController).GetMethod(name)!.GetCustomAttributes<AuthorizeAttribute>(true).Should().Contain(a => !string.IsNullOrWhiteSpace(a.Roles), name + " keeps its own role gate");
    }

    [Fact]
    public async Task AUserWithNeitherReadKey_IsRefusedTheList()
    {
        using var r = await f.Send(HttpMethod.Get, "/api/cost-centers", f.Token("Employee"));
        r.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}

public sealed class CostCentreReadAccessHttpFixture : IAsyncLifetime
{
    private SqliteConnection _anchor = null!;
    private readonly Dictionary<string, string> _tokens = new();
    public AuthorizationPipelineHost Host { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public Guid TenantId { get; } = Guid.NewGuid();
    public Guid CompanyId { get; } = Guid.NewGuid();
    public Guid CostCentreId { get; private set; }
    public string Token(string role) => _tokens[role];

    public static readonly string[] Roles = { "Payroll Manager", "Finance", "Finance Approver", "HR Officer", "HR Manager", "Admin", "Auditor", "Employee" };

    public async Task InitializeAsync()
    {
        var connection = $"Data Source=file:cc-read-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _anchor = new SqliteConnection(connection);
        await _anchor.OpenAsync();
        Host = new AuthorizationPipelineHost(connection);
        Client = Host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        await db.Database.EnsureCreatedAsync();
        db.Tenants.Add(new Tenant { Id = TenantId, Name = "CC read", Slug = $"ccr-{TenantId:N}", IsActive = true });
        db.Companies.Add(new Company { Id = CompanyId, TenantId = TenantId, LegalNameEn = "Read Co", CountryCode = "SA", IsActive = true });
        var cc = new CostCenter { TenantId = TenantId, CompanyId = CompanyId, Code = "OPS", Name = "Ops" };
        db.CostCenters.Add(cc);
        await db.SaveChangesAsync();
        CostCentreId = cc.Id;
        await new AuthSeeder(db).EnsureTenantRolesAsync(TenantId, CancellationToken.None);

        foreach (var role in Roles)
        {
            var user = new User
            {
                TenantId = TenantId, Email = $"{role.Replace(' ', '-')}-{TenantId:N}@ccr.local",
                NormalizedEmail = $"{role.Replace(' ', '-')}-{TenantId:N}@ccr.local".ToUpperInvariant(),
                FullName = role, PasswordHash = "no-login-path", Status = "Active", AccessMode = "FullPortal",
                IsActive = true, IsEmailConfirmed = true, IsGroupScope = true,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            var roleRow = await db.Roles.SingleAsync(r => r.TenantId == TenantId && r.Name == role);
            db.Set<UserRole>().Add(new UserRole { UserId = user.Id, RoleId = roleRow.Id });
            await db.SaveChangesAsync();
        }
        foreach (var role in Roles)
        {
            var email = $"{role.Replace(' ', '-')}-{TenantId:N}@ccr.local";
            var user = await db.Users.IgnoreQueryFilters().AsNoTracking().Include(u => u.Tenant)
                .Include(u => u.UserRoles).ThenInclude(r => r.Role).ThenInclude(r => r!.RolePermissions).ThenInclude(p => p.Permission)
                .Include(u => u.PermissionOverrides).Include(u => u.EmployeeUserAccounts).Include(u => u.EntityAccesses)
                .SingleAsync(u => u.TenantId == TenantId && u.Email == email);
            _tokens[role] = scope.ServiceProvider.GetRequiredService<ITokenService>().CreateAccessToken(user, AuthService.GetRoles(user),
                AuthService.GetPermissions(user), user.Tenant!, Array.Empty<EntityAccessGrant>(),
                EntityScopeClaims.Resolve(true, Array.Empty<EntityAccessGrant>(), Array.Empty<Guid>()), out _);
        }
    }

    public async Task<HttpResponseMessage> Send(HttpMethod method, string path, string bearer, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await Client.SendAsync(request);
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (Host is not null) await Host.DisposeAsync();
        if (_anchor is not null) await _anchor.DisposeAsync();
    }
}
