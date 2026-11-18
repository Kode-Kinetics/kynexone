using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Infrastructure.Government;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Api;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Channels;
using static Zayra.Api.Tests.Government.AnbPayrollApi.AnbApiTestKit;

namespace Zayra.Api.Tests.Government.AnbPayrollApi;

/// <summary>
/// Security P2-C: built the way production will build it (the DI container's <c>gov-bank_anb</c> client from
/// <see cref="GovernmentHttp.AddGovernmentHttpClients"/>), the ANB channel's socket is the guarded one: no redirects, no proxy, the
/// connect-time address check, certificate validation left on. And that guard refuses loopback: a real listener on 127.0.0.1 behind
/// an allow-listed "localhost" origin gets no connection at all.
/// </summary>
public class AnbTransportGuardTests
{
    private static ServiceProvider Container()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        GovernmentHttp.AddGovernmentHttpClients(services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void TheRegisteredAnbClient_IsTheGuardedOne_WithCertificateChecksOn_AndNoRetryHandler()
    {
        using var provider = Container();
        var chain = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(GovernmentHttp.ClientName(GovernmentBodies.BankAnb));

        var handlers = new List<HttpMessageHandler>();
        for (HttpMessageHandler? h = chain; h is not null; h = (h as DelegatingHandler)?.InnerHandler) handlers.Add(h);
        var primary = handlers[^1].Should().BeOfType<SocketsHttpHandler>().Subject;
        primary.AllowAutoRedirect.Should().BeFalse();
        primary.UseProxy.Should().BeFalse();
        primary.UseCookies.Should().BeFalse();
        primary.ConnectCallback.Should().NotBeNull("the SsrfGuard connect-time check");
        primary.SslOptions.RemoteCertificateValidationCallback.Should().BeNull("certificate validation is never switched off");
        handlers.Take(handlers.Count - 1).Should().OnlyContain(h => h.GetType().Namespace!.StartsWith("Microsoft.Extensions.Http", StringComparison.Ordinal),
            "no retry, resilience or logging handler in front of the guarded socket");
    }

    [Fact]
    public async Task ThroughTheRegisteredClient_ALoopbackBankIsNeverConnected_SubmitAndReadAreRefused()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var origin = $"https://localhost:{port}";
        var config = Config();
        config[GovernmentHttp.BaseUrlKey(GovernmentBodies.BankAnb, "sandbox")] = origin;
        config[GovernmentHttp.TokenUrlKey(GovernmentBodies.BankAnb, "sandbox")] = origin;
        using var provider = Container();
        var transport = new GovernmentHttp(provider.GetRequiredService<IHttpClientFactory>(), config);
        var loopbackCredential = new FixedCredentials(new Zayra.Api.Infrastructure.Government.Credentials.UnsealedGovernmentCredential(
            TestScope.TenantId, GovernmentBodies.BankAnb, "sandbox", Zayra.Api.Models.GovernmentCredentialKinds.OAuthClient, TestScope.CompanyId, 1, ClientSecret, ClientId,
            "localhost", "localhost"));
        var batch = BuildBatch(2);
        var receipt = Receipt(batch);
        var channel = AnbGatedChannelFactory.Create(transport, loopbackCredential, new PolicyGate(config, AllOpen), new RecordingLedger(),
            NullLoggerFactory.Instance, limits: new AnbClientLimits(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)));

        var outcome = await channel.SubmitAsync(new BankSubmitRequest(TestScope, new[] { batch.Header, batch.Body }, new BankSubmitOptions(),
            Intent: new BankSubmitIntent(receipt, new Zayra.Api.Application.Auth.RequestContext("127.0.0.1", "test", Guid.NewGuid(), TestScope.TenantId))),
            CancellationToken.None);
        var read = () => channel.GetStatusAsync(TestScope, PayrollId, Binding(batch), CancellationToken.None);

        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Refused, "the token request is refused at connect time, so the POST never starts");
        outcome.RequestSent.Should().BeFalse();
        await read.Should().ThrowAsync<Exception>();
        listener.Pending().Should().BeFalse("the guard refuses a loopback address before connecting");
    }
}
