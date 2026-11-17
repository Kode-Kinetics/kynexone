using System.Net;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Government;
using Zayra.Api.Infrastructure.Government.Credentials;
using Zayra.Api.Infrastructure.Qiwa;
using Zayra.Api.Models;
using Zayra.Api.Tests.FieldEncryption;
using static Zayra.Api.Tests.Government.GovernmentLiveGateTests;

namespace Zayra.Api.Tests.Government;

/// <summary>
/// Regression tests for the review findings on the foundation (security review and audit, 2026-11-16). Each one is written
/// against behaviour, so it can be run against the code before the fix and seen to fail there.
/// </summary>
public sealed class GovernmentReviewRegressionTests : Zayra.Api.Tests.Platform.PlatformTestBase
{
    private const string SecretA = "synthetic-secret-tenant-0001";
    private const string SecretB = "synthetic-secret-company-0002";

    private static RequestContext Ctx(Guid tenant, Guid? user = null) => new("127.0.0.1", "test", user ?? Guid.NewGuid(), tenant);

    private static GovernmentCredentialStore Store(FeKit kit, ZayraDbContext db) => new(db, kit.Keys, kit.Kek, kit.Options, new AuditService(db));

    // ── Credentials: an expired company credential never falls back to the tenant-wide one (audit F4) ──────────

    [Fact]
    public async Task AnExpiredCompanyCredential_IsRefused_AndNeverFallsBackToTheTenantWideOne()
    {
        var kit = new FeKit();
        await using var db = kit.Db(interceptors: false);
        var store = Store(kit, db);
        var tenant = Guid.NewGuid();
        var company = Guid.NewGuid();
        db.Companies.Add(new Company { Id = company, TenantId = tenant, LegalNameEn = "Synthetic", IsActive = true });
        await db.SaveChangesAsync();
        await store.SaveAsync(tenant, null, GovernmentBodies.Gosi, "sandbox", GovernmentCredentialKinds.OAuthClient, SecretA, null, Ctx(tenant), default);
        await store.SaveAsync(tenant, company, GovernmentBodies.Gosi, "sandbox", GovernmentCredentialKinds.OAuthClient, SecretB, null, Ctx(tenant), default);
        (await db.GovernmentCredentials.SingleAsync(x => x.CompanyId == company)).ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();

        (await store.OpenAsync(tenant, company, GovernmentBodies.Gosi, "sandbox", default)).Should().BeNull(
            "the company chose its own credential; a date passing must not silently switch it to another app or account");
        (await store.IsUsableAsync(tenant, company, GovernmentBodies.Gosi, "sandbox", default)).Should().BeFalse();
        // A company with no credential of its own still uses the tenant-wide one.
        (await store.OpenAsync(tenant, Guid.NewGuid(), GovernmentBodies.Gosi, "sandbox", default))!.Secret.Should().Be(SecretA);
    }

    [Fact]
    public async Task TheOnlyCredential_Expired_IsNeverOpened()
    {
        var kit = new FeKit();
        await using var db = kit.Db(interceptors: false);
        var store = Store(kit, db);
        var tenant = Guid.NewGuid();
        await store.SaveAsync(tenant, null, GovernmentBodies.Gosi, "sandbox", GovernmentCredentialKinds.ApiKey, SecretA, null, Ctx(tenant), default);
        (await db.GovernmentCredentials.SingleAsync()).ExpiresAtUtc = DateTime.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();

        (await store.OpenAsync(tenant, null, GovernmentBodies.Gosi, "sandbox", default)).Should().BeNull();
        (await store.IsUsableAsync(tenant, null, GovernmentBodies.Gosi, "sandbox", default)).Should().BeFalse();
    }

    // ── Credentials: company-scoped callers (security P2-4, P2-5; audit F5, F6) ───────────────────────────────

    private sealed record World(FeKit Kit, Guid TenantId, Guid CompanyA, Guid CompanyB);

