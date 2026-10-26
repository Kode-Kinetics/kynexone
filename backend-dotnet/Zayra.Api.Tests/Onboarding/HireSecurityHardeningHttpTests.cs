using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Onboarding;

/// <summary>
/// Hire lifecycle access and pay-approval rules through the real pipeline (SQLite host, same fixture as
/// <see cref="HireIntegrityHttpTests"/>). Each test mints its own users and seeds its own records.
/// </summary>
public class HireSecurityHardeningHttpTests : IClassFixture<HireIntegrityHttpFixture>
{
    private const string Iban1 = "SA0380000000608010167519";
    private const string Iban2 = "SA4420000001234567891234";
    private const string PayGap = "pay:compensationUnapproved";
    private readonly HireIntegrityHttpFixture _fx;
    public HireSecurityHardeningHttpTests(HireIntegrityHttpFixture fixture) => _fx = fixture;

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    private object Body(string name, string? iban = null, Guid? company = null) => new Dictionary<string, object?>
    {
        ["englishName"] = name,
        ["companyId"] = company ?? _fx.CompanyId,
        ["payrollProfile"] = iban is null ? null : new { iban, bankName = "Test Bank", paymentMethod = "BankTransfer" },
    };

    private async Task<(int Id, string Name)> CreateDraftAsync(string token, string? iban = null)
    {
        var name = $"Sec Hire {Guid.NewGuid():N}";
        using var r = await _fx.SendAsync(HttpMethod.Post, "/api/employees", Body(name, iban), token);
        r.StatusCode.Should().Be(HttpStatusCode.Created, await r.Content.ReadAsStringAsync());
        return ((await Json(r)).GetProperty("id").GetInt32(), name);
    }

