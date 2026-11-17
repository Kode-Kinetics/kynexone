using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Infrastructure.Qiwa;

namespace Zayra.Api.Tests.Government;

/// <summary>
/// Security R8: <c>LiveQiwaApiAdapter.AcquireAccessTokenAsync</c> took an environment and ignored it, and its named client had
/// the production host as a fixed base address, so a tenant configured "sandbox" would have called production. With no
/// sandbox host configured, a sandbox call must send nothing at all.
/// </summary>
public sealed class QiwaEnvironmentHostTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<Uri> Uris { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uris.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }

    /// <summary>Hands out clients the way Program.cs registered the named "qiwa" client: production base address.</summary>
    private sealed class ProductionBaseFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.qiwa.tech") };
    }

    [Fact]
    public async Task SandboxTokenRequest_NeverReachesTheProductionQiwaHost()
    {
        var handler = new RecordingHandler();
        var adapter = new LiveQiwaApiAdapter(new ProductionBaseFactory(handler), NullLogger<LiveQiwaApiAdapter>.Instance,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { [QiwaLiveAdapterPolicy.PartnerAgreementKey] = "synthetic-agreement" }).Build());

        await adapter.AcquireAccessTokenAsync("synthetic-id", "synthetic-credential", "sandbox", CancellationToken.None);

        handler.Uris.Should().NotContain(u => u.Host == "api.qiwa.tech");
        handler.Uris.Should().BeEmpty("no sandbox host is configured, so a sandbox call must send nothing");
    }
}
