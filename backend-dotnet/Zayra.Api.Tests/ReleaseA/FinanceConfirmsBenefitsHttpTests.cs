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

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// "Company Finance confirms" (owner-approved design): the built-in Finance Approver and Payroll Manager confirm monthly cash allowances
/// (payroll.approve), so they can open Benefits by grade and its "Waiting for Finance" list (entitlements.read). Read only: they still cannot
/// edit or publish the grid (entitlements.manage), and nobody confirms what they added. Through the REAL pipeline (JWT, HasPermission, the
/// seeded role bundles from AuthSeeder); SQLite stands in for Postgres.
/// </summary>
public sealed class FinanceConfirmsBenefitsHttpTests(FinanceConfirmsBenefitsHttpFixture f) : IClassFixture<FinanceConfirmsBenefitsHttpFixture>
{
    [Theory]
    [InlineData(FinanceConfirmsBenefitsHttpFixture.FinanceApprover)]
    [InlineData(FinanceConfirmsBenefitsHttpFixture.PayrollManager)]
    public async Task AFinanceRole_OpensTheGrid_AndSeesTheAllowanceWaitingForIt(string who)
    {
        var code = await f.SeedPendingAllowanceAsync();

        using var r = await f.Send(HttpMethod.Get, "/api/entitlements/matrix", f.Token(who));

        r.StatusCode.Should().Be(HttpStatusCode.OK, who + ": " + await r.Content.ReadAsStringAsync());
        var waiting = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("components").EnumerateArray()
            .Single(c => c.GetProperty("code").GetString() == code);
        waiting.GetProperty("cashAllowance").GetProperty("financePending").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData(FinanceConfirmsBenefitsHttpFixture.FinanceApprover)]
    [InlineData(FinanceConfirmsBenefitsHttpFixture.PayrollManager)]
    public async Task AFinanceRole_ConfirmsAnAllowanceSomeoneElseAdded(string who)
    {
        var code = await f.SeedPendingAllowanceAsync();

        using var r = await f.Send(HttpMethod.Post, $"/api/entitlements/components/{code}/confirm", f.Token(who), new { });

        r.StatusCode.Should().Be(HttpStatusCode.OK, who + ": " + await r.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(FinanceConfirmsBenefitsHttpFixture.FinanceApprover)]
    [InlineData(FinanceConfirmsBenefitsHttpFixture.PayrollManager)]
    public async Task AFinanceRole_StillCannotEditPublishAddOrRemove(string who)
    {
        var code = await f.SeedPendingAllowanceAsync();

        using var publish = await f.Send(HttpMethod.Put, "/api/entitlements/matrix", f.Token(who), new { effectiveFrom = "2027-01-01", cells = Array.Empty<object>() });
        using var add = await f.Send(HttpMethod.Post, "/api/entitlements/components", f.Token(who), new { nameEn = "Gym" });
        using var edit = await f.Send(HttpMethod.Put, $"/api/entitlements/components/{code}", f.Token(who), new { expectedVersion = "x", nameEn = "Renamed" });
        using var remove = await f.Send(HttpMethod.Post, $"/api/entitlements/components/{code}/remove", f.Token(who));
        using var offerings = await f.Send(HttpMethod.Put, "/api/entitlements/offerings", f.Token(who), new { });

        foreach (var (name, response) in new[] { ("publish", publish), ("add", add), ("edit", edit), ("remove", remove), ("offerings", offerings) })
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{who} {name}: {await response.Content.ReadAsStringAsync()}");
        using var scope = f.Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        (await db.PayComponents.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.TenantId == f.TenantId && x.Code == code)).NameEn.Should().Be("Car");
        (await db.PayComponents.IgnoreQueryFilters().CountAsync(x => x.TenantId == f.TenantId && x.NameEn == "Gym")).Should().Be(0);
    }

    [Fact]
    public async Task TheMaker_StillCannotConfirmTheirOwnAllowance_WhateverFinanceRightsTheyHold()
    {
        var code = await f.SeedPendingAllowanceAsync(maker: await f.UserIdAsync(FinanceConfirmsBenefitsHttpFixture.PayrollManager));

        using var own = await f.Send(HttpMethod.Post, $"/api/entitlements/components/{code}/confirm", f.Token(FinanceConfirmsBenefitsHttpFixture.PayrollManager), new { });
        own.StatusCode.Should().Be(HttpStatusCode.Forbidden, await own.Content.ReadAsStringAsync());
        JsonDocument.Parse(await own.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString().Should().Be("maker_cannot_confirm");

        using var other = await f.Send(HttpMethod.Post, $"/api/entitlements/components/{code}/confirm", f.Token(FinanceConfirmsBenefitsHttpFixture.FinanceApprover), new { });
        other.StatusCode.Should().Be(HttpStatusCode.OK, await other.Content.ReadAsStringAsync());
    }
}

public sealed class FinanceConfirmsBenefitsHttpFixture : IAsyncLifetime
{
    public const string FinanceApprover = "Finance Approver";
    public const string PayrollManager = "Payroll Manager";

