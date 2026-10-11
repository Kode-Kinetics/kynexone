using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// ERP API keys through the REAL Program.cs pipeline (AuthorizationPipelineHost): the scheme, the three
/// locks, the allow-list, company scope, maker/checker, the uniform 401, the audit trail and tenant isolation.
/// Every assertion here failed before the feature existed (no scheme, no management endpoint, no table).
/// </summary>
public sealed class ErpApiKeyHttpTests : IClassFixture<ErpApiKeyHostFixture>
{
    private readonly ErpApiKeyHostFixture _f;
    public ErpApiKeyHttpTests(ErpApiKeyHostFixture fixture) => _f = fixture;

    // ── Management: Admin + MFA, plaintext once, hash only ────────────────────────────────────────

    [Fact]
    public async Task Management_RequiresAnAdminEnrolledInTwoStepSignIn()
    {
        using var reader = await _f.Send(HttpMethod.Get, "/api/finance/gl/api-keys", bearer: _f.TokenReaderA);
        reader.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Json(reader)).GetProperty("error").GetString().Should().Be("admin_required");

        using var noMfa = await _f.Send(HttpMethod.Get, "/api/finance/gl/api-keys", bearer: _f.TokenAdminANoMfa);
        noMfa.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Json(noMfa)).GetProperty("error").GetString().Should().Be("mfa_required");

        using var create = await _f.Send(HttpMethod.Post, "/api/finance/gl/api-keys", bearer: _f.TokenAdminANoMfa,
            body: ErpApiKeyHostFixture.CreateBody("not allowed", null, read: true, confirm: false));
        create.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _f.CountKeysAsync(_f.TenantA, "not allowed")).Should().Be(0);

        using var admin = await _f.Send(HttpMethod.Get, "/api/finance/gl/api-keys", bearer: _f.TokenAdminA);
        admin.StatusCode.Should().Be(HttpStatusCode.OK, await admin.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Create_ReturnsThePlaintextOnce_AndStoresOnlyItsHash()
    {
        var (id, plaintext) = await _f.CreateKeyAsync(_f.TokenAdminA, "Hash only", null, confirm: false);
        plaintext.Should().StartWith("kx_live_").And.HaveLength(51);

        using (var scope = _f.Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            var row = await db.ErpApiKeys.IgnoreQueryFilters().AsNoTracking().SingleAsync(k => k.Id == id);
            row.KeyHash.Should().Be(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext))));
            row.Prefix.Should().Be(plaintext[..16]);
            var everyColumn = string.Join("|", row.Name, row.Prefix, row.KeyHash, row.Scopes, row.IpAllowList, row.CreatedByName);
            everyColumn.Should().NotContain(plaintext[16..], "the secret part of the key is never stored");
            var audits = await db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                .Where(a => a.TenantId == _f.TenantA && a.EntityId == id.ToString()).ToListAsync();
            audits.Should().ContainSingle(a => a.Action == "finance.gl.erp_api_key.created");
            var secret = plaintext[16..];
            audits.Select(a => a.Metadata ?? "").Where(m => m.Contains(secret)).Should().BeEmpty();
        }

        using var list = await _f.Send(HttpMethod.Get, "/api/finance/gl/api-keys", bearer: _f.TokenAdminA);
        var listed = await list.Content.ReadAsStringAsync();
        listed.Should().NotContain(plaintext[16..], "the plaintext is shown once, at creation, and never again");
        listed.Should().Contain(plaintext[..16]);
    }

    [Theory]
    [InlineData("expiry")]
    [InlineData("scope")]
    [InlineData("cidr")]
    [InlineData("foreign-company")]
    public async Task Create_RefusesAnInvalidKey(string defect)
    {
        var body = ErpApiKeyHostFixture.CreateBody($"invalid {defect}", defect == "foreign-company" ? _f.CompanyB1 : null,
            read: defect != "scope", confirm: true, days: defect == "expiry" ? 400 : 90,
            ip: defect == "cidr" ? new[] { "203.0.113.5/24" } : null);
        using var response = await _f.Send(HttpMethod.Post, "/api/finance/gl/api-keys", bearer: _f.TokenAdminA, body: body);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        (await Json(response)).GetProperty("error").GetString().Should().Be("invalid_request");
        (await _f.CountKeysAsync(_f.TenantA, $"invalid {defect}")).Should().Be(0);
    }

    // ── The allow-list ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AGroupKey_ReadsTheAllowListedEndpoints_OfItsOwnTenantOnly()
    {
        var (_, key) = await _f.CreateKeyAsync(_f.TokenAdminA, "Group reader", null, confirm: false);

        using var list = await _f.Send(HttpMethod.Get, "/api/finance/gl/journal-exports", apiKey: key);
        list.StatusCode.Should().Be(HttpStatusCode.OK, await list.Content.ReadAsStringAsync());
        var ids = (await Json(list)).EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();
        ids.Should().Contain(new[] { _f.ExportA1, _f.ExportA2, _f.ExportGroup }).And.NotContain(_f.ExportB);

        using var viaHeader = await _f.Send(HttpMethod.Get, "/api/finance/gl/journal-exports/formats", apiKey: key, keyInXApiKey: true);
        viaHeader.StatusCode.Should().Be(HttpStatusCode.OK, await viaHeader.Content.ReadAsStringAsync());

        using var one = await _f.Send(HttpMethod.Get, $"/api/finance/gl/journal-exports/{_f.ExportA2}", apiKey: key);
        one.StatusCode.Should().Be(HttpStatusCode.OK, await one.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("GET", "/api/employees")]
    [InlineData("GET", "/api/finance/gl/accounts")]
    [InlineData("GET", "/api/finance/gl/api-keys")]
    [InlineData("GET", "/api/finance/gl/journal-exports/preview?period=2026-09")]
    [InlineData("POST", "/api/finance/gl/journal-exports")]
    [InlineData("GET", "/api/finance/gl/reconciliation?period=2026-09")]
    [InlineData("GET", "/api/auth/me")]
    [InlineData("GET", "/health/live")]
    public async Task AValidKey_IsRefusedOnEveryEndpointOutsideTheAllowList(string method, string path)
    {
        var key = await _f.SharedConfirmKeyAsync();
        foreach (var inHeader in new[] { false, true })
        {
            using var response = await _f.Send(new HttpMethod(method), path, apiKey: key, keyInXApiKey: inHeader,
                body: method == "POST" ? new { period = "2026-09" } : null);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"{method} {path} is not allow-listed (X-Api-Key: {inHeader})");
        }
    }

    [Fact]
    public async Task ACompanyKey_CannotReadAnotherCompanysExport()
    {
        var (_, key) = await _f.CreateKeyAsync(_f.TokenAdminA, "A1 only", _f.CompanyA1, confirm: false);

        // Exactly like a company-scoped person: the tenant-wide list is refused, the company's own list is served.
        using var tenantWide = await _f.Send(HttpMethod.Get, "/api/finance/gl/journal-exports", apiKey: key);
        tenantWide.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var list = await _f.Send(HttpMethod.Get, $"/api/finance/gl/journal-exports?companyId={_f.CompanyA1}", apiKey: key);
        list.StatusCode.Should().Be(HttpStatusCode.OK, await list.Content.ReadAsStringAsync());
        var ids = (await Json(list)).EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();
        ids.Should().Contain(new[] { _f.ExportA1, _f.ExportA1ByOther }).And.NotContain(new[] { _f.ExportA2, _f.ExportGroup, _f.ExportB });
        using var own = await _f.Send(HttpMethod.Get, $"/api/finance/gl/journal-exports/{_f.ExportA1ByOther}", apiKey: key);
        own.StatusCode.Should().Be(HttpStatusCode.OK, await own.Content.ReadAsStringAsync());

        foreach (var other in new[] { _f.ExportA2, _f.ExportGroup })
        {
            using var read = await _f.Send(HttpMethod.Get, $"/api/finance/gl/journal-exports/{other}", apiKey: key);
            read.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            using var download = await _f.Send(HttpMethod.Get, $"/api/finance/gl/journal-exports/{other}/download", apiKey: key);
            download.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        using var filtered = await _f.Send(HttpMethod.Get, $"/api/finance/gl/journal-exports?companyId={_f.CompanyA2}", apiKey: key);
        filtered.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ConfirmingOrRejecting_NeedsTheConfirmScope()
    {
        var (_, readOnly) = await _f.CreateKeyAsync(_f.TokenAdminA, "Read only", null, confirm: false);
        var confirmKey = await _f.SharedConfirmKeyAsync();
        var paths = new[] { "confirm", "reject", "journals/confirm", "journals/reject" };
        foreach (var step in paths)
        {
            using var refused = await _f.Send(HttpMethod.Post, $"/api/finance/gl/journal-exports/{_f.ExportA1ByOther}/{step}", apiKey: readOnly,
                body: new { erpDocumentNumber = "ERP-1", reason = "x", journals = Array.Empty<object>() });
            refused.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{step} needs finance.gl.exports.confirm");

            // The confirm-scoped key passes the gate and reaches the action: an unknown export is a 404.
            using var passes = await _f.Send(HttpMethod.Post, $"/api/finance/gl/journal-exports/{Guid.NewGuid()}/{step}", apiKey: confirmKey,
                body: new { erpDocumentNumber = "ERP-1", reason = "x", journals = Array.Empty<object>() });
            passes.StatusCode.Should().Be(HttpStatusCode.NotFound, $"{step}: {await passes.Content.ReadAsStringAsync()}");
        }
    }

    [Fact]
    public async Task AKey_CannotAttestAnExportMadeByThePersonWhoCreatedIt()
    {
        var key = await _f.SharedConfirmKeyAsync(); // created by AdminA, who exported ExportA1
        using var response = await _f.Send(HttpMethod.Post, $"/api/finance/gl/journal-exports/{_f.ExportA1}/confirm", apiKey: key,
            body: new { erpDocumentNumber = "ERP-77" });
        response.StatusCode.Should().Be(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
        (await Json(response)).GetProperty("error").GetString().Should().Be("segregation_of_duties");
    }

    // ── Uniform 401 ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExpiredRevokedWrongAddressAndUnknownKeys_AllGetTheSameBare401()
    {
        var (expiredId, expired) = await _f.CreateKeyAsync(_f.TokenAdminA, "Soon expired", null, confirm: false);
        await _f.ExpireAsync(expiredId);
        var (revokedId, revoked) = await _f.CreateKeyAsync(_f.TokenAdminA, "Soon revoked", null, confirm: false);
        using (var revoke = await _f.Send(HttpMethod.Delete, $"/api/finance/gl/api-keys/{revokedId}", bearer: _f.TokenAdminA))
        {
            revoke.StatusCode.Should().Be(HttpStatusCode.OK);
            (await Json(revoke)).GetProperty("key").GetProperty("status").GetString().Should().Be("Revoked");
        }
        var (_, ipBound) = await _f.CreateKeyAsync(_f.TokenAdminA, "Office only", null, confirm: false, ip: new[] { "203.0.113.0/24" });
        var unknown = "kx_live_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var valid = await _f.SharedConfirmKeyAsync();

        var bodies = new List<string>();
        foreach (var (label, key) in new[] { ("expired", expired), ("revoked", revoked), ("ip", ipBound), ("unknown", unknown), ("malformed", "kx_live_short") })
        {
            using var response = await _f.Send(HttpMethod.Get, "/api/finance/gl/journal-exports", apiKey: key);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, label);
            response.Headers.WwwAuthenticate.Select(h => h.Scheme).Should().Contain("ApiKey", label);
            bodies.Add(await response.Content.ReadAsStringAsync());
        }
        bodies.Distinct().Should().ContainSingle("the caller must not learn why a key was refused");

        // A key together with a person's session is never merged into one principal.
        using var both = await _f.Send(HttpMethod.Get, "/api/finance/gl/journal-exports", bearer: _f.TokenAdminA, apiKey: valid, keyInXApiKey: true);
        both.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Revoking twice is a no-op, not an error.
        using var again = await _f.Send(HttpMethod.Delete, $"/api/finance/gl/api-keys/{revokedId}", bearer: _f.TokenAdminA);
        again.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Audit, last used ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AKey_StopsWhenTheAdministratorWhoCreatedItIsDeactivated()
    {
        var (id, key) = await _f.CreateKeyAsync(_f.TokenAdminA, "Creator leaves", null, confirm: false);
        using (var before = await _f.Send(HttpMethod.Get, "/api/finance/gl/journal-exports/formats", apiKey: key))
            before.StatusCode.Should().Be(HttpStatusCode.OK);

        await _f.SetCreatorActiveAsync(id, active: false);
        try
        {
            using var after = await _f.Send(HttpMethod.Get, "/api/finance/gl/journal-exports/formats", apiKey: key);
            after.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "an integration must not outlive its owner's access");
        }
        finally
        {
            await _f.SetCreatorActiveAsync(id, active: true);
        }

        using var restored = await _f.Send(HttpMethod.Get, "/api/finance/gl/journal-exports/formats", apiKey: key);
        restored.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task EveryCall_IsAuditedWithKeyEndpointStatusAndAddress_AndLastUsedIsRecorded()
    {
        var (id, key) = await _f.CreateKeyAsync(_f.TokenAdminA, "Audited", null, confirm: false);
        using (var ok = await _f.Send(HttpMethod.Get, "/api/finance/gl/journal-exports", apiKey: key))
            ok.StatusCode.Should().Be(HttpStatusCode.OK);
        await _f.ExpireAsync(id);
        using (var refused = await _f.Send(HttpMethod.Get, "/api/finance/gl/journal-exports/formats", apiKey: key))
            refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var scope = _f.Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var rows = await db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.TenantId == _f.TenantA && a.EntityName == nameof(ErpApiKey) && a.EntityId == id.ToString())
            .ToListAsync();
        var call = rows.Should().ContainSingle(a => a.Action == "finance.gl.erp_api_key.call").Subject;
        call.IpAddress.Should().NotBeNullOrEmpty();
        using (var meta = JsonDocument.Parse(call.Metadata!))
        {
            meta.RootElement.GetProperty("status").GetInt32().Should().Be(200);
            meta.RootElement.GetProperty("path").GetString().Should().Be("/api/finance/gl/journal-exports");
            meta.RootElement.GetProperty("keyId").GetGuid().Should().Be(id);
        }
        var refusedRow = rows.Should().ContainSingle(a => a.Action == "finance.gl.erp_api_key.refused").Subject;
        using (var meta = JsonDocument.Parse(refusedRow.Metadata!))
        {
            meta.RootElement.GetProperty("status").GetInt32().Should().Be(401);
            meta.RootElement.GetProperty("reason").GetString().Should().Be("expired");
        }

        var stored = await db.ErpApiKeys.IgnoreQueryFilters().AsNoTracking().SingleAsync(k => k.Id == id);
        stored.LastUsedAtUtc.Should().NotBeNull();
        stored.LastUsedIp.Should().NotBeNullOrEmpty();
    }

    // ── Tenant isolation ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TenantBsKey_SeesOnlyTenantB_AndTenantAsAdminCannotSeeOrRevokeIt()
    {
        var (bKeyId, bKey) = await _f.CreateKeyAsync(_f.TokenAdminB, "Tenant B ERP", null, confirm: true);

        using var list = await _f.Send(HttpMethod.Get, "/api/finance/gl/journal-exports", apiKey: bKey);
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(list)).EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).Should().Equal(_f.ExportB);

        using var crossRead = await _f.Send(HttpMethod.Get, $"/api/finance/gl/journal-exports/{_f.ExportA1}", apiKey: bKey);
        crossRead.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var crossConfirm = await _f.Send(HttpMethod.Post, $"/api/finance/gl/journal-exports/{_f.ExportA1ByOther}/confirm", apiKey: bKey,
            body: new { erpDocumentNumber = "ERP-B" });
        crossConfirm.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var aList = await _f.Send(HttpMethod.Get, "/api/finance/gl/api-keys", bearer: _f.TokenAdminA);
        (await aList.Content.ReadAsStringAsync()).Should().NotContain(bKeyId.ToString());
        using var aRevoke = await _f.Send(HttpMethod.Delete, $"/api/finance/gl/api-keys/{bKeyId}", bearer: _f.TokenAdminA);
        aRevoke.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── OpenAPI ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheOpenApiDocument_DescribesOnlyTheAllowListedEndpoints()
    {
        using var response = await _f.Send(HttpMethod.Get, "/api/finance/gl/api-keys/openapi.json", bearer: _f.TokenAdminA);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var operations = doc.RootElement.GetProperty("paths").EnumerateObject()
            .SelectMany(p => p.Value.EnumerateObject().Select(m => $"{m.Name.ToUpperInvariant()} {p.Name}"))
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        operations.Should().BeEquivalentTo(ErpApiKeyAllowListRatchetTests.AllowListedOperations);
        doc.RootElement.GetProperty("components").GetProperty("securitySchemes").EnumerateObject()
            .Select(s => s.Name).Should().Equal(ErpApiKeyDefaults.Scheme);

        var key = await _f.SharedConfirmKeyAsync();
        using var withKey = await _f.Send(HttpMethod.Get, "/api/finance/gl/api-keys/openapi.json", apiKey: key);
        withKey.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}

/// <summary>Rate limit per key, in its own host so the tiny budget cannot disturb the other tests.</summary>
public sealed class ErpApiKeyRateLimitTests
{
    [Fact]
    public async Task EachKey_HasItsOwnBudget()
    {
        await using var f = ErpApiKeyHostFixture.WithHostSettings(new Dictionary<string, string?>
        {
            ["RateLimit:ErpApiKeyPermitLimit"] = "2",
            ["RateLimit:ErpApiKeyWindowSeconds"] = "600",
        });
        await f.InitializeAsync();
        var (_, first) = await f.CreateKeyAsync(f.TokenAdminA, "Budget one", null, confirm: false);
        var (_, second) = await f.CreateKeyAsync(f.TokenAdminA, "Budget two", null, confirm: false);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            using var r = await f.Send(HttpMethod.Get, "/api/finance/gl/journal-exports/formats", apiKey: first);
            statuses.Add(r.StatusCode);
        }
        statuses.Should().Equal(HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests);

        using var other = await f.Send(HttpMethod.Get, "/api/finance/gl/journal-exports/formats", apiKey: second);
        other.StatusCode.Should().Be(HttpStatusCode.OK, "one key's budget never throttles another key");
        using var person = await f.Send(HttpMethod.Get, "/api/finance/gl/journal-exports/formats", bearer: f.TokenReaderA);
        person.StatusCode.Should().Be(HttpStatusCode.OK, "people using the same endpoint are not in the key budget");
    }
}

