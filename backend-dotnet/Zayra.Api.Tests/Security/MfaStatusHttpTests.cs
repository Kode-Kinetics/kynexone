using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// The status endpoints that drive the grace-period "set up two-step sign-in" prompt, through the real
/// Program.cs pipeline. The harness database has no enforcement-date row, which is exactly the
/// fail-safe case: privileged principals are prompted, nobody is blocked.
/// </summary>
[Collection("AuthorizationPipeline")]
public sealed class MfaStatusHttpTests
{
    private readonly AuthorizationPipelineFixture _fixture;

    public MfaStatusHttpTests(AuthorizationPipelineFixture fixture) => _fixture = fixture;

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? bearer = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return await _fixture.Client.SendAsync(request);
    }

    [Fact]
    public async Task TenantStatus_RequiresAuthentication_AndReportsNotRequiredForAnUnprivilegedUser()
    {
        (await SendAsync(HttpMethod.Get, "/api/auth/mfa/status")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var response = await SendAsync(HttpMethod.Get, "/api/auth/mfa/status", _fixture.TenantTokenWithoutPermission);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("required").GetBoolean().Should().BeFalse("the harness user holds no privileged role");
        body.GetProperty("promptToEnroll").GetBoolean().Should().BeFalse();
        body.GetProperty("enforced").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task PlatformStatus_PromptsAnUnenrolledOperator_WithoutBlocking()
    {
        (await SendAsync(HttpMethod.Get, "/api/platform/auth/mfa/status")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await SendAsync(HttpMethod.Get, "/api/platform/auth/mfa/status", _fixture.TenantTokenWithPermission))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var response = await SendAsync(HttpMethod.Get, "/api/platform/auth/mfa/status", _fixture.PlatformMarketingToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK, "every platform role, not only Owner/Admin, must be able to see and act on it");
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("required").GetBoolean().Should().BeTrue();
        body.GetProperty("enabled").GetBoolean().Should().BeFalse();
        body.GetProperty("enforced").GetBoolean().Should().BeFalse("no enforcement date row exists in this database");
        body.GetProperty("promptToEnroll").GetBoolean().Should().BeTrue();
    }
}
