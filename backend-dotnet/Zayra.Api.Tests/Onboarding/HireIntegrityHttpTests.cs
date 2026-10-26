using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
using Zayra.Api.Tests.Security;

namespace Zayra.Api.Tests.Onboarding;

/// <summary>
/// The create-time integrity rules through the real pipeline (JSON binding, authorization, filters, services).
/// SQLite host: advisory locks are not exercised here (see HireIdentityLockPostgresTests); everything else is.
/// NOT RUN in the author's sandbox (a WebApplicationFactory host cannot start there); the lead runs them on the train.
/// </summary>
public class HireIntegrityHttpTests : IClassFixture<HireIntegrityHttpFixture>
{
    private const string Iban = "SA0380000000608010167519";
    private readonly HireIntegrityHttpFixture _fx;
    public HireIntegrityHttpTests(HireIntegrityHttpFixture fixture) => _fx = fixture;

    private static object Hire(string name, Guid? companyId = null, bool ack = false, string? reason = null, string? iban = null,
        string? iqama = null) => new Dictionary<string, object?>
    {
        ["englishName"] = name,
        ["companyId"] = companyId,
        ["acknowledgeDuplicate"] = ack,
        ["duplicateOverrideReason"] = reason,
        ["payrollProfile"] = iban is null ? null : new { iban, bankName = "Test Bank", paymentMethod = "BankTransfer" },
        ["complianceRecords"] = iqama is null ? null : new[] { new { countryCode = "SA", fieldKey = "iqama_number", fieldLabel = "Iqama", fieldValue = iqama, isSensitive = true, isRequired = false } },
    };

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    // ── Rate limits (C7, G29) ─────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task RateLimit_DuplicateCheck_21stIs429_WithRetryAfter()
    {
        var token = await _fx.NewUserAsync("rl1", "employees.write", "employees.read");
        for (var i = 0; i < 20; i++)
        {
            using var ok = await _fx.SendAsync(HttpMethod.Post, "/api/employees/duplicate-check", new { englishName = "Probe" }, token);
            ok.StatusCode.Should().Be(HttpStatusCode.OK, $"call {i + 1}");
        }
        using var limited = await _fx.SendAsync(HttpMethod.Post, "/api/employees/duplicate-check", new { englishName = "Probe" }, token);
        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter.Should().NotBeNull();
        (await Json(limited)).GetProperty("error").GetString().Should().Be("rate_limited");
    }

