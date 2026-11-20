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
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Recruitment;

/// <summary>
/// On an offer of one company the approver picker used to list an HR Manager who only has access to another company. That person got
/// "not found" on the offer, and a second request stacked a second step behind the stuck one. The picker now lists only people who can
/// act on this offer (active, holding the approval permission, their entity scope reaching the offer's company, not the author), and a
/// request that names anyone else is refused with a clear message and creates no step. Who may approve in general, and the maker-checker
/// rule, are unchanged. Real pipeline over HTTP against Postgres.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class OfferApproverScopeFixTests(PostgresFixture fixture)
{
    private sealed record World(Guid TenantId, Guid CompanyA, Guid CompanyB, Guid ApplicationId, Guid AuthorId,
        Guid InScopeId, Guid GroupId, Guid AllCurrentId, Guid OtherCompanyOnlyId, Guid NoAccessId, Guid RevokedId, Guid InactiveUserId);

    private TestServer Host() => new(new WebHostBuilder().ConfigureServices(s =>
    {
        s.AddSingleton<IConfiguration>(OfferLifecycleServiceTests.Config()); s.AddHttpContextAccessor();
        s.AddScoped<IRequestEntityScopeResolver, RequestEntityScopeResolver>();
        s.AddScoped<ZayraDbContext>(p => fixture.CreateDbWithAccessor(p.GetRequiredService<IHttpContextAccessor>()));
        s.AddSingleton<IDocumentStorage, OfferMemoryStorage>(); s.AddSingleton<IEmailService, ExplicitOfferEmail>();
        s.AddSingleton<ILetterService, ScopeFixLetters>();
        s.AddScoped<IRecruitmentService>(p => new RecruitmentService(p.GetRequiredService<ZayraDbContext>()));
        s.AddScoped<OfferArtifactService>(); s.AddScoped<OfferLifecycleService>(); s.AddScoped<OfferHistoryQueryService>();
        s.AddScoped<OfferDispatchService>(); s.AddScoped<OfferMailReadinessService>();
        s.AddAuthentication("ScopeFix").AddScheme<AuthenticationSchemeOptions, ScopeFixAuthentication>("ScopeFix", _ => { }); s.AddAuthorization();
        s.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>(); s.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();
        s.AddSingleton<IAuthorizationHandler, PermissionAwareRolesAuthorizationHandler>(); s.AddSingleton<IAuthorizationMiddlewareResultHandler, PermissionAwareAuthorizationResultHandler>();
        s.AddControllers().AddApplicationPart(typeof(OffersController).Assembly);
    }).Configure(a => { a.UseRouting(); a.UseAuthentication(); a.UseAuthorization(); a.UseEndpoints(e => e.MapControllers()); }));

    private async Task<HttpClient> ClientAsync(TestServer host, World w, Guid userId)
    {
        await using var db = fixture.CreateDb();
        var permissions = await db.Roles.Where(r => r.TenantId == w.TenantId && r.Name == "HR Manager")
            .SelectMany(r => r.RolePermissions.Select(rp => rp.Permission!.Key)).ToListAsync();
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Tenant", w.TenantId.ToString());
        client.DefaultRequestHeaders.Add("X-Test-User", userId.ToString());
        client.DefaultRequestHeaders.Add("X-Test-Company", w.CompanyA.ToString());
        client.DefaultRequestHeaders.Add("X-Test-Role", "HR Manager");
        client.DefaultRequestHeaders.Add("X-Test-Permissions", string.Join(',', permissions));
        return client;
    }

    private async Task<Guid> FrozenOfferAsync(TestServer host, World w, HttpClient author)
    {
        var created = await author.PostAsJsonAsync("/api/recruitment/offers", new
        {
            applicationId = w.ApplicationId, offeredJobTitle = "Engineer", offeredDepartment = "Engineering",
            startDate = "2027-02-01", basicSalary = 1000, housingAllowance = 0, transportAllowance = 0, otherAllowances = 0,
            probationMonths = 3, responseDeadline = DateTime.UtcNow.AddDays(7), expectedApplicationVersion = await ApplicationVersionAsync(w.ApplicationId),
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var offerId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        (await author.PostAsJsonAsync($"/api/recruitment/offers/{offerId}/freeze", new { expectedVersion = await OfferVersionAsync(offerId) }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        return offerId;
    }

    [Fact]
    public async Task The_picker_lists_only_people_who_can_act_on_this_offer_in_its_company_and_never_its_author()
    {
        var w = await SeedAsync();
        using var host = Host();
        using var author = await ClientAsync(host, w, w.AuthorId);
        var offerId = await FrozenOfferAsync(host, w, author);

        var ids = (await author.GetFromJsonAsync<JsonElement[]>($"/api/recruitment/offers/{offerId}/approver-options"))!
            .Select(o => o.GetProperty("userId").GetGuid()).ToList();

        ids.Should().Contain([w.InScopeId, w.GroupId, w.AllCurrentId], "a grant to this company, group scope and an all-companies grant all reach it");
        ids.Should().NotContain(w.OtherCompanyOnlyId, "a person with access to another company only cannot open this offer");
        ids.Should().NotContain(w.NoAccessId, "a person with no company access at all cannot open any offer");
        ids.Should().NotContain(w.RevokedId, "a revoked grant gives no access");
        ids.Should().NotContain(w.InactiveUserId);
        ids.Should().NotContain(w.AuthorId, "the maker never checks their own offer");
    }

    [Theory]
    [InlineData("other")]
    [InlineData("none")]
    [InlineData("revoked")]
    public async Task A_request_naming_someone_who_cannot_reach_the_offers_company_is_refused_and_creates_no_step(string who)
    {
        var w = await SeedAsync();
        using var host = Host();
        using var author = await ClientAsync(host, w, w.AuthorId);
        var offerId = await FrozenOfferAsync(host, w, author);
        var versionBefore = await OfferVersionAsync(offerId);
        var approver = who switch { "other" => w.OtherCompanyOnlyId, "none" => w.NoAccessId, _ => w.RevokedId };

        var refused = await author.PostAsJsonAsync($"/api/recruitment/offers/{offerId}/approvals",
            new { approverName = "Someone", approverUserId = approver, approverRole = "HR Manager", expectedVersion = versionBefore });

        refused.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, await refused.Content.ReadAsStringAsync());
        var body = await refused.Content.ReadAsStringAsync();
        body.Should().Contain("offer_approver_out_of_scope").And.Contain("access");
        await using var db = fixture.CreateDb();
        (await db.OfferApprovals.CountAsync(a => a.OfferLetterId == offerId)).Should().Be(0, "no step may be created");
        var offer = await db.OfferLetters.AsNoTracking().SingleAsync(o => o.Id == offerId);
        offer.Status.Should().Be("Draft"); offer.Version.Should().Be(versionBefore, "the refused request changes nothing");
    }

    [Fact]
    public async Task The_author_is_still_refused_and_a_person_with_access_to_the_company_is_still_accepted()
    {
        var w = await SeedAsync();
        using var host = Host();
        using var author = await ClientAsync(host, w, w.AuthorId);
        var offerId = await FrozenOfferAsync(host, w, author);

        var self = await author.PostAsJsonAsync($"/api/recruitment/offers/{offerId}/approvals",
            new { approverName = "Author", approverUserId = w.AuthorId, approverRole = "HR Manager", expectedVersion = await OfferVersionAsync(offerId) });
        self.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await self.Content.ReadAsStringAsync()).Should().Contain("offer_maker_checker");

        foreach (var approver in new[] { w.InScopeId, w.GroupId })
        {
            var ok = await author.PostAsJsonAsync($"/api/recruitment/offers/{offerId}/approvals",
                new { approverName = "Checker", approverUserId = approver, approverRole = "HR Manager", expectedVersion = await OfferVersionAsync(offerId) });
            ok.StatusCode.Should().Be(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync());
        }
        await using var db = fixture.CreateDb();
        (await db.OfferApprovals.CountAsync(a => a.OfferLetterId == offerId)).Should().Be(2);
    }

    private async Task<long> ApplicationVersionAsync(Guid id) { await using var db = fixture.CreateDb(); return await db.JobApplications.AsNoTracking().Where(a => a.Id == id).Select(a => a.Version).SingleAsync(); }
    private async Task<long> OfferVersionAsync(Guid id) { await using var db = fixture.CreateDb(); return await db.OfferLetters.AsNoTracking().Where(o => o.Id == id).Select(o => o.Version).SingleAsync(); }

    private async Task<World> SeedAsync()
    {
        var seed = await OfferLifecycleTestData.SeedAsync(fixture);
        await using var db = fixture.CreateDb();
        var tenantId = seed.Actor.TenantId; var companyA = seed.Actor.CompanyId!.Value;
        var companyB = new Company { TenantId = tenantId, DefaultCurrency = "SAR" };
        db.Companies.Add(companyB);
        await new AuthSeeder(db).EnsureTenantRolesAsync(tenantId, default);
        db.Departments.Add(new() { TenantId = tenantId, NameEn = "Engineering", Code = "ENG" });
        db.Designations.Add(new() { TenantId = tenantId, TitleEn = "Engineer", Code = "ENG" });
        var hr = await db.Roles.SingleAsync(r => r.TenantId == tenantId && r.Name == "HR Manager");

        User NewUser(string name, bool group = false, bool active = true)
        {
            var email = $"{name}-{Guid.NewGuid():N}@example.test";
            var user = new User { TenantId = tenantId, Email = email, NormalizedEmail = email.ToUpperInvariant(), FullName = $"Test {name}",
                Status = active ? "Active" : "Inactive", IsActive = active, IsGroupScope = group };
            user.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = hr.Id });
            db.Users.Add(user);
            return user;
        }
        void Grant(User user, Guid? company, string mode = EntityGrantModes.SelectedCompanies, bool active = true) =>
            db.UserEntityAccesses.Add(new UserEntityAccess { TenantId = tenantId, UserId = user.Id, CompanyId = company, GrantMode = mode, Role = "HR Manager", IsActive = active });

        var author = NewUser("author"); Grant(author, companyA);
        var inScope = NewUser("inscope"); Grant(inScope, companyA);
        var group = NewUser("group", group: true);
        var allCurrent = NewUser("allcurrent"); Grant(allCurrent, null, EntityGrantModes.AllCurrentCompanies);
        var other = NewUser("othercompany"); Grant(other, companyB.Id);
        var none = NewUser("noaccess");
        var revoked = NewUser("revoked"); Grant(revoked, companyA, active: false);
        var inactive = NewUser("inactive", group: true, active: false);
        await db.SaveChangesAsync();
        return new World(tenantId, companyA, companyB.Id, seed.ApplicationId, author.Id, inScope.Id, group.Id, allCurrent.Id, other.Id, none.Id, revoked.Id, inactive.Id);
    }
}

file sealed class ScopeFixLetters : ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}

file sealed class ScopeFixAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
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
