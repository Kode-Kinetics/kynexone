using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zayra.Api.Application.AI;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Setup;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Owner decision (2026-10-20): company policy documents are not uploaded, parsed or sent to any AI; only manual
/// company setup is enabled. Through the REAL Program.cs pipeline: every policy upload / extraction / preparation /
/// review / library / ask endpoint answers 403 feature_not_enabled, writes nothing and calls no model while the
/// opt-in <c>policy_intake</c> flag is off (the default for every tenant). With the flag on the endpoints work again,
/// but policy text still never reaches the model gateway, even with an external provider configured.
/// </summary>
public sealed class PolicyIntakeKillSwitchHttpTests(PolicyIntakeKillSwitchFixture f) : IClassFixture<PolicyIntakeKillSwitchFixture>
{
    private static readonly string Missing = "00000000-0000-0000-0000-00000000abcd";

    /// <summary>Every policy-intake endpoint, with a body the action would accept.</summary>
    public static TheoryData<string, string> Endpoints => new()
    {
        { "GET", "/api/ai/policy/documents" },
        { "UPLOAD", "/api/ai/policy/documents/upload" },
        { "DELETE", "/api/ai/policy/documents/{doc}" },
        { "POST", "/api/ai/policy/ask" },
        { "GET", "/api/ai/policy/documents/{doc}/text" },
        { "PUBLISH", "/api/ai/policy/documents/{doc}/publish" },
        { "POST", "/api/ai/policy/documents/{doc}/withdraw" },
        { "GET", "/api/ai/policy/employee/documents" },
        { "POST", "/api/ai/policy/employee/ask" },
        { "GET", "/api/ai/policy/employee/documents/{doc}/text" },
        { "EXTRACT_LOCAL", "/api/setup-assistant/policy/extract" },
        { "EXTRACT_AI", "/api/setup-assistant/policy/extract" },
        { "REVIEW", "/api/setup-assistant/policy/reviews" },
        { "GET", "/api/setup-assistant/policy/reviews?companyId={company}&documentId={doc}" },
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task WithTheFlagOff_EveryPolicyEndpointIsRefused_WritesNothing_AndCallsNoModel(string kind, string path)
    {
        var before = await f.Counts(f.OffTenant);
        var calls = f.Model.Calls;

        using var response = await f.Send(kind, path, f.OffTenant);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{kind} {path}: {await response.Content.ReadAsStringAsync()}");
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("error").GetString().Should().Be("feature_not_enabled");
        body.GetProperty("feature").GetString().Should().Be(FeatureKeys.PolicyIntake);
        (await f.Counts(f.OffTenant)).Should().Be(before, "a refused policy request writes no document, chunk, review, audit or AI-usage row");
        f.Model.Calls.Should().Be(calls, "a refused policy request never reaches the model gateway");
    }

    [Fact]
    public async Task WithTheFlagOn_PolicyIntakeWorks_ButPolicyTextNeverReachesTheModel()
    {
        var calls = f.Model.Calls;

        using var upload = await f.Send("UPLOAD", "/api/ai/policy/documents/upload", f.OnTenant);
        upload.StatusCode.Should().Be(HttpStatusCode.OK, await upload.Content.ReadAsStringAsync());

        using var local = await f.Send("EXTRACT_LOCAL", "/api/setup-assistant/policy/extract", f.OnTenant);
        local.StatusCode.Should().Be(HttpStatusCode.OK, await local.Content.ReadAsStringAsync());
        (await local.Content.ReadFromJsonAsync<PolicyExtractionResult>())!.Provider.Should().Be("local-rules");

        // An external provider IS configured on this host (AI_PROVIDER=anthropic with a key). The AI path still
        // answers without calling it: the policy-content gate refuses before the gateway.
        using var ai = await f.Send("EXTRACT_AI", "/api/setup-assistant/policy/extract", f.OnTenant);
        ai.StatusCode.Should().Be(HttpStatusCode.OK, await ai.Content.ReadAsStringAsync());
        var extraction = (await ai.Content.ReadFromJsonAsync<PolicyExtractionResult>())!;
        extraction.Provider.Should().Be("fallback");
        extraction.Proposals.Should().BeEmpty();
        extraction.Message.Should().Contain("never sent to an AI provider");

        using var ask = await f.Send("POST", "/api/ai/policy/ask", f.OnTenant);
        ask.StatusCode.Should().Be(HttpStatusCode.OK, await ask.Content.ReadAsStringAsync());
        (await ask.Content.ReadFromJsonAsync<PolicyAskResponse>())!.Provider.Should().Be("fallback");

        using var history = await f.Send("GET", "/api/setup-assistant/policy/reviews?companyId={company}&documentId={doc}", f.OnTenant);
        history.StatusCode.Should().Be(HttpStatusCode.OK, await history.Content.ReadAsStringAsync());

        f.Model.Calls.Should().Be(calls, "company policy text is never sent to any model provider");
    }
}

public sealed class PolicyIntakeKillSwitchFixture : IAsyncLifetime
{
    public sealed class RecordingModel : ILlmClient
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new LlmResponse(true, request.Provider, request.Model, "{\"proposals\":[]}", 10, 10));
        }
    }

    public sealed record TenantSeed(Guid TenantId, Guid CompanyId, Guid DocumentId, string Hash, string Token);
    public sealed record RowCounts(int Documents, int Chunks, int PolicyAudits, int AiLogs, int AiUsage);

    private const string PolicyText = "Annual leave\nEmployees receive thirty days of annual leave.\nOnboarding\nHR arranges contract signing.\n";
    private readonly string _connection = $"Data Source=file:policy-kill-{Guid.NewGuid():N}?mode=memory&cache=shared";
    private SqliteConnection? _anchor;
    private AuthorizationPipelineHost? _host;
    private HttpClient? _client;

    public RecordingModel Model { get; } = new();
    public TenantSeed OffTenant { get; private set; } = null!;
    public TenantSeed OnTenant { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _anchor = new SqliteConnection(_connection);
        await _anchor.OpenAsync();
        _host = new AuthorizationPipelineHost(_connection,
            configureTestServices: services =>
            {
                services.RemoveAll<ILlmClient>();
                services.AddSingleton<ILlmClient>(Model);
            },
            // An external provider with a key: anything that forwards policy text would reach RecordingModel.
            hostSettings: new Dictionary<string, string?> { ["AI_PROVIDER"] = "anthropic", ["ANTHROPIC_API_KEY"] = "test-key-not-a-secret" });
        _client = _host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using (var scope = _host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ZayraDbContext>().Database.EnsureCreatedAsync();
        OffTenant = await Seed("off", enableFlag: false);
        OnTenant = await Seed("on", enableFlag: true);
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_host is not null) await _host.DisposeAsync();
        if (_anchor is not null) await _anchor.DisposeAsync();
    }

    private async Task<TenantSeed> Seed(string name, bool enableFlag)
    {
        var tenantId = Guid.NewGuid(); var companyId = Guid.NewGuid();
        using var scope = _host!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = $"Policy kill {name}", Slug = $"policy-kill-{name}-{tenantId:N}", IsActive = true });
        db.Companies.Add(new Company { Id = companyId, TenantId = tenantId, LegalNameEn = "Policy Company", CountryCode = "SA", IsActive = true });
        var email = $"hr-{name}@policy-kill.local";
        var hr = new User { TenantId = tenantId, Email = email, NormalizedEmail = email.ToUpperInvariant(), FullName = "HR",
            PasswordHash = "no-login-path", Status = "Active", AccessMode = "FullPortal", IsActive = true, IsEmailConfirmed = true, IsGroupScope = true };
        db.Users.Add(hr);
        var role = new Role { TenantId = tenantId, Name = "Admin", NormalizedName = "ADMIN" };
        db.Roles.Add(role);
        db.Set<UserRole>().Add(new UserRole { UserId = hr.Id, RoleId = role.Id });
        foreach (var permission in new[] { "organization.write", "ai.query", "ess.read" })
            db.Set<UserPermissionOverride>().Add(new UserPermissionOverride { TenantId = tenantId, UserId = hr.Id, PermissionKey = permission, Effect = "Allow", IsActive = true });
        var employee = new Employee { TenantId = tenantId, CompanyId = companyId, FullName = "HR", EnglishName = "HR",
            EmployeeCode = $"PK-{name}", Status = "Active", JoiningDate = DateTime.UtcNow, UserAccountId = hr.Id };
        db.Employees.Add(employee);
        if (enableFlag)
            db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = tenantId, FeatureKey = FeatureKeys.PolicyIntake, IsEnabled = true });
        await db.SaveChangesAsync();
        db.EmployeeUserAccounts.Add(new EmployeeUserAccount { TenantId = tenantId, EmployeeId = employee.Id, UserId = hr.Id,
            AccessMode = "FullPortal", Status = "Active", IsPrimary = true, RequiresPasswordSetup = false });
        await db.SaveChangesAsync();

        // A document that already exists from before the switch (written through the service, not the gated API).
        var documents = scope.ServiceProvider.GetRequiredService<IPolicyDocumentService>();
        using var content = new MemoryStream(Encoding.UTF8.GetBytes(PolicyText));
        var doc = await documents.UploadAsync(tenantId, hr.Id, content, "handbook.txt", "text/plain", default);
        var entity = await db.PolicyDocuments.IgnoreQueryFilters().SingleAsync(d => d.Id == doc.Id);
        entity.CompanyId = companyId;
        await db.SaveChangesAsync();

        var user = await db.Users.IgnoreQueryFilters().AsNoTracking().Include(u => u.Tenant)
            .Include(u => u.UserRoles).ThenInclude(r => r.Role).ThenInclude(r => r!.RolePermissions).ThenInclude(p => p.Permission)
            .Include(u => u.PermissionOverrides).Include(u => u.EmployeeUserAccounts).Include(u => u.EntityAccesses)
            .SingleAsync(u => u.TenantId == tenantId && u.Email == email);
        var token = scope.ServiceProvider.GetRequiredService<ITokenService>().CreateAccessToken(user, AuthService.GetRoles(user), AuthService.GetPermissions(user),
            user.Tenant!, Array.Empty<EntityAccessGrant>(), EntityScopeClaims.Resolve(true, Array.Empty<EntityAccessGrant>(), Array.Empty<Guid>()), out _);
        return new(tenantId, companyId, doc.Id, doc.ContentSha256, token);
    }

    public async Task<HttpResponseMessage> Send(string kind, string path, TenantSeed who)
    {
        path = path.Replace("{doc}", who.DocumentId.ToString()).Replace("{company}", who.CompanyId.ToString());
        var profile = Zayra.Api.Tests.SetupConfigurationTests.Profile();
        HttpRequestMessage request = kind switch
        {
            "GET" => new(HttpMethod.Get, path),
            "DELETE" => new(HttpMethod.Delete, path),
            "POST" => new(HttpMethod.Post, path) { Content = JsonContent.Create(new PolicyAskRequest("How many days of annual leave?")) },
            "UPLOAD" => new(HttpMethod.Post, path)
            {
                Content = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(PolicyText)), "file", "new-handbook.txt" } },
            },
            "PUBLISH" => new(HttpMethod.Post, path) { Content = JsonContent.Create(new PublishPolicyRequest(who.CompanyId, who.Hash)) },
            "EXTRACT_LOCAL" => new(HttpMethod.Post, path) { Content = JsonContent.Create(new PolicyExtractionRequest(who.DocumentId, false, profile)) },
            "EXTRACT_AI" => new(HttpMethod.Post, path) { Content = JsonContent.Create(new PolicyExtractionRequest(who.DocumentId, true, profile)) },
            "REVIEW" => new(HttpMethod.Post, path)
            {
                Content = JsonContent.Create(new PolicyReviewRequest(who.CompanyId, who.DocumentId, who.Hash, "proposal", "leave.annual",
                    "Annual leave is thirty days", "Reviewed", "Checked against the handbook")),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
        using (request)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", who.Token);
            return await _client!.SendAsync(request);
        }
    }

    public async Task<RowCounts> Counts(TenantSeed who)
    {
        using var scope = _host!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var t = who.TenantId;
        return new(
            await db.PolicyDocuments.IgnoreQueryFilters().CountAsync(d => d.TenantId == t && !d.IsDeleted),
            await db.DocumentChunks.IgnoreQueryFilters().CountAsync(c => c.TenantId == t),
            await db.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.TenantId == t
                && (a.Action.StartsWith("policy.") || a.Action == "setup.policy_review_recorded")),
            await db.AIHRQueryLogs.IgnoreQueryFilters().CountAsync(l => l.TenantId == t),
            await db.TenantAiUsages.IgnoreQueryFilters().CountAsync(u => u.TenantId == t));
    }
}
