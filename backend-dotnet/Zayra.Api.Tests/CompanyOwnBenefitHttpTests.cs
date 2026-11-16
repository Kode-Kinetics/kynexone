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
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;
using Zayra.Api.Tests.Security;

namespace Zayra.Api.Tests;

/// <summary>
/// A company's own benefits through the REAL pipeline (JWT, HasPermission, entity scope): reaching one company is not enough to add, edit or remove a
/// benefit — the caller needs <c>entitlements.manage</c>, and a company admin whose permission was denied to them is refused like any other; and a per-user
/// Deny of <c>payroll.approve</c> takes Finance's confirm away from someone whose role grants it. SQLite stands in for Postgres.
/// </summary>
public sealed class CompanyOwnBenefitHttpTests(CompanyOwnBenefitHttpFixture f) : IClassFixture<CompanyOwnBenefitHttpFixture>
{
    private async Task<string> AddOwnBenefitAsControl()
    {
        using var r = await f.Send(HttpMethod.Post, "/api/entitlements/components", f.Token(CompanyOwnBenefitHttpFixture.CompanyHrManager),
            new { nameEn = "Parking " + Guid.NewGuid().ToString("N")[..6], companyId = f.CompanyA });
        r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString()!;
    }

    [Fact]
    public async Task ACompanyHrManager_WithEntitlementsManage_AddsTheirCompanysOwnBenefit()
    {
        var code = await AddOwnBenefitAsControl();
        code.Should().StartWith("BEN_PARKING_");
    }

    [Theory]
    [InlineData(CompanyOwnBenefitHttpFixture.CompanyAdminDenied)]
    [InlineData(CompanyOwnBenefitHttpFixture.CompanyHrOfficer)]
    public async Task ACompanyUserWithoutEntitlementsManage_CannotAddEditOrRemoveTheirCompanysOwnBenefit(string who)
    {
        var existing = await AddOwnBenefitAsControl();

        using var add = await f.Send(HttpMethod.Post, "/api/entitlements/components", f.Token(who), new { nameEn = "Gym", companyId = f.CompanyA });
        using var edit = await f.Send(HttpMethod.Put, $"/api/entitlements/components/{existing}", f.Token(who), new { expectedVersion = "x", nameEn = "Renamed" });
        using var remove = await f.Send(HttpMethod.Post, $"/api/entitlements/components/{existing}/remove", f.Token(who));

        add.StatusCode.Should().Be(HttpStatusCode.Forbidden, who + " add: " + await add.Content.ReadAsStringAsync());
        edit.StatusCode.Should().Be(HttpStatusCode.Forbidden, who + " edit");
        remove.StatusCode.Should().Be(HttpStatusCode.Forbidden, who + " remove");
        using var scope = f.Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        (await db.PayComponents.IgnoreQueryFilters().CountAsync(x => x.TenantId == f.TenantId && x.NameEn == "Gym")).Should().Be(0);
        (await db.PayComponents.IgnoreQueryFilters().SingleAsync(x => x.TenantId == f.TenantId && x.Code == existing)).IsDeleted.Should().BeFalse();
    }

    [Fact]
    public async Task APerUserDenyOfPayrollApprove_BlocksConfirmation_ThoughTheRoleGrantsIt()
    {
        var code = await f.SeedPendingAllowanceAsync();

        using var denied = await f.Send(HttpMethod.Post, $"/api/entitlements/components/{code}/confirm", f.Token(CompanyOwnBenefitHttpFixture.FinanceDenied), new { });
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden, await denied.Content.ReadAsStringAsync());
        using (var scope = f.Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            (await db.PayComponents.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.TenantId == f.TenantId && x.Code == code)).FinanceConfirmedAtUtc.Should().BeNull();
        }

        using var allowed = await f.Send(HttpMethod.Post, $"/api/entitlements/components/{code}/confirm", f.Token(CompanyOwnBenefitHttpFixture.FinanceApprover), new { });
        allowed.StatusCode.Should().Be(HttpStatusCode.OK, "the same role without the deny confirms it: " + await allowed.Content.ReadAsStringAsync());
    }
}

public sealed class CompanyOwnBenefitHttpFixture : IAsyncLifetime
{
    public const string CompanyHrManager = "Company HR Manager";
    public const string CompanyAdminDenied = "Company Admin denied entitlements.manage";
    public const string CompanyHrOfficer = "Company HR Officer";
    public const string FinanceDenied = "HR Manager denied payroll.approve";
    public const string FinanceApprover = "Finance Approver";