public sealed class ErpApiKeyHostFixture : IAsyncLifetime, IAsyncDisposable
{
    private readonly IReadOnlyDictionary<string, string?>? _hostSettings;
    private SqliteConnection _anchor = null!;
    private string? _sharedConfirmKey;

    public ErpApiKeyHostFixture() { }
    private ErpApiKeyHostFixture(IReadOnlyDictionary<string, string?> hostSettings) => _hostSettings = hostSettings;
    public static ErpApiKeyHostFixture WithHostSettings(IReadOnlyDictionary<string, string?> hostSettings) => new(hostSettings);

    public AuthorizationPipelineHost Host { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;

    public Guid TenantA { get; } = Guid.NewGuid();
    public Guid TenantB { get; } = Guid.NewGuid();
    public Guid CompanyA1 { get; } = Guid.NewGuid();
    public Guid CompanyA2 { get; } = Guid.NewGuid();
    public Guid CompanyB1 { get; } = Guid.NewGuid();
    public Guid ExportA1 { get; } = Guid.NewGuid();
    public Guid ExportA1ByOther { get; } = Guid.NewGuid();
    public Guid ExportA2 { get; } = Guid.NewGuid();
    public Guid ExportGroup { get; } = Guid.NewGuid();
    public Guid ExportB { get; } = Guid.NewGuid();

    public string TokenAdminA { get; private set; } = null!;
    public string TokenAdminANoMfa { get; private set; } = null!;
    public string TokenReaderA { get; private set; } = null!;
    public string TokenAdminB { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connection = $"Data Source=file:erp-api-keys-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _anchor = new SqliteConnection(connection);
        await _anchor.OpenAsync();
        Host = new AuthorizationPipelineHost(connection, hostSettings: _hostSettings);
        Client = Host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        Guid adminA, otherA;
        using (var scope = Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.Tenants.Add(new Tenant { Id = TenantA, Name = "ERP A", Slug = $"erp-a-{TenantA:N}", IsActive = true, AccountType = TenantAccountTypes.Group });
            db.Tenants.Add(new Tenant { Id = TenantB, Name = "ERP B", Slug = $"erp-b-{TenantB:N}", IsActive = true });
            db.Companies.AddRange(
                new Company { Id = CompanyA1, TenantId = TenantA, LegalNameEn = "A One", CountryCode = "SA", IsActive = true },
                new Company { Id = CompanyA2, TenantId = TenantA, LegalNameEn = "A Two", CountryCode = "SA", IsActive = true },
                new Company { Id = CompanyB1, TenantId = TenantB, LegalNameEn = "B One", CountryCode = "SA", IsActive = true });
            var roleA = new Role { TenantId = TenantA, Name = "Admin", NormalizedName = "ADMIN" };
            var roleB = new Role { TenantId = TenantB, Name = "Admin", NormalizedName = "ADMIN" };
            db.Roles.AddRange(roleA, roleB);
            var a = NewUser(TenantA, "admin-a", mfa: true);
            var aNoMfa = NewUser(TenantA, "admin-a-nomfa", mfa: false);
            var reader = NewUser(TenantA, "reader-a", mfa: false);
            var other = NewUser(TenantA, "exporter-a", mfa: false);
            var b = NewUser(TenantB, "admin-b", mfa: true);
            db.Users.AddRange(a, aNoMfa, reader, other, b);
            db.Set<UserRole>().AddRange(
                new UserRole { UserId = a.Id, RoleId = roleA.Id },
                new UserRole { UserId = aNoMfa.Id, RoleId = roleA.Id },
                new UserRole { UserId = b.Id, RoleId = roleB.Id });
            db.Set<UserPermissionOverride>().Add(new UserPermissionOverride
            {
                TenantId = TenantA, UserId = reader.Id, PermissionKey = "finance.gl.read", Effect = "Allow", IsActive = true,
            });
            adminA = a.Id;
            otherA = other.Id;
            db.GlJournalExports.AddRange(
                Export(ExportA1, TenantA, CompanyA1, adminA),
                Export(ExportA1ByOther, TenantA, CompanyA1, otherA),
                Export(ExportA2, TenantA, CompanyA2, otherA),
                Export(ExportGroup, TenantA, null, otherA),
                Export(ExportB, TenantB, CompanyB1, null));
            await db.SaveChangesAsync();
        }

        TokenAdminA = await MintAsync("admin-a", TenantA);
        TokenAdminANoMfa = await MintAsync("admin-a-nomfa", TenantA);
        TokenReaderA = await MintAsync("reader-a", TenantA);
        TokenAdminB = await MintAsync("admin-b", TenantB);
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (Host is not null) await Host.DisposeAsync();
        if (_anchor is not null) await _anchor.DisposeAsync();
    }

    async ValueTask IAsyncDisposable.DisposeAsync() => await DisposeAsync();

    public static object CreateBody(string name, Guid? companyId, bool read, bool confirm, int days = 90, string[]? ip = null) => new
    {
        name,
        companyId,
        scopes = new[] { read ? ErpApiKeyScopes.ExportsRead : null, confirm ? ErpApiKeyScopes.ExportsConfirm : null }
            .Where(s => s is not null).ToArray(),
        expiresAtUtc = DateTime.UtcNow.AddDays(days),
        ipAllowList = ip ?? Array.Empty<string>(),
    };

    public async Task<(Guid Id, string Plaintext)> CreateKeyAsync(string adminToken, string name, Guid? companyId, bool confirm, string[]? ip = null)
    {
        using var response = await Send(HttpMethod.Post, "/api/finance/gl/api-keys", bearer: adminToken,
            body: CreateBody(name, companyId, read: true, confirm: confirm, ip: ip));
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, text);
        using var doc = JsonDocument.Parse(text);
        return (doc.RootElement.GetProperty("key").GetProperty("id").GetGuid(), doc.RootElement.GetProperty("plaintext").GetString()!);
    }

