using System.Diagnostics;
using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Sentry;
using Zayra.Api.Infrastructure.Government;
using Zayra.Api.Infrastructure.Operations;
using static Zayra.Api.Tests.Government.GovernmentLiveGateTests;

namespace Zayra.Api.Tests.Government;

/// <summary>
/// E7: a request to a government or bank body carries nothing of our tracing. No Sentry handler sits on the <c>gov-*</c> clients
/// (it would add <c>sentry-trace</c> and <c>baggage</c> to every call and record an http.client span naming the URL path, which
/// holds a national ID in the Qiwa status lookup), the socket handler propagates no W3C trace context, and the URL an OpenTelemetry
/// span keeps for a government request is the templated route only. Every value below is synthetic; nothing leaves the process.
/// </summary>
[Collection(GovernmentTelemetryCollection.Name)]
public sealed class GovernmentTelemetryIsolationTests
{
    private static readonly string[] TraceHeaders = ["sentry-trace", "baggage", "traceparent", "tracestate"];
    private const string NationalId = "1012345678";
    private const string Establishment = "7000123456";

    /// <summary>Answers 200 and keeps the headers of every request it saw, per client.</summary>
    private sealed class HeaderCapture : HttpMessageHandler
    {
        public List<(Uri Uri, string[] Headers)> Seen { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Seen) Seen.Add((request.RequestUri!, request.Headers.Select(h => h.Key.ToLowerInvariant()).ToArray()));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }

