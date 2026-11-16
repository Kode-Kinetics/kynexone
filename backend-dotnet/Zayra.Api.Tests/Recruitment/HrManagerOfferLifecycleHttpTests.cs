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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Controllers.Recruitment;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Infrastructure.Recruitment;
using Zayra.Api.Infrastructure.Scope;
using Zayra.Api.Infrastructure.Seed;

namespace Zayra.Api.Tests.Recruitment;

/// <summary>
/// The seeded HR Manager runs the whole offer lifecycle over HTTP with the permissions its seeded role really holds
/// (the claims below are read from the database after AuthSeeder ran, never typed in): create, freeze, request approval
/// from a second HR Manager, and that second HR Manager decides. The maker-checker rule still binds the author, and the
/// roles without the recruitment keys (HR Officer, Employee) and other tenants still get nothing.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class HrManagerOfferLifecycleHttpTests(PostgresFixture fixture)
{
    private sealed record World(Guid TenantId, Guid CompanyId, Guid ApplicationId, Guid AuthorId, Guid CheckerId, Guid OfficerId, Guid EmployeeId,
        Dictionary<Guid, string> Roles);

    private TestServer Host() => new(new WebHostBuilder().ConfigureServices(s =>
    {
        s.AddSingleton<IConfiguration>(OfferLifecycleServiceTests.Config()); s.AddHttpContextAccessor();
        s.AddScoped<IRequestEntityScopeResolver, RequestEntityScopeResolver>();
        s.AddScoped<ZayraDbContext>(p => fixture.CreateDbWithAccessor(p.GetRequiredService<IHttpContextAccessor>()));
        s.AddSingleton<IDocumentStorage, OfferMemoryStorage>(); s.AddSingleton<IEmailService, ExplicitOfferEmail>();
        s.AddSingleton<ILetterService, NoLetters>();
        s.AddScoped<IRecruitmentService>(p => new RecruitmentService(p.GetRequiredService<ZayraDbContext>()));
        s.AddScoped<OfferArtifactService>(); s.AddScoped<OfferLifecycleService>(); s.AddScoped<OfferHistoryQueryService>();
        s.AddScoped<OfferDispatchService>(); s.AddScoped<OfferMailReadinessService>();
        s.AddAuthentication("HrmOffer").AddScheme<AuthenticationSchemeOptions, HrmOfferAuthentication>("HrmOffer", _ => { }); s.AddAuthorization();
        s.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>(); s.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();
        s.AddSingleton<IAuthorizationHandler, PermissionAwareRolesAuthorizationHandler>(); s.AddSingleton<IAuthorizationMiddlewareResultHandler, PermissionAwareAuthorizationResultHandler>();
        s.AddControllers().AddApplicationPart(typeof(OffersController).Assembly);
    }).Configure(a => { a.UseRouting(); a.UseAuthentication(); a.UseAuthorization(); a.UseEndpoints(e => e.MapControllers()); }));

    /// <summary>A caller whose role AND permissions come from the seeded database rows for that user.</summary>
    private async Task<HttpClient> ClientAsync(TestServer host, World w, Guid userId, Guid? tenant = null)
    {
        await using var db = fixture.CreateDb();
        var role = w.Roles[userId];
        var permissions = await db.Roles.Where(r => r.TenantId == w.TenantId && r.Name == role)
            .SelectMany(r => r.RolePermissions.Select(rp => rp.Permission!.Key)).ToListAsync();
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Tenant", (tenant ?? w.TenantId).ToString());
        client.DefaultRequestHeaders.Add("X-Test-User", userId.ToString());
        client.DefaultRequestHeaders.Add("X-Test-Company", w.CompanyId.ToString());
        client.DefaultRequestHeaders.Add("X-Test-Role", role);
        client.DefaultRequestHeaders.Add("X-Test-Permissions", string.Join(',', permissions));
        return client;
    }

    [Fact]
    public async Task HrManager_creates_freezes_and_requests_approval_and_a_second_HrManager_decides()
    {
        var w = await SeedAsync();
        using var host = Host();
        using var author = await ClientAsync(host, w, w.AuthorId);
        using var checker = await ClientAsync(host, w, w.CheckerId);

        var created = await author.PostAsJsonAsync("/api/recruitment/offers", new
        {
            applicationId = w.ApplicationId, offeredJobTitle = "Engineer", offeredDepartment = "Engineering",
            startDate = "2027-02-01", basicSalary = 1000, housingAllowance = 0, transportAllowance = 0, otherAllowances = 0,
            probationMonths = 3, responseDeadline = DateTime.UtcNow.AddDays(7), expectedApplicationVersion = await ApplicationVersionAsync(w.ApplicationId),
        });
        (await created.Content.ReadAsStringAsync()).Should().NotBeNull();
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var offerId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        var frozen = await author.PostAsJsonAsync($"/api/recruitment/offers/{offerId}/freeze", new { expectedVersion = await OfferVersionAsync(offerId) });
        frozen.StatusCode.Should().Be(HttpStatusCode.OK, await frozen.Content.ReadAsStringAsync());

        // The dropdown offers the second HR Manager, never the author.
        var options = await author.GetFromJsonAsync<JsonElement[]>($"/api/recruitment/offers/{offerId}/approver-options");
        options!.Select(o => o.GetProperty("userId").GetGuid()).Should().Contain(w.CheckerId).And.NotContain(w.AuthorId);

        // The author cannot name themselves as the checker.
        var self = await author.PostAsJsonAsync($"/api/recruitment/offers/{offerId}/approvals",
            new { approverName = "Author", approverUserId = w.AuthorId, approverRole = "HR Manager", expectedVersion = await OfferVersionAsync(offerId) });
        self.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await self.Content.ReadAsStringAsync()).Should().Contain("offer_maker_checker");

        var requested = await author.PostAsJsonAsync($"/api/recruitment/offers/{offerId}/approvals",
            new { approverName = "Checker", approverUserId = w.CheckerId, approverRole = "HR Manager", expectedVersion = await OfferVersionAsync(offerId) });
        requested.StatusCode.Should().Be(HttpStatusCode.OK, await requested.Content.ReadAsStringAsync());
        var approvalId = JsonDocument.Parse(await requested.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        (await OfferStatusAsync(offerId)).Should().Be("PendingApproval");

        // The author cannot decide the step they requested; the named second HR Manager can.
        var byAuthor = await author.PatchAsJsonAsync($"/api/recruitment/offers/{offerId}/approvals/{approvalId}/decide",
            new { decision = "Approved", comments = "self", expectedVersion = await OfferVersionAsync(offerId) });
        byAuthor.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await OfferStatusAsync(offerId)).Should().Be("PendingApproval");

        var decided = await checker.PatchAsJsonAsync($"/api/recruitment/offers/{offerId}/approvals/{approvalId}/decide",
            new { decision = "Approved", comments = "ok", expectedVersion = await OfferVersionAsync(offerId) });
        decided.StatusCode.Should().Be(HttpStatusCode.OK, await decided.Content.ReadAsStringAsync());
        (await OfferStatusAsync(offerId)).Should().Be("Approved");

        // Nothing else widened: the company offer policy stays Admin-only (the action re-checks the Admin role).
        (await author.GetAsync("/api/recruitment/offers/policy")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await author.PutAsJsonAsync("/api/recruitment/offers/policy", new { precision = 2, requiredComponentCodes = Array.Empty<string>(), expectedVersion = 0 }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task HrOfficer_and_Employee_are_refused_every_offer_action_and_another_tenant_sees_nothing()
    {
        var w = await SeedAsync();
        using var host = Host();
        var offerId = await SeedDraftOfferAsync(w);
        var body = new { applicationId = w.ApplicationId, offeredJobTitle = "Engineer", offeredDepartment = "Engineering", startDate = "2027-02-01",
            basicSalary = 1000, housingAllowance = 0, transportAllowance = 0, otherAllowances = 0, probationMonths = 3 };

        foreach (var user in new[] { w.OfficerId, w.EmployeeId })
        {
            using var client = await ClientAsync(host, w, user);
            (await client.GetAsync($"/api/recruitment/offers/{offerId}")).StatusCode.Should().Be(HttpStatusCode.Forbidden, "read, " + w.Roles[user]);
            (await client.GetAsync($"/api/recruitment/offers/{offerId}/approver-options")).StatusCode.Should().Be(HttpStatusCode.Forbidden, "options, " + w.Roles[user]);
            (await client.PostAsJsonAsync("/api/recruitment/offers", body)).StatusCode.Should().Be(HttpStatusCode.Forbidden, "create, " + w.Roles[user]);
            (await client.PostAsJsonAsync($"/api/recruitment/offers/{offerId}/freeze", new { expectedVersion = 1 })).StatusCode.Should().Be(HttpStatusCode.Forbidden, "freeze, " + w.Roles[user]);
            (await client.PostAsJsonAsync($"/api/recruitment/offers/{offerId}/approvals", new { approverName = "x", approverUserId = w.CheckerId, expectedVersion = 1 }))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden, "request approval, " + w.Roles[user]);
            (await client.PatchAsJsonAsync($"/api/recruitment/offers/{offerId}/approvals/{Guid.NewGuid()}/decide", new { decision = "Approved", expectedVersion = 1 }))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden, "decide, " + w.Roles[user]);
        }

        // An HR Manager of a DIFFERENT tenant holds the same keys but reaches none of this tenant's offer.
        using var foreign = await ClientAsync(host, w, w.AuthorId, tenant: Guid.NewGuid());
        (await foreign.GetAsync($"/api/recruitment/offers/{offerId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await foreign.PostAsJsonAsync($"/api/recruitment/offers/{offerId}/approvals", new { approverName = "x", approverUserId = w.CheckerId, expectedVersion = 1 }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await foreign.PatchAsJsonAsync($"/api/recruitment/offers/{offerId}/approvals/{Guid.NewGuid()}/decide", new { decision = "Approved", expectedVersion = 1 }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_approver_dropdown_lists_only_users_who_can_actually_decide()
    {
        var w = await SeedAsync();
        // A third HR Manager whose tenant explicitly denied them recruitment.approve: named by role, unable to decide.
        Guid deniedId;
        await using (var db = fixture.CreateDb())
        {
            var hr = await db.Roles.SingleAsync(r => r.TenantId == w.TenantId && r.Name == "HR Manager");
            var denied = NewUser(w.TenantId, "denied", hr);
            db.Users.Add(denied);
            db.UserPermissionOverrides.Add(new Zayra.Api.Models.UserPermissionOverride
            { TenantId = w.TenantId, UserId = denied.Id, PermissionKey = "recruitment.approve", Effect = "Deny", Reason = "test" });
            await db.SaveChangesAsync();
            deniedId = denied.Id;
        }
        var offerId = await SeedDraftOfferAsync(w);
        using var host = Host();
        using var author = await ClientAsync(host, w, w.AuthorId);

        var ids = (await author.GetFromJsonAsync<JsonElement[]>($"/api/recruitment/offers/{offerId}/approver-options"))!
            .Select(o => o.GetProperty("userId").GetGuid()).ToList();
        // (The seeded draft has no recorded author, so authors are not excluded here; the first test covers the author.)
        ids.Should().Contain(new[] { w.CheckerId, w.AuthorId });
        ids.Should().NotContain(deniedId);

        // The tenant strips the key from its own HR Manager role: nobody holding only that role can decide any more.
        await using (var db = fixture.CreateDb())
        {
            var approve = await db.Permissions.SingleAsync(p => p.Key == "recruitment.approve");
            var role = await db.Roles.SingleAsync(r => r.TenantId == w.TenantId && r.Name == "HR Manager");
            db.RolePermissions.RemoveRange(await db.RolePermissions.Where(rp => rp.RoleId == role.Id && rp.PermissionId == approve.Id).ToListAsync());
            await db.SaveChangesAsync();
        }
        using var author2 = await ClientAsync(host, w, w.AuthorId);
        (await author2.GetFromJsonAsync<JsonElement[]>($"/api/recruitment/offers/{offerId}/approver-options"))!.Should().BeEmpty();
    }

    private async Task<long> ApplicationVersionAsync(Guid id) { await using var db = fixture.CreateDb(); return await db.JobApplications.AsNoTracking().Where(a => a.Id == id).Select(a => a.Version).SingleAsync(); }
    private async Task<long> OfferVersionAsync(Guid id) { await using var db = fixture.CreateDb(); return await db.OfferLetters.AsNoTracking().Where(o => o.Id == id).Select(o => o.Version).SingleAsync(); }
    private async Task<string> OfferStatusAsync(Guid id) { await using var db = fixture.CreateDb(); return await db.OfferLetters.AsNoTracking().Where(o => o.Id == id).Select(o => o.Status).SingleAsync(); }

    private async Task<Guid> SeedDraftOfferAsync(World w)
    {
        await using var db = fixture.CreateDb();
        var offer = new Zayra.Api.Models.OfferLetter { TenantId = w.TenantId, CompanyId = w.CompanyId, ApplicationId = w.ApplicationId, CandidateName = "Test Candidate",
            OfferedJobTitle = "Engineer", OfferedDepartment = "Engineering", StartDate = new(2027, 1, 1), BasicSalary = 1000, GrossSalary = 1000, Status = "Draft" };
        db.OfferLetters.Add(offer); await db.SaveChangesAsync();
        return offer.Id;
    }

    private async Task<World> SeedAsync()
    {
        var seed = await OfferLifecycleTestData.SeedAsync(fixture);
        await using var db = fixture.CreateDb();
        var tenantId = seed.Actor.TenantId;
        await new AuthSeeder(db).EnsureTenantRolesAsync(tenantId, default);
        db.Departments.Add(new() { TenantId = tenantId, NameEn = "Engineering", Code = "ENG" });
        db.Designations.Add(new() { TenantId = tenantId, TitleEn = "Engineer", Code = "ENG" });
        var roles = await db.Roles.Where(r => r.TenantId == tenantId).ToDictionaryAsync(r => r.Name);
        var author = NewUser(tenantId, "author", roles["HR Manager"]);
        var checker = NewUser(tenantId, "checker", roles["HR Manager"]);
        var officer = NewUser(tenantId, "officer", roles["HR Officer"]);
        var employee = NewUser(tenantId, "employee", roles["Employee"]);
        db.Users.AddRange(author, checker, officer, employee);
        await db.SaveChangesAsync();
        return new World(tenantId, seed.Actor.CompanyId!.Value, seed.ApplicationId, author.Id, checker.Id, officer.Id, employee.Id,
            new() { [author.Id] = "HR Manager", [checker.Id] = "HR Manager", [officer.Id] = "HR Officer", [employee.Id] = "Employee" });
    }

    private static User NewUser(Guid tenantId, string name, Role role)
    {
        var email = $"{name}-{Guid.NewGuid():N}@example.test";
        var user = new User { TenantId = tenantId, Email = email, NormalizedEmail = email.ToUpperInvariant(), FullName = $"Test {name}", Status = "Active", IsActive = true };
        user.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        return user;
    }
}

file sealed class NoLetters : ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}

file sealed class HrmOfferAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-Tenant", out var tenant)) return Task.FromResult(AuthenticateResult.NoResult());
        var company = Request.Headers["X-Test-Company"].ToString();
        var claims = new List<Claim> { new("tenant_id", tenant.ToString()), new(ClaimTypes.NameIdentifier, Request.Headers["X-Test-User"].ToString()),
            new(ClaimTypes.Role, Request.Headers["X-Test-Role"].ToString()), new("name", "HTTP reviewer"),
            new("entity_scope", $"{{\"v\":2,\"m\":\"companies\",\"c\":[\"{company}\"]}}"), new("entity_scope_strict", "true") };
        claims.AddRange(Request.Headers["X-Test-Permissions"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(p => new Claim("permission", p)));
        return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
    }
}
