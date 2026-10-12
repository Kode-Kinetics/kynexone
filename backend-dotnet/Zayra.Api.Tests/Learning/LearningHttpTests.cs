using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Ess;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Learning;
using Zayra.Api.Models;
using Zayra.Api.Tests.Security;

namespace Zayra.Api.Tests.Learning;

/// <summary>
/// Learning through the REAL Program.cs pipeline (AuthorizationPipelineHost): the permission on every endpoint, the
/// opt-in flag, ESS employee resolution, the cmi5 CORS provider, the Origin rule, the request size cap and the
/// return-URL filter. The service-level suites cover behaviour; this one covers the wiring.
/// </summary>
public sealed class LearningHttpTests(LearningHttpFixture f) : IClassFixture<LearningHttpFixture>
{
    private static readonly Guid Missing = Guid.Parse("00000000-0000-0000-0000-00000000abcd");

    public static TheoryData<string, string, string, HttpStatusCode> Matrix => new()
    {
        { "GET", "/api/learning/courses", "anonymous", HttpStatusCode.Unauthorized },
        { "GET", "/api/learning/courses", "plain", HttpStatusCode.Forbidden },
        { "GET", "/api/learning/courses", "reader", HttpStatusCode.OK },
        { "POST", "/api/learning/courses", "reader", HttpStatusCode.Forbidden },
        { "POST", "/api/learning/paths", "reader", HttpStatusCode.Forbidden },
        { "POST", "/api/learning/rules", "reader", HttpStatusCode.Forbidden },
        { "POST", "/api/learning/assignments", "reader", HttpStatusCode.Forbidden },
        { "GET", "/api/learning/assignments", "reader", HttpStatusCode.Forbidden },
        { "GET", "/api/learning/reports/summary", "reader", HttpStatusCode.Forbidden },
        { "GET", "/api/learning/reports/pending", "reader", HttpStatusCode.Forbidden },
        { "GET", "/api/learning/credentials", "reader", HttpStatusCode.Forbidden },
        { "POST", $"/api/learning/credentials/{"00000000-0000-0000-0000-00000000abcd"}/revoke", "reader", HttpStatusCode.Forbidden },
        { "POST", $"/api/learning/credentials/{"00000000-0000-0000-0000-00000000abcd"}/correct", "reader", HttpStatusCode.Forbidden },
        { "POST", $"/api/learning/enrollments/{"00000000-0000-0000-0000-00000000abcd"}/verify", "reader", HttpStatusCode.Forbidden },
        { "POST", $"/api/learning/enrollments/{"00000000-0000-0000-0000-00000000abcd"}/reject-verification", "reader", HttpStatusCode.Forbidden },
        { "POST", $"/api/learning/enrollments/{"00000000-0000-0000-0000-00000000abcd"}/reset-attempts", "reader", HttpStatusCode.Forbidden },
        { "GET", "/api/learning/reports/summary", "hr", HttpStatusCode.OK },
        { "GET", "/api/learning/reports/pending", "hr", HttpStatusCode.OK },
        { "POST", $"/api/learning/enrollments/{"00000000-0000-0000-0000-00000000abcd"}/verify", "hr", HttpStatusCode.NotFound },
        { "POST", $"/api/learning/credentials/{"00000000-0000-0000-0000-00000000abcd"}/revoke", "hr", HttpStatusCode.BadRequest },
        { "GET", "/api/ess/learning", "plain", HttpStatusCode.Forbidden },
        { "GET", "/api/ess/learning", "learner", HttpStatusCode.OK },
        { "POST", $"/api/ess/learning/enrollments/{"00000000-0000-0000-0000-00000000abcd"}/launch", "reader", HttpStatusCode.Forbidden },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task EveryEndpoint_AnswersOnlyToItsPermission(string method, string path, string who, HttpStatusCode expected)
    {
        using var response = await f.Send(new HttpMethod(method), path, f.Token(who), method == "POST" ? new { } : null);
        response.StatusCode.Should().Be(expected, $"{who} {method} {path}: {await response.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task ATenantWithoutTheOptIn_IsRefusedEverywhere()
    {
        foreach (var path in new[] { "/api/learning/courses", "/api/learning/reports/summary", "/api/ess/learning" })
        {
            using var response = await f.Send(HttpMethod.Get, path, f.Token("hrB"));
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, path);
            (await Json(response)).GetProperty("error").GetString().Should().Be("feature_not_enabled");
        }
    }

    [Fact]
    public async Task Ess_ResolvesTheEmployeeFromTheLogin_AndALoginWithoutALinkIsToldWhy()
    {
        using var unlinked = await f.Send(HttpMethod.Get, "/api/ess/learning", f.Token("unlinked"));
        unlinked.StatusCode.Should().Be(HttpStatusCode.Conflict, await unlinked.Content.ReadAsStringAsync());
        (await Json(unlinked)).GetProperty("error").GetString().Should().Be("no_employee_record");
        // Another employee's enrollment id is simply not found for this learner.
        using var foreign = await f.Send(HttpMethod.Get, $"/api/ess/learning/enrollments/{Missing}", f.Token("learner"));
        foreign.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Cmi5OverHttp_CorsOnlyForAnOpenSessionsOrigin_OriginRequired_SizeCapped_ReturnUrlFiltered()
    {
        const string au = "https://au.example.test:5411";
        var enrollmentId = await f.AssignedCmi5EnrollmentAsync(au);

        using (var bad = await f.Send(HttpMethod.Post, $"/api/ess/learning/enrollments/{enrollmentId}/launch", f.Token("learner"),
                   new { returnUrl = "javascript:alert(document.cookie)" }))
            bad.StatusCode.Should().Be(HttpStatusCode.OK, await bad.Content.ReadAsStringAsync());
        (await f.LatestSessionAsync(enrollmentId)).ReturnUrl.Should().BeNull();

        using var launch = await f.Send(HttpMethod.Post, $"/api/ess/learning/enrollments/{enrollmentId}/launch", f.Token("learner"),
            new { returnUrl = "http://localhost:5173/ess/learning" });
        launch.StatusCode.Should().Be(HttpStatusCode.OK, await launch.Content.ReadAsStringAsync());
        (await f.LatestSessionAsync(enrollmentId)).ReturnUrl.Should().Be("http://localhost:5173/ess/learning");
        var launchUrl = (await Json(launch)).GetProperty("launchUrl").GetString()!;
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(launchUrl).Query);
        var fetchToken = query["fetch"].ToString()[(query["fetch"].ToString().LastIndexOf('/') + 1)..];

        // CORS: the AU origin of an open session is allowed on the AU routes; any other origin is not.
        using (var preflight = await f.Preflight("/api/learning/xapi/statements", au))
            preflight.Headers.TryGetValues("Access-Control-Allow-Origin", out var allowed).Should().BeTrue();
        using (var foreignPreflight = await f.Preflight("/api/learning/xapi/statements", "https://evil.test"))
            foreignPreflight.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();

        using var fetch = await f.Send(HttpMethod.Post, $"/api/learning/cmi5/fetch/{fetchToken}", null, null, origin: au);
        fetch.StatusCode.Should().Be(HttpStatusCode.OK, await fetch.Content.ReadAsStringAsync());
        var authToken = (await Json(fetch)).GetProperty("auth-token").GetString()!;

        var statement = new
        {
            id = Guid.NewGuid(),
            actor = JsonDocument.Parse(query["actor"].ToString()).RootElement.Clone(),
            verb = new { id = Cmi5Verbs.Initialized },
            @object = new { id = query["activityId"].ToString(), objectType = "Activity" },
            context = new { registration = query["registration"].ToString(), extensions = new Dictionary<string, string> { [Cmi5Verbs.SessionIdExtension] = (await f.LatestSessionAsync(enrollmentId)).Id.ToString() } },
        };
        using (var noOrigin = await f.Xapi(authToken, statement, origin: null))
            noOrigin.StatusCode.Should().Be(HttpStatusCode.Forbidden, await noOrigin.Content.ReadAsStringAsync());
        using (var ok = await f.Xapi(authToken, statement, origin: au))
            ok.StatusCode.Should().Be(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync());

        var huge = Encoding.UTF8.GetBytes("[" + string.Join(",", Enumerable.Repeat("{\"pad\":\"" + new string('x', 1000) + "\"}", 300)) + "]");
        using var tooBig = await f.XapiRaw(authToken, huge, au);
        tooBig.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task StandingRules_AreCreatedAndDeactivatedOnlyByAGroupLevelUser()
    {
        var pathId = await f.ActivePathAsync();
        var body = new { pathId, targetKind = "Company", targetId = f.CompanyA, dueInDays = 30, isMandatory = true };
        using (var companyCreate = await f.Send(HttpMethod.Post, "/api/learning/rules", f.Token("hrCompany"), body))
        {
            companyCreate.StatusCode.Should().Be(HttpStatusCode.Forbidden, await companyCreate.Content.ReadAsStringAsync());
            (await Json(companyCreate)).GetProperty("error").GetString().Should().Be("group_scope_required");
        }
        using var created = await f.Send(HttpMethod.Post, "/api/learning/rules", f.Token("hr"), body);
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var ruleId = (await Json(created)).GetProperty("rule").GetProperty("id").GetGuid();

        using (var companyDeactivate = await f.Send(HttpMethod.Post, $"/api/learning/rules/{ruleId}/deactivate", f.Token("hrCompany"), new { }))
        {
            companyDeactivate.StatusCode.Should().Be(HttpStatusCode.Forbidden, await companyDeactivate.Content.ReadAsStringAsync());
            (await Json(companyDeactivate)).GetProperty("error").GetString().Should().Be("group_scope_required");
        }
        using (var stillActive = await f.Send(HttpMethod.Get, "/api/learning/rules", f.Token("hr")))
            (await Json(stillActive)).EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == ruleId).GetProperty("isActive").GetBoolean().Should().BeTrue();
        using var groupDeactivate = await f.Send(HttpMethod.Post, $"/api/learning/rules/{ruleId}/deactivate", f.Token("hr"), new { });
        groupDeactivate.StatusCode.Should().Be(HttpStatusCode.OK, await groupDeactivate.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("data:text/html,hi", null)]
    [InlineData("//evil.test/x", null)]
    [InlineData("https://evil.test/ess", null)]
    [InlineData("ftp://localhost:5173/x", null)]
    [InlineData("http://localhost:5173/ess/learning?x=1", "http://localhost:5173/ess/learning?x=1")]
    [InlineData("https://kynexone.vercel.app/ess/learning", "https://kynexone.vercel.app/ess/learning")]
    public void SafeReturnUrl_KeepsOnlyHttpUrlsOnTheAppsOwnOrigins(string input, string? expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cors:AllowedOrigins:0"] = "http://localhost:5173",
            ["Cors:AllowedOrigins:1"] = "https://kynexone.vercel.app",
        }).Build();
        new EssLearningController(null!, null!, configuration).SafeReturnUrl(input).Should().Be(expected);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
}

public sealed class LearningHttpFixture : IAsyncLifetime
{
    private SqliteConnection _anchor = null!;
    private readonly Dictionary<string, string> _tokens = new();

    public AuthorizationPipelineHost Host { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public Guid TenantA { get; } = Guid.NewGuid();
    public Guid TenantB { get; } = Guid.NewGuid();
    public Guid CompanyA { get; } = Guid.NewGuid();
    public int LearnerEmployeeId { get; private set; }

    private static readonly string[] Hr = ["employees.read", "learning.read", "learning.manage", "learning.assign", "learning.report", "learning.records.correct"];

    public async Task InitializeAsync()
    {
        var connection = $"Data Source=file:learning-http-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _anchor = new SqliteConnection(connection);
        await _anchor.OpenAsync();
        Host = new AuthorizationPipelineHost(connection);
        Client = Host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using (var scope = Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.Tenants.Add(new Tenant { Id = TenantA, Name = "Learning A", Slug = $"lrn-a-{TenantA:N}", IsActive = true });
            db.Tenants.Add(new Tenant { Id = TenantB, Name = "Learning B", Slug = $"lrn-b-{TenantB:N}", IsActive = true });
            db.Companies.Add(new Company { Id = CompanyA, TenantId = TenantA, LegalNameEn = "Learning Co", CountryCode = "SA", IsActive = true });
            db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = TenantA, FeatureKey = FeatureKeys.Learning, IsEnabled = true });
            var employee = new Employee
            {
                TenantId = TenantA, EmployeeCode = "LRN-HTTP-1", FullName = "Lina Learner", Status = EmployeeStatuses.Active, CompanyId = CompanyA,
                EmploymentType = "Full Time", Nationality = "Saudi", JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            };
            db.Employees.Add(employee);
            var users = new Dictionary<string, (User User, string[] Permissions)>
            {
                ["hr"] = (NewUser(TenantA, "hr", mfa: true, groupScope: true), Hr),
                ["reader"] = (NewUser(TenantA, "reader", mfa: false, groupScope: true), ["learning.read"]),
                ["plain"] = (NewUser(TenantA, "plain", mfa: false, groupScope: true), ["employees.read"]),
                ["learner"] = (NewUser(TenantA, "learner", mfa: false, groupScope: true), ["ess.read", "ess.write"]),
                ["unlinked"] = (NewUser(TenantA, "unlinked", mfa: false, groupScope: true), ["ess.read", "ess.write"]),
                ["hrB"] = (NewUser(TenantB, "hrB", mfa: true, groupScope: true), Hr.Append("ess.read").ToArray()),
                // learning.manage, but scoped to one company (not group-level).
                ["hrCompany"] = (NewUser(TenantA, "hrCompany", mfa: true, groupScope: false), Hr),
            };
            db.Users.AddRange(users.Values.Select(u => u.User));
            await db.SaveChangesAsync();
            LearnerEmployeeId = employee.Id;
            foreach (var (user, permissions) in users.Values)
                db.Set<UserPermissionOverride>().AddRange(permissions.Select(p => new UserPermissionOverride
                {
                    TenantId = user.TenantId, UserId = user.Id, PermissionKey = p, Effect = "Allow", IsActive = true, Reason = "learning http test",
                }));
            db.Set<UserEntityAccess>().Add(new UserEntityAccess
            {
                TenantId = TenantA, UserId = users["hrCompany"].User.Id, CompanyId = CompanyA, Role = "HR Officer", IsActive = true,
            });
            db.Set<EmployeeUserAccount>().Add(new EmployeeUserAccount
            {
                TenantId = TenantA, EmployeeId = employee.Id, UserId = users["learner"].User.Id, AccessMode = "FullPortal", Status = "Active",
                RequiresPasswordSetup = false,
            });
            await db.SaveChangesAsync();
        }
        foreach (var (name, tenant) in new[] { ("hr", TenantA), ("reader", TenantA), ("plain", TenantA), ("learner", TenantA), ("unlinked", TenantA), ("hrB", TenantB) })
            _tokens[name] = await MintAsync(name, tenant);
        _tokens["hrCompany"] = await MintAsync("hrCompany", TenantA, companyId: CompanyA);
    }

    public string? Token(string who) => who == "anonymous" ? null : _tokens[who];

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (Host is not null) await Host.DisposeAsync();
        if (_anchor is not null) await _anchor.DisposeAsync();
    }