    [Fact]
    public async Task RateLimit_TwoUsersSameIp_IndependentBuckets_AndUnauthenticatedIs401NotConsumingABucket()
    {
        var a = await _fx.NewUserAsync("rl2a", "employees.write", "employees.read");
        var b = await _fx.NewUserAsync("rl2b", "employees.write", "employees.read");
        for (var i = 0; i < 20; i++)
        {
            using var anon = await _fx.SendAsync(HttpMethod.Post, "/api/employees/duplicate-check", new { englishName = "Probe" }, null);
            anon.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        for (var i = 0; i < 21; i++)
        {
            using var r = await _fx.SendAsync(HttpMethod.Post, "/api/employees/duplicate-check", new { englishName = "Probe" }, a);
            if (i < 20) r.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        using var other = await _fx.SendAsync(HttpMethod.Post, "/api/employees/duplicate-check", new { englishName = "Probe" }, b);
        other.StatusCode.Should().Be(HttpStatusCode.OK, "a second user behind the same address has their own bucket");
    }

    [Fact]
    public async Task RateLimit_DeriveWorkEmail_And_Transliterate_AreLimitedPerUser()
    {
        var token = await _fx.NewUserAsync("rl3", "employees.write", "employees.read");
        HttpStatusCode last = default;
        for (var i = 0; i < 31; i++)
        {
            using var r = await _fx.SendAsync(HttpMethod.Post, "/api/employees/derive-work-email", new { englishName = "Sara Ali", companyId = _fx.CompanyId }, token);
            last = r.StatusCode;
        }
        last.Should().Be(HttpStatusCode.TooManyRequests, "30 per user per minute");
        for (var i = 0; i < 61; i++)
        {
            using var r = await _fx.SendAsync(HttpMethod.Post, "/api/localization/transliterate", new { text = "Sara", kind = "person-name" }, token);
            last = r.StatusCode;
        }
        last.Should().Be(HttpStatusCode.TooManyRequests, "60 per user per minute");
    }

    // ── Duplicate check bounds and masking (G15, G16) ────────────────────────────────────────────
    [Fact]
    public async Task DuplicateCheck_21IdentityValues_400_AndValueOver64Chars_400()
    {
        var token = await _fx.NewUserAsync("dc1", "employees.write", "employees.read");
        var many = Enumerable.Range(0, 21).Select(i => new { fieldKey = "iqama_number", value = $"21234567{i:00}" }).ToArray();
        using var tooMany = await _fx.SendAsync(HttpMethod.Post, "/api/employees/duplicate-check", new { identityValues = many }, token);
        tooMany.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var tooLong = await _fx.SendAsync(HttpMethod.Post, "/api/employees/duplicate-check",
            new { identityValues = new[] { new { fieldKey = "iqama_number", value = new string('9', 65) } } }, token);
        tooLong.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DuplicateCheck_MatchInInvisibleCompany_ReturnsMaskedShape_NoCodeNameBranch()
    {
        var hidden = await _fx.SeedEmployeeInOtherCompanyAsync("Hidden Person Name", "2999888777");
        var token = await _fx.NewScopedUserAsync("dc2", _fx.CompanyId, "employees.write", "employees.read");
        using var r = await _fx.SendAsync(HttpMethod.Post, "/api/employees/duplicate-check",
            new { identityValues = new[] { new { fieldKey = "iqama_number", value = "2999888777" } } }, token);
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        var text = await r.Content.ReadAsStringAsync();
        text.Should().NotContain("Hidden Person Name").And.NotContain(hidden.EmployeeCode);
        var match = JsonDocument.Parse(text).RootElement.GetProperty("matches")[0];
        match.GetProperty("canView").GetBoolean().Should().BeFalse();
    }

    // ── Request size and idempotency (G20, G20b, C2) ─────────────────────────────────────────────
    [Fact]
    public async Task Create_Body70KB_413()
    {
        var token = await _fx.NewUserAsync("sz1", "employees.write", "employees.read");
        using var r = await _fx.SendAsync(HttpMethod.Post, "/api/employees", Hire(new string('x', 70_000)), token);
        r.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task Create_Body64KB_NotRejectedForSize()
    {
        var token = await _fx.NewUserAsync("sz2", "employees.write", "employees.read");
        using var r = await _fx.SendAsync(HttpMethod.Post, "/api/employees", Hire(new string('x', 60_000)), token);
        r.StatusCode.Should().NotBe(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task Create_NonUuidIdempotencyKey_400()
    {
        var token = await _fx.NewUserAsync("ik0", "employees.write", "employees.read");
        using var r = await _fx.SendAsync(HttpMethod.Post, "/api/employees", Hire("Key Test"), token, idempotencyKey: "not-a-uuid");
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_SameIdempotencyKey_ReplaysSameEmployee_NoSecondRow()
    {
        var token = await _fx.NewUserAsync("ik1", "employees.write", "employees.read");
        var key = Guid.NewGuid().ToString();
        var name = $"Replay {Guid.NewGuid():N}";
        using var first = await _fx.SendAsync(HttpMethod.Post, "/api/employees", Hire(name), token, idempotencyKey: key);
        first.StatusCode.Should().Be(HttpStatusCode.Created, await first.Content.ReadAsStringAsync());
        using var second = await _fx.SendAsync(HttpMethod.Post, "/api/employees", Hire(name), token, idempotencyKey: key);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(second)).GetProperty("id").GetInt32().Should().Be((await Json(first)).GetProperty("id").GetInt32());
        (await _fx.CountByNameAsync(name)).Should().Be(1);
    }

    [Fact]
    public async Task Create_SameKey_DifferentBody_422()
    {
        var token = await _fx.NewUserAsync("ik2", "employees.write", "employees.read");
        var key = Guid.NewGuid().ToString();
        using var first = await _fx.SendAsync(HttpMethod.Post, "/api/employees", Hire($"Body A {Guid.NewGuid():N}"), token, idempotencyKey: key);
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        using var second = await _fx.SendAsync(HttpMethod.Post, "/api/employees", Hire($"Body B {Guid.NewGuid():N}"), token, idempotencyKey: key);
        second.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await Json(second)).GetProperty("code").GetString().Should().Be("idempotency_key_reuse_mismatch");
    }

    [Fact]
    public async Task Create_SameKey_DifferentUser_422_NeverReturnsTheFirstUsersEmployee()
    {
        var a = await _fx.NewUserAsync("ik3a", "employees.write", "employees.read");
        var b = await _fx.NewUserAsync("ik3b", "employees.write", "employees.read");
        var key = Guid.NewGuid().ToString();
        var name = $"Shared {Guid.NewGuid():N}";
        using var first = await _fx.SendAsync(HttpMethod.Post, "/api/employees", Hire(name), a, idempotencyKey: key);
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        using var second = await _fx.SendAsync(HttpMethod.Post, "/api/employees", Hire(name), b, idempotencyKey: key);
        second.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await second.Content.ReadAsStringAsync()).Should().NotContain(name);
    }

    [Fact]
    public async Task Create_KeyOnOtherTenant_IsIndependent()
    {
        var a = await _fx.NewUserAsync("ik4a", "employees.write", "employees.read");
        var other = await _fx.NewOtherTenantUserAsync("ik4b", "employees.write", "employees.read");
        var key = Guid.NewGuid().ToString();
        using var first = await _fx.SendAsync(HttpMethod.Post, "/api/employees", Hire($"Tenant A {Guid.NewGuid():N}"), a, idempotencyKey: key);
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        using var second = await _fx.SendAsync(HttpMethod.Post, "/api/employees", Hire($"Tenant B {Guid.NewGuid():N}"), other, idempotencyKey: key);
        second.StatusCode.Should().Be(HttpStatusCode.Created, await second.Content.ReadAsStringAsync());
    }

    // ── Masking (G22) ────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Create_UserWithoutSensitive_ResponseMasksBank_AndReplayUsesTheCallersMask()
    {
        var plain = await _fx.NewUserAsync("mk1", "employees.write", "employees.read");
        var sensitive = await _fx.NewUserAsync("mk2", "employees.write", "employees.read", "employees.sensitive");
        var key = Guid.NewGuid().ToString();
        var name = $"Masked {Guid.NewGuid():N}";
        using var created = await _fx.SendAsync(HttpMethod.Post, "/api/employees", Hire(name, iban: Iban), plain, idempotencyKey: key);
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        (await created.Content.ReadAsStringAsync()).Should().NotContain(Iban);
        using var replay = await _fx.SendAsync(HttpMethod.Post, "/api/employees", Hire(name, iban: Iban), plain, idempotencyKey: key);
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        (await replay.Content.ReadAsStringAsync()).Should().NotContain(Iban);

        var key2 = Guid.NewGuid().ToString();
        var name2 = $"Visible {Guid.NewGuid():N}";
        using var withSensitive = await _fx.SendAsync(HttpMethod.Post, "/api/employees", Hire(name2, iban: Iban), sensitive, idempotencyKey: key2);
        withSensitive.StatusCode.Should().Be(HttpStatusCode.Created);
        using var visibleReplay = await _fx.SendAsync(HttpMethod.Post, "/api/employees", Hire(name2, iban: Iban), sensitive, idempotencyKey: key2);
        (await visibleReplay.Content.ReadAsStringAsync()).Should().Contain(Iban);
    }

    // ── Override policy (G21b) ───────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Override_RequiresPermissionAndReason()
    {
        await _fx.SeedEmployeeAsync("Existing Person", "2111000111");
        var officer = await _fx.NewUserAsync("ov1", "employees.write", "employees.read");
        var manager = await _fx.NewUserAsync("ov2", "employees.write", "employees.read", "employees.duplicate_override");

        using var denied = await _fx.SendAsync(HttpMethod.Post, "/api/employees",
            Hire($"Dup {Guid.NewGuid():N}", ack: true, reason: "Same person, second legal entity", iqama: "2111000111"), officer);
        denied.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await Json(denied)).GetProperty("code").GetString().Should().Be("override_not_permitted");

        using var shortReason = await _fx.SendAsync(HttpMethod.Post, "/api/employees",
            Hire($"Dup {Guid.NewGuid():N}", ack: true, reason: "too short", iqama: "2111000111"), manager);
        shortReason.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await Json(shortReason)).GetProperty("code").GetString().Should().Be("override_reason_required");

        using var ok = await _fx.SendAsync(HttpMethod.Post, "/api/employees",
            Hire($"Dup {Guid.NewGuid():N}", ack: true, reason: "Same person, second legal entity", iqama: "2111000111"), manager);
        ok.StatusCode.Should().Be(HttpStatusCode.Created, await ok.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task DuplicateAudit_OmitsOutOfScopeIds_AndHoldsNoIdentityValue()
    {
        var hidden = await _fx.SeedEmployeeInOtherCompanyAsync("Far Away Person", "2555444333");
        var manager = await _fx.NewScopedUserAsync("ov3", _fx.CompanyId, "employees.write", "employees.read", "employees.duplicate_override");
        using var ok = await _fx.SendAsync(HttpMethod.Post, "/api/employees",
            Hire($"Dup {Guid.NewGuid():N}", companyId: _fx.CompanyId, ack: true, reason: "Same person, other entity", iqama: "2555444333"), manager);
        ok.StatusCode.Should().Be(HttpStatusCode.Created, await ok.Content.ReadAsStringAsync());
        var audit = await _fx.AuditPayloadsAsync("employee.duplicate_override_created");
        audit.Should().NotBeEmpty();
        audit.Should().OnlyContain(p => !p.Contains("\"" + hidden.Id + "\"") && !p.Contains("2555444333"));
    }

    // ── Audit minimisation (G21) ─────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Audit_Create_HoldsNoIbanOrIdValue_AndCompensationAuditIsLast4AndHashOnly()
    {
        var writer = await _fx.NewUserAsync("au1", "employees.write", "employees.read");
        var name = $"Audited {Guid.NewGuid():N}";
        using var r = await _fx.SendAsync(HttpMethod.Post, "/api/employees", Hire(name, iban: Iban, iqama: "2777666555"), writer);
        r.StatusCode.Should().Be(HttpStatusCode.Created, await r.Content.ReadAsStringAsync());
        foreach (var payload in await _fx.AllAuditMetadataAsync())
        {
            payload.Should().NotContain(Iban).And.NotContain("2777666555");
        }
        var comp = await _fx.AuditPayloadsAsync("employee.created_compensation_set");
        comp.Should().NotBeEmpty();
        comp.Should().OnlyContain(p => p.Contains("ibanLast4") && p.Contains("7519") && p.Contains("ibanHash") && !p.Contains(Iban));
    }

    // ── Foreign-tenant company at group level ────────────────────────────────────────────────────
    [Fact]
    public async Task Create_ForeignTenantCompanyGroupLevel_Rejected()
    {
        var group = await _fx.NewUserAsync("ft1", "employees.write", "employees.read");
        var before = await _fx.EmployeeCountAsync();
        using var r = await _fx.SendAsync(HttpMethod.Post, "/api/employees", Hire($"Foreign {Guid.NewGuid():N}", companyId: _fx.OtherCompanyId), group);
        ((int)r.StatusCode).Should().BeInRange(400, 499).And.NotBe(201);
        (await _fx.EmployeeCountAsync()).Should().Be(before);
    }

    // ── Compensation gate ────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Activate_BlockedUntilCompensationApproved_ApprovalRefusedToCreator_AllowedToFinance()
    {
        var writer = await _fx.NewUserAsync("cg1", "employees.write", "employees.read", "employees.sensitive", "payroll.approve");
        var finance = await _fx.NewUserAsync("cg2", "employees.read", "employees.sensitive", "payroll.approve");
        using var created = await _fx.SendAsync(HttpMethod.Post, "/api/employees", Hire($"Pay {Guid.NewGuid():N}", iban: Iban), writer);
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var id = (await Json(created)).GetProperty("id").GetInt32();

        using var activate = await _fx.SendAsync(new HttpMethod("PATCH"), $"/api/employees/{id}/status",
            new { status = "Active", effectiveDate = "2026-01-01", reason = "Try before approval" }, writer);
        activate.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await activate.Content.ReadAsStringAsync()).Should().Contain("pay:compensationUnapproved");

        using var self = await _fx.SendAsync(HttpMethod.Post, $"/api/employees/{id}/compensation/approve", new { note = "I checked it myself" }, writer);
        self.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var approved = await _fx.SendAsync(HttpMethod.Post, $"/api/employees/{id}/compensation/approve", new { note = "Checked against the offer letter" }, finance);
        approved.StatusCode.Should().Be(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync());
        (await _fx.OpenCompensationGapsAsync(id)).Should().Be(0);
        (await _fx.AuditPayloadsAsync("employee.created_compensation_approved")).Should().OnlyContain(p => !p.Contains(Iban));
    }

    // ── Draft lifecycle ──────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task DraftDelete_CreatorWithinSevenDays_HardDeletes()
    {
        var token = await _fx.NewUserAsync("dd1", "employees.write", "employees.read");
        using var created = await _fx.SendAsync(HttpMethod.Post, "/api/employees", Hire($"Discard {Guid.NewGuid():N}"), token);
        var id = (await Json(created)).GetProperty("id").GetInt32();
        using var del = await _fx.SendAsync(HttpMethod.Delete, $"/api/employees/{id}/draft", null, token);
        del.StatusCode.Should().Be(HttpStatusCode.NoContent, await del.Content.ReadAsStringAsync());
        (await _fx.EmployeeExistsAsync(id)).Should().BeFalse("a discard is a hard delete, not a soft one");
        (await _fx.AuditPayloadsAsync("employee.draft_discarded")).Should().NotBeEmpty();
    }

    [Fact]
    public async Task DraftDelete_OtherUser_Forbidden()
    {
        var maker = await _fx.NewUserAsync("dd2", "employees.write", "employees.read");
        var other = await _fx.NewUserAsync("dd3", "employees.write", "employees.read");
        using var created = await _fx.SendAsync(HttpMethod.Post, "/api/employees", Hire($"Not yours {Guid.NewGuid():N}"), maker);
        var id = (await Json(created)).GetProperty("id").GetInt32();
        using var del = await _fx.SendAsync(HttpMethod.Delete, $"/api/employees/{id}/draft", null, other);
        del.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _fx.EmployeeExistsAsync(id)).Should().BeTrue();
    }

    [Fact]
    public async Task DraftDelete_ActivatedEmployee_Forbidden()
    {
        var maker = await _fx.NewUserAsync("dd4", "employees.write", "employees.read");
        using var created = await _fx.SendAsync(HttpMethod.Post, "/api/employees", Hire($"Active {Guid.NewGuid():N}"), maker);
        var id = (await Json(created)).GetProperty("id").GetInt32();
        await _fx.SetStatusAsync(id, "Active");
        using var del = await _fx.SendAsync(HttpMethod.Delete, $"/api/employees/{id}/draft", null, maker);
        del.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _fx.EmployeeExistsAsync(id)).Should().BeTrue();
    }

    [Fact]
    public async Task HireDraftAutosave_StaleBase_409()
    {
        var token = await _fx.NewUserAsync("hd1", "employees.write", "employees.read");
        using var created = await _fx.SendAsync(HttpMethod.Post, "/api/employees", Hire($"Autosave {Guid.NewGuid():N}"), token);
        var id = (await Json(created)).GetProperty("id").GetInt32();
        using var stale = await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}/hire-draft",
            new { baseUpdatedAtUtc = DateTime.UtcNow.AddDays(-30), request = Hire("Autosave Renamed") }, token);
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Json(stale)).GetProperty("code").GetString().Should().Be("draft_changed_elsewhere");
    }

    // ── Rehire (G27) ─────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Rehire_ReusesTheRecord_NoSecondRow_HistoryAppended()
    {
        var token = await _fx.NewUserAsync("rh1", "employees.write", "employees.read");
        var former = await _fx.SeedEmployeeAsync("Former Employee", "2333222111", status: "Terminated");
        var before = await _fx.EmployeeCountAsync();
        using var r = await _fx.SendAsync(HttpMethod.Post, $"/api/employees/{former.Id}/rehire",
            new { startDate = DateTime.UtcNow.Date, reason = "Returning after a two year break" }, token);
        r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        (await _fx.EmployeeCountAsync()).Should().Be(before, "a rehire reuses the record");
        (await _fx.StatusOfAsync(former.Id)).Should().Be("Draft");
        (await _fx.StatusHistoryCountAsync(former.Id)).Should().Be(1);

        using var again = await _fx.SendAsync(HttpMethod.Post, $"/api/employees/{former.Id}/rehire",
            new { startDate = DateTime.UtcNow.Date, reason = "Second attempt on a draft" }, token);
        again.StatusCode.Should().Be(HttpStatusCode.Conflict, "only a former employee can be rehired");
    }
}

