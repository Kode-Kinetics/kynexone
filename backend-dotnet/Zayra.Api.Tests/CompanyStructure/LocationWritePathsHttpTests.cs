using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Data;
using Zayra.Api.Models;
using Zayra.Api.Tests.Onboarding;

namespace Zayra.Api.Tests.CompanyStructure;

/// <summary>
/// Every door that writes a location (the Setup screen's /api/admin/locations, the /api/locations form and its CSV import)
/// applies one rule: the company scope of the row, the code lock, a clean 409 on a taken code, validated input, an audit row.
/// </summary>
public sealed class LocationWritePathsHttpTests : IClassFixture<HireIntegrityHttpFixture>
{
    private readonly HireIntegrityHttpFixture _fx;
    public LocationWritePathsHttpTests(HireIntegrityHttpFixture fixture) => _fx = fixture;

    private static int _n;
    private static string Code(string stem) => $"{stem}{Interlocked.Increment(ref _n):D4}{Guid.NewGuid():N}"[..14].ToUpperInvariant();

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    private Task<string> GroupAsync() => _fx.NewUserInRoleAsync("HR Manager", $"lg{Guid.NewGuid():N}"[..10], "organization.write", "organization.delete", "organization.read");
    private Task<string> ScopedAsync() => _fx.NewScopedUserAsync($"ls{Guid.NewGuid():N}"[..10], _fx.CompanyId, "organization.write", "organization.read");

    private async Task<Location> SeedAsync(string code, string name, Guid? companyViaBranch)
    {
        using var s = _fx.Host.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<ZayraDbContext>();
        Guid? branchId = null;
        if (companyViaBranch is Guid company)
        {
            var b = new Branch { TenantId = _fx.TenantId, CompanyId = company, Code = Code("B"), NameEn = "Branch" };
            db.Branches.Add(b);
            branchId = b.Id;
        }
        var loc = new Location { TenantId = _fx.TenantId, BranchId = branchId, Code = code, NameEn = name };
        db.Locations.Add(loc);
        await db.SaveChangesAsync();
        return loc;
    }

    private async Task<Location?> ReadAsync(Guid id)
    {
        using var s = _fx.Host.Services.CreateScope();
        return await s.ServiceProvider.GetRequiredService<ZayraDbContext>().Locations.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(l => l.Id == id);
    }

    private async Task<int> CountByCodeAsync(string code)
    {
        using var s = _fx.Host.Services.CreateScope();
        return await s.ServiceProvider.GetRequiredService<ZayraDbContext>().Locations.IgnoreQueryFilters().CountAsync(l => l.TenantId == _fx.TenantId && l.Code == code);
    }

    private Task<HttpResponseMessage> Import(string token, string csv, bool commit = true) =>
        _fx.SendAsync(HttpMethod.Post, commit ? "/api/locations/import" : "/api/locations/import-preview", new { csv }, token);

    // ── scope on the CSV import ──────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Import_ByCompanyScopedCaller_CannotOverwriteOrAddGroupLevelLocations()
    {
        var group = await SeedAsync(Code("G"), "Group HQ", null);
        var scoped = await ScopedAsync();
        var fresh = Code("N");
        var csv = $"Code,NameEn\n{group.Code},Hijacked\n{fresh},Brand New";

        using var preview = await Import(scoped, csv, commit: false);
        preview.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(preview)).GetProperty("wouldSkip").GetInt32().Should().Be(2);

