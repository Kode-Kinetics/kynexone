using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Controllers.Recruitment;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Infrastructure.Recruitment;
using Zayra.Api.Infrastructure.Scope;

namespace Zayra.Api.Tests;

[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class OfferDispatchHttpTests(PostgresFixture fixture)
{
    private TestServer Host()=>new(new WebHostBuilder().ConfigureServices(s=>
    {
        s.AddSingleton<IConfiguration>(OfferLifecycleServiceTests.Config()); s.AddHttpContextAccessor();
        s.AddScoped<IRequestEntityScopeResolver,RequestEntityScopeResolver>();
        s.AddScoped<ZayraDbContext>(p=>fixture.CreateDbWithAccessor(p.GetRequiredService<IHttpContextAccessor>()));
        s.AddSingleton<IDocumentStorage,OfferMemoryStorage>();s.AddSingleton<IEmailService,ExplicitOfferEmail>();
        s.AddScoped<OfferArtifactService>();s.AddScoped<OfferLifecycleService>();s.AddScoped<OfferHistoryQueryService>();
        s.AddScoped<OfferDispatchService>();s.AddScoped<OfferMailReadinessService>();
        s.AddAuthentication("OfferTest").AddScheme<AuthenticationSchemeOptions,OfferHttpAuthentication>("OfferTest",_=>{});s.AddAuthorization();
        s.AddSingleton<IAuthorizationPolicyProvider,PermissionPolicyProvider>();s.AddSingleton<IAuthorizationHandler,PermissionAuthorizationHandler>();
        s.AddSingleton<IAuthorizationHandler,PermissionAwareRolesAuthorizationHandler>();s.AddSingleton<IAuthorizationMiddlewareResultHandler,PermissionAwareAuthorizationResultHandler>();
        s.AddControllers().AddApplicationPart(typeof(OffersController).Assembly);
    }).Configure(a=>{a.UseRouting();a.UseAuthentication();a.UseAuthorization();a.UseEndpoints(e=>e.MapControllers());}));

    private static HttpClient Client(TestServer host,OfferLifecycleSeed seed,string role,Guid? company=null)
    {
        var client=host.CreateClient();client.DefaultRequestHeaders.Add("X-Test-Tenant",seed.Actor.TenantId.ToString());
        client.DefaultRequestHeaders.Add("X-Test-User",seed.Actor.UserId.ToString());client.DefaultRequestHeaders.Add("X-Test-Company",(company??seed.Actor.CompanyId)!.Value.ToString());
        client.DefaultRequestHeaders.Add("X-Test-Role",role);client.DefaultRequestHeaders.Add("X-Test-Permissions","recruitment.read,recruitment.write");return client;
    }

    [Theory]
    [InlineData("Recruiter")]
    [InlineData("HR Officer")]
    public async Task NonManagersCannotIssueCancelOrReconcileEvenWithWritePermission(string role)
    {
        var seed=await OfferLifecycleTestData.SeedAsync(fixture);using var host=Host();using var client=Client(host,seed,role);
        foreach(var path in new[]{"email",$"dispatches/{Guid.NewGuid()}/cancel",$"dispatches/{Guid.NewGuid()}/reconcile"})
        {
            var response=await client.PostAsJsonAsync($"/api/recruitment/offers/{seed.OfferId}/{path}",new{expectedVersion=1,idempotencyKey="http-key",subject="Offer",message="Review",reviewedSenderFingerprint="reviewed-sender-v1",reason="Review",action="ApplyRetainedRelayEvidence"});
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden,path);
        }
    }

    [Fact]
    public async Task ChildCannotAuthorizeWrongCompanyAndReadinessContainsOnlySafeIdentity()
    {
        var seed=await OfferLifecycleTestData.SeedAsync(fixture);using var host=Host();using var client=Client(host,seed,"HR Manager",Guid.NewGuid());
        (await client.GetAsync($"/api/recruitment/offers/{seed.OfferId}/dispatches/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var own=Client(host,seed,"Recruiter");var response=await own.GetAsync("/api/recruitment/offers/mail-readiness");response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json=await response.Content.ReadAsStringAsync();json.Should().Contain("hr@example.invalid").And.Contain("\"canManageSettings\":false");
        json.Should().NotContain("password").And.NotContain("username").And.NotContain("smtp.host");
    }
}

file sealed class OfferHttpAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-Tenant", out var tenant)) return Task.FromResult(AuthenticateResult.NoResult());
        var company = Request.Headers["X-Test-Company"].ToString();
        var claims = new[] { new Claim("tenant_id", tenant.ToString()), new Claim(ClaimTypes.NameIdentifier, Request.Headers["X-Test-User"].ToString()),
            new Claim(ClaimTypes.Role, Request.Headers["X-Test-Role"].ToString()), new Claim("name", "HTTP reviewer"),
            new Claim("entity_scope", $"{{\"v\":2,\"m\":\"companies\",\"c\":[\"{company}\"]}}"), new Claim("entity_scope_strict", "true") };
        return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity(claims.Concat(Request.Headers["X-Test-Permissions"].ToString().Split(',').Select(p => new Claim("permission", p))), Scheme.Name)), Scheme.Name)));
    }
}