    private SqliteConnection _anchor = null!;
    private readonly Dictionary<string, string> _tokens = new();
    private readonly Dictionary<string, (string Email, EntityAccessGrant[] Grants, bool Group, Guid[] Companies)> _meta = new();
    public AuthorizationPipelineHost Host { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public Guid TenantId { get; } = Guid.NewGuid();
    public Guid CompanyA { get; } = Guid.NewGuid();
    public Guid CompanyB { get; } = Guid.NewGuid();
    public string Token(string key) => _tokens[key];

    public async Task InitializeAsync()
    {
        var connection = $"Data Source=file:company-own-benefit-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _anchor = new SqliteConnection(connection);
        await _anchor.OpenAsync();
        Host = new AuthorizationPipelineHost(connection);
        Client = Host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        await db.Database.EnsureCreatedAsync();
        db.Tenants.Add(new Tenant { Id = TenantId, Name = "Company own benefits", Slug = $"cob-{TenantId:N}", IsActive = true });
        db.Companies.AddRange(
            new Company { Id = CompanyA, TenantId = TenantId, LegalNameEn = "Co A", CountryCode = "SA", IsActive = true, DefaultCurrency = "SAR" },
            new Company { Id = CompanyB, TenantId = TenantId, LegalNameEn = "Co B", CountryCode = "SA", IsActive = true, DefaultCurrency = "SAR" });
        db.TenantFeatureFlags.AddRange(
            new TenantFeatureFlag { TenantId = TenantId, FeatureKey = FeatureKeys.ReleaseA, IsEnabled = true },
            new TenantFeatureFlag { TenantId = TenantId, FeatureKey = FeatureKeys.BenefitDefinitionV1, IsEnabled = true });
        db.GlDrivers.Add(new GlDriver
        {
            TenantId = TenantId, CompanyId = null, Key = "EARN:CAR", Label = "EARN:CAR", Category = GlDriverCategories.Earning, PostingSide = "DR",
            DefaultCode = "5210", DefaultName = "Account 5210", MatchMode = "Exact", MatchComponentCode = "CAR", SortOrder = 70,
        });
        await db.SaveChangesAsync();
        await new AuthSeeder(db).EnsureTenantRolesAsync(TenantId, CancellationToken.None);

        await AddUser(db, CompanyHrManager, "HR Manager", false, [CompanyA]);
        await AddUser(db, CompanyAdminDenied, "Admin", false, [CompanyA], deny: "entitlements.manage");
        await AddUser(db, CompanyHrOfficer, "HR Officer", false, [CompanyA]);
        await AddUser(db, FinanceDenied, "HR Manager", true, [], deny: "payroll.approve");
        await AddUser(db, FinanceApprover, "Finance Approver", true, []);
        foreach (var key in _meta.Keys.ToList()) await IssueToken(scope, key);
    }

    /// <summary>A group cash allowance someone else added, waiting for Finance.</summary>
    public async Task<string> SeedPendingAllowanceAsync()
    {
        var code = "ALW_CAR" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        db.PayComponents.Add(new PayComponent
        {
            TenantId = TenantId, CompanyId = null, IsOffered = true, Code = code, NameEn = "Car", NameAr = "سيارة", ComponentType = PayComponentTypes.Earning,
            CalcMethod = PayComponentCalcMethods.GradePackage, EntitlementClass = PayEntitlementClasses.QiwaWage, StatutoryFloor = PayStatutoryFloors.None,
            GlDriverKey = "EARN:CAR", WpsIncluded = true, EosbIncluded = true, DisplayOrder = 9100, IsActive = true, CreatedBy = Guid.NewGuid(),
        });
        await db.SaveChangesAsync();
        return code;
    }

    private async Task AddUser(ZayraDbContext db, string key, string role, bool group, Guid[] companies, string? deny = null)
    {
        var email = $"{key.Replace(' ', '-').Replace('.', '-')}-{TenantId:N}@cob.local";
        var user = new User
        {
            TenantId = TenantId, Email = email, NormalizedEmail = email.ToUpperInvariant(), FullName = key, PasswordHash = "no-login-path",
            Status = "Active", AccessMode = "FullPortal", IsActive = true, IsEmailConfirmed = true, IsGroupScope = group,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var roleRow = await db.Roles.SingleAsync(r => r.TenantId == TenantId && r.Name == role);
        db.Set<UserRole>().Add(new UserRole { UserId = user.Id, RoleId = roleRow.Id });
        foreach (var c in companies) db.Set<UserEntityAccess>().Add(new UserEntityAccess { TenantId = TenantId, UserId = user.Id, CompanyId = c, Role = role, IsActive = true });
        if (deny is not null)
            db.UserPermissionOverrides.Add(new UserPermissionOverride { TenantId = TenantId, UserId = user.Id, PermissionKey = deny, Effect = "Deny", Reason = "test", IsActive = true });
        await db.SaveChangesAsync();
        _meta[key] = (email, companies.Select(c => new EntityAccessGrant(c, role)).ToArray(), group, companies);
    }

    private async Task IssueToken(IServiceScope scope, string key)
    {
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var m = _meta[key];
        var user = await db.Users.IgnoreQueryFilters().AsNoTracking().Include(u => u.Tenant)
            .Include(u => u.UserRoles).ThenInclude(r => r.Role).ThenInclude(r => r!.RolePermissions).ThenInclude(p => p.Permission)
            .Include(u => u.PermissionOverrides).Include(u => u.EmployeeUserAccounts).Include(u => u.EntityAccesses)
            .SingleAsync(u => u.TenantId == TenantId && u.Email == m.Email);
        _tokens[key] = scope.ServiceProvider.GetRequiredService<ITokenService>().CreateAccessToken(user, AuthService.GetRoles(user),
            AuthService.GetPermissions(user), user.Tenant!, m.Grants, EntityScopeClaims.Resolve(m.Group, m.Grants, m.Companies), out _);
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
