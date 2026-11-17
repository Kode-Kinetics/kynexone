using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Infrastructure.Government;
using Zayra.Api.Infrastructure.Qiwa;
using static Zayra.Api.Tests.Government.GovernmentLiveGateTests;

namespace Zayra.Api.Tests.Government;

/// <summary>
/// GovernmentHttp (architecture 3.2, A5): the base URL comes only from a per-environment allow-list, never from request data;
/// a POST is sent once and never retried; token caches are keyed tenant + body + environment + credential version. And the
/// Qiwa environment defect (security R8): a tenant configured "sandbox" must never reach the production Qiwa host.
/// </summary>
public sealed class GovernmentHttpTests
{
    private const string QiwaProductionHost = "api.qiwa.tech";
    private static readonly GovernmentLiveDecision Open = GovernmentLivePolicy.Decide(GovernmentBodies.Gosi, new GovernmentLiveGates(true, true, true, true));

    [Fact]
    public void BaseUrl_ComesFromTheAllowList_PerEnvironment_AndAnUnlistedEnvironmentIsRefused()
    {
        var http = new GovernmentHttp(new SingleHandlerFactory(new CountingHandler()),
            Config((GovernmentHttp.BaseUrlKey(GovernmentBodies.Gosi, "sandbox"), "https://gosi-sandbox.example.test/")));

        http.ResolveBaseUrl(GovernmentBodies.Gosi, "sandbox").Host.Should().Be("gosi-sandbox.example.test");
        var production = () => http.ResolveBaseUrl(GovernmentBodies.Gosi, "production");
        production.Should().Throw<GovernmentTransportRefusedException>().Which.Code.Should().Be(GovernmentHttp.HostRefusedCode);
        var unknownEnv = () => http.ResolveBaseUrl(GovernmentBodies.Gosi, "test");
        unknownEnv.Should().Throw<GovernmentTransportRefusedException>();
    }

    [Theory]
    [InlineData("http://gosi-sandbox.example.test")]
    [InlineData("https://user:pw@gosi-sandbox.example.test")]
    [InlineData("https://gosi-sandbox.example.test/?x=1")]
    [InlineData("not a url")]
    public void BaseUrl_MustBeAPlainHttpsOrigin(string configured)
    {
        var http = new GovernmentHttp(new SingleHandlerFactory(new CountingHandler()),
            Config((GovernmentHttp.BaseUrlKey(GovernmentBodies.Gosi, "sandbox"), configured)));
        var act = () => http.ResolveBaseUrl(GovernmentBodies.Gosi, "sandbox");
        act.Should().Throw<GovernmentTransportRefusedException>();
    }

    [Fact]
    public void ASandboxUrl_PointingAtAProductionHost_IsRefused()
    {
        var http = new GovernmentHttp(new SingleHandlerFactory(new CountingHandler()),
            Config((GovernmentHttp.BaseUrlKey(GovernmentBodies.Qiwa, "sandbox"), "https://" + QiwaProductionHost)));
        var act = () => http.ResolveBaseUrl(GovernmentBodies.Qiwa, "sandbox");
        act.Should().Throw<GovernmentTransportRefusedException>();
    }

