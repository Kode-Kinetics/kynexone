using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Tests.Security;

namespace Zayra.Api.Tests.SelfieAttendance;

/// <summary>
/// The selfie demo exception through the REAL Program.cs pipeline: the <c>SelfieDemoException__*</c> settings render.yaml
/// sets, bound at startup; the global opt-in guard (which must let a listed tenant's upload through without a flag row);
/// consent, upload and punch over HTTP. Storage here is NOT on any KSA allow-list and no tenant has a sign-off. Speaks
/// HTTP only, so it also runs against the code before the exception — where the listed tenant is refused.
/// </summary>
public sealed class SelfieDemoExceptionHttpTests : IClassFixture<SelfieDemoExceptionHttpFixture>
{
    private readonly SelfieDemoExceptionHttpFixture _fx;
    public SelfieDemoExceptionHttpTests(SelfieDemoExceptionHttpFixture fx) => _fx = fx;

    [Fact]
    public async Task ListedTenant_ConsentsUploadsAndPunches_ThroughTheGuard_WithoutAFlagOrSignOff()
    {
        var view = await _fx.GetJsonAsync(_fx.DemoToken, "/api/ess/attendance-verification");
        var selfie = view.GetProperty("selfie");
        Assert.True(selfie.GetProperty("enabled").GetBoolean(), view.ToString());
        Assert.Contains("deleted automatically 7 days", selfie.GetProperty("demoNotice").GetProperty("message").GetString());

        using (var consent = new HttpRequestMessage(HttpMethod.Post, "/api/ess/biometric-consent") { Content = JsonContent.Create(new { policyVersion = "1", channel = "Mobile" }) })
        {
            consent.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _fx.DemoToken);
            var consented = await _fx.Client.SendAsync(consent);
            Assert.True(consented.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, await consented.Content.ReadAsStringAsync());
        }

        var upload = await _fx.UploadAsync(_fx.DemoToken, SelfieAttendanceTests.SelfieJpeg());
        Assert.True(upload.StatusCode == HttpStatusCode.Created, $"{upload.StatusCode}: {await upload.Content.ReadAsStringAsync()}");
        var evidenceId = (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("evidenceId").GetGuid();

        using var punch = new HttpRequestMessage(HttpMethod.Post, "/api/attendance/punch/mobile")
        {
            Content = JsonContent.Create(new { employeeId = 0, punchDirection = "In", evidenceId }),
        };
        punch.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _fx.DemoToken);
        var punched = await _fx.Client.SendAsync(punch);
        Assert.True(punched.StatusCode == HttpStatusCode.OK, await punched.Content.ReadAsStringAsync());
        Assert.Equal("Selfie", (await punched.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("verificationMethod").GetString());

        using var scope = _fx.Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        Assert.Equal(1, await db.AttendanceAuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.TenantId == _fx.DemoTenantId && a.Action == "attendance.selfie.demo_exception_active"));
    }

    [Fact]
    public async Task UnlistedTenant_IsStillRefusedByTheGuard_AndSeesNoNotice()
    {
        var upload = await _fx.UploadAsync(_fx.OtherToken, SelfieAttendanceTests.SelfieJpeg());
        Assert.Equal(HttpStatusCode.Forbidden, upload.StatusCode);
        Assert.Contains("feature_not_enabled", await upload.Content.ReadAsStringAsync());

        var view = await _fx.GetJsonAsync(_fx.OtherToken, "/api/ess/attendance-verification");
        Assert.False(view.GetProperty("selfie").GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("selfie").GetProperty("demoNotice").ValueKind);
    }
}

/// <summary>Boots Program.cs (SQLite, Development) with the exception listing one tenant's slug, and seeds that tenant and another.</summary>
public sealed class SelfieDemoExceptionHttpFixture : IAsyncLifetime
{
    private SqliteConnection _anchor = null!;
    private readonly string _demoSlug = $"evostel-demo-{Guid.NewGuid():N}"[..24];

    public AuthorizationPipelineHost Host { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    internal MemoryDocumentStorage Storage { get; } = new();
    public string DemoToken { get; private set; } = null!;
    public string OtherToken { get; private set; } = null!;
    public Guid DemoTenantId { get; private set; }

    public async Task InitializeAsync()
    {
        var connectionString = $"Data Source=file:selfie-demo-http-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _anchor = new SqliteConnection(connectionString);
        await _anchor.OpenAsync();
        Host = new AuthorizationPipelineHost(connectionString,
            services =>
            {
                foreach (var d in services.Where(d => d.ServiceType == typeof(IDocumentStorage)).ToList()) services.Remove(d);
                services.AddSingleton<IDocumentStorage>(Storage);
            },
            hostSettings: new Dictionary<string, string?>
            {
                // Exactly the render.yaml keys (env var __ is the configuration :).
                ["SelfieDemoException:TenantSlugs:0"] = _demoSlug,
                ["SelfieDemoException:ExpiresUtc"] = DateTime.UtcNow.AddDays(14).ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["SelfieDemoException:EvidenceRetentionDays"] = "7",
                ["SelfieDemoException:ApprovedBy"] = "owner 2026-10-08 (demo, test data only)",
            });
        Client = Host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        Guid demoUser, otherUser;
        using (var scope = Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            await db.Database.EnsureCreatedAsync();
            DemoTenantId = await SeedTenantAsync(db, _demoSlug);
            var other = await SeedTenantAsync(db, $"other-{Guid.NewGuid():N}"[..20]);
            demoUser = (await SelfieHttpPipelineFixture.SeedEmployeeUserAsync(db, DemoTenantId, "demo")).UserId;
            otherUser = (await SelfieHttpPipelineFixture.SeedEmployeeUserAsync(db, other, "other")).UserId;
        }
        using (var scope = Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            var tokens = scope.ServiceProvider.GetRequiredService<ITokenService>();
            DemoToken = await SelfieHttpPipelineFixture.MintAsync(db, tokens, demoUser);
            OtherToken = await SelfieHttpPipelineFixture.MintAsync(db, tokens, otherUser);
        }
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (Host is not null) await Host.DisposeAsync();
        if (_anchor is not null) await _anchor.DisposeAsync();
    }

    public async Task<JsonElement> GetJsonAsync(string token, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await Client.SendAsync(request);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: {response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<HttpResponseMessage> UploadAsync(string token, byte[] bytes)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        content.Add(file, "file", "selfie.jpg");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/attendance/evidence/selfie") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await Client.SendAsync(request);
    }

    private static async Task<Guid> SeedTenantAsync(ZayraDbContext db, string slug)
    {
        var tenant = new Tenant { Name = slug, Slug = slug, IsActive = true };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        await new AuthSeeder(db).EnsureTenantRolesAsync(tenant.Id);
        return tenant.Id;
    }
}
