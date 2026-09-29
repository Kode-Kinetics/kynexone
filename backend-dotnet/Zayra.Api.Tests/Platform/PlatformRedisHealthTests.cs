using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Controllers;

namespace Zayra.Api.Tests.Platform;

/// <summary>
/// GET /platform/health — the distributed-cache component.
///
/// <para>The check used to resolve <c>IConnectionMultiplexer</c>. Nothing in the composition root
/// has ever registered that interface — <c>AddStackExchangeRedisCache</c> registers
/// <c>IDistributedCache</c> and nothing else, and the entire repository held exactly one
/// reference to <c>IConnectionMultiplexer</c>: that line. So the probe returned "not_configured"
/// unconditionally. It could not report "ok" with Redis live, and could not report a failure with
/// Redis down. A check with no reachable failing state and no reachable passing state is not a
/// check.</para>
///
/// <para>Confirmed against the live Render service (srv-d8slkb77f7vs73d2k92g, 2026-09-21): it
/// declares no REDIS_URL and no Redis variable of any kind, so production genuinely runs on the
/// in-memory fallback. Registering a multiplexer would therefore have added a dependency nothing
/// connects to; the honest fix is for the check to interrogate what IS registered. These tests
/// pin all four verdicts, including the two the old implementation could never produce.</para>
/// </summary>
public class PlatformRedisHealthTests : PlatformTestBase
{
    [Fact]
    public async Task Health_WithInMemoryFallback_ReportsFallbackMemory()
    {
        var status = await RedisStatusAsync(services => services.AddDistributedMemoryCache());

        status.Should().Be("fallback_memory",
            "no REDIS_URL means Program.cs registered MemoryDistributedCache, and /health/ready " +
            "already calls that state fallback_memory");
    }

    /// <summary>
    /// THE regression. With a live Redis-backed IDistributedCache the check must report "ok".
    /// Against the old implementation this returned "not_configured" — the state that made the
    /// check meaningless.
    /// </summary>
    [Fact]
    public async Task Health_WithLiveRedisBackedCache_ReportsOk()
    {
        var cache = new ProbeRecordingCache();

        var status = await RedisStatusAsync(services => services.AddSingleton<IDistributedCache>(cache));

        status.Should().Be("ok", "a distributed cache that answers a round trip is reachable");
        cache.Reads.Should().ContainSingle()
            .Which.Should().Be(PlatformController.RedisProbeKey,
                "the verdict must come from an actual round trip, not from inspecting configuration");
    }

    /// <summary>
    /// The other verdict the old implementation could never reach: Redis registered but down.
    /// </summary>
    [Fact]
    public async Task Health_WhenRedisBackedCacheIsUnreachable_ReportsError()
    {
        var status = await RedisStatusAsync(services =>
            services.AddSingleton<IDistributedCache>(new UnreachableCache()));

        status.Should().Be("error", "an unreachable Redis must not be reported as healthy");
    }

    [Fact]
    public async Task Health_WithNoDistributedCacheRegistered_ReportsNotConfigured()
    {
        var status = await RedisStatusAsync(_ => { });

        status.Should().Be("not_configured",
            "distinct from fallback_memory: Program.cs always registers one of the two, so this " +
            "means a composition-root problem rather than an absent Redis");
    }

    /// <summary>A probe must never WRITE to a shared production cache.</summary>
    [Fact]
    public async Task Health_ProbeIsReadOnly()
    {
        var cache = new ProbeRecordingCache();

        await RedisStatusAsync(services => services.AddSingleton<IDistributedCache>(cache));

        cache.Writes.Should().BeEmpty("the health probe reads; it must not mutate a shared cache");
        cache.Removals.Should().BeEmpty();
    }

    // ── harness ───────────────────────────────────────────────────────────────────────────────

    private static async Task<string> RedisStatusAsync(Action<IServiceCollection> configureServices)
    {
        await using var db = CreateDb();
        var controller = CreateController(db);

        var services = new ServiceCollection();
        configureServices(services);
        controller.ControllerContext.HttpContext.RequestServices = services.BuildServiceProvider();

        var result = await controller.Health(CancellationToken.None) as OkObjectResult;
        result.Should().NotBeNull();

        var components = result!.Value!.GetType().GetProperty("components")!.GetValue(result.Value)!;
        var redis = components.GetType().GetProperty("redis")!.GetValue(components)!;
        return (string)redis.GetType().GetProperty("status")!.GetValue(redis)!;
    }

    /// <summary>
    /// Stands in for the RedisCache that AddStackExchangeRedisCache registers: an IDistributedCache
    /// that is NOT MemoryDistributedCache and answers a round trip. A miss is the expected result —
    /// it still proves the connection is live.
    /// </summary>
    private sealed class ProbeRecordingCache : IDistributedCache
    {
        public List<string> Reads { get; } = [];
        public List<string> Writes { get; } = [];
        public List<string> Removals { get; } = [];

        public byte[]? Get(string key) { Reads.Add(key); return null; }
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        {
            Reads.Add(key);
            return Task.FromResult<byte[]?>(null);
        }

        public void Refresh(string key) { }
        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
        public void Remove(string key) => Removals.Add(key);
        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            Removals.Add(key);
            return Task.CompletedTask;
        }

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => Writes.Add(key);
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options,
            CancellationToken token = default)
        {
            Writes.Add(key);
            return Task.CompletedTask;
        }
    }

    /// <summary>Redis configured but not answering — the failure the old check could not report.</summary>
    private sealed class UnreachableCache : IDistributedCache
    {
        private static Exception Down() => new InvalidOperationException("No connection is available.");

        public byte[]? Get(string key) => throw Down();
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => throw Down();
        public void Refresh(string key) => throw Down();
        public Task RefreshAsync(string key, CancellationToken token = default) => throw Down();
        public void Remove(string key) => throw Down();
        public Task RemoveAsync(string key, CancellationToken token = default) => throw Down();
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw Down();
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options,
            CancellationToken token = default) => throw Down();
    }
}