    [Theory]
    [InlineData("//evil.example.test/x")]
    [InlineData("https://evil.example.test/x")]
    [InlineData("status")]
    public async Task ARelativePath_CannotChangeTheHost(string path)
    {
        var handler = new CountingHandler();
        var http = new GovernmentHttp(new SingleHandlerFactory(handler),
            Config((GovernmentHttp.BaseUrlKey(GovernmentBodies.Gosi, "sandbox"), "https://gosi-sandbox.example.test")));
        var act = async () => { using var _ = await http.ExchangeAsync(Open, "sandbox", HttpMethod.Get, path, null, null, default); };
        await act.Should().ThrowAsync<GovernmentTransportRefusedException>();
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task APost_ThatFails_IsSentExactlyOnce_NeverRetried()
    {
        var handler = new CountingHandler(HttpStatusCode.ServiceUnavailable);
        var factory = new SingleHandlerFactory(handler);
        var http = new GovernmentHttp(factory, Config((GovernmentHttp.BaseUrlKey(GovernmentBodies.Gosi, "sandbox"), "https://gosi-sandbox.example.test")));

        using var response = await http.ExchangeAsync(Open, "sandbox", HttpMethod.Post, "/submit", new StringContent("{}"), null, default);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        handler.Calls.Should().Be(1);
        handler.Uris.Single().Should().Be(new Uri("https://gosi-sandbox.example.test/submit"));
        factory.Names.Should().Equal(GovernmentHttp.ClientName(GovernmentBodies.Gosi));
    }

    [Fact]
    public void TokenCacheKey_SeparatesEnvironmentAndCredentialVersion()
    {
        var tenant = Guid.NewGuid();
        var cache = new QiwaOAuthTokenCache();
        var sandboxV1 = new GovernmentTokenCacheKey(tenant, GovernmentBodies.Qiwa, "sandbox", "v1");
        cache.Set(sandboxV1, "sandbox-token", 300);

        cache.TryGet(sandboxV1).Should().Be("sandbox-token");
        cache.TryGet(sandboxV1 with { Environment = "production" }).Should().BeNull("a sandbox token must never be used against production");
        cache.TryGet(sandboxV1 with { CredentialVersion = "v2" }).Should().BeNull("a rotated credential must not reuse the old token");
        cache.TryGet(sandboxV1 with { TenantId = Guid.NewGuid() }).Should().BeNull();

        cache.Invalidate(tenant);
        cache.TryGet(sandboxV1).Should().BeNull();
    }

    // ── Security R8: the Qiwa environment is honoured ────────────────────────────────────────────────────

    private static LiveQiwaApiAdapter QiwaAdapter(HttpMessageHandler handler, params (string, string?)[] extra) =>
        new(new SingleHandlerFactory(handler, new Uri("https://" + QiwaProductionHost)), NullLogger<LiveQiwaApiAdapter>.Instance,
            Config([(QiwaLiveAdapterPolicy.PartnerAgreementKey, "synthetic-agreement"), .. extra]));

    private static readonly QiwaEmployeePayload Payload = new("E", "1", "NationalId", "SA", "Saudi", "1", "7-1", "W", "C");

    [Fact]
    public async Task Qiwa_SandboxEnvironment_NeverReachesTheProductionHost()
    {
        var handler = new CountingHandler();
        var adapter = QiwaAdapter(handler);

        var token = await adapter.AcquireAccessTokenAsync("synthetic-id", "synthetic-credential", "sandbox", default);
        var push = await adapter.PushEmployeeAsync("synthetic-token", "sandbox", Payload, Guid.NewGuid(), default);
        var status = await adapter.GetEmployeeStatusAsync("synthetic-token", "sandbox", "7-1", "1", default);

        token.Should().BeNull();
        push.Success.Should().BeFalse();
        status.Success.Should().BeFalse();
        handler.Uris.Should().NotContain(u => u.Host == QiwaProductionHost);
        handler.Calls.Should().Be(0, "no sandbox host is configured, so nothing may be sent at all");
    }

    [Fact]
    public async Task Qiwa_SandboxEnvironment_GoesToTheConfiguredSandboxHost()
    {
        var handler = new CountingHandler();
        var adapter = QiwaAdapter(handler, (GovernmentHttp.BaseUrlKey(GovernmentBodies.Qiwa, "sandbox"), "https://qiwa-sandbox.example.test"));

        await adapter.PushEmployeeAsync("synthetic-token", "sandbox", Payload, Guid.NewGuid(), default);

        handler.Uris.Should().ContainSingle().Which.Host.Should().Be("qiwa-sandbox.example.test");
    }

    [Fact]
    public async Task Qiwa_ProductionEnvironment_KeepsItsCurrentHost()
    {
        var handler = new CountingHandler();
        var adapter = QiwaAdapter(handler);

        await adapter.PushEmployeeAsync("synthetic-token", "production", Payload, Guid.NewGuid(), default);

        handler.Uris.Should().ContainSingle().Which.Should().Be(new Uri("https://api.qiwa.tech/api/v1/establishments/7-1/employees"));
    }

    [Fact]
    public async Task Qiwa_ACallWithoutAnEnvironment_SendsNothing()
    {
        var handler = new CountingHandler();
        var adapter = QiwaAdapter(handler);

        var push = await adapter.PushEmployeeAsync("synthetic-token", Payload, Guid.NewGuid(), default);
        var status = await adapter.GetEmployeeStatusAsync("synthetic-token", "7-1", "1", default);

        push.ErrorCode.Should().Be(LiveQiwaApiAdapter.EnvironmentRequiredCode);
        status.ErrorCode.Should().Be(LiveQiwaApiAdapter.EnvironmentRequiredCode);
        handler.Calls.Should().Be(0);
    }
}
