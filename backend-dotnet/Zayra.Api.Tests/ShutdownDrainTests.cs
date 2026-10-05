using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Zayra.Api.Infrastructure.Operations;
using Zayra.Api.Tests.Security;

namespace Zayra.Api.Tests;

/// <summary>
/// Graceful drain: on shutdown /health/ready must say 503 BEFORE the server stops accepting, the
/// instance must keep serving during the drain delay, and in-flight requests must finish.
/// /health/live must not change (a draining instance is alive).
/// </summary>
public sealed class ShutdownDrainTests
{
    [Fact]
    public void Defaults_AreNoDrainDelayInAnyEnvironment_AndThirtySecondShutdown()
    {
        var empty = new ConfigurationBuilder().Build();
        new ShutdownDrain(empty, new Env(Environments.Production), NullLogger<ShutdownDrain>.Instance)
            .ReadinessDrainDelay.Should().Be(TimeSpan.Zero,
                "a single instance with no deploy overlap gains nothing from a drain delay; it is opt-in");
        new ShutdownDrain(empty, new Env(Environments.Development), NullLogger<ShutdownDrain>.Instance)
            .ReadinessDrainDelay.Should().Be(TimeSpan.Zero);
        ShutdownDrain.ShutdownTimeout(empty).Should().Be(TimeSpan.FromSeconds(30));

        var tuned = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Shutdown:ReadinessDrainSeconds"] = "12",
            ["Shutdown:TimeoutSeconds"] = "45",
        }).Build();
        new ShutdownDrain(tuned, new Env(Environments.Production), NullLogger<ShutdownDrain>.Instance)
            .ReadinessDrainDelay.Should().Be(TimeSpan.FromSeconds(12));
        ShutdownDrain.ShutdownTimeout(tuned).Should().Be(TimeSpan.FromSeconds(45));
    }

    /// <summary>
    /// The real Program.cs composition: HostOptions carries the 30s budget, /health/ready answers
    /// 503 "draining" once draining begins, and /health/live stays 200.
    /// </summary>
    [Fact]
    public async Task ProgramComposition_ReadinessReportsDrainingWhileDraining_LivenessUnchanged()
    {
        var connectionString = $"Data Source=file:drain-{Guid.NewGuid():N}?mode=memory&cache=shared";
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();
        await using var host = new AuthorizationPipelineHost(connectionString);
        using var client = host.CreateClient();

        host.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout
            .Should().Be(TimeSpan.FromSeconds(30));
        var before = await client.GetAsync("/health/ready");
        (await before.Content.ReadAsStringAsync()).Should().NotContain("draining");

        // The test host disposes its container the moment the application stops, so the HTTP half
        // drives the drain flag directly; the next test proves Program.cs attaches it to Stopping.
        host.Services.GetRequiredService<ShutdownDrain>().BeginDrain();

        var ready = await client.GetAsync("/health/ready");
        ready.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, await ready.Content.ReadAsStringAsync());
        using (var body = JsonDocument.Parse(await ready.Content.ReadAsStringAsync()))
            body.RootElement.GetProperty("status").GetString().Should().Be("draining");

        var live = await client.GetAsync("/health/live");
        live.StatusCode.Should().Be(HttpStatusCode.OK, "a draining instance is alive and must not be restarted");
    }

    [Fact]
    public async Task ProgramComposition_AttachesTheDrainToApplicationStopping()
    {
        var connectionString = $"Data Source=file:drain-{Guid.NewGuid():N}?mode=memory&cache=shared";
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();
        await using var host = new AuthorizationPipelineHost(connectionString);
        _ = host.CreateClient();
        var drain = host.Services.GetRequiredService<ShutdownDrain>();
        drain.IsDraining.Should().BeFalse();

        host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();

        drain.IsDraining.Should().BeTrue("SIGTERM must flip readiness before the server stops");
    }

    /// <summary>
    /// The mechanism on real Kestrel, with the same wiring Program.cs uses: during the drain delay the
    /// server still accepts and answers readiness with 503, and a request that was in flight when
    /// shutdown began completes normally.
    /// </summary>
    [Fact]
    public async Task OnKestrel_ReadinessGoes503WhileStillServing_ThenInFlightRequestCompletes()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration["Shutdown:ReadinessDrainSeconds"] = "1";
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<ShutdownDrain>();
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = ShutdownDrain.ShutdownTimeout(builder.Configuration));
        await using var app = builder.Build();
        var drain = app.Services.GetRequiredService<ShutdownDrain>();
        drain.Attach(app.Lifetime);

        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.MapGet("/slow", async () =>
        {
            slowStarted.SetResult();
            await Task.Delay(TimeSpan.FromMilliseconds(1500));
            return Results.Text("done");
        });
        app.MapGet("/ready", (ShutdownDrain d) => d.IsDraining ? Results.StatusCode(503) : Results.Ok());
        await app.StartAsync();
        var baseAddress = new Uri(app.Urls.First());

        using var inflightClient = new HttpClient { BaseAddress = baseAddress };
        using var probeClient = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.Zero }) { BaseAddress = baseAddress };
        (await probeClient.GetAsync("/ready")).StatusCode.Should().Be(HttpStatusCode.OK);

        var slow = inflightClient.GetAsync("/slow");
        await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stopping = Task.Run(() => app.StopAsync());

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!drain.IsDraining && DateTime.UtcNow < deadline) await Task.Delay(10);
        drain.IsDraining.Should().BeTrue();
        (await probeClient.GetAsync("/ready")).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            "readiness flips first, while the server is still accepting connections");

        var response = await slow.WaitAsync(TimeSpan.FromSeconds(10));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("done", "the in-flight request drains rather than being cut");
        await stopping.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
