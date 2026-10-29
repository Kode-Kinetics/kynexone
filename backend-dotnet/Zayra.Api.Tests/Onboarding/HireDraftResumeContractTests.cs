using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Data;

namespace Zayra.Api.Tests.Onboarding;

/// <summary>
/// CD67: the draft the wizard resumes is the REAL GET /api/employees/{id} response. This captures that JSON and keeps the
/// frontend fixture (frontend/unit/fixtures/hire-real/employee-draft.json) in step with it: set REGEN_HIRE_FIXTURE=1 to
/// rewrite the file; otherwise the test fails when the live shape and the checked-in file disagree.
/// Autosave never raises acknowledgement warnings (CD28) and never clears a stored nationality.
/// </summary>
public class HireDraftResumeContractTests : IClassFixture<HireIntegrityHttpFixture>
{
    private readonly HireIntegrityHttpFixture _fx;
    public HireDraftResumeContractTests(HireIntegrityHttpFixture fixture) => _fx = fixture;

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    private static string FixturePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !(Directory.Exists(Path.Combine(dir.FullName, "frontend")) && Directory.Exists(Path.Combine(dir.FullName, "backend-dotnet"))))
            dir = dir.Parent;
        dir.Should().NotBeNull("the repository root must be reachable from the test output");
        return Path.Combine(dir!.FullName, "frontend", "unit", "fixtures", "hire-real", "employee-draft.json");
    }

    private Dictionary<string, object?> DraftBody(string name, Action<Dictionary<string, object?>>? tweak = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["englishName"] = name, ["companyId"] = _fx.CompanyId, ["joiningDate"] = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
            ["contractType"] = "Unlimited",
        };
        tweak?.Invoke(body);
        return body;
    }

    private async Task<(int Id, DateTime Basis)> CreateDraftAsync(string token, Dictionary<string, object?> body)
    {
        using var created = await _fx.SendAsync(HttpMethod.Post, "/api/employees", body, token);
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var id = (await Json(created)).GetProperty("id").GetInt32();
        return (id, await BasisAsync(id));
    }

    private async Task<DateTime> BasisAsync(int id)
    {
        using var scope = _fx.Host.Services.CreateScope();
        var e = await scope.ServiceProvider.GetRequiredService<ZayraDbContext>().Employees.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == id);
        return e.UpdatedAtUtc ?? e.CreatedAtUtc;
    }

    // ── fixture helpers ─────────────────────────────────────────────────────────────────────────
    private static readonly Regex UnspecifiedDateTimeRx = new(@"^\d{4}-\d{2}-\d{2}T[\d:.]+$");
    private static readonly Regex GuidRx = new("^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$");

    /// <summary>Stable copy: ids, GUIDs, stamps and codes are replaced; every field name, value kind and format stays.</summary>
    private static string Normalise(JsonElement live)
    {
        var guids = new Dictionary<string, string>();
        object? Walk(string? key, JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object: return e.EnumerateObject().ToDictionary(p => p.Name, p => Walk(p.Name, p.Value), StringComparer.Ordinal);
                case JsonValueKind.Array: return e.EnumerateArray().Select(x => Walk(key, x)).ToList();
                case JsonValueKind.String:
                    var s = e.GetString()!;
                    if (GuidRx.IsMatch(s)) { if (!guids.TryGetValue(s, out var g)) guids[s] = g = $"00000000-0000-0000-0000-{guids.Count + 1:D12}"; return g; }
                    if (key is "createdAtUtc" or "updatedAtUtc" or "createdAt" or "updatedAt") return "2026-10-27T08:00:00Z";
                    if (key == "employeeCode") return "EMP-0001";
                    // The SQLite host hands back Unspecified kinds; the production context stamps every DateTime Utc on read
                    // (KynexDbContext.UtcDateTimeConverter), so production serialises 2026-11-01T00:00:00Z. The fixture records that.
                    if (UnspecifiedDateTimeRx.IsMatch(s)) return s + "Z";
                    return s;
                case JsonValueKind.Number: return key == "id" ? 44 : (object?)JsonSerializer.Deserialize<JsonElement>(e.GetRawText());
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                default: return null;
            }
        }
        return JsonSerializer.Serialize(Walk(null, live), new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    private static void Shape(string path, JsonElement e, ISet<string> into)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object: foreach (var p in e.EnumerateObject()) Shape(path + "." + p.Name, p.Value, into); break;
            case JsonValueKind.Array: into.Add(path + "[]"); foreach (var x in e.EnumerateArray()) Shape(path + "[]", x, into); break;
            default: into.Add(path + ":" + (e.ValueKind is JsonValueKind.True or JsonValueKind.False ? "bool" : e.ValueKind.ToString())); break;
        }
    }

    [Fact]
    public async Task GetDraft_ExposesNationalityCodeAndState_AndTheFrontendFixtureIsTheLiveShape()
    {
        var token = await _fx.NewUserAsync("rs1", "employees.write", "employees.read", "employees.sensitive");
        // The draft carries a salary, so a second person who could approve it must exist (strict separation of duties).
        await _fx.NewUserAsync("rs1f", "payroll.approve");
        var (id, _) = await CreateDraftAsync(token, DraftBody("Alex Morgan", b =>
        {
            b["nationalityCode"] = "EG"; b["dateOfBirth"] = "1990-06-01"; b["mobileNumber"] = "+966512345678"; b["workEmail"] = "alex.morgan@example.com";
            b["employmentType"] = "Full-time";
            b["payrollProfile"] = new { iban = "SA0380000000608010167519", bankName = "Al Rajhi Bank", salaryCurrency = "SAR" };
            b["salaryBreakdown"] = new { basicSalary = 12000.5m, housingAllowance = 3000m, currency = "SAR" };
        }));

        using var get = await _fx.SendAsync(HttpMethod.Get, $"/api/employees/{id}", null, token);
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        var live = await Json(get);

        live.GetProperty("nationalityCode").GetString().Should().Be("EG", "a resumed draft must be able to restore the ISO code, not guess it from the English name");
        live.GetProperty("nationalityMappingState").GetString().Should().Be("Confirmed");
        live.GetProperty("nationality").GetString().Should().NotBeNullOrWhiteSpace();
        live.GetProperty("joiningDate").GetString().Should().MatchRegex(@"^\d{4}-\d{2}-\d{2}T[\d:.]+Z?$", "this is the real, time-qualified serialisation the client must normalise");
        live.GetProperty("status").GetString().Should().Be("Draft");

        var path = FixturePath();
        var rendered = Normalise(live);
        if (Environment.GetEnvironmentVariable("REGEN_HIRE_FIXTURE") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, rendered + "\n");
        }
        File.Exists(path).Should().BeTrue("run with REGEN_HIRE_FIXTURE=1 to write " + path);
        var checkedIn = new HashSet<string>(); var current = new HashSet<string>();
        Shape("$", JsonDocument.Parse(await File.ReadAllTextAsync(path)).RootElement, checkedIn);
        Shape("$", JsonDocument.Parse(rendered).RootElement, current);
        checkedIn.Should().BeEquivalentTo(current, "the frontend fixture must be exactly what GET /api/employees/{id} returns for a draft (REGEN_HIRE_FIXTURE=1 rewrites it)");
        JsonDocument.Parse(await File.ReadAllTextAsync(path)).RootElement.GetProperty("joiningDate").GetString().Should().Be("2026-11-01T00:00:00Z");
    }

    [Fact]
    public async Task Autosave_WithABackdatedJoiningDate_Returns200_NoAcknowledgementWarning()
    {
        var token = await _fx.NewUserAsync("rs2", "employees.write", "employees.read");
        var (id, basis) = await CreateDraftAsync(token, DraftBody($"Backdate {Guid.NewGuid():N}"));
        var backdated = DraftBody("Backdate Saved", b => b["joiningDate"] = DateTime.UtcNow.Date.AddDays(-90));

        using var saved = await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}/hire-draft", new { baseUpdatedAtUtc = basis, request = backdated }, token);
        saved.StatusCode.Should().Be(HttpStatusCode.OK, "autosave never fights typing; the warning surfaces at Create/Activate: " + await saved.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Autosave_OmittingNationality_KeepsTheStoredNationality_AndAnExplicitOneReplacesIt()
    {
        var token = await _fx.NewUserAsync("rs3", "employees.write", "employees.read");
        var (id, basis) = await CreateDraftAsync(token, DraftBody($"Keep Nat {Guid.NewGuid():N}", b => b["nationalityCode"] = "EG"));

        using var first = await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}/hire-draft",
            new { baseUpdatedAtUtc = basis, request = DraftBody("Keep Nat Edited") }, token);
        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        var stored = await StoredNationalityAsync(id);
        stored.Code.Should().Be("EG");
        stored.Text.Should().NotBeNullOrWhiteSpace();

        var next = (await Json(first)).GetProperty("updatedAtUtc").GetDateTime();
        using var second = await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}/hire-draft",
            new { baseUpdatedAtUtc = next, request = DraftBody("Keep Nat Edited", b => b["nationalityCode"] = "PK") }, token);
        second.StatusCode.Should().Be(HttpStatusCode.OK, await second.Content.ReadAsStringAsync());
        (await StoredNationalityAsync(id)).Code.Should().Be("PK");
    }

    private async Task<(string? Code, string Text)> StoredNationalityAsync(int id)
    {
        using var scope = _fx.Host.Services.CreateScope();
        var e = await scope.ServiceProvider.GetRequiredService<ZayraDbContext>().Employees.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == id);
        return (e.NationalityCode, e.Nationality);
    }
}
