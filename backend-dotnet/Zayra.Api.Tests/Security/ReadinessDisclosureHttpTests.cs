using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// <c>/health/ready</c> is Render's healthCheckPath, so it has to stay anonymous — which made it the
/// one endpoint that told the whole internet how many tenants we have, the names of our six workers,
/// how outbound SMTP is wired and whether Qiwa is live. These tests boot the real Program.cs and pin
/// the split: the anonymous probe returns status + pendingMigrations only, and the detail lives at
/// <c>/health/ready/details</c> behind the PlatformAdmin policy (as does <c>/health/telemetry</c>,
/// whose queue counters are cross-tenant).
/// </summary>
[Collection("AuthorizationPipeline")]
public sealed class ReadinessDisclosureHttpTests
{
    private readonly AuthorizationPipelineFixture _fixture;

    public ReadinessDisclosureHttpTests(AuthorizationPipelineFixture fixture) => _fixture = fixture;

    private async Task<HttpResponseMessage> GetAsync(string path, string? bearerToken = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (bearerToken is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return await _fixture.Client.SendAsync(request);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    [Fact]
    public async Task AnonymousReadiness_ReturnsOnlyStatusUtcAndPendingMigrations()
    {
        var response = await GetAsync("/health/ready");

        // The harness database is built by EnsureCreated, so migrations read as pending and the
        // probe answers 503. Either status code must carry the same minimal body.
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable);
        var body = await JsonAsync(response);
        body.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            new[] { "status", "utc", "pendingMigrations" },
            "the anonymous readiness probe must not disclose tenant counts, worker names, SMTP/Qiwa "
            + "modes or queue depths");
        body.GetProperty("status").GetString().Should().BeOneOf("ready", "not_ready");

        var raw = await response.Content.ReadAsStringAsync();
        foreach (var leaked in new[] { "tenants", "activeTenants", "dependencies", "workers", "smtp", "qiwa", "queues" })
            raw.Should().NotContainEquivalentOf($"\"{leaked}\"");
    }

    [Fact]
    public async Task ReadinessDetails_RejectsAnonymousCallers()
    {
        var response = await GetAsync("/health/ready/details");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ReadinessDetails_RejectsTenantUsers()
    {
        var response = await GetAsync("/health/ready/details", _fixture.TenantTokenWithPermission);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "a tenant user is authenticated but is not a platform operator");
    }

    [Fact]
    public async Task ReadinessDetails_RejectsPlatformClaimsOnTenantAudience()
    {
        var response = await GetAsync("/health/ready/details", _fixture.PlatformOwnerTokenOnTenantAudience);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ReadinessDetails_GivesPlatformOperatorsTheFullEvidence()
    {
        var response = await GetAsync("/health/ready/details", _fixture.PlatformOwnerToken);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable);
        var body = await JsonAsync(response);
        body.TryGetProperty("activeTenants", out _).Should().BeTrue();
        body.TryGetProperty("dependencies", out var deps).Should().BeTrue();
        deps.TryGetProperty("workers", out _).Should().BeTrue();
        deps.TryGetProperty("smtp", out _).Should().BeTrue();
        body.GetProperty("status").GetString().Should().Be(
            (await JsonAsync(await GetAsync("/health/ready"))).GetProperty("status").GetString(),
            "the public probe and the detail must apply the same status rule");
    }

    [Fact]
    public async Task Telemetry_IsPlatformOperatorsOnly()
    {
        (await GetAsync("/health/telemetry")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await GetAsync("/health/telemetry", _fixture.TenantTokenWithPermission)).StatusCode
            .Should().Be(HttpStatusCode.Forbidden, "its queue counters are cross-tenant aggregates");
        (await GetAsync("/health/telemetry", _fixture.PlatformOwnerToken)).StatusCode
            .Should().NotBe(HttpStatusCode.Unauthorized).And.NotBe(HttpStatusCode.Forbidden);
    }
}