public sealed class HireIntegrityHttpFixture : IAsyncLifetime
{
    private SqliteConnection _anchor = null!;
    private HttpClient _client = null!;
    public AuthorizationPipelineHost Host { get; private set; } = null!;
    public Guid TenantId { get; private set; }
    public Guid CompanyId { get; private set; }
    public Guid OtherCompanyId { get; private set; }     // second company of the SAME tenant (for scope tests)
    public Guid OtherTenantId { get; private set; }
    private Guid _otherTenantCompany;

    public async Task InitializeAsync()
    {
        var connection = $"Data Source=file:hire-integrity-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _anchor = new SqliteConnection(connection);
        await _anchor.OpenAsync();
        Host = new AuthorizationPipelineHost(connection);
        _client = Host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        await db.Database.EnsureCreatedAsync();
        TenantId = await SeedTenantAsync(db, "Hire integrity A", c => CompanyId = c, secondCompany: c => OtherCompanyId = c);
        OtherTenantId = await SeedTenantAsync(db, "Hire integrity B", c => _otherTenantCompany = c);
        await TestApprovalConfig.EnsureDeciderAsync(db, TenantId);
    }

    private static async Task<Guid> SeedTenantAsync(ZayraDbContext db, string name, Action<Guid> company, Action<Guid>? secondCompany = null)
    {
        var tenant = new Tenant { Name = name, Slug = $"hire-{Guid.NewGuid():N}", IsActive = true };
        db.Tenants.Add(tenant);
        var main = new Company { TenantId = tenant.Id, LegalNameEn = name, TradeName = name, CountryCode = "SA", IsActive = true, DefaultCurrency = "SAR" };
        db.Companies.Add(main);
        company(main.Id);
        if (secondCompany is not null)
        {
            var second = new Company { TenantId = tenant.Id, LegalNameEn = name + " Two", TradeName = name + " Two", CountryCode = "SA", IsActive = true, DefaultCurrency = "SAR" };
            db.Companies.Add(second);
            secondCompany(second.Id);
        }
        db.TenantSubscriptions.Add(new TenantSubscription { TenantId = tenant.Id, Plan = "Enterprise", Status = "Active", MaxEmployees = 10_000 });
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    public Task<string> NewUserAsync(string name, params string[] permissions) => MintAsync(TenantId, name, null, permissions);
    public Task<string> NewScopedUserAsync(string name, Guid companyId, params string[] permissions) => MintAsync(TenantId, name, companyId, permissions);
    public Task<string> NewOtherTenantUserAsync(string name, params string[] permissions) => MintAsync(OtherTenantId, name, null, permissions);

    private async Task<string> MintAsync(Guid tenantId, string name, Guid? scopedCompany, string[] permissions)
    {
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var email = $"{name}-{Guid.NewGuid():N}@hire.local";
        var role = await db.Roles.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Name == "HR Officer");
        if (role is null) { role = new Role { TenantId = tenantId, Name = "HR Officer", NormalizedName = "HR OFFICER" }; db.Roles.Add(role); }
        var user = new User
        {
            TenantId = tenantId, Email = email, NormalizedEmail = email.ToUpperInvariant(), FullName = name, PasswordHash = "no-login-path",
            Status = "Active", AccessMode = "FullPortal", IsActive = true, IsEmailConfirmed = true, IsGroupScope = scopedCompany is null,
        };
        db.Users.Add(user);
        db.Set<UserRole>().Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        foreach (var p in permissions)
            db.Set<UserPermissionOverride>().Add(new UserPermissionOverride { TenantId = tenantId, UserId = user.Id, PermissionKey = p, Effect = "Allow", IsActive = true, Reason = "hire integrity test" });
        if (scopedCompany is Guid cid)
            db.Set<UserEntityAccess>().Add(new UserEntityAccess { TenantId = tenantId, UserId = user.Id, CompanyId = cid, Role = "HR Officer" });
        await db.SaveChangesAsync();

        var tokens = scope.ServiceProvider.GetRequiredService<ITokenService>();
        var loaded = await db.Users.IgnoreQueryFilters().AsNoTracking().Include(u => u.Tenant)
            .Include(u => u.UserRoles).ThenInclude(r => r.Role).ThenInclude(r => r!.RolePermissions).ThenInclude(p => p.Permission)
            .Include(u => u.PermissionOverrides).Include(u => u.EmployeeUserAccounts).Include(u => u.EntityAccesses)
            .SingleAsync(u => u.Id == user.Id);
        var grants = loaded.EntityAccesses.Where(e => e.IsActive).Select(e => new EntityAccessGrant(e.CompanyId, e.Role, e.GrantMode)).ToArray();
        var entityScope = EntityScopeClaims.Resolve(loaded.IsGroupScope, grants, Array.Empty<Guid>());
        return tokens.CreateAccessToken(loaded, AuthService.GetRoles(loaded), AuthService.GetPermissions(loaded), loaded.Tenant!, grants, entityScope, out _);
    }

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, string? token, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        return _client.SendAsync(request);
    }

    private ZayraDbContext NewDb(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<ZayraDbContext>();

    public async Task<Employee> SeedEmployeeAsync(string name, string iqama, string status = "Active")
    {
        using var scope = Host.Services.CreateScope();
        var db = NewDb(scope);
        var e = new Employee { TenantId = TenantId, CompanyId = CompanyId, EmployeeCode = $"SEED-{Guid.NewGuid():N}"[..16], FullName = name, EnglishName = name, IqamaNumber = iqama, Status = status, JoiningDate = DateTime.UtcNow.Date };
        db.Employees.Add(e);
        await db.SaveChangesAsync();
        return e;
    }

    public async Task<Employee> SeedEmployeeInOtherCompanyAsync(string name, string iqama)
    {
        using var scope = Host.Services.CreateScope();
        var db = NewDb(scope);
        var e = new Employee { TenantId = TenantId, CompanyId = OtherCompanyId, EmployeeCode = $"FAR-{Guid.NewGuid():N}"[..16], FullName = name, EnglishName = name, IqamaNumber = iqama, Status = "Active", JoiningDate = DateTime.UtcNow.Date };
        db.Employees.Add(e);
        await db.SaveChangesAsync();
        return e;
    }

    public async Task<int> EmployeeCountAsync() { using var s = Host.Services.CreateScope(); return await NewDb(s).Employees.IgnoreQueryFilters().CountAsync(); }
    public async Task<int> CountByNameAsync(string name) { using var s = Host.Services.CreateScope(); return await NewDb(s).Employees.IgnoreQueryFilters().CountAsync(e => e.EnglishName == name); }
    public async Task<bool> EmployeeExistsAsync(int id) { using var s = Host.Services.CreateScope(); return await NewDb(s).Employees.IgnoreQueryFilters().AnyAsync(e => e.Id == id); }
    public async Task<string> StatusOfAsync(int id) { using var s = Host.Services.CreateScope(); return await NewDb(s).Employees.IgnoreQueryFilters().Where(e => e.Id == id).Select(e => e.Status).SingleAsync(); }
    public async Task<int> StatusHistoryCountAsync(int id) { using var s = Host.Services.CreateScope(); return await NewDb(s).EmployeeStatusHistories.IgnoreQueryFilters().CountAsync(h => h.EmployeeId == id); }
    public async Task<int> OpenCompensationGapsAsync(int id) { using var s = Host.Services.CreateScope(); return await NewDb(s).EmployeeImportGaps.IgnoreQueryFilters().CountAsync(g => g.EmployeeId == id && g.GapType == "pay:compensationUnapproved" && g.ResolvedAtUtc == null); }
    public async Task SetStatusAsync(int id, string status)
    {
        using var s = Host.Services.CreateScope();
        var db = NewDb(s);
        var e = await db.Employees.IgnoreQueryFilters().SingleAsync(x => x.Id == id);
        e.Status = status;
        await db.SaveChangesAsync();
    }
    public async Task<List<string>> AuditPayloadsAsync(string action)
    {
        using var s = Host.Services.CreateScope();
        return (await NewDb(s).AuditLogs.IgnoreQueryFilters().Where(a => a.Action == action).Select(a => a.Metadata).ToListAsync()).Select(m => m ?? "").ToList();
    }
    public async Task<List<string>> AllAuditMetadataAsync()
    {
        using var s = Host.Services.CreateScope();
        return (await NewDb(s).AuditLogs.IgnoreQueryFilters().Select(a => a.Metadata).ToListAsync()).Select(m => m ?? "").ToList();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (Host is not null) await Host.DisposeAsync();
        if (_anchor is not null) await _anchor.DisposeAsync();
    }
}
