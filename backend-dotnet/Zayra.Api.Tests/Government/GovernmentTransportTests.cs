using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Government;
using Zayra.Api.Infrastructure.Government.Credentials;
using Zayra.Api.Infrastructure.Qiwa;
using Zayra.Api.Models;
using static Zayra.Api.Tests.Government.GovernmentLiveGateTests;

namespace Zayra.Api.Tests.Government;

/// <summary>
/// The one government transport: the registered clients are guarded (no redirects, no proxy, no cookies, connect-time address
/// check), a credential is bound to its body, environment and host, the Qiwa worker builds its token key from the environment and
/// the credential's version, and a live Qiwa call runs only through the tenant's real gate.
/// </summary>
public sealed class GovernmentTransportTests
{
    [Fact]
    public void EveryRegisteredGovernmentClient_UsesTheGuardedHandler()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        GovernmentHttp.AddGovernmentHttpClients(services);
        using var provider = services.BuildServiceProvider();
        var handlers = provider.GetRequiredService<IHttpMessageHandlerFactory>();

        foreach (var body in GovernmentBodies.All)
        {
            var chain = handlers.CreateHandler(GovernmentHttp.ClientName(body));
            GovernmentHttp.FollowsRedirects(chain).Should().BeFalse($"{body} must never follow a redirect");
            var primary = Primary(chain).Should().BeOfType<SocketsHttpHandler>().Subject;
            (primary.UseProxy, primary.UseCookies, primary.AutomaticDecompression).Should().Be((false, false, DecompressionMethods.None));
            primary.ConnectCallback.Should().NotBeNull("the connect-time address check refuses internal and metadata addresses");
        }
    }

    private static HttpMessageHandler Primary(HttpMessageHandler handler)
    {
        while (handler is DelegatingHandler delegating && delegating.InnerHandler is not null) handler = delegating.InnerHandler;
        return handler;
    }

    [Fact]
    public void TheHandler_IsTheSameGuardTheBankClientNeeds()
    {
        var handler = GovernmentHttp.CreateHandler();
        (handler.AllowAutoRedirect, handler.UseProxy, handler.UseCookies).Should().Be((false, false, false));
        handler.MaxResponseHeadersLength.Should().Be(64);
        handler.ConnectCallback.Should().NotBeNull();
        GovernmentHttp.FollowsRedirects(new HttpClientHandler { AllowAutoRedirect = true }).Should().BeTrue();
    }

    [Fact]
    public void ACredential_IsBoundToItsBodyEnvironmentAndHost()
    {
        var http = new GovernmentHttp(new SingleHandlerFactory(new CountingHandler()),
            Config((GovernmentHttp.BaseUrlKey(GovernmentBodies.BankAnb, "sandbox"), "https://api.bank-sandbox.example.test")));
        UnsealedGovernmentCredential Credential(string env, string? host) =>
            new(GovernmentBodies.BankAnb, env, GovernmentCredentialKinds.OAuthClient, null, 1, "synthetic", "client", host);

        http.RequireBoundTo(Credential("sandbox", "api.bank-sandbox.example.test"), GovernmentBodies.BankAnb, "sandbox");
        var production = () => http.RequireBoundTo(Credential("sandbox", null), GovernmentBodies.BankAnb, "production");
        production.Should().Throw<GovernmentTransportRefusedException>().Which.Code.Should().Be(GovernmentHttp.CredentialBindingCode);
        var otherHost = () => http.RequireBoundTo(Credential("sandbox", "api.other.example.test"), GovernmentBodies.BankAnb, "sandbox");
        otherHost.Should().Throw<GovernmentTransportRefusedException>().Which.Code.Should().Be(GovernmentHttp.CredentialBindingCode);
        var otherBody = () => http.RequireBoundTo(Credential("sandbox", null), GovernmentBodies.Gosi, "sandbox");
        otherBody.Should().Throw<GovernmentTransportRefusedException>();
    }

    [Theory]
    [InlineData("/a/../b")]
    [InlineData("/a/./b")]
    public async Task ADotSegment_IsRefused(string path)
    {
        var handler = new CountingHandler();
        var http = new GovernmentHttp(new SingleHandlerFactory(handler),
            Config((GovernmentHttp.BaseUrlKey(GovernmentBodies.Gosi, "sandbox"), "https://gosi-sandbox.example.test")));
        var decision = GovernmentLivePolicy.Decide(GovernmentBodies.Gosi, new GovernmentLiveGates(true, true, true, true));
        var act = async () => { using var _ = await http.ExchangeAsync(decision, "sandbox", HttpMethod.Get, path, null, null, default); };
        await act.Should().ThrowAsync<GovernmentTransportRefusedException>();
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public void TheWorkersTokenKey_ChangesWithTheEnvironmentAndTheCredential()
    {
        var tenant = Guid.NewGuid();
        var credential = new QiwaApiCredential { TenantId = tenant, ClientId = "c", CreatedAtUtc = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc) };
        var before = QiwaSyncWorker.TokenKey(tenant, "sandbox", credential);

        QiwaSyncWorker.TokenKey(tenant, "production", credential).Should().NotBe(before);
        credential.UpdatedAtUtc = credential.CreatedAtUtc.AddMinutes(5); // a rotation saves the credential again
        QiwaSyncWorker.TokenKey(tenant, "sandbox", credential).Should().NotBe(before);
        QiwaSyncWorker.TokenKey(Guid.NewGuid(), "sandbox", credential).TenantId.Should().NotBe(tenant);
    }

    // ── Qiwa live path: the real gate (CTO decision F) ─────────────────────────────────────────────────────

    private sealed class LiveDouble : IQiwaApiAdapter
    {
        public List<string?> Environments { get; } = [];
        public string AdapterName => "live-double";
        public bool IsLiveIntegration => true;
        public Task<string?> AcquireAccessTokenAsync(string clientId, string clientSecret, string environment, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<string?> AcquireAccessTokenAsync(GovernmentLiveDecision decision, string clientId, string clientSecret, CancellationToken ct)
            => Task.FromResult<string?>(decision.AllowsWrites ? "synthetic-token" : null);
        public Task<QiwaApiResult> PushEmployeeAsync(string accessToken, QiwaEmployeePayload payload, Guid idempotencyKey, CancellationToken ct)
            => Task.FromResult(new QiwaApiResult(false, "NO_DECISION", null, null));
        public Task<QiwaApiResult> PushEmployeeAsync(GovernmentLiveDecision decision, string accessToken, QiwaEmployeePayload payload, Guid idempotencyKey, CancellationToken ct)
        {
            Environments.Add(decision.Environment);
            return Task.FromResult(new QiwaApiResult(decision.AllowsWrites, null, null, "{\"status\":\"ok\"}"));
        }
        public Task<QiwaApiResult> GetEmployeeStatusAsync(string accessToken, string establishmentId, string employeeIdNumber, CancellationToken ct)
            => Task.FromResult(new QiwaApiResult(false, null, null, null));
    }

    [Fact]
    public async Task ALiveQiwaPush_RunsOnlyWithTheTenantsConsent_AgreementSwitchAndClearance()
    {
        var protection = DataProtectionProvider.Create("ZayraGovTransportTests");
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var (tenantId, logId) = await GovernmentReviewRegressionTests.SeedQiwaAsync(db, protection);
        db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = tenantId, FeatureKey = FeatureKeys.GovQiwaApi, IsEnabled = true, ConfigJson = GovernmentApiTests.StampedConsent("identity") });
        await db.SaveChangesAsync();
        var config = Config((QiwaLiveAdapterPolicy.LiveSwitchEnvVar, "true"), (QiwaLiveAdapterPolicy.PartnerAgreementKey, "AGR-SYNTHETIC"),
            (GovernmentLivePolicy.LegalClearanceKey(GovernmentBodies.Qiwa), "LEGAL-SYNTHETIC-1"));
        var adapter = new LiveDouble();

        await new QiwaSyncWorker(new GovernmentReviewRegressionTests.SingleDbScopeFactory(db), adapter, new QiwaOAuthTokenCache(), protection,
            NullLogger<QiwaSyncWorker>.Instance, configuration: config).ProcessOnceAsync(CancellationToken.None);

        adapter.Environments.Should().Equal("sandbox");
        (await db.QiwaSyncLogs.IgnoreQueryFilters().SingleAsync(l => l.Id == logId)).Status.Should().Be(QiwaSyncLogStatuses.Success);
    }
}
