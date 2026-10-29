using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Application.Employees.Onboarding;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Localization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Onboarding;

/// <summary>
/// Owner feedback on the live hire wizard (OD7 to OD10), through the real pipeline as an ordinary HR user
/// (employees.write, no override permission).
/// </summary>
public class HireWizardOwnerFeedbackTests : IClassFixture<HireIntegrityHttpFixture>
{
    private readonly HireIntegrityHttpFixture _fx;
    public HireWizardOwnerFeedbackTests(HireIntegrityHttpFixture fixture) => _fx = fixture;

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();
    private Task<string> HrAsync() => _fx.NewUserAsync($"hr-{Guid.NewGuid():N}", "employees.write", "employees.read");

    // ── OD7: the work email is whatever IT issued ───────────────────────────────────────────────
    [Theory]
    [InlineData("")]
    [InlineData("acme.sa")]
    public async Task WorkEmail_OnAnyDomain_IsAcceptedOnCreateAndDraftAutosave(string companyDomain)
    {
        using (var s = _fx.Host.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<ZayraDbContext>();
            var company = await db.Companies.IgnoreQueryFilters().SingleAsync(c => c.Id == _fx.CompanyId);
            company.EmailDomain = companyDomain;
            await db.SaveChangesAsync();
        }
        var token = await HrAsync();
        var address = $"noah.{Guid.NewGuid():N}@issued.example";
        using var created = await _fx.SendAsync(HttpMethod.Post, "/api/employees",
            new { englishName = "Noah External", companyId = _fx.CompanyId, workEmail = address }, token);
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var id = (await Json(created)).GetProperty("id").GetInt32();

        var second = $"noah2.{Guid.NewGuid():N}@issued.example";
        DateTime basis;
        using (var s = _fx.Host.Services.CreateScope())
        {
            var e = await s.ServiceProvider.GetRequiredService<ZayraDbContext>().Employees.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == id);
            e.WorkEmail.Should().Be(address);
            basis = e.UpdatedAtUtc ?? e.CreatedAtUtc;
        }
        using var saved = await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}/hire-draft",
            new { baseUpdatedAtUtc = basis, request = new { englishName = "Noah External", companyId = _fx.CompanyId, workEmail = second } }, token);
        saved.StatusCode.Should().Be(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
        using (var s = _fx.Host.Services.CreateScope())
            (await s.ServiceProvider.GetRequiredService<ZayraDbContext>().Employees.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == id))
                .WorkEmail.Should().Be(second);
    }

    // ── OD8: any active grade; a different one from the designation's is a warning with a reason ─
    private async Task<(Guid Designation, Guid DesignationGrade, Guid OtherGrade)> SeedGradesAsync()
    {
        using var s = _fx.Host.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var tag = Guid.NewGuid().ToString("N")[..6];
        var g1 = new Grade { TenantId = _fx.TenantId, CompanyId = _fx.CompanyId, Code = "A" + tag, Name = "Grade A " + tag, IsActive = true };
        var g2 = new Grade { TenantId = _fx.TenantId, CompanyId = _fx.CompanyId, Code = "B" + tag, Name = "Grade B " + tag, IsActive = true };
        var d = new Designation { TenantId = _fx.TenantId, CompanyId = _fx.CompanyId, Code = "D" + tag, TitleEn = "Analyst " + tag, GradeId = g1.Id, IsActive = true };
        db.AddRange(g1, g2, d);
        await db.SaveChangesAsync();
        return (d.Id, g1.Id, g2.Id);
    }

    [Fact]
    public async Task GradeDifferentFromTheDesignation_IsAWarning_NotARefusal_AndTheAcknowledgementIsAudited()
    {
        var (designation, _, other) = await SeedGradesAsync();
        var token = await HrAsync();
        object Body(object[]? acks) => new Dictionary<string, object?>
        {
            ["englishName"] = "Grade Exception", ["companyId"] = _fx.CompanyId, ["designationId"] = designation, ["gradeId"] = other,
            ["acknowledgedWarnings"] = acks,
        };
        using var first = await _fx.SendAsync(HttpMethod.Post, "/api/employees", Body(null), token);
        first.StatusCode.Should().Be(HttpStatusCode.Conflict, await first.Content.ReadAsStringAsync());
        var problem = await Json(first);
        problem.GetProperty("code").GetString().Should().Be("warnings_require_acknowledgement");
        var warning = problem.GetProperty("warnings").EnumerateArray().Single(w => w.GetProperty("code").GetString() == "grade_differs_from_designation");
        warning.GetProperty("field").GetString().Should().Be("gradeId");
        (warning.TryGetProperty("requiresPermission", out var perm) && perm.GetBoolean()).Should().BeFalse("any HR user may confirm this");

        using var shortReason = await _fx.SendAsync(HttpMethod.Post, "/api/employees",
            Body([new { code = "grade_differs_from_designation", reason = "short" }]), token);
        (await Json(shortReason)).GetProperty("code").GetString().Should().Be("override_reason_required");

        using var ok = await _fx.SendAsync(HttpMethod.Post, "/api/employees",
            Body([new { code = "grade_differs_from_designation", reason = "Seconded from the regional office at a higher grade" }]), token);
        ok.StatusCode.Should().Be(HttpStatusCode.Created, await ok.Content.ReadAsStringAsync());
        var id = (await Json(ok)).GetProperty("id").GetInt32();
        using var scope = _fx.Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        (await db.Employees.IgnoreQueryFilters().AsNoTracking().SingleAsync(e => e.Id == id)).GradeId.Should().Be(other);
        var audits = await db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.EntityId == id.ToString() && a.Action == "employee.warning_overridden").ToListAsync();
        audits.Should().ContainSingle(a => a.Metadata != null && a.Metadata.Contains("grade_differs_from_designation"));
    }

    [Fact]
    public async Task DraftAutosave_NeverRaisesTheGradeWarning()
    {
        var (designation, _, other) = await SeedGradesAsync();
        var token = await HrAsync();
        using var created = await _fx.SendAsync(HttpMethod.Post, "/api/employees", new { englishName = "Autosave Grade", companyId = _fx.CompanyId }, token);
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var id = (await Json(created)).GetProperty("id").GetInt32();
        DateTime basis;
        using (var s = _fx.Host.Services.CreateScope())
        {
            var e = await s.ServiceProvider.GetRequiredService<ZayraDbContext>().Employees.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == id);
            basis = e.UpdatedAtUtc ?? e.CreatedAtUtc;
        }
        using var saved = await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}/hire-draft",
            new { baseUpdatedAtUtc = basis, request = new { englishName = "Autosave Grade", companyId = _fx.CompanyId, designationId = designation, gradeId = other } }, token);
        saved.StatusCode.Should().Be(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AnInactiveGrade_IsStillRefused()
    {
        var (designation, _, other) = await SeedGradesAsync();
        using (var s = _fx.Host.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<ZayraDbContext>();
            (await db.Grades.IgnoreQueryFilters().SingleAsync(g => g.Id == other)).IsActive = false;
            await db.SaveChangesAsync();
        }
        using var r = await _fx.SendAsync(HttpMethod.Post, "/api/employees",
            new { englishName = "Inactive Grade", companyId = _fx.CompanyId, designationId = designation, gradeId = other }, await HrAsync());
        r.StatusCode.Should().Be((HttpStatusCode)422, await r.Content.ReadAsStringAsync());
    }

    // ── OD9: the Arabic name suggestion, as the wizard calls it ─────────────────────────────────
    [Theory]
    [InlineData("Mohammed Abdullah Al-Qahtani", "محمد عبد الله القحطاني")]
    [InlineData("Fatimah Ahmed Al-Harbi", "فاطمة أحمد الحربي")]
    [InlineData("Abdulrahman bin Saad Al Otaibi", "عبد الرحمن بن سعد العتيبي")]
    [InlineData("Aisha bint Khalid Al-Shehri", "عائشة بنت خالد الشهري")]
    [InlineData("Muhammad Imran Khan", "محمد عمران خان")]
    [InlineData("Mohammad Tariq Hussain", "محمد طارق حسين")]
    [InlineData("Zainab Fatima Siddiqui", "زينب فاطمة صديقي")]
    [InlineData("Abdul Majeed Qureshi", "عبد المجيد قريشي")]
    public async Task PersonNameEndpoint_GivesAReliableSuggestion_ForCommonArabicAndSouthAsianNames(string name, string expected)
    {
        using var r = await _fx.SendAsync(HttpMethod.Post, "/api/localization/transliterate",
            new { text = name, target = "ar", kind = "person-name" }, await HrAsync());
        r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        var body = await Json(r);
        body.GetProperty("requiresManualEntry").GetBoolean().Should().BeFalse($"'{name}' is a common name");
        body.GetProperty("suggestion").GetString().Should().Be(expected);
    }

    [Fact]
    public async Task PersonNameEndpoint_StillAsksForManualEntry_ForANameItDoesNotKnow()
    {
        using var r = await _fx.SendAsync(HttpMethod.Post, "/api/localization/transliterate",
            new { text = "Zxqv Plorth", target = "ar", kind = "person-name" }, await HrAsync());
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(r);
        body.GetProperty("requiresManualEntry").GetBoolean().Should().BeTrue();
        body.GetProperty("suggestion").GetString().Should().BeEmpty();
    }

    [Theory]
    [InlineData("Al")]
    [InlineData("Bin")]
    [InlineData("Abdul Unknown")]
    [InlineData("Al Zxqv")]
    [InlineData("Hassan")]
    [InlineData("Muhammad bin")]
    public void NewRules_NeverGuess(string name) =>
        new TransliterationService().ToArabicPersonName(name).Should().BeEmpty();

    // ── OD10: the nationality list reaches the picker whole ─────────────────────────────────────
    [Fact]
    public async Task NationalitiesEndpoint_ReturnsTheWholeCatalog_WithTheFieldNamesTheClientReads()
    {
        using var r = await _fx.SendAsync(HttpMethod.Get, "/api/reference/nationalities", null, await HrAsync());
        r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        var list = (await Json(r)).EnumerateArray().ToList();
        list.Count.Should().BeGreaterThanOrEqualTo(245);
        list.All(e => e.TryGetProperty("iso2", out _) && e.TryGetProperty("nameEn", out _) && e.TryGetProperty("nameAr", out _)).Should().BeTrue();
        list.Select(e => e.GetProperty("iso2").GetString()).Should().OnlyHaveUniqueItems();
        list.Should().OnlyContain(e => e.GetProperty("nameEn").GetString()!.Length > 1 && e.GetProperty("nameAr").GetString()!.Length > 1);
    }

    [Fact]
    public void Catalog_HasEveryIsoCountryWithArabicNames_AndArabicSearchWorks()
    {
        NationalityCatalog.Entries.Count.Should().BeGreaterThanOrEqualTo(245);
        foreach (var code in new[] { "SA", "PK", "IN", "EG", "PH", "BD", "NP", "LK", "ID", "SD", "YE", "JO", "SY", "GB", "US", "TW", "PS", "CW", "SS" })
            NationalityCatalog.FindByIso2(code).Should().NotBeNull(code);
        NationalityCatalog.Entries.Should().OnlyContain(e => System.Text.RegularExpressions.Regex.IsMatch(e.NameAr, "[؀-ۿ]"));
        NationalityCatalog.TryResolve("باكستان")!.Iso2.Should().Be("PK");
        NationalityCatalog.TryResolve("الفلبين")!.Iso2.Should().Be("PH");
        NationalityCatalog.TryResolve("سريلانكا")!.Iso2.Should().Be("LK");
    }

    // ── review fixes: the grade reason is collected at activation, per designation/grade pair ──
    private async Task<int> DraftWithDifferentGradeAsync(string token)
    {
        var (designation, _, other) = await SeedGradesAsync();
        using var created = await _fx.SendAsync(HttpMethod.Post, "/api/employees", new { englishName = "Activation Grade", companyId = _fx.CompanyId }, token);
        var id = (await Json(created)).GetProperty("id").GetInt32();
        await SetPairAsync(id, designation, other);
        return id;
    }

    private async Task SetPairAsync(int id, Guid designation, Guid grade)
    {
        using var s = _fx.Host.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var e = await db.Employees.IgnoreQueryFilters().SingleAsync(x => x.Id == id);
        e.DesignationId = designation; e.GradeId = grade;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Activation_OfADraftGivenADifferentGradeByAutosave_AsksForTheReason()
    {
        var token = await _fx.NewUserAsync($"hr-{Guid.NewGuid():N}", "employees.write", "employees.read");
        var id = await DraftWithDifferentGradeAsync(token);
        using var r = await _fx.SendAsync(HttpMethod.Patch, $"/api/employees/{id}/status",
            new { status = "Active", effectiveDate = "2026-01-01", reason = "Hire complete" }, token);
        r.StatusCode.Should().Be(HttpStatusCode.Conflict, await r.Content.ReadAsStringAsync());
        (await Json(r)).GetProperty("warnings").EnumerateArray().Should().Contain(w => w.GetProperty("code").GetString() == "grade_differs_from_designation");
    }

    private static Zayra.Api.Infrastructure.Employees.Onboarding.EmployeeInputGuard GuardFor(ZayraDbContext db) => new(db);
    private static Zayra.Api.Application.Auth.RequestContext Ctx(Guid tenant) => new("127.0.0.1", "test", Guid.NewGuid(), tenant, ["HR Manager"], []);

    private static async Task<(ZayraDbContext Db, Guid Tenant, int Id, Guid Desig, Guid G1, Guid G2)> GuardWorldAsync()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var t = Guid.NewGuid();
        var g1 = new Grade { TenantId = t, Code = "A", Name = "A", IsActive = true };
        var g2 = new Grade { TenantId = t, Code = "B", Name = "B", IsActive = true };
        var g3 = new Grade { TenantId = t, Code = "C", Name = "C", IsActive = true };
        var d = new Designation { TenantId = t, Code = "D", TitleEn = "D", GradeId = g1.Id, IsActive = true };
        var c = new Company { TenantId = t, LegalNameEn = "C", TradeName = "C", CountryCode = "SA", Jurisdiction = "t", RegistrationNumber = "RC-1", DefaultCurrency = "SAR", IsActive = true };
        var e = new Employee { TenantId = t, CompanyId = c.Id, EmployeeCode = "E1", FullName = "X Person", EnglishName = "X Person", Status = "Draft", JoiningDate = DateTime.UtcNow.Date, DesignationId = d.Id, GradeId = g2.Id };
        db.AddRange(g1, g2, g3, d, c, e);
        await db.SaveChangesAsync();
        return (db, t, e.Id, d.Id, g2.Id, g3.Id);
    }

    [Fact]
    public async Task ActivationGuard_AcceptsAnAcknowledgement_AuditsThePair_AndDoesNotAskAgainForTheSamePair()
    {
        var (db, t, id, desig, g2, g3) = await GuardWorldAsync();
        await using var _ = db;
        var guard = GuardFor(db);
        await Assert.ThrowsAsync<Zayra.Api.Application.Employees.Onboarding.EmployeeWarningsRequireAckException>(() => guard.EnforceActivationAsync(t, id, Ctx(t), default));
        var ack = new[] { new AcknowledgedWarning("grade_differs_from_designation", "Seconded at a higher grade") };
        await guard.EnforceActivationAsync(t, id, Ctx(t), default, ack);
        await db.SaveChangesAsync();
        (await db.AuditLogs.IgnoreQueryFilters().Where(a => a.Action == "employee.warning_overridden").ToListAsync())
            .Should().ContainSingle(a => a.Metadata!.Contains(g2.ToString()) && a.Metadata.Contains(desig.ToString()));
        await guard.EnforceActivationAsync(t, id, Ctx(t), default);     // same pair, recorded reason: no second prompt
        // a changed pair asks again
        (await db.Employees.IgnoreQueryFilters().SingleAsync(e => e.Id == id)).GradeId = g3;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<Zayra.Api.Application.Employees.Onboarding.EmployeeWarningsRequireAckException>(() => guard.EnforceActivationAsync(t, id, Ctx(t), default));
    }

    [Theory]
    [InlineData("Offboarded")]
    [InlineData("Suspended")]
    [InlineData("Terminated")]
    public async Task Reinstatement_OfSomeoneWithAMismatchedPair_RaisesNoGradeWarning_ButFirstActivationStillDoes(string status)
    {
        var (db, t, id, _, _, _) = await GuardWorldAsync();
        await using var _ = db;
        await Assert.ThrowsAsync<Zayra.Api.Application.Employees.Onboarding.EmployeeWarningsRequireAckException>(() => GuardFor(db).EnforceActivationAsync(t, id, Ctx(t), default));
        (await db.Employees.IgnoreQueryFilters().SingleAsync(e => e.Id == id)).Status = status;
        await db.SaveChangesAsync();
        await GuardFor(db).EnforceActivationAsync(t, id, Ctx(t), default);
    }

    [Fact]
    public async Task ActivationGuard_AShortReasonIsRefused_AndTheDesignationsOwnGradeNeedsNothing()
    {
        var (db, t, id, _, _, _) = await GuardWorldAsync();
        await using var _ = db;
        var short1 = new[] { new AcknowledgedWarning("grade_differs_from_designation", "short") };
        (await Assert.ThrowsAsync<EmployeeValidationException>(() => GuardFor(db).EnforceActivationAsync(t, id, Ctx(t), default, short1)))
            .Code.Should().Be("override_reason_required");
        var e = await db.Employees.IgnoreQueryFilters().SingleAsync(x => x.Id == id);
        e.GradeId = (await db.Designations.IgnoreQueryFilters().SingleAsync()).GradeId;
        await db.SaveChangesAsync();
        await GuardFor(db).EnforceActivationAsync(t, id, Ctx(t), default);
    }

    [Theory]
    [InlineData("Abubakr Ahmed", "أبو بكر أحمد")]
    [InlineData("Abu Bakr Ahmed", "أبو بكر أحمد")]
    [InlineData("Abdulrahman Ali", "عبد الرحمن علي")]
    [InlineData("Abdurrahman Ali", "عبد الرحمن علي")]
    [InlineData("Abdullah Ali", "عبد الله علي")]
    [InlineData("Bakr Ali", "بكر علي")]
    public void JoinedAbuAndAbdForms_KeepTheirFullReading(string name, string expected) =>
        new TransliterationService().ToArabicPersonName(name).Should().Be(expected);
}
