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
using Zayra.Api.Infrastructure.Entitlements.Usage;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;
using Zayra.Api.Tests.Security;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// Benefit usage follows "HR makes, Finance approves, the subject never decides". Approving or rejecting a request needs its own key,
/// benefits.usage.approve, held by the built-in Finance roles and Admin and NOT by HR Manager or HR Officer, so the people who make
/// requests are never the people who approve them (the old key, payroll.approve, is in the HR Manager bundle). Through the REAL
/// pipeline (JWT, HasPermission, the seeded role bundles from AuthSeeder); SQLite stands in for Postgres.
/// </summary>
public sealed class BenefitUsageApproverHttpTests(BenefitUsageApproverHttpFixture f) : IClassFixture<BenefitUsageApproverHttpFixture>
{
    private static readonly object Approve = new { decision = "Approved" };
    private static readonly object Reject = new { decision = "Rejected", reason = "Booking is for someone else" };

    [Theory]
    [InlineData(BenefitUsageApproverHttpFixture.HrManager, "approve")]
    [InlineData(BenefitUsageApproverHttpFixture.HrManager, "reject")]
    [InlineData(BenefitUsageApproverHttpFixture.HrOfficer, "approve")]
    public async Task AnHrRole_IsRefused_OnApproveAndReject_AndTheRequestStaysPending(string who, string action)
    {
        var id = await f.SeedPendingAsync();

        using var r = await f.Send(HttpMethod.Post, $"/api/entitlements/usage/requests/{id}/decision", f.Token(who), action == "approve" ? Approve : Reject);

        r.StatusCode.Should().Be(HttpStatusCode.Forbidden, who + " " + action + ": " + await r.Content.ReadAsStringAsync());
        (await f.StatusAsync(id)).Should().Be(BenefitUsageStatuses.Pending);
    }

    [Theory]
    [InlineData(BenefitUsageApproverHttpFixture.Finance)]
    [InlineData(BenefitUsageApproverHttpFixture.FinanceApprover)]
    [InlineData(BenefitUsageApproverHttpFixture.PayrollManager)]
    public async Task AFinanceApprover_CanApprove(string who)
    {
        var id = await f.SeedPendingAsync();

        using var r = await f.Send(HttpMethod.Post, $"/api/entitlements/usage/requests/{id}/decision", f.Token(who), Approve);

        r.StatusCode.Should().Be(HttpStatusCode.OK, who + ": " + await r.Content.ReadAsStringAsync());
        (await f.StatusAsync(id)).Should().Be(BenefitUsageStatuses.Approved);
    }

    [Theory]
    [InlineData(BenefitUsageApproverHttpFixture.Finance)]
    [InlineData(BenefitUsageApproverHttpFixture.FinanceApprover)]
    public async Task AFinanceApprover_CanReject(string who)
    {
        var id = await f.SeedPendingAsync();

        using var r = await f.Send(HttpMethod.Post, $"/api/entitlements/usage/requests/{id}/decision", f.Token(who), Reject);

        r.StatusCode.Should().Be(HttpStatusCode.OK, who + ": " + await r.Content.ReadAsStringAsync());
        (await f.StatusAsync(id)).Should().Be(BenefitUsageStatuses.Rejected);
    }