    public async Task<HttpResponseMessage> Send(HttpMethod method, string path, string? bearer, object? body = null, string? origin = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (origin is not null) request.Headers.Add("Origin", origin);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await Client.SendAsync(request);
    }

    public async Task<HttpResponseMessage> Preflight(string path, string origin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "authorization,content-type,x-experience-api-version");
        return await Client.SendAsync(request);
    }

    public Task<HttpResponseMessage> Xapi(string authToken, object statement, string? origin) =>
        XapiRaw(authToken, JsonSerializer.SerializeToUtf8Bytes(statement), origin);

    public async Task<HttpResponseMessage> XapiRaw(string authToken, byte[] body, string? origin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/learning/xapi/statements");
        request.Headers.TryAddWithoutValidation("Authorization", "Basic " + authToken);
        request.Headers.Add("X-Experience-API-Version", "1.0.3");
        if (origin is not null) request.Headers.Add("Origin", origin);
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return await Client.SendAsync(request);
    }

    /// <summary>HR builds, publishes and activates a one-lesson path over HTTP; returns the path id.</summary>
    public async Task<Guid> ActivePathAsync()
    {
        var hr = Token("hr");
        var code = "RULE" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        using var course = await Send(HttpMethod.Post, "/api/learning/courses", hr, new
        {
            code, title = "Induction", contentKind = LearningContentKinds.Lesson, lessonHtml = "<p>Welcome.</p>",
            completionRule = LearningCompletionRules.Acknowledgement, issuesCertificate = false,
        });
        var courseText = await course.Content.ReadAsStringAsync();
        course.StatusCode.Should().BeOneOf([HttpStatusCode.OK, HttpStatusCode.Created], courseText);
        var courseId = JsonDocument.Parse(courseText).RootElement.GetProperty("id").GetGuid();
        using (var publish = await Send(HttpMethod.Post, $"/api/learning/courses/{courseId}/publish", hr, new { }))
            publish.StatusCode.Should().Be(HttpStatusCode.OK, await publish.Content.ReadAsStringAsync());
        using var path = await Send(HttpMethod.Post, "/api/learning/paths", hr, new
        {
            code, name = "Induction", renewalWindowDays = 30, items = new[] { new { courseId, isRequired = true } },
        });
        var pathText = await path.Content.ReadAsStringAsync();
        path.StatusCode.Should().BeOneOf([HttpStatusCode.OK, HttpStatusCode.Created], pathText);
        var pathId = JsonDocument.Parse(pathText).RootElement.GetProperty("id").GetGuid();
        using (var activate = await Send(HttpMethod.Post, $"/api/learning/paths/{pathId}/activate", hr, new { }))
            activate.StatusCode.Should().Be(HttpStatusCode.OK, await activate.Content.ReadAsStringAsync());
        return pathId;
    }

    /// <summary>HR builds, publishes and assigns a cmi5 unit to the learner, all over HTTP; returns the learner's enrollment id.</summary>
    public async Task<Guid> AssignedCmi5EnrollmentAsync(string auOrigin)
    {
        var hr = Token("hr");
        var code = "HTTP" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        using var course = await Send(HttpMethod.Post, "/api/learning/courses", hr, new
        {
            code, title = "Fire safety unit", contentKind = LearningContentKinds.Cmi5, cmi5AuUrl = auOrigin + "/index.html",
            cmi5ActivityId = $"https://au.example.test/{code.ToLowerInvariant()}", cmi5MoveOn = Cmi5MoveOnValues.Completed,
            completionRule = LearningCompletionRules.Cmi5MoveOn, issuesCertificate = false,
        });
        var courseText = await course.Content.ReadAsStringAsync();
        course.StatusCode.Should().BeOneOf([HttpStatusCode.OK, HttpStatusCode.Created], courseText);
        var courseId = JsonDocument.Parse(courseText).RootElement.GetProperty("id").GetGuid();
        using (var publish = await Send(HttpMethod.Post, $"/api/learning/courses/{courseId}/publish", hr, new { }))
            publish.StatusCode.Should().Be(HttpStatusCode.OK, await publish.Content.ReadAsStringAsync());
        using var path = await Send(HttpMethod.Post, "/api/learning/paths", hr, new
        {
            code, name = "Fire safety", renewalWindowDays = 30, items = new[] { new { courseId, isRequired = true } },
        });
        var pathText = await path.Content.ReadAsStringAsync();
        path.StatusCode.Should().BeOneOf([HttpStatusCode.OK, HttpStatusCode.Created], pathText);
        var pathId = JsonDocument.Parse(pathText).RootElement.GetProperty("id").GetGuid();
        using (var activate = await Send(HttpMethod.Post, $"/api/learning/paths/{pathId}/activate", hr, new { }))
            activate.StatusCode.Should().Be(HttpStatusCode.OK, await activate.Content.ReadAsStringAsync());
        using (var assign = await Send(HttpMethod.Post, "/api/learning/assignments", hr, new
               {
                   pathId, targetKind = "Employee", employeeIds = new[] { LearnerEmployeeId }, isMandatory = true,
               }))
            assign.StatusCode.Should().Be(HttpStatusCode.OK, await assign.Content.ReadAsStringAsync());

        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var assignment = await db.LearningAssignments.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(a => a.TenantId == TenantA && a.PathId == pathId && a.EmployeeId == LearnerEmployeeId);
        return await db.LearningEnrollments.IgnoreQueryFilters().AsNoTracking().Where(e => e.AssignmentId == assignment.Id).Select(e => e.Id).SingleAsync();
    }

    public async Task<LearningLaunchSession> LatestSessionAsync(Guid enrollmentId)
    {
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var sessions = await db.LearningLaunchSessions.IgnoreQueryFilters().AsNoTracking().Where(s => s.EnrollmentId == enrollmentId).ToListAsync();
        return sessions.OrderBy(s => s.LaunchedAtUtc).Last();
    }

    private static User NewUser(Guid tenantId, string name, bool mfa, bool groupScope) => new()
    {
        TenantId = tenantId, Email = $"{name}-{tenantId:N}@lrn-http.local", NormalizedEmail = $"{name}-{tenantId:N}@lrn-http.local".ToUpperInvariant(),
        FullName = name, PasswordHash = "no-login-path", Status = "Active", AccessMode = "FullPortal", IsActive = true, IsEmailConfirmed = true,
        IsGroupScope = groupScope, MFAEnabled = mfa, MfaSecretEncrypted = mfa ? "enrolled-for-test" : null,
    };

    private async Task<string> MintAsync(string name, Guid tenantId, Guid? companyId = null)
    {
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var email = $"{name}-{tenantId:N}@lrn-http.local";
        var user = await db.Users.IgnoreQueryFilters().AsNoTracking().Include(u => u.Tenant)
            .Include(u => u.UserRoles).ThenInclude(r => r.Role).ThenInclude(r => r!.RolePermissions).ThenInclude(p => p.Permission)
            .Include(u => u.PermissionOverrides).Include(u => u.EmployeeUserAccounts).Include(u => u.EntityAccesses)
            .SingleAsync(u => u.TenantId == tenantId && u.Email == email);
        EntityAccessGrant[] grants = companyId is Guid company ? [new EntityAccessGrant(company, "HR Officer")] : [];
        return scope.ServiceProvider.GetRequiredService<ITokenService>().CreateAccessToken(user, AuthService.GetRoles(user),
            AuthService.GetPermissions(user), user.Tenant!, grants,
            EntityScopeClaims.Resolve(companyId is null, grants, companyId is Guid c ? [c] : Array.Empty<Guid>()), out _);
    }
}
