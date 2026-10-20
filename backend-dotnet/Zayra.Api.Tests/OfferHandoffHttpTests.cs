using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Recruitment;

namespace Zayra.Api.Tests;

[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class OfferHandoffHttpTests(PostgresFixture fixture)
{
    [Fact]
    public async Task HttpRoutesEnforceParentVisibilitySensitivePermissionAndReadOnlySourceContract()
    {
        var seed = await OfferLifecycleTestData.SeedAsync(fixture); Guid id;
        await using (var db = fixture.CreateDb())
        {
            var offer = await db.OfferLetters.SingleAsync(o => o.Id == seed.OfferId);
            offer.CompensationSnapshotJson = OfferCompensationHandoffTests.Draft().AcceptedOfferCompensationJson;
            await db.SaveChangesAsync();
            id = (await new RecruitmentService(db).AcceptOfferAsync(seed.Actor.TenantId, seed.OfferId, seed.Actor.UserId, "Recruiter")).OnboardingDraftId!.Value;
        }
        using var host = new TestServer(new WebHostBuilder().ConfigureServices(services =>
        {
            services.AddAuthentication("Handoff").AddScheme<AuthenticationSchemeOptions, HandoffAuthentication>("Handoff", _ => { });
            services.AddAuthorization();
            services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
            services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();
            services.AddSingleton<IAuthorizationHandler, PermissionAwareRolesAuthorizationHandler>();
            services.AddSingleton<IAuthorizationMiddlewareResultHandler, PermissionAwareAuthorizationResultHandler>();
            services.AddControllers().AddApplicationPart(typeof(EmployeesController).Assembly).AddControllersAsServices();
            services.AddScoped(_ => fixture.CreateDb());
            services.AddScoped<EmployeesController>(p => EmployeeDraftLifecyclePostgresTests.Employees(p.GetRequiredService<Zayra.Api.Data.ZayraDbContext>(), seed.Actor.TenantId, Guid.NewGuid()));
        }).Configure(app => { app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseEndpoints(e => e.MapControllers()); }));
        HttpClient Client(string? permissions, Guid? tenant = null, Guid? company = null)
        {
            var client = host.CreateClient();
            if (permissions is not null)
            {
                client.DefaultRequestHeaders.Add("X-Tenant", (tenant ?? seed.Actor.TenantId).ToString());
                client.DefaultRequestHeaders.Add("X-Company", (company ?? seed.Actor.CompanyId!.Value).ToString());
                client.DefaultRequestHeaders.Add("X-Permissions", permissions);
            }
            return client;
        }
        var route = $"/api/employees/drafts/{id}/offer-compensation";
        using (var client = Client(null)) (await client.GetAsync(route)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using (var client = Client("employees.write"))
        {
            (await client.GetAsync(route)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await client.PutAsJsonAsync(route, new { expectedVersion = 1, dispositions = Array.Empty<object>(), confirm = false })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        using (var client = Client("employees.approve,employees.sensitive"))
        {
            var get = await client.GetAsync(route); get.StatusCode.Should().Be(HttpStatusCode.OK);
            (await get.Content.ReadAsStringAsync()).Should().Contain("Family cover");
            (await client.PutAsJsonAsync(route, new { expectedVersion = 1, dispositions = Array.Empty<object>(), confirm = false })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        using (var client = Client("employees.write,employees.sensitive", company: Guid.NewGuid()))
            (await client.GetAsync(route)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        using (var client = Client("employees.write,employees.sensitive", tenant: Guid.NewGuid()))
            (await client.GetAsync(route)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        using (var client = Client("employees.write,employees.sensitive"))
        {
            (await client.PutAsJsonAsync(route, new { expectedVersion = 1, dispositions = Array.Empty<object>(), confirm = false, currency = "USD", basicSalary = 1 })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await client.PutAsJsonAsync(route, new { expectedVersion = 1, dispositions = OfferCompensationHandoffTests.Dispositions(), confirm = false })).StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }
}

file sealed class HandoffAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Guid.TryParse(Request.Headers["X-Tenant"], out var tenant)) return Task.FromResult(AuthenticateResult.NoResult());
        var claims = new List<Claim> { new("tenant_id", tenant.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, "HR Manager"), new(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "companies", c = new[] { Guid.Parse(Request.Headers["X-Company"].ToString()) } })) };
        claims.AddRange(Request.Headers["X-Permissions"].ToString().Split(',').Select(p => new Claim("permission", p)));
        return Task.FromResult(AuthenticateResult.Success(new(new(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
    }
}