    private SqliteConnection _anchor = null!;
    private readonly Dictionary<string, string> _tokens = new();
    private readonly Dictionary<string, string> _emails = new();
    public AuthorizationPipelineHost Host { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public Guid TenantId { get; } = Guid.NewGuid();
    public string Token(string key) => _tokens[key];

    public async Task InitializeAsync()
    {
        var connection = $"Data Source=file:finance-confirms-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _anchor = new SqliteConnection(connection);
        await _anchor.OpenAsync();
        Host = new AuthorizationPipelineHost(connection);
        Client = Host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        await db.Database.EnsureCreatedAsync();
        db.Tenants.Add(new Tenant { Id = TenantId, Name = "Finance confirms", Slug = $"fcb-{TenantId:N}", IsActive = true });
        db.Companies.Add(new Company { TenantId = TenantId, LegalNameEn = "Co A", CountryCode = "SA", IsActive = true, DefaultCurrency = "SAR" });
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
        foreach (var role in new[] { FinanceApprover, PayrollManager }) await AddUser(db, role, role);
        foreach (var key in _emails.Keys.ToList()) await IssueToken(scope, key);
    }

    /// <summary>A group cash allowance waiting for Finance, added by <paramref name="maker"/> (default: someone else).</summary>
    public async Task<string> SeedPendingAllowanceAsync(Guid? maker = null)
    {
        var code = "ALW_CAR" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        db.PayComponents.Add(new PayComponent
        {
            TenantId = TenantId, CompanyId = null, IsOffered = true, Code = code, NameEn = "Car", NameAr = "سيارة", ComponentType = PayComponentTypes.Earning,
            CalcMethod = PayComponentCalcMethods.GradePackage, EntitlementClass = PayEntitlementClasses.QiwaWage, StatutoryFloor = PayStatutoryFloors.None,
            GlDriverKey = "EARN:CAR", WpsIncluded = true, EosbIncluded = true, DisplayOrder = 9100, IsActive = true, CreatedBy = maker ?? Guid.NewGuid(),
        });
        await db.SaveChangesAsync();
        return code;
    }

    public async Task<Guid> UserIdAsync(string key)
    {
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        return await db.Users.IgnoreQueryFilters().Where(u => u.TenantId == TenantId && u.Email == _emails[key]).Select(u => u.Id).SingleAsync();
    }

    private async Task AddUser(ZayraDbContext db, string key, string role)
    {
        var email = $"{key.Replace(' ', '-')}-{TenantId:N}@fcb.local";
        var user = new User
        {
            TenantId = TenantId, Email = email, NormalizedEmail = email.ToUpperInvariant(), FullName = key, PasswordHash = "no-login-path",
            Status = "Active", AccessMode = "FullPortal", IsActive = true, IsEmailConfirmed = true, IsGroupScope = true,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var roleRow = await db.Roles.SingleAsync(r => r.TenantId == TenantId && r.Name == role);
        db.Set<UserRole>().Add(new UserRole { UserId = user.Id, RoleId = roleRow.Id });
        await db.SaveChangesAsync();
        _emails[key] = email;
    }

    private async Task IssueToken(IServiceScope scope, string key)
    {
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var user = await db.Users.IgnoreQueryFilters().AsNoTracking().Include(u => u.Tenant)
            .Include(u => u.UserRoles).ThenInclude(r => r.Role).ThenInclude(r => r!.RolePermissions).ThenInclude(p => p.Permission)
            .Include(u => u.PermissionOverrides).Include(u => u.EmployeeUserAccounts).Include(u => u.EntityAccesses)
            .SingleAsync(u => u.TenantId == TenantId && u.Email == _emails[key]);
        _tokens[key] = scope.ServiceProvider.GetRequiredService<ITokenService>().CreateAccessToken(user, AuthService.GetRoles(user),
            AuthService.GetPermissions(user), user.Tenant!, [], EntityScopeClaims.Resolve(true, [], []), out _);
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