    /// <summary>A group key with both scopes, created by AdminA (who exported ExportA1).</summary>
    public async Task<string> SharedConfirmKeyAsync() =>
        _sharedConfirmKey ??= (await CreateKeyAsync(TokenAdminA, "Shared ERP", null, confirm: true)).Plaintext;

    public async Task ExpireAsync(Guid id)
    {
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var key = await db.ErpApiKeys.IgnoreQueryFilters().SingleAsync(k => k.Id == id);
        key.CreatedAtUtc = DateTime.UtcNow.AddDays(-2);
        key.ExpiresAtUtc = DateTime.UtcNow.AddDays(-1);
        await db.SaveChangesAsync();
    }

    /// <summary>Activates or deactivates the administrator who created the key.</summary>
    public async Task SetCreatorActiveAsync(Guid keyId, bool active)
    {
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var key = await db.ErpApiKeys.IgnoreQueryFilters().SingleAsync(k => k.Id == keyId);
        var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == key.CreatedByUserId);
        user.IsActive = active;
        await db.SaveChangesAsync();
    }

    public async Task<int> CountKeysAsync(Guid tenantId, string name)
    {
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        return await db.ErpApiKeys.IgnoreQueryFilters().CountAsync(k => k.TenantId == tenantId && k.Name == name);
    }

    public async Task<HttpResponseMessage> Send(HttpMethod method, string path, string? bearer = null, string? apiKey = null,
        bool keyInXApiKey = false, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (apiKey is not null)
        {
            if (keyInXApiKey) request.Headers.Add(ErpApiKeyDefaults.HeaderName, apiKey);
            else request.Headers.TryAddWithoutValidation("Authorization", $"ApiKey {apiKey}");
        }
        if (body is not null) request.Content = JsonContent.Create(body);
        return await Client.SendAsync(request);
    }

    private static User NewUser(Guid tenantId, string name, bool mfa) => new()
    {
        TenantId = tenantId,
        Email = $"{name}@erp-keys.local",
        NormalizedEmail = $"{name}@erp-keys.local".ToUpperInvariant(),
        FullName = name,
        PasswordHash = "no-login-path",
        Status = "Active",
        AccessMode = "FullPortal",
        IsActive = true,
        IsEmailConfirmed = true,
        IsGroupScope = true,
        MFAEnabled = mfa,
        MfaSecretEncrypted = mfa ? "enrolled-for-test" : null,
    };

    private static GlJournalExport Export(Guid id, Guid tenantId, Guid? companyId, Guid? exportedBy) => new()
    {
        Id = id,
        TenantId = tenantId,
        CompanyId = companyId,
        CompanyCode = companyId is null ? "" : "C1",
        Period = "2026-09",
        FormatKey = "generic-csv",
        Currency = "SAR",
        FileName = $"journal-{id:N}.csv",
        FileHash = new string('a', 64),
        ExportedByUserId = exportedBy,
        ExportedByName = "Exporter",
    };

    private async Task<string> MintAsync(string name, Guid tenantId)
    {
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var user = await db.Users.IgnoreQueryFilters().AsNoTracking().Include(u => u.Tenant)
            .Include(u => u.UserRoles).ThenInclude(r => r.Role).ThenInclude(r => r!.RolePermissions).ThenInclude(p => p.Permission)
            .Include(u => u.PermissionOverrides).Include(u => u.EmployeeUserAccounts).Include(u => u.EntityAccesses)
            .SingleAsync(u => u.TenantId == tenantId && u.Email == $"{name}@erp-keys.local");
        return scope.ServiceProvider.GetRequiredService<ITokenService>().CreateAccessToken(user, AuthService.GetRoles(user),
            AuthService.GetPermissions(user), user.Tenant!, Array.Empty<EntityAccessGrant>(),
            EntityScopeClaims.Resolve(true, Array.Empty<EntityAccessGrant>(), Array.Empty<Guid>()), out _);
    }
}