    private static async Task<World> TwoCompanyTenantAsync()
    {
        var kit = new FeKit();
        var w = new World(kit, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await using var db = kit.Db(interceptors: false);
        db.Companies.Add(new Company { Id = w.CompanyA, TenantId = w.TenantId, LegalNameEn = "Synthetic A", IsActive = true });
        db.Companies.Add(new Company { Id = w.CompanyB, TenantId = w.TenantId, LegalNameEn = "Synthetic B", IsActive = true });
        await db.SaveChangesAsync();
        return w;
    }

    private static (ZayraDbContext Db, DefaultHttpContext Http) CompanyUser(World w, Guid company)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", w.TenantId.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "companies", c = new[] { company.ToString() } })),
            new("permission", GovernmentCredentialsController.Permission),
        };
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        return (new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(w.Kit.DbName).Options,
            new HttpContextAccessor { HttpContext = http }), http);
    }

    [Fact]
    public async Task ACompanyScopedUser_OpeningTheTenantWideCredential_GetsIt_AndLastUseIsRecorded()
    {
        var w = await TwoCompanyTenantAsync();
        await using (var system = w.Kit.Db(interceptors: false))
            await Store(w.Kit, system).SaveAsync(w.TenantId, null, GovernmentBodies.Gosi, "sandbox", GovernmentCredentialKinds.OAuthClient, SecretA, null, Ctx(w.TenantId), default);
        var (db, _) = CompanyUser(w, w.CompanyA);
        await using (db)
        {
            var opened = await Store(w.Kit, db).OpenAsync(w.TenantId, w.CompanyA, GovernmentBodies.Gosi, "sandbox", default);
            opened!.Secret.Should().Be(SecretA);
        }
        await using var verify = w.Kit.Db(interceptors: false);
        (await verify.GovernmentCredentials.SingleAsync()).LastUsedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task ACompanyScopedUser_SavingWithoutACompany_TwiceRotatesOneRowOfTheirOwnCompany()
    {
        var w = await TwoCompanyTenantAsync();
        for (var i = 0; i < 2; i++)
        {
            var (db, http) = CompanyUser(w, w.CompanyA);
            await using (db)
            {
                var controller = new GovernmentCredentialsController(Store(w.Kit, db), StepUpTestKit.AlwaysConfirmed(db))
                { ControllerContext = new ControllerContext { HttpContext = http } };
                http.Request.Headers[Zayra.Api.Infrastructure.Auth.StepUpService.HeaderName] = $"synthetic-step-up-{i}";
                var result = await controller.Put(GovernmentBodies.Gosi,
                    new GovernmentCredentialSaveRequest("sandbox", GovernmentCredentialKinds.OAuthClient, i == 0 ? SecretA : SecretB, null, null), default);
                result.Should().BeOfType<OkObjectResult>();
            }
        }
        await using var verify = w.Kit.Db(interceptors: false);
        var rows = await verify.GovernmentCredentials.ToListAsync();
        rows.Should().ContainSingle("the second save is a rotation of the same row, not a second row");
        rows[0].CompanyId.Should().Be(w.CompanyA);
        rows[0].CredentialVersion.Should().Be(2);
        (await Store(w.Kit, verify).OpenAsync(w.TenantId, w.CompanyA, GovernmentBodies.Gosi, "sandbox", default))!.Secret.Should().Be(SecretB);
    }

    // ── Transport: writes need a write decision; redirects are refused; one send (security P1-1, P2-1; audit F2, F10) ──

    private static GovernmentLiveDecision OpenReadDecision() =>
        GovernmentLivePolicy.Decide(GovernmentBodies.Gosi, new GovernmentLiveGates(true, true, true, true));

    private static GovernmentHttp Http(HttpMessageHandler handler) => new(new SingleHandlerFactory(handler),
        Config((GovernmentHttp.BaseUrlKey(GovernmentBodies.Gosi, "sandbox"), "https://gosi-sandbox.example.test")));

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task AWrite_WithAReadDecision_SendsNothing(string method)
    {
        var handler = new CountingHandler();
        var send = async () =>
        {
            using var _ = await Http(handler).ExchangeAsync(OpenReadDecision(), "sandbox", new HttpMethod(method), "/submit", new StringContent("{}"), null, default);
        };
        await send.Should().ThrowAsync<GovernmentTransportRefusedException>();
        handler.Calls.Should().Be(0, "only a live-write decision, which carries the legal clearance, may send a write");
    }

    private sealed class RedirectingHandler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
            response.Headers.Location = new Uri("https://evil.example.test/steal");
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task ARedirect_IsNeverFollowed_AndIsARefusal()
    {
        var handler = new RedirectingHandler();
        var send = async () =>
        {
            using var _ = await Http(handler).ExchangeAsync(OpenReadDecision(), "sandbox", HttpMethod.Get, "/status", null, null, default);
        };
        await send.Should().ThrowAsync<GovernmentTransportRefusedException>();
        handler.Calls.Should().Be(1);
    }

    private sealed class TimingOutHandler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            throw new TaskCanceledException("synthetic timeout", new TimeoutException());
        }
    }

    [Fact]
    public async Task ATimeout_IsSentOnce_AndReachesTheCaller()
    {
        var handler = new TimingOutHandler();
        var send = async () =>
        {
            using var _ = await Http(handler).ExchangeAsync(OpenReadDecision(), "sandbox", HttpMethod.Get, "/status", null, null, default);
        };
        await send.Should().ThrowAsync<TaskCanceledException>();
        handler.Calls.Should().Be(1, "a timeout is an unknown outcome for a person to resolve, never a reason to send again");
    }

    [Theory]
    [MemberData(nameof(BodiesTimesMasks))]
    public void TheRefusalReason_NamesTheFirstClosedGate_ForEveryBody(string body, int mask)
    {
        var gates = new GovernmentLiveGates((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0, (mask & 8) != 0);
        var expected = !gates.OperatorEnabled ? GovernmentLivePolicy.OperatorDisabled
            : !gates.PartnerAgreement ? GovernmentLivePolicy.NoPartnerAgreement
            : !gates.TenantConsent ? GovernmentLivePolicy.TenantNotOptedIn
            : !gates.Credential ? GovernmentLivePolicy.NoCredential
            : null;

        var decision = GovernmentLivePolicy.Decide(body, gates);

        decision.IsOpen.Should().Be(expected is null);
        decision.ReasonCode.Should().Be(expected);
    }

    public static IEnumerable<object[]> BodiesTimesMasks() =>
        GovernmentBodies.All.SelectMany(b => Enumerable.Range(0, 16).Select(m => new object[] { b, m }));

    // ── Consent: Owner only, server-stamped, never reused (security P2-3; audit F13; CTO decision G) ───────────

    private const string TypedConsent =
        "{\"consent\":{\"authorisedBy\":\"Typed Signatory\",\"authorisedOn\":\"2026-11-01\",\"agreementReference\":\"AGR-2026-001\",\"dataCategories\":[\"payroll\"]}}";

    private static async Task<(PlatformController Controller, Guid TenantId, Guid UserId)> PlatformAsync(ZayraDbContext db, string role)
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Synthetic", Slug = $"t-{Guid.NewGuid():N}", IsActive = true };
        db.Tenants.Add(tenant);
        var userId = Guid.NewGuid();
        db.PlatformUsers.Add(new PlatformUser { Id = userId, Email = $"{userId:N}@platform.test", FullName = "Platform person", PasswordHash = "x", Role = role, IsActive = true });
        await db.SaveChangesAsync();
        var controller = CreateController(db, role);
        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", userId.ToString()), new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Email, $"{userId:N}@platform.test"),
            new Claim(ClaimTypes.Role, "PlatformAdmin"), new Claim("is_platform_admin", "true"), new Claim("platform_role", role),
        ], "Test"));
        return (controller, tenant.Id, userId);
    }

    [Fact]
    public async Task APlatformAdmin_CannotSwitchAConnectionOn_ButCanSwitchItOff()
    {
        await using var db = CreateDb();
        var (controller, tenantId, _) = await PlatformAsync(db, PlatformRoles.Admin);

        var on = await controller.SetFeatureFlag(tenantId, FeatureKeys.GovGosiApi, new SetFeatureFlagRequest(true, TypedConsent), default);

        ((ObjectResult)on).StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        (await db.TenantFeatureFlags.CountAsync(f => f.IsEnabled)).Should().Be(0);
        (await controller.SetFeatureFlag(tenantId, FeatureKeys.GovGosiApi, new SetFeatureFlagRequest(false, null), default)).Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task TheServerStampsWhoAndWhen_AndIgnoresTypedOnes()
    {
        await using var db = CreateDb();
        var (controller, tenantId, ownerId) = await PlatformAsync(db, PlatformRoles.Owner);

        (await controller.SetFeatureFlag(tenantId, FeatureKeys.GovGosiApi, new SetFeatureFlagRequest(true, TypedConsent), default)).Should().BeOfType<OkObjectResult>();

        var stored = (await db.TenantFeatureFlags.SingleAsync()).ConfigJson!;
        stored.Should().Contain(ownerId.ToString()).And.Contain("AGR-2026-001").And.NotContain("Typed Signatory").And.NotContain("2026-11-01\"");
        (await GovernmentOptIn.HasConsentAsync(db, tenantId, GovernmentBodies.Gosi, default)).Should().BeTrue();
    }

    [Fact]
    public async Task SwitchingBackOn_NeedsAFreshConsent_TheStoredOneIsNeverReused()
    {
        await using var db = CreateDb();
        var (controller, tenantId, _) = await PlatformAsync(db, PlatformRoles.Owner);
        await controller.SetFeatureFlag(tenantId, FeatureKeys.GovGosiApi, new SetFeatureFlagRequest(true, TypedConsent), default);
        await controller.SetFeatureFlag(tenantId, FeatureKeys.GovGosiApi, new SetFeatureFlagRequest(false, null), default);
        (await db.TenantFeatureFlags.SingleAsync()).ConfigJson.Should().Contain("withdrawnAtUtc", "switching off marks the record withdrawn");

        var again = await controller.SetFeatureFlag(tenantId, FeatureKeys.GovGosiApi, new SetFeatureFlagRequest(true, null), default);

        again.Should().BeOfType<UnprocessableEntityObjectResult>();
        (await GovernmentOptIn.HasConsentAsync(db, tenantId, GovernmentBodies.Gosi, default)).Should().BeFalse();
        (await controller.SetFeatureFlag(tenantId, FeatureKeys.GovGosiApi, new SetFeatureFlagRequest(true, TypedConsent), default)).Should().BeOfType<OkObjectResult>();
    }

    // ── Credential API: one step-up proof per change; the request never prints its secret (security P3-1, P3-2) ──

    [Fact]
    public async Task AStepUpProof_OpensOneChange_NotTwo()
    {
        var kit = new FeKit();
        await using var db = kit.Db(interceptors: false);
        var tenant = Guid.NewGuid();
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("tenant_id", tenant.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "test")),
        };
        http.Request.Headers[Zayra.Api.Infrastructure.Auth.StepUpService.HeaderName] = "synthetic-step-up-proof";
        var controller = new GovernmentCredentialsController(Store(kit, db), StepUpTestKit.AlwaysConfirmed(db))
        { ControllerContext = new ControllerContext { HttpContext = http } };
        var request = new GovernmentCredentialSaveRequest("sandbox", GovernmentCredentialKinds.OAuthClient, SecretA, null, null);

        (await controller.Put(GovernmentBodies.Gosi, request, default)).Should().BeOfType<OkObjectResult>();
        var second = await controller.Put(GovernmentBodies.Gosi, request with { Secret = SecretB }, default);

        ((ObjectResult)second).StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        (await db.GovernmentCredentials.SingleAsync()).CredentialVersion.Should().Be(1);
    }

    [Fact]
    public void TheSaveRequest_NeverPrintsItsSecret() =>
        new GovernmentCredentialSaveRequest("sandbox", GovernmentCredentialKinds.OAuthClient, SecretA, null, null).ToString()
            .Should().NotContain(SecretA);

    // ── Qiwa worker (audit F21; CTO decision F) ─────────────────────────────────────────────────────────────

    private static readonly IDataProtectionProvider Protection = DataProtectionProvider.Create("ZayraGovRegressionTests");

    private sealed class ScriptedAdapter(bool live, QiwaApiResult push) : IQiwaApiAdapter
    {
        public int Tokens, Pushes;
        public string AdapterName => live ? "live-double" : "double";
        public bool IsLiveIntegration => live;
        public Task<string?> AcquireAccessTokenAsync(string clientId, string clientSecret, string environment, CancellationToken ct)
        { Interlocked.Increment(ref Tokens); return Task.FromResult<string?>("synthetic-token"); }
        public Task<QiwaApiResult> PushEmployeeAsync(string accessToken, QiwaEmployeePayload payload, Guid idempotencyKey, CancellationToken ct)
        { Interlocked.Increment(ref Pushes); return Task.FromResult(push); }
        public Task<QiwaApiResult> GetEmployeeStatusAsync(string accessToken, string establishmentId, string employeeIdNumber, CancellationToken ct)
            => Task.FromResult(push);
    }

    internal sealed class SingleDbScopeFactory(ZayraDbContext db) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new DbScope(db);
        private sealed class DbScope(ZayraDbContext db) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = new DbProvider(db);
            public void Dispose() { }
        }
        private sealed class DbProvider(ZayraDbContext db) : IServiceProvider
        {
            public object? GetService(Type serviceType) => serviceType == typeof(ZayraDbContext) ? db : null;
        }
    }

    internal static async Task<(Guid TenantId, Guid LogId)> SeedQiwaAsync(ZayraDbContext db, IDataProtectionProvider protection)
    {
        var tenantId = Guid.NewGuid();
        var employee = new Employee
        {
            Id = Random.Shared.Next(1_000_000, 2_000_000), TenantId = tenantId, EmployeeCode = "EMP-Q1", FullName = "Synthetic Employee",
            Status = "Active", SaudiOrNonSaudi = "Saudi", IdType = "NationalId", IdNumber = "1234567890", Nationality = "Saudi",
            OccupationCode = "2421", EstablishmentId = "7000123456", WorkLocationId = "WL-1", ContractReference = "CONTRACT-1",
        };
        db.Employees.Add(employee);
        db.QiwaTenantConnections.Add(new QiwaTenantConnection { TenantId = tenantId, EstablishmentId = "7000123456", Environment = "sandbox" });
        db.QiwaApiCredentials.Add(new QiwaApiCredential
        {
            TenantId = tenantId, ClientId = "synthetic-client", Environment = "sandbox",
            EncryptedClientSecret = protection.CreateProtector(QiwaIntegrationService.SecretPurpose).Protect("synthetic-secret"),
        });
        var log = new QiwaSyncLog { Id = Guid.NewGuid(), TenantId = tenantId, EmployeeId = employee.Id, Direction = "Push", Status = QiwaSyncLogStatuses.Pending };
        db.QiwaSyncLogs.Add(log);
        await db.SaveChangesAsync();
        return (tenantId, log.Id);
    }

    [Theory]
    [InlineData(GovernmentHttp.HostRefusedCode)]
    [InlineData(GovernmentLivePolicy.RefusedCode)]
    [InlineData(LiveQiwaApiAdapter.EnvironmentRequiredCode)]
    public async Task ARefusedQiwaPush_IsDeadLetteredAtOnce_NotRetried(string code)
    {
        await using var db = CreateDb();
        var (_, logId) = await SeedQiwaAsync(db, Protection);
        var adapter = new ScriptedAdapter(live: false, new QiwaApiResult(false, code, "refused before sending", null));

        await new QiwaSyncWorker(new SingleDbScopeFactory(db), adapter, new QiwaOAuthTokenCache(), Protection, NullLogger<QiwaSyncWorker>.Instance)
            .ProcessOnceAsync(CancellationToken.None);

        var log = await db.QiwaSyncLogs.IgnoreQueryFilters().SingleAsync(l => l.Id == logId);
        log.Status.Should().Be(QiwaSyncLogStatuses.DeadLetter, "a configuration refusal cannot heal by retrying");
        adapter.Pushes.Should().Be(1);
    }

    [Fact]
    public async Task ALiveQiwaAdapter_WithoutAConsentRecord_IsNeverAsked()
    {
        await using var db = CreateDb();
        var (_, logId) = await SeedQiwaAsync(db, Protection);
        var adapter = new ScriptedAdapter(live: true, new QiwaApiResult(true, null, null, "{\"status\":\"ok\"}"));

        await new QiwaSyncWorker(new SingleDbScopeFactory(db), adapter, new QiwaOAuthTokenCache(), Protection, NullLogger<QiwaSyncWorker>.Instance)
            .ProcessOnceAsync(CancellationToken.None);

        adapter.Tokens.Should().Be(0);
        adapter.Pushes.Should().Be(0, "a live push needs the tenant's recorded consent like every other body");
        var log = await db.QiwaSyncLogs.IgnoreQueryFilters().SingleAsync(l => l.Id == logId);
        log.Status.Should().NotBe(QiwaSyncLogStatuses.Success);
        (await db.Employees.IgnoreQueryFilters().SingleAsync()).QiwaSyncStatus.Should().NotBe(QiwaSyncStatuses.Synced);
    }
}
