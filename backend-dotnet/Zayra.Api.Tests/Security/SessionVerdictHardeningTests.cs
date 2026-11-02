using System.Net;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Http;
using Zayra.Api.Infrastructure.Qiwa;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>Security-review follow-ups for the session verdict cache (L5 eviction gaps, L6 bounds, M2, M4, L9).</summary>
public sealed class SessionVerdictHardeningTests
{
    private static MemorySessionVerdictCache NewCache(int ttl = 30, int maxEntries = 1000, int maxVersions = 1000) =>
        MemorySessionVerdictCache.Create(Options.Create(new SessionVerdictCacheOptions
        { TtlSeconds = ttl, MaxEntries = maxEntries, MaxVersionEntries = maxVersions }));

    private sealed class Rig
    {
        public required ZayraDbContext Db;
        public required MemorySessionVerdictCache Cache;
        public required Tenant Tenant;
        public required User User;
        public SessionVerdictKey Key() => Cache.KeyFor(Tenant.Id, User.Id, "fp");
    }

    private static async Task<Rig> NewRigAsync()
    {
        var cache = NewCache();
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(new SessionVerdictInvalidationInterceptor(cache)).Options);
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "T", Slug = "t" };
        var user = new User
        {
            Id = Guid.NewGuid(), TenantId = tenant.Id, Tenant = tenant, Email = "a@t.local", NormalizedEmail = "A@T.LOCAL",
            FullName = "A", PasswordHash = "x"
        };
        db.Tenants.Add(tenant);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return new Rig { Db = db, Cache = cache, Tenant = tenant, User = user };
    }

    // ── L5: tenant-level evictions ────────────────────────────────────────────────────────────

    [Fact]
    public async Task EmployeeSoftDelete_EvictsTheTenant()
    {
        var r = await NewRigAsync();
        var emp = new Employee { TenantId = r.Tenant.Id, EmployeeCode = "E1", FullName = "E", Status = EmployeeStatuses.Active };
        r.Db.Employees.Add(emp);
        await r.Db.SaveChangesAsync();
        r.Cache.RememberCurrent(r.Key());

        emp.IsDeleted = true;
        await r.Db.SaveChangesAsync();

        r.Cache.IsKnownCurrent(r.Key()).Should().BeFalse();
    }

    [Fact]
    public async Task EmployeeHardDelete_EvictsTheTenant()
    {
        var r = await NewRigAsync();
        var emp = new Employee { TenantId = r.Tenant.Id, EmployeeCode = "E1", FullName = "E", Status = EmployeeStatuses.Active };
        r.Db.Employees.Add(emp);
        await r.Db.SaveChangesAsync();
        r.Cache.RememberCurrent(r.Key());

        r.Db.Employees.Remove(emp);
        await r.Db.SaveChangesAsync();

        r.Cache.IsKnownCurrent(r.Key()).Should().BeFalse();
    }

    [Fact]
    public async Task OrdinaryEmployeeEdit_DoesNotEvict()
    {
        var r = await NewRigAsync();
        var emp = new Employee { TenantId = r.Tenant.Id, EmployeeCode = "E1", FullName = "E", Status = EmployeeStatuses.Active };
        r.Db.Employees.Add(emp);
        await r.Db.SaveChangesAsync();
        r.Cache.RememberCurrent(r.Key());

        emp.FullName = "Renamed";
        await r.Db.SaveChangesAsync();

        r.Cache.IsKnownCurrent(r.Key()).Should().BeTrue("a name edit cannot change any session verdict");
    }

    [Fact]
    public async Task EmployeeUserAccountWithoutAUser_EvictsTheTenant_AndWithAUser_EvictsThatUser()
    {
        var r = await NewRigAsync();
        r.Cache.RememberCurrent(r.Key());
        r.Db.EmployeeUserAccounts.Add(new EmployeeUserAccount { TenantId = r.Tenant.Id, EmployeeId = 1, UserId = null });
        await r.Db.SaveChangesAsync();
        r.Cache.IsKnownCurrent(r.Key()).Should().BeFalse();

        r.Cache.RememberCurrent(r.Key());
        r.Db.EmployeeUserAccounts.Add(new EmployeeUserAccount { TenantId = r.Tenant.Id, EmployeeId = 2, UserId = r.User.Id });
        await r.Db.SaveChangesAsync();
        r.Cache.IsKnownCurrent(r.Key()).Should().BeFalse();
    }

    [Fact]
    public async Task CompanyDeactivation_SecuritySettingAndIdentityProviderFlips_EvictTheTenant()
    {
        var r = await NewRigAsync();
        var company = new Company { TenantId = r.Tenant.Id, LegalNameEn = "C", IsActive = true };
        var policy = new SecuritySetting { Id = Guid.NewGuid(), TenantId = r.Tenant.Id };
        var idp = new TenantIdentityProviderSetting { TenantId = r.Tenant.Id };
        r.Db.AddRange(company, policy, idp);
        await r.Db.SaveChangesAsync();

        foreach (var change in new Action[]
                 { () => company.IsActive = false, () => policy.MfaRequired = true, () => idp.EnforceSsoLogin = true })
        {
            r.Cache.RememberCurrent(r.Key());
            change();
            await r.Db.SaveChangesAsync();
            r.Cache.IsKnownCurrent(r.Key()).Should().BeFalse();
        }
    }

    // ── L5: impersonation + platform admin through the validator ──────────────────────────────

    [Fact]
    public async Task ImpersonationToken_IsNeverServedFromTheCache_AndStillRejected()
    {
        var r = await NewRigAsync();
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, r.User.Id.ToString()), new("tenant_id", r.Tenant.Id.ToString()),
            new(TenantSessionSecurity.SessionStampClaim, TenantSessionSecurity.StampValue(r.User)),
            new("impersonated_by", Guid.NewGuid().ToString())
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        // Even a verdict planted under the exact key must not be honoured for an impersonation token.
        r.Cache.RememberCurrent(r.Cache.KeyFor(r.Tenant.Id, r.User.Id, CurrentSessionValidator.Fingerprint(principal)));

        var result = await CurrentSessionValidator.ValidateAsync(principal, r.Db, r.Cache, CancellationToken.None);

        result.Current.Should().BeFalse();
    }

    [Fact]
    public async Task PlatformAdmin_IsCachedAndDeactivationTakesEffectOnTheNextRequest()
    {
        var r = await NewRigAsync();
        var admin = new PlatformUser { Email = "p@k.local", FullName = "P", PasswordHash = "x", Role = PlatformRoles.Admin };
        r.Db.PlatformUsers.Add(admin);
        await r.Db.SaveChangesAsync();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, admin.Id.ToString()),
            new Claim("is_platform_admin", "true"),
            new Claim("platform_role", PlatformRoles.Admin),
            new Claim(PlatformSessionSecurity.SessionStampClaim, PlatformSessionSecurity.StampValue(admin.UpdatedAtUtc!.Value)),
        }, "test"));

        (await CurrentSessionValidator.ValidateAsync(principal, r.Db, r.Cache, CancellationToken.None)).Current.Should().BeTrue();
        var disposed = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase("x").Options);
        await disposed.DisposeAsync();
        (await CurrentSessionValidator.ValidateAsync(principal, disposed, r.Cache, CancellationToken.None)).Current
            .Should().BeTrue("the second request is served from the cache with no database access");

        admin.IsActive = false;
        await r.Db.SaveChangesAsync();

        (await CurrentSessionValidator.ValidateAsync(principal, r.Db, r.Cache, CancellationToken.None)).Current.Should().BeFalse();
    }

    [Fact]
    public void Fingerprint_IgnoresClaimOrder_ButDistinguishesTokens()
    {
        ClaimsPrincipal P(params (string, string)[] c) =>
            new(new ClaimsIdentity(c.Select(x => new Claim(x.Item1, x.Item2)), "test"));
        var a = P(("permission", "x"), ("permission", "y"), ("jti", "1"));
        var reordered = P(("jti", "1"), ("permission", "y"), ("permission", "x"));
        var otherToken = P(("permission", "x"), ("permission", "y"), ("jti", "2"));

        CurrentSessionValidator.Fingerprint(a).Should().Be(CurrentSessionValidator.Fingerprint(reordered));
        CurrentSessionValidator.Fingerprint(a).Should().NotBe(CurrentSessionValidator.Fingerprint(otherToken));
    }

    // ── L6: bounds ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Cache_NeverHoldsMoreThanMaxEntries()
    {
        var cache = NewCache(maxEntries: 3);
        var keys = Enumerable.Range(0, 20).Select(i => cache.KeyFor(Guid.NewGuid(), Guid.NewGuid(), $"fp{i}")).ToList();
        foreach (var k in keys) cache.RememberCurrent(k);

        keys.Count(cache.IsKnownCurrent).Should().BeLessThanOrEqualTo(3);
    }

    [Fact]
    public void VersionMaps_AreBounded_AndPruningKillsEveryExistingVerdict()
    {
        var cache = NewCache(maxVersions: 5);
        var tenant = Guid.NewGuid();
        var bystander = Guid.NewGuid();
        var before = cache.KeyFor(tenant, bystander, "fp");
        cache.RememberCurrent(before);

        for (var i = 0; i < 50; i++) cache.InvalidateUser(Guid.NewGuid());

        cache.VersionEntryCount.Should().BeLessThanOrEqualTo(5 + 1);
        cache.IsKnownCurrent(cache.KeyFor(tenant, bystander, "fp")).Should().BeFalse(
            "a prune resets per-user versions, so it must also bump the global version");
    }

    // ── M2: topology warning ──────────────────────────────────────────────────────────────────

    private static IConfiguration Cfg(params (string, string)[] v) =>
        new ConfigurationBuilder().AddInMemoryCollection(v.ToDictionary(x => x.Item1, x => (string?)x.Item2)).Build();

    [Fact]
    public void Topology_WarnsOnlyWhenMultiInstanceAndEnabled()
    {
        var on = new SessionVerdictCacheOptions { TtlSeconds = 15 };
        SessionVerdictCacheTopology.WarningFor(Cfg(), on).Should().BeNull();
        SessionVerdictCacheTopology.WarningFor(Cfg(("Auth:SessionVerdictCache:InstanceCount", "2")), on)
            .Should().Contain("2 instances").And.Contain("TtlSeconds=0");
        SessionVerdictCacheTopology.WarningFor(Cfg(("WEB_CONCURRENCY", "3")), on).Should().Contain("3 instances");
        SessionVerdictCacheTopology.WarningFor(Cfg(("WEB_CONCURRENCY", "3")), new SessionVerdictCacheOptions { TtlSeconds = 0 })
            .Should().BeNull("the kill switch means there is nothing to warn about");
    }

    // ── M4: compression exclusions ────────────────────────────────────────────────────────────

    private static IResponseCompressionProvider CompressionProvider()
    {
        var services = new ServiceCollection().AddLogging();
        KynexResponseCompressionExtensions.AddKynexResponseCompression(services);
        return services.BuildServiceProvider().GetRequiredService<IResponseCompressionProvider>();
    }

    private static DefaultHttpContext JsonRequest(string path)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Request.Headers.AcceptEncoding = "br, gzip";
        ctx.Response.ContentType = "application/json";
        return ctx;
    }

    [Theory]
    [InlineData("/api/auth/login")]
    [InlineData("/api/auth/refresh")]
    [InlineData("/api/auth/mfa/setup")]
    [InlineData("/api/auth/mfa/backup-codes")]
    [InlineData("/api/platform/auth/login")]
    [InlineData("/api/platform/auth/mfa/setup")]
    public void AuthAndMfaEndpoints_AreNeverCompressed(string path)
    {
        var provider = CompressionProvider();
        var ctx = JsonRequest(path);
        provider.ShouldCompressResponse(ctx).Should().BeFalse();
        provider.GetCompressionProvider(ctx).Should().BeNull();
    }

    [Theory]
    [InlineData("/api/employees")]
    [InlineData("/api/platform/tenants")]
    [InlineData("/api/authority")] // a different segment that merely shares a prefix
    public void OtherJsonEndpoints_AreCompressed(string path)
    {
        var provider = CompressionProvider();
        var ctx = JsonRequest(path);
        provider.ShouldCompressResponse(ctx).Should().BeTrue();
        provider.GetCompressionProvider(ctx).Should().NotBeNull();
    }

    // ── L9: Qiwa timeout is a handled failure ─────────────────────────────────────────────────

    private sealed class ThrowingHandler(Exception toThrow) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw toThrow;
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler) { BaseAddress = new Uri("https://api.qiwa.tech") };
    }

    private static LiveQiwaApiAdapter Adapter(Exception toThrow) => new(new Factory(new ThrowingHandler(toThrow)),
        NullLogger<LiveQiwaApiAdapter>.Instance,
        Cfg((QiwaLiveAdapterPolicy.PartnerAgreementKey, "agreement-1")));

    private static readonly QiwaEmployeePayload Payload = new("E", "1", "NationalId", "SA", "Saudi", "1", "7-1", "W", "C");

    [Fact]
    public async Task HttpClientTimeout_IsReportedAsANetworkErrorNotSuccessOrCancellation()
    {
        var timeout = new TaskCanceledException("timed out", new TimeoutException());
        var adapter = Adapter(timeout);

        var push = await adapter.PushEmployeeAsync("t", Payload, Guid.NewGuid(), CancellationToken.None);
        var status = await adapter.GetEmployeeStatusAsync("t", "7-1", "1", CancellationToken.None);
        var token = await adapter.AcquireAccessTokenAsync("id", "secret", "production", CancellationToken.None);

        push.Success.Should().BeFalse();
        push.ErrorCode.Should().Be("NETWORK_ERROR");
        status.ErrorCode.Should().Be("NETWORK_ERROR");
        token.Should().BeNull();
    }

    [Fact]
    public async Task CallerCancellation_IsNotRecordedAsAQiwaFailure()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var adapter = Adapter(new TaskCanceledException("cancelled", null, cts.Token));

        var act = () => adapter.PushEmployeeAsync("t", Payload, Guid.NewGuid(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