    [Fact]
    public async Task ASuperAdmin_CanApprove()
    {
        var id = await f.SeedPendingAsync();

        using var r = await f.Send(HttpMethod.Post, $"/api/entitlements/usage/requests/{id}/decision", f.Token(BenefitUsageApproverHttpFixture.Admin), Approve);

        r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TheRequester_EvenAFinanceUser_CannotDecideTheirOwnRequest()
    {
        var id = await f.SeedPendingAsync(requestedBy: await f.UserIdAsync(BenefitUsageApproverHttpFixture.Finance));

        using var own = await f.Send(HttpMethod.Post, $"/api/entitlements/usage/requests/{id}/decision", f.Token(BenefitUsageApproverHttpFixture.Finance), Approve);
        own.StatusCode.Should().Be(HttpStatusCode.Forbidden, await own.Content.ReadAsStringAsync());
        JsonDocument.Parse(await own.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString().Should().Be(BenefitUsageErrors.MakerCannotApprove);
        (await f.StatusAsync(id)).Should().Be(BenefitUsageStatuses.Pending);

        using var other = await f.Send(HttpMethod.Post, $"/api/entitlements/usage/requests/{id}/decision", f.Token(BenefitUsageApproverHttpFixture.FinanceApprover), Approve);
        other.StatusCode.Should().Be(HttpStatusCode.OK, await other.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TheEmployeeItIsFor_EvenAFinanceUser_CannotDecideIt()
    {
        var id = await f.SeedPendingAsync();

        using var r = await f.Send(HttpMethod.Post, $"/api/entitlements/usage/requests/{id}/decision", f.Token(BenefitUsageApproverHttpFixture.FinanceSubject), Approve);

        r.StatusCode.Should().Be(HttpStatusCode.Forbidden, await r.Content.ReadAsStringAsync());
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString().Should().Be("subject_cannot_decide");
        (await f.StatusAsync(id)).Should().Be(BenefitUsageStatuses.Pending);
    }

    [Fact]
    public async Task OneDecider_PerRequest_ASecondDecisionIsRefused()
    {
        var id = await f.SeedPendingAsync();
        (await f.Send(HttpMethod.Post, $"/api/entitlements/usage/requests/{id}/decision", f.Token(BenefitUsageApproverHttpFixture.Finance), Approve))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        using var second = await f.Send(HttpMethod.Post, $"/api/entitlements/usage/requests/{id}/decision", f.Token(BenefitUsageApproverHttpFixture.FinanceApprover), Reject);

        second.StatusCode.Should().Be(HttpStatusCode.Conflict, await second.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(BenefitUsageApproverHttpFixture.Finance)]
    [InlineData(BenefitUsageApproverHttpFixture.FinanceApprover)]
    [InlineData(BenefitUsageApproverHttpFixture.HrManager)]
    public async Task TheFinanceQueue_IsReadable_ByFinanceWithoutBroadEntitlementsAccess(string who)
    {
        await f.SeedPendingAsync();

        using var list = await f.Send(HttpMethod.Get, "/api/entitlements/usage/requests?status=Pending", f.Token(who));

        list.StatusCode.Should().Be(HttpStatusCode.OK, who + ": " + await list.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task FinanceReadsTheQueue_ButStillHasNoAccessToTheBenefitsGridOrPackages()
    {
        using var grid = await f.Send(HttpMethod.Get, "/api/entitlements/matrix", f.Token(BenefitUsageApproverHttpFixture.Finance));

        grid.StatusCode.Should().Be(HttpStatusCode.Forbidden, "Finance reads the benefit-usage queue only; it is not given entitlements.read");
    }
}

public sealed class BenefitUsageApproverHttpFixture : IAsyncLifetime
{
    public const string HrManager = "HR Manager";
    public const string HrOfficer = "HR Officer";
    public const string Finance = "Finance";
    public const string FinanceApprover = "Finance Approver";
    public const string PayrollManager = "Payroll Manager";
    public const string Admin = "Admin";
    /// <summary>A Finance login that is also the employee the requests are for.</summary>
    public const string FinanceSubject = "Finance Subject";

    private SqliteConnection _anchor = null!;
    private readonly Dictionary<string, string> _tokens = new();
    private readonly Dictionary<string, string> _emails = new();
    private PackageSeed _seed = null!;
    private Guid _entitlementId;
    public AuthorizationPipelineHost Host { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public Guid TenantId => _seed.TenantId;
    public string Token(string key) => _tokens[key];

    public async Task InitializeAsync()
    {
        var connection = $"Data Source=file:benefit-usage-approver-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _anchor = new SqliteConnection(connection);
        await _anchor.OpenAsync();
        Host = new AuthorizationPipelineHost(connection);
        Client = Host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        await db.Database.EnsureCreatedAsync();

        _seed = new PackageSeed();
        // Only what the request needs (PackageSeed.SaveAsync also writes a loan policy that SQLite rejects).
        db.AddRange(_seed.Tenant, _seed.Company, _seed.Grade, _seed.Mohammed);
        await db.SaveChangesAsync();
        db.Add(_seed.Term);
        await db.SaveChangesAsync();
        var tenant = await db.Tenants.SingleAsync(t => t.Id == _seed.TenantId);
        tenant.IsActive = true;
        db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = TenantId, FeatureKey = FeatureKeys.ReleaseA, IsEnabled = true });
        var row = new EmployeeEntitlement
        {
            TenantId = TenantId, CompanyId = _seed.Company.Id, EmployeeId = _seed.Mohammed.PublicId, ContractId = _seed.Term.Id,
            PayComponentCode = "AIR_TICKET", EntitlementClass = PayEntitlementClasses.Contractual, ValueType = GradeEntitlementValueTypes.Quantity,
            Quantity = 50, CoverageTier = CoverageTiers.Economy, LimitPeriod = EntitlementLimitPeriods.Annual, Source = EntitlementSources.Migrated,
            VerificationState = EntitlementVerificationStates.Verified,
            EffectiveFrom = _seed.Term.StartDate, EffectiveTo = _seed.Term.EndDate,
        };
        db.EmployeeEntitlements.Add(row);
        await db.SaveChangesAsync();
        _entitlementId = row.Id;

        await new AuthSeeder(db).EnsureTenantRolesAsync(TenantId, CancellationToken.None);
        foreach (var role in new[] { HrManager, HrOfficer, Finance, FinanceApprover, PayrollManager, Admin }) await AddUser(db, role, role);
        await AddUser(db, FinanceSubject, Finance, userId: _seed.UserId);
        foreach (var key in _emails.Keys.ToList()) await IssueToken(scope, key);
    }

    /// <summary>A Pending request for Mohammed, raised by <paramref name="requestedBy"/> (default: someone else).</summary>
    public async Task<Guid> SeedPendingAsync(Guid? requestedBy = null)
    {
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var request = new BenefitUsageRequest
        {
            TenantId = TenantId, CompanyId = _seed.Company.Id, EmployeeId = _seed.Mohammed.Id, EntitlementId = _entitlementId, BenefitCode = "AIR_TICKET",
            PeriodStart = _seed.Term.StartDate, PeriodEnd = _seed.Term.EndDate!.Value, UsageDate = new DateOnly(2026, 8, 10), Units = 1,
            EvidenceReference = "test-booking-1", Status = BenefitUsageStatuses.Pending, RequestedByUserId = requestedBy ?? Guid.NewGuid(),
        };
        db.BenefitUsageRequests.Add(request);
        await db.SaveChangesAsync();
        return request.Id;
    }

    public async Task<string> StatusAsync(Guid id)
    {
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        return await db.BenefitUsageRequests.IgnoreQueryFilters().AsNoTracking().Where(x => x.Id == id).Select(x => x.Status).SingleAsync();
    }

    public async Task<Guid> UserIdAsync(string key)
    {
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        return await db.Users.IgnoreQueryFilters().Where(u => u.TenantId == TenantId && u.Email == _emails[key]).Select(u => u.Id).SingleAsync();
    }

    private async Task AddUser(ZayraDbContext db, string key, string role, Guid? userId = null)
    {
        var email = $"{key.Replace(' ', '-')}-{TenantId:N}@bua.local";
        var user = new User
        {
            TenantId = TenantId, Email = email, NormalizedEmail = email.ToUpperInvariant(), FullName = key, PasswordHash = "no-login-path",
            Status = "Active", AccessMode = "FullPortal", IsActive = true, IsEmailConfirmed = true, IsGroupScope = true,
        };
        if (userId is Guid id) user.Id = id;
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