        using var r = await Import(scoped, csv);
        r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        var body = await Json(r);
        body.GetProperty("skipped").GetInt32().Should().Be(2);
        body.GetProperty("updated").GetInt32().Should().Be(0);
        body.GetProperty("created").GetInt32().Should().Be(0);
        (await ReadAsync(group.Id))!.NameEn.Should().Be("Group HQ", "a scoped caller cannot edit a group-level location by spreadsheet");
        (await CountByCodeAsync(fresh)).Should().Be(0);
    }

    [Fact]
    public async Task Import_ByCompanyScopedCaller_MayUpdateALocationOfTheirOwnCompany_ButNotAnotherCompanys()
    {
        var own = await SeedAsync(Code("O"), "Own site", _fx.CompanyId);
        var far = await SeedAsync(Code("F"), "Far site", _fx.OtherCompanyId);
        var scoped = await ScopedAsync();

        using var r = await Import(scoped, $"Code,NameEn\n{own.Code},Own renamed\n{far.Code},Far renamed");
        r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        var body = await Json(r);
        body.GetProperty("updated").GetInt32().Should().Be(1);
        body.GetProperty("skipped").GetInt32().Should().Be(1);
        (await ReadAsync(own.Id))!.NameEn.Should().Be("Own renamed");
        (await ReadAsync(far.Id))!.NameEn.Should().Be("Far site");
    }

    [Fact]
    public async Task Import_OnUpdate_HonoursIsActive_AndWritesAnAuditRowWithBeforeAndAfter()
    {
        var loc = await SeedAsync(Code("A"), "Active site", null);
        var group = await GroupAsync();

        using var r = await Import(group, $"Code,NameEn,IsActive\n{loc.Code},Active site,false");
        r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        (await ReadAsync(loc.Id))!.IsActive.Should().BeFalse("the IsActive column of an update row is honoured, as it is on a create row");
        var audit = string.Join("\n", await _fx.AuditPayloadsAsync("organization.location_updated"));
        audit.Should().Contain(loc.Code).And.Contain("\"before\"").And.Contain("\"after\"");
    }

    // ── one code rule on every door ──────────────────────────────────────────────────────────────
    [Fact]
    public async Task CreateDeleteRecreate_TheSameCode_Succeeds()
    {
        var group = await GroupAsync();
        var code = Code("R");
        using var first = await _fx.SendAsync(HttpMethod.Post, "/api/admin/locations", new { code, nameEn = "Round one" }, group);
        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        var id = (await Json(first)).GetProperty("id").GetGuid();
        using (var del = await _fx.SendAsync(HttpMethod.Delete, $"/api/admin/locations/{id}", null, group))
            (await Json(del)).GetProperty("outcome").GetString().Should().Be("deleted");

        using var again = await _fx.SendAsync(HttpMethod.Post, "/api/admin/locations", new { code, nameEn = "Round two" }, group);
        again.StatusCode.Should().Be(HttpStatusCode.OK, await again.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/api/admin/locations", true)]
    [InlineData("/api/locations", false)]
    public async Task CreateWithATakenCode_IsA409_EvenWhenOnlyTheLetterCaseDiffers(string route, bool admin)
    {
        var group = await GroupAsync();
        var code = Code("T");
        (await _fx.SendAsync(HttpMethod.Post, route, new { code, nameEn = "First" }, group)).StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Created);

        foreach (var variant in new[] { code, code.ToLowerInvariant() })
        {
            using var dup = await _fx.SendAsync(HttpMethod.Post, route, new { code = variant, nameEn = "Second" }, group);
            dup.StatusCode.Should().Be(HttpStatusCode.Conflict, $"{route} {variant}");
            (await Json(dup)).GetProperty("message").GetString().Should().Contain(code, admin ? "the Setup screen reads body.message" : "");
        }
    }

    [Fact]
    public async Task Update_ToAnotherLocationsCode_IsA409_AndChangesNothing()
    {
        var a = await SeedAsync(Code("U"), "Alpha", null);
        var b = await SeedAsync(Code("V"), "Beta", null);
        var group = await GroupAsync();

        using var r = await _fx.SendAsync(HttpMethod.Put, $"/api/locations/{b.Id}", new { code = a.Code }, group);
        r.StatusCode.Should().Be(HttpStatusCode.Conflict, await r.Content.ReadAsStringAsync());
        (await ReadAsync(b.Id))!.Code.Should().Be(b.Code);
    }

    [Fact]
    public async Task EditOnTheSetupScreen_RefusesToChangeTheCodeOfALocationInUse_AndKeepsTheUnusedOnesFree()
    {
        var group = await GroupAsync();
        var code = Code("E");
        using var created = await _fx.SendAsync(HttpMethod.Post, "/api/admin/locations", new { code, nameEn = "Editable" }, group);
        var id = (await Json(created)).GetProperty("id").GetGuid();
        var renamed = Code("W");
        using var ok = await _fx.SendAsync(HttpMethod.Put, $"/api/admin/locations/{id}", new { code = renamed, nameEn = "Editable" }, group);
        ok.StatusCode.Should().Be(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync());
        (await ReadAsync(id))!.Code.Should().Be(renamed);

        using (var s = _fx.Host.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<ZayraDbContext>();
            db.Employees.Add(new Employee { TenantId = _fx.TenantId, CompanyId = _fx.CompanyId, EmployeeCode = $"LK-{Guid.NewGuid():N}"[..14], FullName = "Uses It", EnglishName = "Uses It", Status = "Active", JoiningDate = DateTime.UtcNow.Date, WorkLocationId = id.ToString() });
            await db.SaveChangesAsync();
        }
        using var locked = await _fx.SendAsync(HttpMethod.Put, $"/api/admin/locations/{id}", new { code = Code("X"), nameEn = "Editable" }, group);
        locked.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(locked)).GetProperty("error").GetString().Should().Be("code_locked");
        (await ReadAsync(id))!.Code.Should().Be(renamed);
    }

    // ── audit on every write ─────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("/api/admin/locations")]
    [InlineData("/api/locations")]
    public async Task CreateAndUpdate_WriteAuditRows_WithBeforeAndAfter(string route)
    {
        var group = await GroupAsync();
        var code = Code("D");
        using var created = await _fx.SendAsync(HttpMethod.Post, route, new { code, nameEn = "Audited site" }, group);
        var id = (await Json(created)).GetProperty("id").GetGuid();
        using var updated = await _fx.SendAsync(HttpMethod.Put, $"{route}/{id}", new { code, nameEn = "Audited renamed", reason = "Sign changed" }, group);
        updated.StatusCode.Should().Be(HttpStatusCode.OK, await updated.Content.ReadAsStringAsync());

        var createdAudit = string.Join("\n", await _fx.AuditPayloadsAsync("organization.location_created"));
        createdAudit.Should().Contain(code).And.Contain("Audited site");
        var updatedAudit = (await _fx.AuditPayloadsAsync("organization.location_updated")).Single(m => m.Contains(code));
        updatedAudit.Should().Contain("Audited site").And.Contain("Audited renamed").And.Contain("Sign changed");
    }

    // ── validation and response shape ────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("/api/admin/locations")]
    [InlineData("/api/locations")]
    public async Task OversizeOrMalformedInput_Is400(string route)
    {
        var group = await GroupAsync();
        foreach (var body in new object[]
        {
            new { code = new string('C', 41), nameEn = "Too long a code" },
            new { code = "bad code<script>", nameEn = "Odd characters" },
            new { code = Code("L"), nameEn = new string('N', 181) },
            new { code = Code("M"), nameEn = "Latitude", latitude = 123m, longitude = 10m },
        }.Where(b => route == "/api/admin/locations" || !JsonSerializer.Serialize(b).Contains("latitude")))   // only the Setup screen's door takes coordinates
        {
            using var r = await _fx.SendAsync(HttpMethod.Post, route, body, group);
            r.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"{route} {JsonSerializer.Serialize(body)}");
            (await Json(r)).GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public async Task TheResponses_AreADto_NotTheStoredEntity_AndKeepTheFieldsTheSetupScreenReads()
    {
        var group = await GroupAsync();
        var code = Code("S");
        using var r = await _fx.SendAsync(HttpMethod.Post, "/api/admin/locations",
            new { code, nameEn = "Shaped", nameAr = "موقع", addressLine1 = "1 Main", city = "Riyadh", countryCode = "SA", postalCode = "11564", latitude = 24.7m, longitude = 46.6m, geofenceRadiusMeters = 150m }, group);
        r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        var created = await Json(r);
        foreach (var field in new[] { "id", "code", "nameEn", "nameAr", "addressLine1", "city", "countryCode", "postalCode", "latitude", "longitude", "geofenceRadiusMeters", "isActive" })
            created.TryGetProperty(field, out _).Should().BeTrue(field);
        foreach (var internalField in new[] { "tenantId", "isDeleted", "createdBy", "updatedBy" })
            created.TryGetProperty(internalField, out _).Should().BeFalse(internalField);

        using var list = await _fx.SendAsync(HttpMethod.Get, "/api/admin/locations", null, group);
        var row = (await Json(list)).EnumerateArray().Single(e => e.GetProperty("code").GetString() == code);
        row.TryGetProperty("tenantId", out _).Should().BeFalse();
        row.GetProperty("geofenceRadiusMeters").GetDecimal().Should().Be(150m);
    }
}
