using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Zayra.Api.Infrastructure.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Zayra.Api.Controllers.Recruitment;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Recruitment;
using Zayra.Api.Infrastructure.Scope;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Notifications;

namespace Zayra.Api.Tests;

[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class OfferHistoryHttpTests(PostgresFixture fixture)
{
    private TestServer Host(bool enabled = false) => new(new WebHostBuilder().ConfigureServices(services =>
    {
        services.AddSingleton<IConfiguration>(OfferLifecycleServiceTests.Config(enabled));
        services.AddHttpContextAccessor(); services.AddScoped<IRequestEntityScopeResolver, RequestEntityScopeResolver>();
        services.AddScoped<ZayraDbContext>(provider => fixture.CreateDbWithAccessor(provider.GetRequiredService<IHttpContextAccessor>()));
        services.AddSingleton<IDocumentStorage, OfferMemoryStorage>();
        services.AddScoped<OfferArtifactService>(); services.AddScoped<OfferLifecycleService>(); services.AddScoped<OfferHistoryQueryService>();
        services.AddScoped<IRecruitmentService, RecruitmentService>();
        services.AddSingleton<ILetterService, OfferHttpNoLetters>();
        services.AddSingleton<INotificationService, OfferHttpNoNotifications>();
        services.AddAuthentication("OfferTest").AddScheme<AuthenticationSchemeOptions, OfferHttpAuthentication>("OfferTest", _ => { });
        services.AddAuthorization();
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddSingleton<IAuthorizationHandler, PermissionAwareRolesAuthorizationHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, PermissionAwareAuthorizationResultHandler>();
        services.AddControllers().AddApplicationPart(typeof(OffersController).Assembly);
    }).Configure(app =>
    {
        app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseEndpoints(endpoints => endpoints.MapControllers());
    }));

    private static void Authenticate(HttpClient client, OfferLifecycleSeed seed, Guid company, string role = "HR Manager", string permissions = "recruitment.read,recruitment.write")
    {
        client.DefaultRequestHeaders.Add("X-Test-Tenant", seed.Actor.TenantId.ToString());
        client.DefaultRequestHeaders.Add("X-Test-User", seed.Actor.UserId.ToString());
        client.DefaultRequestHeaders.Add("X-Test-Company", company.ToString());
        client.DefaultRequestHeaders.Add("X-Test-Role", role);
        client.DefaultRequestHeaders.Add("X-Test-Permissions", permissions);
    }

    private static readonly (string Method, string Path)[] Children =
    [ ("GET", "workspace"), ("GET", "events"), ("GET", "original"), ("PUT", "draft"),
      ("POST", "revisions"), ("POST", "freeze"), ("POST", "withdraw"), ("POST", "responses"), ("POST", "external-sends") ];

    [Theory]
    [InlineData("Recruiter")]
    [InlineData("HR Officer")]
    public async Task RetainedResponseAndApprovalAdaptersCannotBypassManagerAudience(string role)
    {
        var seed = await OfferLifecycleTestData.SeedAsync(fixture);
        using var server = Host(); using var client = server.CreateClient();
        Authenticate(client, seed, seed.Actor.CompanyId!.Value, role, "recruitment.read,recruitment.write,recruitment.approve");
        foreach (var prefix in new[] { "offers", "applications/offers" })
        foreach (var decision in new[] { "accept", "decline" })
        {
            var response = await client.SendAsync(new(new HttpMethod(prefix == "offers" ? "PATCH" : "POST"), $"/api/recruitment/{prefix}/{seed.OfferId}/{decision}")
            { Content = JsonContent.Create(new { expectedVersion = 1, decision = decision == "accept" ? "Accepted" : "Declined",
                source = "StaffRecorded", occurredAtUtc = DateTime.UtcNow, reason = "Recorded response" }) });
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{role} {prefix}/{decision}");
        }
        var added = await client.PostAsJsonAsync($"/api/recruitment/offers/{seed.OfferId}/approvals", new { expectedVersion = 1, approverName = "Checker" });
        added.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var decided = await client.PatchAsJsonAsync($"/api/recruitment/offers/{seed.OfferId}/approvals/{Guid.NewGuid()}/decide", new { expectedVersion = 1, decision = "Approved" });
        decided.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await using var db = fixture.CreateDb();
        (await db.OfferLetters.SingleAsync(o => o.Id == seed.OfferId)).Status.Should().Be("Sent");
        (await db.JobOpenings.SingleAsync(o => o.Id == seed.OpeningId)).FilledCount.Should().Be(0);
        (await db.EmployeeDrafts.CountAsync(d => d.TenantId == seed.Actor.TenantId)).Should().Be(0);
        (await db.OfferApprovals.CountAsync(a => a.OfferLetterId == seed.OfferId)).Should().Be(0);
    }

    [Theory]
    [InlineData("HR Officer", HttpStatusCode.Forbidden)]
    [InlineData("Recruiter", HttpStatusCode.Conflict)]
    public async Task BothCreationAdaptersUseTheSameAuthorAudience(string role, HttpStatusCode expected)
    {
        var seed = await OfferLifecycleTestData.SeedAsync(fixture);
        using var server = Host(); using var client = server.CreateClient(); Authenticate(client, seed, seed.Actor.CompanyId!.Value, role);
        var body = new { applicationId = seed.ApplicationId, startDate = "2027-01-01", probationMonths = 3,
            offeredJobTitle = "Engineer", components = Array.Empty<object>() };
        (await client.PostAsJsonAsync("/api/recruitment/offers", body)).StatusCode.Should().Be(expected);
        (await client.PostAsJsonAsync($"/api/recruitment/applications/{seed.ApplicationId}/offer", new { startDate = "2027-01-01", probationMonths = 3, components = Array.Empty<object>() })).StatusCode.Should().Be(expected);
        await using var db = fixture.CreateDb(); (await db.OfferLetters.CountAsync(o => o.ApplicationId == seed.ApplicationId)).Should().Be(1);
    }

    [Fact]
    public async Task ApprovalSubmissionUsesWriteWhileDecisionRequiresApproveAndManagerAudience()
    {
        var seed = await OfferLifecycleTestData.SeedAsync(fixture);
        using var server = Host();
        foreach (var permission in new[] { "recruitment.write", "recruitment.approve" })
        {
            using var client = server.CreateClient(); Authenticate(client, seed, seed.Actor.CompanyId!.Value, permissions: permission);
            var add = await client.PostAsJsonAsync($"/api/recruitment/offers/{seed.OfferId}/approvals", new { expectedVersion = 1, approverName = "Checker" });
            add.StatusCode.Should().Be(permission == "recruitment.write" ? HttpStatusCode.Conflict : HttpStatusCode.Forbidden);
            var decide = await client.PatchAsJsonAsync($"/api/recruitment/offers/{seed.OfferId}/approvals/{Guid.NewGuid()}/decide", new { expectedVersion = 1, decision = "Approved" });
            decide.StatusCode.Should().Be(permission == "recruitment.approve" ? HttpStatusCode.Conflict : HttpStatusCode.Forbidden);
        }
    }

    private static object Body(string path) => path switch
    {
        "draft" => new { expectedVersion = 1, startDate = "2027-02-01", probationMonths = 3,
            recipientEmail = "candidate@example.invalid", offeredJobTitle = "Engineer", offeredDepartment = "Engineering" },
        "responses" => new { expectedVersion = 1, decision = "Accepted", source = "StaffRecorded", occurredAtUtc = DateTime.UtcNow, reason = "Recorded" },
        "external-sends" => new { expectedVersion = 1, idempotencyKey = "http-1", channel = "Email", occurredAtUtc = DateTime.UtcNow },
        _ => new { expectedVersion = 1, reason = "Reviewed" }
    };

    private static Task<HttpResponseMessage> Send(HttpClient client, Guid id, string method, string path) =>
        client.SendAsync(new(new HttpMethod(method), $"/api/recruitment/offers/{id}/{path}")
        { Content = method == "GET" ? null : JsonContent.Create(Body(path)) });

    [Fact]
    public async Task EveryChildRouteRequiresAuthenticationAndCannotExposeAnotherCompany()
    {
        var seed = await OfferLifecycleTestData.SeedAsync(fixture);
        await using var db = fixture.CreateDb();
        var other = new Zayra.Api.Models.Company { TenantId = seed.Actor.TenantId };
        db.Companies.Add(other); await db.SaveChangesAsync();
        using var server = Host(); using var anonymous = server.CreateClient(); using var outsider = server.CreateClient();
        Authenticate(outsider, seed, other.Id);
        foreach (var (method, path) in Children)
        {
            (await Send(anonymous, seed.OfferId, method, path)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, path);
            (await Send(outsider, seed.OfferId, method, path)).StatusCode.Should().Be(HttpStatusCode.NotFound, path);
        }
    }

    [Fact]
    public async Task RecruiterCanAuthorButCannotPerformManagerMutations()
    {
        var seed = await OfferLifecycleTestData.SeedAsync(fixture);
        using var server = Host(); using var client = server.CreateClient(); Authenticate(client, seed, seed.Actor.CompanyId!.Value, "Recruiter");
        (await Send(client, seed.OfferId, "GET", "workspace")).StatusCode.Should().Be(HttpStatusCode.OK);
        foreach (var path in new[] { "draft", "revisions", "freeze" })
        {
            var response = await Send(client, seed.OfferId, path == "draft" ? "PUT" : "POST", path);
            response.StatusCode.Should().Be(HttpStatusCode.Conflict, path);
            (await response.Content.ReadAsStringAsync()).Should().Contain("managed_offer_lifecycle_disabled");
        }
        foreach (var path in new[] { "withdraw", "responses", "external-sends" })
            (await Send(client, seed.OfferId, "POST", path)).StatusCode.Should().Be(HttpStatusCode.Forbidden, path);
    }

    [Fact]
    public async Task OfficerIsReadOnlyEvenWithWritePermission()
    {
        var seed = await OfferLifecycleTestData.SeedAsync(fixture);
        using var server = Host(true); using var client = server.CreateClient(); Authenticate(client, seed, seed.Actor.CompanyId!.Value, "HR Officer");
        var response = await Send(client, seed.OfferId, "GET", "workspace"); response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var capabilities = json.RootElement.GetProperty("capabilities");
        capabilities.GetProperty("revise").GetProperty("allowed").GetBoolean().Should().BeFalse();
        foreach (var (method, path) in Children.Where(x => x.Method != "GET"))
            (await Send(client, seed.OfferId, method, path)).StatusCode.Should().Be(HttpStatusCode.Forbidden, path);
        foreach (var action in new[] { "withdraw", "recordResponse", "issue" })
        {
            capabilities.GetProperty(action).GetProperty("allowed").GetBoolean().Should().BeFalse(action);
            capabilities.GetProperty(action).GetProperty("reason").GetString().Should().Contain("role");
        }
    }

    [Fact]
    public async Task ManagerRoleCannotBypassMissingEffectivePermissions()
    {
        var seed = await OfferLifecycleTestData.SeedAsync(fixture);
        using var server = Host(); using var denied = server.CreateClient();
        Authenticate(denied, seed, seed.Actor.CompanyId!.Value, permissions: "none");
        (await denied.GetAsync("/api/recruitment/offers/history")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        foreach (var (method, path) in Children)
            (await Send(denied, seed.OfferId, method, path)).StatusCode.Should().Be(HttpStatusCode.Forbidden, path);
        using var readOnly = server.CreateClient();
        Authenticate(readOnly, seed, seed.Actor.CompanyId!.Value, permissions: "recruitment.read");
        var workspace = await Send(readOnly, seed.OfferId, "GET", "workspace");
        workspace.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = System.Text.Json.JsonDocument.Parse(await workspace.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("capabilities").GetProperty("revise").GetProperty("allowed").GetBoolean().Should().BeFalse();
        foreach (var (method, path) in Children.Where(x => x.Method != "GET"))
            (await Send(readOnly, seed.OfferId, method, path)).StatusCode.Should().Be(HttpStatusCode.Forbidden, path);
    }

    [Fact]
    public async Task DisabledManagedLifecycleKeepsHistoryReadableAndReturnsActionableConflict()
    {
        var seed = await OfferLifecycleTestData.SeedAsync(fixture);
        using var server = Host(); using var client = server.CreateClient(); Authenticate(client, seed, seed.Actor.CompanyId!.Value);
        (await client.GetAsync("/api/recruitment/offers/history")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Send(client, seed.OfferId, "GET", "workspace")).StatusCode.Should().Be(HttpStatusCode.OK);
        var blocked = await Send(client, seed.OfferId, "POST", "freeze");
        blocked.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await blocked.Content.ReadAsStringAsync()).Should().Contain("managed_offer_lifecycle_disabled");
        (await Send(client, seed.OfferId, "GET", "original")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}

file sealed class OfferHttpNoNotifications : INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => throw new InvalidOperationException("No email permitted in HTTP lifecycle tests.");
}

file sealed class OfferHttpNoLetters : ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken ct = default) => throw new NotSupportedException();
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