    private async Task<HttpResponseMessage> AutosaveAsync(string token, int id, string name, string? iban)
    {
        var current = await UpdatedAtAsync(id);
        return await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}/hire-draft",
            new { baseUpdatedAtUtc = current, request = Body(name, iban) }, token);
    }

    private async Task<DateTime> UpdatedAtAsync(int id)
    {
        using var s = _fx.Host.Services.CreateScope();
        var e = await s.ServiceProvider.GetRequiredService<ZayraDbContext>().Employees.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == id);
        return e.UpdatedAtUtc ?? e.CreatedAtUtc;
    }

    private async Task<int> SeedAsync(Guid? companyId, string status, string? iban = null, Guid? createdBy = null)
    {
        using var s = _fx.Host.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var e = new Employee
        {
            TenantId = _fx.TenantId, CompanyId = companyId, EmployeeCode = $"SEC-{Guid.NewGuid():N}"[..16], FullName = "Seeded Person",
            EnglishName = "Seeded Person", Status = status, JoiningDate = DateTime.UtcNow.Date, CreatedBy = createdBy,
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5),
        };
        db.Employees.Add(e);
        await db.SaveChangesAsync();
        if (iban is not null)
        {
            db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile { TenantId = _fx.TenantId, EmployeeId = e.Id, Iban = iban, BankName = "Test Bank", SalaryCurrency = "SAR" });
            await db.SaveChangesAsync();
        }
        return e.Id;
    }

    private async Task<(int Open, int Resolved)> GapsAsync(int id)
    {
        using var s = _fx.Host.Services.CreateScope();
        var gaps = await s.ServiceProvider.GetRequiredService<ZayraDbContext>().EmployeeImportGaps.IgnoreQueryFilters()
            .Where(g => g.EmployeeId == id && g.GapType == PayGap).ToListAsync();
        return (gaps.Count(g => g.ResolvedAtUtc == null), gaps.Count(g => g.ResolvedAtUtc != null));
    }

    private Task<HttpResponseMessage> ApproveAsync(string token, int id) =>
        _fx.SendAsync(HttpMethod.Post, $"/api/employees/{id}/compensation/approve", new { note = "Checked against the offer letter" }, token);

    // ── CD38: a company-less employee is invisible to a scoped caller on every hire route ─────────
    [Theory]
    [InlineData("PUT", "hire-draft")]
    [InlineData("DELETE", "draft")]
    [InlineData("POST", "rehire")]
    [InlineData("GET", "hire-review")]
    [InlineData("POST", "compensation/approve")]
    public async Task CompanyLessEmployee_IsNotFound_ForAScopedCaller_OnEveryHireRoute(string verb, string route)
    {
        var id = await SeedAsync(companyId: null, status: "Terminated");
        var scoped = await _fx.NewScopedUserAsync("cd38", _fx.CompanyId, "employees.write", "employees.read", "payroll.approve", "employees.sensitive");
        object? body = route switch
        {
            "hire-draft" => new { baseUpdatedAtUtc = (DateTime?)null, request = Body("Anyone", null) },
            "rehire" => new { startDate = DateTime.UtcNow.Date, reason = "Returning after a long break" },
            "compensation/approve" => new { note = "Checked" },
            _ => null,
        };
        using var r = await _fx.SendAsync(new HttpMethod(verb), $"/api/employees/{id}/{route}", body, scoped);
        r.StatusCode.Should().Be(HttpStatusCode.NotFound, $"{verb} {route}");
    }

    [Fact]
    public async Task CompanyLessEmployee_IsStillReadable_ByAGroupLevelCaller()
    {
        var id = await SeedAsync(companyId: null, status: "Draft");
        var group = await _fx.NewUserAsync("cd38g", "employees.read");
        using var r = await _fx.SendAsync(HttpMethod.Get, $"/api/employees/{id}/hire-review", null, group);
        r.StatusCode.Should().NotBe(HttpStatusCode.NotFound);
    }

    // ── CD37: every pay write on a non-Active employee opens or re-opens the gap ──────────────────
    [Fact]
    public async Task Autosave_AddingPay_ToAHireCreatedWithoutPay_OpensTheGap()
    {
        var a = await _fx.NewUserAsync("cd37a", "employees.write", "employees.read");
        var (id, name) = await CreateDraftAsync(a);
        (await GapsAsync(id)).Open.Should().Be(0);
        using var save = await AutosaveAsync(a, id, name, Iban1);
        save.StatusCode.Should().Be(HttpStatusCode.OK, await save.Content.ReadAsStringAsync());
        (await GapsAsync(id)).Open.Should().Be(1);
    }

    [Fact]
    public async Task Autosave_ChangingPay_AfterApproval_ReopensTheGap_ButAnUnchangedSaveDoesNot()
    {
        var a = await _fx.NewUserAsync("cd37b", "employees.write", "employees.read");
        var finance = await _fx.NewUserAsync("cd37bf", "payroll.approve", "employees.sensitive", "employees.read");
        var (id, name) = await CreateDraftAsync(a, Iban1);
        (await ApproveAsync(finance, id)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await GapsAsync(id)).Open.Should().Be(0);

        using var same = await AutosaveAsync(a, id, name, Iban1);
        same.StatusCode.Should().Be(HttpStatusCode.OK, await same.Content.ReadAsStringAsync());
        (await GapsAsync(id)).Open.Should().Be(0, "the approved figures did not change");

        using var changed = await AutosaveAsync(a, id, name, Iban2);
        changed.StatusCode.Should().Be(HttpStatusCode.OK, await changed.Content.ReadAsStringAsync());
        (await GapsAsync(id)).Open.Should().Be(1, "Finance approved a different account");
    }

    [Fact]
    public async Task Activation_ComparesTheLivePay_WithTheApprovedPay()
    {
        var a = await _fx.NewUserAsync("cd37c", "employees.write", "employees.read");
        var finance = await _fx.NewUserAsync("cd37cf", "payroll.approve", "employees.sensitive", "employees.read");
        var (id, _) = await CreateDraftAsync(a, Iban1);
        (await ApproveAsync(finance, id)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PayBlockerListedAsync(id)).Should().BeFalse("the stored account is the approved one");

        // A write that bypasses every service hook: the activation gate must still notice.
        using (var s = _fx.Host.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<ZayraDbContext>();
            var profile = await db.EmployeePayrollProfiles.IgnoreQueryFilters().SingleAsync(p => p.EmployeeId == id);
            profile.Iban = Iban2;
            await db.SaveChangesAsync();
        }
        (await PayBlockerListedAsync(id)).Should().BeTrue("the stored account is no longer the approved one");
    }

    private async Task<bool> PayBlockerListedAsync(int id)
    {
        using var s = _fx.Host.Services.CreateScope();
        var guard = s.ServiceProvider.GetRequiredService<IEmployeeActivationGuard>();
        var snapshot = await guard.BuildSnapshotAsync(_fx.TenantId, id, default);
        snapshot.Should().NotBeNull();
        try
        {
            await guard.EnsureActivatableAsync(_fx.TenantId, _fx.CompanyId, snapshot!, new RequestContext("127.0.0.1", "test", Guid.NewGuid(), _fx.TenantId), default);
            return false;
        }
        catch (EmployeeActivationBlockedException ex)
        {
            return ex.Readiness.Blocking.Any(b => b.Key == PayGap);
        }
    }

    [Fact]
    public async Task Approver_CannotBeAnyoneWhoWrotePay_SinceTheLastApproval()
    {
        var a = await _fx.NewUserAsync("cd39a", "employees.write", "employees.read");
        var editor = await _fx.NewUserAsync("cd39b", "employees.write", "employees.read", "payroll.approve", "employees.sensitive");
        var third = await _fx.NewUserAsync("cd39c", "payroll.approve", "employees.sensitive", "employees.read");
        var (id, name) = await CreateDraftAsync(a, Iban1);

        // Another editor changes the account on the same draft, then the creator edits again.
        (await AutosaveAsync(editor, id, name, Iban2)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await AutosaveAsync(a, id, name, Iban1)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var byEditor = await ApproveAsync(editor, id);
        byEditor.StatusCode.Should().Be(HttpStatusCode.Forbidden, "an earlier editor of the figures cannot approve them");
        (await Json(byEditor)).GetProperty("error").GetString().Should().Be("second_person_required");
        (await ApproveAsync(third, id)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Import_OfAHireOutsideTheActiveWorkforce_WithBankDetails_OpensThePayGap()
    {
        var token = await _fx.NewUserAsync("cd37i", "employees.bulk_import", "employees.write", "employees.read", "employees.sensitive");
        var name = $"Imported Person {Guid.NewGuid():N}"[..28];
        var csv = $"FullName,Nationality,CompanyLegalName,IBAN\n{name},Indian,Hire integrity A,{Iban1}\n";
        using var r = await _fx.SendAsync(HttpMethod.Post, "/api/employees/import", new { csvContent = csv }, token);
        r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        using var s = _fx.Host.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var id = await db.Employees.IgnoreQueryFilters().Where(e => e.FullName == name).Select(e => e.Id).SingleAsync();
        (await GapsAsync(id)).Open.Should().Be(1);
    }

    // ── CD40: rehire validates references, the start-date window, and re-opens the pay gap ─────────
    [Fact]
    public async Task Rehire_WithAGradeFromAnotherTenant_Is422_InvalidReference()
    {
        var token = await _fx.NewUserAsync("cd40a", "employees.write", "employees.read");
        var id = await SeedAsync(_fx.CompanyId, "Terminated");
        var foreign = await SeedGradeAsync(_fx.OtherTenantId, _fx.ForeignTenantCompanyId, active: true);
        using var r = await Rehire(token, id, new { startDate = DateTime.UtcNow.Date, gradeId = foreign, reason = "Returning after a long break" });
        r.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, await r.Content.ReadAsStringAsync());
        (await Json(r)).GetProperty("code").GetString().Should().Be("invalid_reference");
        (await _fx.StatusOfAsync(id)).Should().Be("Terminated");
    }

    [Fact]
    public async Task Rehire_WithAnInactiveGrade_Is422_InvalidReference()
    {
        var token = await _fx.NewUserAsync("cd40b", "employees.write", "employees.read");
        var id = await SeedAsync(_fx.CompanyId, "Terminated");
        var inactive = await SeedGradeAsync(_fx.TenantId, _fx.CompanyId, active: false);
        using var r = await Rehire(token, id, new { startDate = DateTime.UtcNow.Date, gradeId = inactive, reason = "Returning after a long break" });
        r.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, await r.Content.ReadAsStringAsync());
        (await Json(r)).GetProperty("code").GetString().Should().Be("invalid_reference");
    }

    [Theory]
    [InlineData(-18600)]
    [InlineData(800)]
    public async Task Rehire_StartDateOutsideTheWindow_IsValidationFailed(int daysFromToday)
    {
        var token = await _fx.NewUserAsync("cd40c", "employees.write", "employees.read");
        var id = await SeedAsync(_fx.CompanyId, "Terminated");
        using var r = await Rehire(token, id, new { startDate = DateTime.UtcNow.Date.AddDays(daysFromToday), reason = "Returning after a long break" });
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest, await r.Content.ReadAsStringAsync());
        (await Json(r)).GetProperty("code").GetString().Should().Be("validation_failed");
        (await _fx.StatusOfAsync(id)).Should().Be("Terminated");
    }

    [Fact]
    public async Task Rehire_OfSomeoneWithBankDetails_OpensThePayGap()
    {
        var token = await _fx.NewUserAsync("cd40d", "employees.write", "employees.read");
        var id = await SeedAsync(_fx.CompanyId, "Terminated", Iban1);
        (await GapsAsync(id)).Open.Should().Be(0);
        using var r = await Rehire(token, id, new { startDate = DateTime.UtcNow.Date, reason = "Returning after a long break" });
        r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        (await GapsAsync(id)).Open.Should().Be(1);
    }

    private Task<HttpResponseMessage> Rehire(string token, int id, object body) =>
        _fx.SendAsync(HttpMethod.Post, $"/api/employees/{id}/rehire", body, token);

    private async Task<Guid> SeedGradeAsync(Guid tenantId, Guid companyId, bool active)
    {
        using var s = _fx.Host.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var g = new Grade { TenantId = tenantId, CompanyId = companyId, Code = $"G{Guid.NewGuid():N}"[..8], Name = "Grade", IsActive = active };
        db.Grades.Add(g);
        await db.SaveChangesAsync();
        return g.Id;
    }

    // ── CD41: the override permission reaches the guard ──────────────────────────────────────────
    private object InvalidChecksumHire(string name) => new Dictionary<string, object?>
    {
        ["englishName"] = name,
        ["companyId"] = _fx.CompanyId,
        ["complianceRecords"] = new[] { new { countryCode = "SA", fieldKey = "iqama_number", fieldLabel = "Iqama", fieldValue = "2000000009", isSensitive = true, isRequired = false } },
        ["acknowledgedWarnings"] = new[] { new { code = "id_checksum_invalid", reason = "Confirmed against the original card" } },
    };

    [Fact]
    public async Task ValidationOverride_HrManagerWithThePermission_Succeeds()
    {
        var manager = await _fx.NewUserAsync("cd41a", "employees.write", "employees.read", "employees.override_validation");
        using var r = await _fx.SendAsync(HttpMethod.Post, "/api/employees", InvalidChecksumHire($"Override {Guid.NewGuid():N}"), manager);
        r.StatusCode.Should().Be(HttpStatusCode.Created, await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ValidationOverride_HrOfficerWithoutThePermission_IsRefused()
    {
        var officer = await _fx.NewUserAsync("cd41b", "employees.write", "employees.read");
        using var r = await _fx.SendAsync(HttpMethod.Post, "/api/employees", InvalidChecksumHire($"Override {Guid.NewGuid():N}"), officer);
        r.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, await r.Content.ReadAsStringAsync());
        (await Json(r)).GetProperty("code").GetString().Should().Be("override_not_permitted");
    }

    // ── CD42: nationality confirm ────────────────────────────────────────────────────────────────
    [Fact]
    public async Task NationalityConfirm_NeedsAReason_AndALegalState_AndWritesHistoryAndAudit()
    {
        var token = await _fx.NewUserAsync("cd42n", "employees.write", "employees.read");
        var id = await SeedAsync(_fx.CompanyId, "Draft");
        await SetNationalityStateAsync(id, "ProposedAuto", "Egyptian", "EG");

        using var noReason = await _fx.SendAsync(HttpMethod.Post, $"/api/employees/{id}/nationality/confirm", new { nationalityCode = "EG" }, token);
        noReason.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await Json(noReason)).GetProperty("code").GetString().Should().Be("override_reason_required");

        using var ok = await _fx.SendAsync(HttpMethod.Post, $"/api/employees/{id}/nationality/confirm",
            new { nationalityCode = "EG", reason = "Confirmed from the passport copy" }, token);
        ok.StatusCode.Should().Be(HttpStatusCode.NoContent, await ok.Content.ReadAsStringAsync());
        (await NationalityHistoryCountAsync(id)).Should().Be(1);
        (await _fx.AuditPayloadsAsync("employee.nationality_confirmed")).Should().NotBeEmpty();

        // Confirmed already: a second confirm is not a legal transition.
        using var again = await _fx.SendAsync(HttpMethod.Post, $"/api/employees/{id}/nationality/confirm",
            new { nationalityCode = "SA", reason = "Changing my mind afterwards" }, token);
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    private async Task SetNationalityStateAsync(int id, string state, string text, string code)
    {
        using var s = _fx.Host.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var e = await db.Employees.IgnoreQueryFilters().SingleAsync(x => x.Id == id);
        e.NationalityMappingState = state; e.Nationality = text; e.NationalityCode = code;
        await db.SaveChangesAsync();
    }

    private async Task<int> NationalityHistoryCountAsync(int id)
    {
        using var s = _fx.Host.Services.CreateScope();
        return await s.ServiceProvider.GetRequiredService<ZayraDbContext>().EmployeeHistories.IgnoreQueryFilters()
            .CountAsync(h => h.EmployeeId == id && h.FieldName == "Nationality");
    }

    // ── CD42: idempotency and autosave concurrency ────────────────────────────────────────────────
    [Fact]
    public async Task Create_SameKey_SameName_DifferentDetails_Is422()
    {
        var token = await _fx.NewUserAsync("cd42i", "employees.write", "employees.read");
        var key = Guid.NewGuid().ToString();
        var name = $"Same Name {Guid.NewGuid():N}";
        using var first = await _fx.SendAsync(HttpMethod.Post, "/api/employees", Body(name, Iban1), token, idempotencyKey: key);
        first.StatusCode.Should().Be(HttpStatusCode.Created, await first.Content.ReadAsStringAsync());
        using var retry = await _fx.SendAsync(HttpMethod.Post, "/api/employees", Body(name, Iban1), token, idempotencyKey: key);
        retry.StatusCode.Should().Be(HttpStatusCode.OK, "an identical retry is a replay");
        using var different = await _fx.SendAsync(HttpMethod.Post, "/api/employees", Body(name, Iban2), token, idempotencyKey: key);
        different.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await Json(different)).GetProperty("code").GetString().Should().Be("idempotency_key_reuse_mismatch");
    }

    [Fact]
    public async Task Create_KeyOfADiscardedDraft_Is422_NotAPermanent404()
    {
        var token = await _fx.NewUserAsync("cd42d", "employees.write", "employees.read");
        var key = Guid.NewGuid().ToString();
        var name = $"Discarded {Guid.NewGuid():N}";
        using var first = await _fx.SendAsync(HttpMethod.Post, "/api/employees", Body(name), token, idempotencyKey: key);
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await Json(first)).GetProperty("id").GetInt32();
        using var discard = await _fx.SendAsync(HttpMethod.Delete, $"/api/employees/{id}/draft", null, token);
        discard.StatusCode.Should().Be(HttpStatusCode.NoContent, await discard.Content.ReadAsStringAsync());
        using var replay = await _fx.SendAsync(HttpMethod.Post, "/api/employees", Body(name), token, idempotencyKey: key);
        replay.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await Json(replay)).GetProperty("code").GetString().Should().Be("idempotency_key_reuse_mismatch");
    }

    [Fact]
    public async Task Autosave_TwoSavesFromTheSameBase_OneWins_OtherIs409()
    {
        var token = await _fx.NewUserAsync("cd42c", "employees.write", "employees.read");
        var (id, name) = await CreateDraftAsync(token);
        var basis = await UpdatedAtAsync(id);
        object Save(string suffix) => new { baseUpdatedAtUtc = basis, request = Body(name + suffix) };
        var one = _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}/hire-draft", Save(" A"), token);
        var two = _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}/hire-draft", Save(" B"), token);
        var results = await Task.WhenAll(one, two);
        results.Select(r => r.StatusCode).OrderBy(s => (int)s).Should().Equal(HttpStatusCode.OK, HttpStatusCode.Conflict);
    }
}