    [Fact]
    public async Task E7_TheGovernmentClients_CarryNoSentryOrTraceHeader_EvenWithSentryTracingSwitchedOn()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
            Args =
            [
                $"--{Observability.SentryDsnKey}=https://publickey@o0.ingest.example.test/1",
                $"--{Observability.SentryTracesSampleRateKey}=1.0",
            ],
        });
        builder.AddKynexObservability();
        GovernmentHttp.AddGovernmentHttpClients(builder.Services);
        var government = new HeaderCapture();
        var control = new HeaderCapture();
        // The test swaps ONLY the primary (socket) handler, as a recorder; every handler Sentry or anything else adds stays in place.
        foreach (var body in GovernmentBodies.All)
            builder.Services.AddHttpClient(GovernmentHttp.ClientName(body)).ConfigurePrimaryHttpMessageHandler(() => government);
        builder.Services.AddHttpClient("not-government").ConfigurePrimaryHttpMessageHandler(() => control);
        await using var app = builder.Build();

        var hub = app.Services.GetRequiredService<IHub>();
        var transaction = hub.StartTransaction("synthetic-request", "http.server");
        hub.ConfigureScope(scope => scope.Transaction = transaction);
        using var incoming = new Activity("synthetic-incoming").Start();
        var factory = app.Services.GetRequiredService<IHttpClientFactory>();
        try
        {
            // The control: an ordinary client in the same app does get Sentry's headers, so Sentry tracing really is on.
            using (await factory.CreateClient("not-government").GetAsync("https://other.example.test/ping")) { }
            control.Seen.Should().ContainSingle().Which.Headers.Should().Contain("sentry-trace",
                "otherwise this test proves nothing: Sentry must be active in this app");

            foreach (var body in GovernmentBodies.All)
                using (await factory.CreateClient(GovernmentHttp.ClientName(body))
                           .GetAsync($"https://{body.Replace('_', '-')}.example.test/api/v1/establishments/{Establishment}/employees/{NationalId}")) { }
            // And through the transport itself, as the Qiwa status lookup sends it.
            var http = new GovernmentHttp(factory, Config((GovernmentHttp.BaseUrlKey(GovernmentBodies.Gosi, "sandbox"), "https://gosi-sandbox.example.test")));
            var read = TelemetryDecisions.OpenRead(GovernmentBodies.Gosi, "sandbox");
            using (await http.ExchangeAsync(read, "sandbox", HttpMethod.Get, $"/api/v1/establishments/{Establishment}/employees/{NationalId}", null, null,
                       CancellationToken.None)) { }
        }
        finally
        {
            hub.ConfigureScope(scope => scope.Transaction = null);
        }

        government.Seen.Should().HaveCount(GovernmentBodies.All.Count + 1);
        foreach (var (uri, headers) in government.Seen)
            headers.Should().NotContain(h => TraceHeaders.Contains(h), $"a request to {uri.Host} must carry no Sentry or trace header");
    }

    [Fact]
    public void E7_TheGovernmentSocketHandler_PropagatesNoTraceContext()
    {
        using var handler = GovernmentHttp.CreateHandler();

        handler.ActivityHeadersPropagator.Should().BeNull("the runtime's own W3C propagation (traceparent, baggage) is switched off for government hosts");
    }

    [Fact]
    public async Task E7_AGovernmentRequest_LeavesWithoutTraceparent_WhileAnOrdinaryOneWouldCarryIt()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        using var incoming = new Activity("synthetic-incoming").SetIdFormat(ActivityIdFormat.W3C).Start();

        // Neither request reaches a network: the address is a loopback literal (the guard refuses it at connect) and the control's
        // connect callback throws. Trace headers are written onto the request BEFORE the connect, so the message shows them.
        var government = await SendAndKeepHeaders(GovernmentHttp.CreateHandler());
        var ordinary = await SendAndKeepHeaders(new SocketsHttpHandler { ConnectCallback = (_, _) => throw new IOException("synthetic: never dial") });

        ordinary.Should().Contain("traceparent", "the control shows the runtime would propagate the trace context");
        government.Should().NotContain(h => TraceHeaders.Contains(h));
    }

    private static async Task<string[]> SendAndKeepHeaders(SocketsHttpHandler handler)
    {
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:9/api/v1/establishments/{Establishment}/employees/{NationalId}");
        try { using var _ = await client.SendAsync(request); }
        catch (HttpRequestException) { }
        catch (IOException) { }
        catch (OperationCanceledException) { }
        return request.Headers.Select(h => h.Key.ToLowerInvariant()).ToArray();
    }

    [Fact]
    public async Task E7_TheUrlOfAGovernmentRequest_IsScrubbedInTheTelemetrySpan()
    {
        // The URL exactly as the transport builds it for a Qiwa status lookup and a bank status poll.
        var capture = new CountingHandler();
        var http = new GovernmentHttp(new SingleHandlerFactory(capture), Config(
            (GovernmentHttp.BaseUrlKey(GovernmentBodies.Qiwa, "sandbox"), "https://qiwa-sandbox.example.test"),
            (GovernmentHttp.BaseUrlKey(GovernmentBodies.BankAnb, "sandbox"), "https://bank-sandbox.example.test")));
        using (await http.ExchangeAsync(TelemetryDecisions.OpenRead(GovernmentBodies.Qiwa, "sandbox"), "sandbox", HttpMethod.Get,
                   $"/api/v1/establishments/{Establishment}/employees/{NationalId}?nationalId={NationalId}", null, null, CancellationToken.None)) { }
        using (await http.ExchangeAsync(TelemetryDecisions.OpenRead(GovernmentBodies.BankAnb, "sandbox"), "sandbox", HttpMethod.Get,
                   "/payroll/v2/payrolls/88001234/status?iban=SA0380000000608010167519", null, null, CancellationToken.None)) { }

        var services = new ServiceCollection();
        OpenTelemetryRegistration.Register(services);
        using var provider = services.BuildServiceProvider();
        var enrich = provider.GetRequiredService<IOptionsMonitor<OpenTelemetry.Instrumentation.Http.HttpClientTraceInstrumentationOptions>>()
            .Get(Options.DefaultName).EnrichWithHttpRequestMessage;
        enrich.Should().NotBeNull();

        capture.Uris.Should().HaveCount(2);
        foreach (var uri in capture.Uris)
        {
            using var span = new Activity("GET");
            span.SetTag("url.full", uri.ToString());
            span.SetTag("http.url", uri.ToString());
            span.SetTag("url.query", uri.Query);
            enrich!(span, new HttpRequestMessage(HttpMethod.Get, uri));
            var kept = string.Join("|", span.Tags.Select(t => t.Value));
            kept.Should().NotContainAny(NationalId, Establishment, "88001234", "SA0380000000608010167519", "iban", "nationalId");
            span.GetTagItem("url.full").Should().BeOfType<string>().Which.Should().StartWith("https://").And.Contain("{id}");
        }
    }
}

/// <summary>The telemetry tests start activities and a Sentry hub: they run on their own, never next to another test.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GovernmentTelemetryCollection
{
    public const string Name = "GovernmentTelemetry";
}

/// <summary>Open read decisions for the telemetry tests, made the only way there is: by the policy.</summary>
internal static class TelemetryDecisions
{
    public static GovernmentLiveDecision OpenRead(string body, string environment) =>
        GovernmentLivePolicy.Decide(body, new GovernmentLiveGates(true, true, true, true), environment);
}
