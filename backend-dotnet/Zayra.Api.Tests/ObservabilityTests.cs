using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Zayra.Api.Infrastructure.Operations;
using Zayra.Api.Infrastructure.Qiwa;
using Zayra.Api.Tests.Security;

namespace Zayra.Api.Tests;

public sealed class ObservabilityTests
{
    [Fact]
    public async Task WithoutAnOtlpEndpoint_NothingIsRegistered()
    {
        await using var app = Build(Environments.Production);
        app.Services.GetService<TracerProvider>().Should().BeNull("no endpoint means no OpenTelemetry at all");
        app.Services.GetService<MeterProvider>().Should().BeNull();
    }

    [Fact]
    public async Task WithAnOtlpEndpoint_TracingAndMetricsAreRegistered()
    {
        await using var app = Build(Environments.Production, $"--{Observability.OtlpEndpointKey}=http://127.0.0.1:4317");
        app.Services.GetService<TracerProvider>().Should().NotBeNull();
        app.Services.GetService<MeterProvider>().Should().NotBeNull();
    }

    /// <summary>End to end: with the endpoint set, a database span reaches an OTLP/HTTP collector.</summary>
    [Fact]
    public async Task WithAnOtlpEndpoint_SpansAreExportedToTheCollector()
    {
        using var collector = new System.Net.HttpListener();
        var port = FreePort();
        collector.Prefixes.Add($"http://127.0.0.1:{port}/");
        collector.Start();
        // Metrics and logs may arrive too, in any order; answer everything until traces show up.
        var received = Task.Run(async () =>
        {
            while (true)
            {
                var context = await collector.GetContextAsync();
                var path = context.Request.Url!.AbsolutePath;
                context.Response.StatusCode = 200;
                context.Response.Close();
                if (path == "/v1/traces") return path;
            }
        });

        var app = Build(Environments.Production,
            $"--{Observability.OtlpEndpointKey}=http://127.0.0.1:{port}",
            "--OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf");
        await app.StartAsync();
        using (var source = new ActivitySource(Observability.NpgsqlSource))
        using (source.StartActivity("SELECT 1")) { }
        await app.StopAsync();
        await app.DisposeAsync(); // flushes the batch processor

        (await received.WaitAsync(TimeSpan.FromSeconds(15))).Should().Be("/v1/traces");
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Production today: the real Program.cs composition, no endpoint, so no OpenTelemetry.</summary>
    [Fact]
    public async Task ProgramComposition_WithoutEndpoint_RegistersNoTelemetryPipeline()
    {
        var connectionString = $"Data Source=file:otel-{Guid.NewGuid():N}?mode=memory&cache=shared";
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();
        await using var host = new AuthorizationPipelineHost(connectionString);
        _ = host.CreateClient();
        host.Services.GetService<TracerProvider>().Should().BeNull();
    }

    [Fact]
    public async Task ConsoleLogsAreJsonOutsideDevelopment_TextInDevelopment_AndOverridable()
    {
        await using (var prod = Build(Environments.Production))
            FormatterName(prod).Should().Be(ConsoleFormatterNames.Json);
        await using (var dev = Build(Environments.Development))
            FormatterName(dev).Should().NotBe(ConsoleFormatterNames.Json);
        await using (var optedOut = Build(Environments.Production, "--Logging:Console:FormatterName=simple"))
            FormatterName(optedOut).Should().NotBe(ConsoleFormatterNames.Json);
    }

    [Fact]
    public async Task RequestLogsCarryTenantIdAndTraceId_AndTheSpanCarriesTheTenant()
    {
        var tenantId = Guid.NewGuid().ToString();
        var capture = new ScopeCapturingProvider();
        Activity? requestActivity = null;

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(capture);
        builder.AddKynexObservability();
        await using var app = builder.Build();
        // Stand-in for UseAuthentication: a validated principal with the tenant claim.
        app.Use((context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tenant_id", tenantId)], "test"));
            return next();
        });
        app.UseTenantLogScope();
        app.MapGet("/probe", (ILogger<ObservabilityTests> log) =>
        {
            requestActivity = Activity.Current;
            log.LogInformation("probe handled");
            return Results.Ok();
        });
        await app.StartAsync();

        // No ActivityListener and no OpenTelemetry: the production default. The host still creates the
        // request Activity (its own diagnostics logger is enabled), so TraceId is in scope regardless.
        var response = await app.GetTestClient().GetAsync("/probe");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var entry = capture.Entries.Single(e => e.Message == "probe handled");
        entry.Scope.Should().ContainKey("TenantId").WhoseValue.Should().Be(tenantId);
        entry.Scope.Should().ContainKey("TraceId");
        requestActivity.Should().NotBeNull();
        requestActivity!.GetTagItem(Observability.TenantTag).Should().Be(tenantId);
        await app.StopAsync();
    }

    [Theory]
    [InlineData("""{"error":"invalid_client","error_description":"client 1234 for ops@example.com"}""", "invalid_client")]
    [InlineData("""{"error":"x@y.com"}""", "unparsed")]
    [InlineData("<html>upstream</html>", "unparsed")]
    public void QiwaTokenFailureLogsOnlyTheOAuthErrorCode(string body, string expected) =>
        LiveQiwaApiAdapter.OAuthErrorCode(body).Should().Be(expected);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static WebApplication Build(string environment, params string[] args)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment, Args = args });
        builder.AddKynexObservability();
        return builder.Build();
    }

    private static string? FormatterName(WebApplication app) =>
        app.Services.GetRequiredService<IOptionsMonitor<ConsoleLoggerOptions>>().CurrentValue.FormatterName;

    private sealed record Entry(string Message, IReadOnlyDictionary<string, object?> Scope);

    private sealed class ScopeCapturingProvider : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();
        public List<Entry> Entries { get; } = [];
        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);
        public void Dispose() { }

        private sealed class CapturingLogger(ScopeCapturingProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner._scopes.Push(state);
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var scope = new Dictionary<string, object?>();
                owner._scopes.ForEachScope((s, acc) =>
                {
                    if (s is IEnumerable<KeyValuePair<string, object?>> pairs)
                        foreach (var (k, v) in pairs) acc[k] = v?.ToString();
                    else if (s is IEnumerable<KeyValuePair<string, object>> pairs2)
                        foreach (var (k, v) in pairs2) acc[k] = v?.ToString();
                }, scope);
                lock (owner.Entries) owner.Entries.Add(new Entry(formatter(state, exception), scope));
            }
        }
    }
}
