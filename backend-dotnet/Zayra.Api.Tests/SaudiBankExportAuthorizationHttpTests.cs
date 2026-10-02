using System.Net;
using System.Net.Http.Headers;
using Zayra.Api.Tests.Security;
namespace Zayra.Api.Tests;

// Real ASP.NET authorization pipeline, isolated SQLite host. Not a production DB or bank test.
[Collection("AuthorizationPipeline")]
public sealed class SaudiBankExportAuthorizationHttpTests(AuthorizationPipelineFixture fixture)
{
    [Fact]
    public async Task AnonymousRequestCannotDiscoverExportFormats()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/payroll/bank-exports/formats");
        using var result = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
    }
    [Fact]
    public async Task AuthenticatedEmployeeWithoutExportPermissionIsForbidden()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/payroll/bank-exports/formats");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fixture.TenantTokenWithoutPermission);
        using var result = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
    }
}
