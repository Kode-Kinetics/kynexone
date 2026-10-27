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

/// <summary>WP-P3 through the real pipeline (auth, scope, filters). SQLite host, like the P1 hire integrity tests.</summary>
public class OnboardingTemplatesHttpTests : IClassFixture<OnboardingTemplatesFixture>
{
    private readonly OnboardingTemplatesFixture _fx;
    public OnboardingTemplatesHttpTests(OnboardingTemplatesFixture fixture) => _fx = fixture;

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();
    private const string Manage = "onboarding.templates.manage";

    private static object Task_(string title, string owner = "HR", int offset = 0, string cls = "any", bool blocks = false, bool evidence = false, string? titleAr = null) => new
    { title, titleAr, description = "", ownerRole = owner, dueOffsetDays = offset, blocksActivation = blocks, condition = new { nationalityClass = cls }, isStatutory = false, evidenceRequired = evidence };

    private async Task<Guid> NewChecklistAsync(string token, Guid company, string name, bool isDefault, params object[] tasks)
    {
        using var r = await _fx.SendAsync(HttpMethod.Post, "/api/onboarding/checklist-templates", new { name, companyId = company, isDefault, tasks }, token);
        r.StatusCode.Should().Be(HttpStatusCode.Created, await r.Content.ReadAsStringAsync());
        return (await Json(r)).GetProperty("id").GetGuid();
    }

    private object Hire(string name, string nationality, Guid? templateId = null, Guid? company = null, DateTime? start = null) => new Dictionary<string, object?>
    {
        ["englishName"] = name, ["companyId"] = company ?? _fx.CompanyId, ["nationalityCode"] = nationality, ["hireTemplateId"] = templateId,
        ["joiningDate"] = start ?? new DateTime(2026, 11, 8, 0, 0, 0, DateTimeKind.Utc),
    };

    private async Task<(Guid TemplateId, Guid ChecklistId)> StandardTemplateAsync(string token, string label)
    {
        var checklist = await NewChecklistAsync(token, _fx.CompanyId, "Checklist " + label, false,
            Task_("Open mailbox", "IT", -1), Task_("Register with GOSI", "Gov", 0), Task_("Issue Iqama via Muqeem", "Gov", 30, "expat"), Task_("Saudi-only briefing", "HR", 1, "saudi", blocks: true));
        using var r = await _fx.SendAsync(HttpMethod.Post, "/api/onboarding/hire-templates", new { name = "Template " + label, companyId = _fx.CompanyId, checklistId = checklist, defaults = new { probationDays = 90 } }, token);
        r.StatusCode.Should().Be(HttpStatusCode.Created, await r.Content.ReadAsStringAsync());
        return ((await Json(r)).GetProperty("id").GetGuid(), checklist);
    }

    private async Task<List<(string Title, string Owner, DateOnly? Due, bool Blocks)>> TasksOfAsync(int employeeId)
    {
        using var s = _fx.Host.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var pid = await db.Employees.IgnoreQueryFilters().Where(e => e.Id == employeeId).Select(e => e.PublicId).SingleAsync();
        return (await db.OnboardingTasks.IgnoreQueryFilters().Where(t => t.EmployeeId == pid).OrderBy(t => t.OrderIndex).ToListAsync())
            .Select(t => (t.TaskTitle, t.OwnerRole, t.DueDate, t.BlocksActivation)).ToList();
    }

    [Fact]
    public async Task TemplatesCrud_RequiresManagePermission()
    {
        var reader = await _fx.NewUserAsync("reader", "HR Officer", "employees.read");
        var manager = await _fx.NewUserAsync("mgr", "HR Manager", Manage);
        foreach (var path in new[] { "/api/onboarding/checklist-templates", "/api/onboarding/checklist-templates/starters" })
        {
            using var denied = await _fx.SendAsync(HttpMethod.Get, path, null, reader);
            denied.StatusCode.Should().Be(HttpStatusCode.Forbidden, path);
            using var ok = await _fx.SendAsync(HttpMethod.Get, path, null, manager);
            ok.StatusCode.Should().Be(HttpStatusCode.OK, path);
        }
        using var post = await _fx.SendAsync(HttpMethod.Post, "/api/onboarding/hire-templates", new { name = "x", companyId = _fx.CompanyId }, reader);
        post.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var unauth = await _fx.SendAsync(HttpMethod.Get, "/api/onboarding/checklist-templates", null, null);
        unauth.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        // The picker needs to read hire templates: whoever may hire may list and resolve them, but not change them.
        var hirer = await _fx.NewUserAsync("hirer", "HR Officer", "employees.write");
        using var list = await _fx.SendAsync(HttpMethod.Get, "/api/onboarding/hire-templates", null, hirer);
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        using var write = await _fx.SendAsync(HttpMethod.Delete, $"/api/onboarding/hire-templates/{Guid.NewGuid()}", null, hirer);
        write.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Checklist_Validation_RefusesBadOwnerDuplicateTitleAndUnknownCondition()
    {
        var token = await _fx.NewUserAsync("val", "HR Manager", Manage);
        async Task<HttpResponseMessage> Post(params object[] tasks) => await _fx.SendAsync(HttpMethod.Post, "/api/onboarding/checklist-templates", new { name = "V", companyId = _fx.CompanyId, tasks }, token);
        (await Post(Task_("A", "Janitor"))).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await Post(Task_("A"), Task_("A"))).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await Post(Task_("A", cls: "martian"))).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await Post(Task_("A", offset: 4000))).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        using var noCompany = await _fx.SendAsync(HttpMethod.Post, "/api/onboarding/checklist-templates", new { name = "V", tasks = Array.Empty<object>() }, token);
        noCompany.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        using var foreign = await _fx.SendAsync(HttpMethod.Post, "/api/onboarding/checklist-templates", new { name = "V", companyId = _fx.ForeignCompanyId, tasks = Array.Empty<object>() }, token);
        foreign.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var body = await Json(await Post(Task_("A", "Janitor")));
        body.GetProperty("titleKey").GetString().Should().Be("onboarding.error.validation_failed");
    }

    // AC64 + AC65 + CD4
    [Fact]
    public async Task Create_WithHireTemplate_RaisesTasksWithOwnersAndDues_SaudiGetsNoIqama()
    {
        var token = await _fx.NewUserAsync("hire1", "HR Manager", Manage, "employees.write", "employees.read");
        var (templateId, _) = await StandardTemplateAsync(token, "ac64");

        using var saudi = await _fx.SendAsync(HttpMethod.Post, "/api/employees", Hire("Saudi Hire", "SA", templateId), token);
        saudi.StatusCode.Should().Be(HttpStatusCode.Created, await saudi.Content.ReadAsStringAsync());
        var tasks = await TasksOfAsync((await Json(saudi)).GetProperty("id").GetInt32());
        tasks.Select(t => t.Title).Should().Equal("Open mailbox", "Register with GOSI", "Saudi-only briefing");
        tasks.Select(t => t.Owner).Should().Equal("IT", "Gov", "HR");
        tasks.Select(t => t.Due).Should().Equal(new DateOnly(2026, 11, 7), new DateOnly(2026, 11, 8), new DateOnly(2026, 11, 9));
        tasks.Where(t => t.Title != "Saudi-only briefing").Should().OnlyContain(t => !t.Blocks);

        using var expat = await _fx.SendAsync(HttpMethod.Post, "/api/employees", Hire("Expat Hire", "PK", templateId), token);
        expat.StatusCode.Should().Be(HttpStatusCode.Created, await expat.Content.ReadAsStringAsync());
        (await TasksOfAsync((await Json(expat)).GetProperty("id").GetInt32())).Select(t => t.Title)
            .Should().Equal("Open mailbox", "Register with GOSI", "Issue Iqama via Muqeem");

        using var unknown = await _fx.SendAsync(HttpMethod.Post, "/api/employees", Hire("Unknown Hire", "", templateId), token);
        (await TasksOfAsync((await Json(unknown)).GetProperty("id").GetInt32())).Select(t => t.Title).Should().Equal("Open mailbox", "Register with GOSI");
    }

    [Fact]
    public async Task Create_WithTemplate_GeneratesTasksOnce_OnReplayedIdempotencyKey()
    {
        var token = await _fx.NewUserAsync("idem", "HR Manager", Manage, "employees.write", "employees.read");
        var (templateId, _) = await StandardTemplateAsync(token, "idem");
        var key = Guid.NewGuid().ToString();
        using var first = await _fx.SendAsync(HttpMethod.Post, "/api/employees", Hire("Replayed Hire", "SA", templateId), token, key);
        first.StatusCode.Should().Be(HttpStatusCode.Created, await first.Content.ReadAsStringAsync());
        using var replay = await _fx.SendAsync(HttpMethod.Post, "/api/employees", Hire("Replayed Hire", "SA", templateId), token, key);
        replay.IsSuccessStatusCode.Should().BeTrue(await replay.Content.ReadAsStringAsync());
        var id = (await Json(first)).GetProperty("id").GetInt32();
        (await Json(replay)).GetProperty("id").GetInt32().Should().Be(id);
        (await TasksOfAsync(id)).Should().HaveCount(3);
    }

    // The company default checklist applies when no template is named.
    [Fact]
    public async Task Create_WithoutTemplate_UsesTheCompanyDefaultChecklist()
    {
        var token = await _fx.NewUserAsync("dflt", "HR Manager", Manage, "employees.write", "employees.read");
        await NewChecklistAsync(token, _fx.OtherCompanyId, "Other company default", true, Task_("Other company task"));
        using var r = await _fx.SendAsync(HttpMethod.Post, "/api/employees", Hire("No Template Hire", "SA", company: _fx.OtherCompanyId), token);
        r.StatusCode.Should().Be(HttpStatusCode.Created, await r.Content.ReadAsStringAsync());
        (await TasksOfAsync((await Json(r)).GetProperty("id").GetInt32())).Select(t => t.Title).Should().Equal("Other company task");
    }

    // CD54 end to end: moving the start date through the draft save moves open due dates.
    [Fact]
    public async Task UpdatingTheJoiningDate_MovesOpenTaskDues()
    {
        var token = await _fx.NewUserAsync("rebase", "HR Manager", Manage, "employees.write", "employees.read");
        var (templateId, _) = await StandardTemplateAsync(token, "rebase");
        using var created = await _fx.SendAsync(HttpMethod.Post, "/api/employees", Hire("Moved Start", "SA", templateId), token);
        var emp = await Json(created);
        var id = emp.GetProperty("id").GetInt32();
        var basis = emp.GetProperty("updatedAtUtc").ValueKind == JsonValueKind.Null ? emp.GetProperty("createdAtUtc").GetDateTime() : emp.GetProperty("updatedAtUtc").GetDateTime();
        using var put = await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}/hire-draft", new
        {
            baseUpdatedAtUtc = basis,
            request = new Dictionary<string, object?> { ["englishName"] = "Moved Start", ["companyId"] = _fx.CompanyId, ["nationalityCode"] = "SA", ["joiningDate"] = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc) },
        }, token);
        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        (await TasksOfAsync(id)).Select(t => t.Due).Should().Equal(new DateOnly(2026, 11, 30), new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 2));
    }

    // AC68
    [Fact]
    public async Task TemplateEdit_DoesNotMutateExistingInstances()
    {
        var token = await _fx.NewUserAsync("edit", "HR Manager", Manage, "employees.write", "employees.read");
        var (templateId, checklistId) = await StandardTemplateAsync(token, "ac68");
        using var created = await _fx.SendAsync(HttpMethod.Post, "/api/employees", Hire("Before Edit", "SA", templateId), token);
        var id = (await Json(created)).GetProperty("id").GetInt32();
        var before = await TasksOfAsync(id);

        using var get = await _fx.SendAsync(HttpMethod.Get, $"/api/onboarding/checklist-templates/{checklistId}", null, token);
        var doc = await Json(get);
        var edited = doc.GetProperty("tasks").EnumerateArray().Select(t => (object)new
        {
            id = t.GetProperty("id").GetGuid(), title = "RENAMED " + t.GetProperty("title").GetString(), ownerRole = "Finance", dueOffsetDays = 99, blocksActivation = true,
            condition = new { nationalityClass = "any" }, isStatutory = false, evidenceRequired = false,
        }).ToArray();
        using var put = await _fx.SendAsync(HttpMethod.Put, $"/api/onboarding/checklist-templates/{checklistId}", new { name = "Edited", companyId = _fx.CompanyId, isDefault = false, tasks = edited }, token);
        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());

        (await TasksOfAsync(id)).Should().Equal(before, "tasks already raised are copies; the template edit never reaches them");
    }

    [Fact]
    public async Task PutChecklist_RemovesDroppedTasks_ReusesATitle_AndKeepsOrder()
    {
        var token = await _fx.NewUserAsync("put", "HR Manager", Manage);
        var id = await NewChecklistAsync(token, _fx.CompanyId, "Put test", false, Task_("One"), Task_("Two"), Task_("Three"));
        using var put = await _fx.SendAsync(HttpMethod.Put, $"/api/onboarding/checklist-templates/{id}", new { name = "Put test", companyId = _fx.CompanyId, tasks = new[] { Task_("Three"), Task_("One"), Task_("Four") } }, token);
        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        (await Json(put)).GetProperty("tasks").EnumerateArray().Select(t => t.GetProperty("title").GetString()).Should().Equal("Three", "One", "Four");
        // Bring "Two" back: the retired row with that title is reused (the title is unique per checklist).
        using var back = await _fx.SendAsync(HttpMethod.Put, $"/api/onboarding/checklist-templates/{id}", new { name = "Put test", companyId = _fx.CompanyId, tasks = new[] { Task_("Two") } }, token);
        back.StatusCode.Should().Be(HttpStatusCode.OK, await back.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Create_WithHireTemplateId_ForeignTenant_And_OutOfScope_SameInvalidReference422()
    {
        var admin = await _fx.NewUserAsync("refadmin", "HR Manager", Manage, "employees.write", "employees.read");
        var foreignAdmin = await _fx.NewOtherTenantUserAsync("foreignadmin", "HR Manager", Manage);
        using var foreignT = await _fx.SendAsync(HttpMethod.Post, "/api/onboarding/hire-templates", new { name = "Foreign", companyId = _fx.ForeignCompanyId }, foreignAdmin);
        foreignT.StatusCode.Should().Be(HttpStatusCode.Created, await foreignT.Content.ReadAsStringAsync());
        var foreignId = (await Json(foreignT)).GetProperty("id").GetGuid();
        using var otherCo = await _fx.SendAsync(HttpMethod.Post, "/api/onboarding/hire-templates", new { name = "Other company", companyId = _fx.OtherCompanyId }, admin);
        var otherCoId = (await Json(otherCo)).GetProperty("id").GetGuid();

        // A caller scoped to the first company only.
        var scoped = await _fx.NewScopedUserAsync("scoped", _fx.CompanyId, "HR Officer", "employees.write", "employees.read");
        var texts = new List<string>();
        foreach (var id in new Guid?[] { Guid.NewGuid(), foreignId })
        {
            using var r = await _fx.SendAsync(HttpMethod.Post, "/api/employees", Hire("Nope", "SA", id), admin);
            r.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            texts.Add(await r.Content.ReadAsStringAsync());
        }
        using var outOfScope = await _fx.SendAsync(HttpMethod.Post, "/api/employees", Hire("Nope", "SA", otherCoId), scoped);
        outOfScope.StatusCode.Should().BeOneOf(HttpStatusCode.UnprocessableEntity, HttpStatusCode.Forbidden);
        // The template of another company, named on a hire in this company: same refusal, no oracle.
        using var wrongCompany = await _fx.SendAsync(HttpMethod.Post, "/api/employees", Hire("Nope", "SA", otherCoId), admin);
        wrongCompany.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        texts.Add(await wrongCompany.Content.ReadAsStringAsync());
        texts.Select(t => JsonDocument.Parse(t).RootElement.GetProperty("errors").GetProperty("hireTemplateId")[0].GetString()).Distinct().Should().Equal("invalid_reference");
        (await _fx.CountEmployeesNamedAsync("Nope")).Should().Be(0, "a refused hire creates nothing");
    }

    [Fact]
    public async Task HireTemplates_CrudResolveAndRemoveRestore()
    {
        var token = await _fx.NewUserAsync("ht", "HR Manager", Manage, "employees.write", "employees.read");
        var designation = await _fx.SeedAsync(db => { var d = new Designation { TenantId = _fx.TenantId, CompanyId = _fx.CompanyId, TitleEn = "Cashier", Code = "CSH", IsActive = true }; db.Designations.Add(d); return d.Id; });
        var wrongCompanyDesignation = await _fx.SeedAsync(db => { var d = new Designation { TenantId = _fx.TenantId, CompanyId = _fx.OtherCompanyId, TitleEn = "Other", Code = "OTH", IsActive = true }; db.Designations.Add(d); return d.Id; });

        using var bad = await _fx.SendAsync(HttpMethod.Post, "/api/onboarding/hire-templates", new { name = "Bad", companyId = _fx.CompanyId, matchDesignationId = wrongCompanyDesignation }, token);
        bad.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        using var unknownKey = await _fx.SendAsync(HttpMethod.Post, "/api/onboarding/hire-templates", new { name = "Bad2", companyId = _fx.CompanyId, defaults = new { salary = 9000 } }, token);
        unknownKey.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        using var created = await _fx.SendAsync(HttpMethod.Post, "/api/onboarding/hire-templates", new { name = "Cashier hire", companyId = _fx.CompanyId, matchDesignationId = designation, essCompletionSections = new[] { "id", "bank" }, defaults = new { employmentType = "Full-time", probationDays = 90 } }, token);
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var id = (await Json(created)).GetProperty("id").GetGuid();
        using var dupName = await _fx.SendAsync(HttpMethod.Post, "/api/onboarding/hire-templates", new { name = "CASHIER HIRE", companyId = _fx.CompanyId }, token);
        dupName.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Json(dupName)).GetProperty("code").GetString().Should().Be("template_name_taken");
        using var dupMatch = await _fx.SendAsync(HttpMethod.Post, "/api/onboarding/hire-templates", new { name = "Cashier again", companyId = _fx.CompanyId, matchDesignationId = designation }, token);
        dupMatch.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Json(dupMatch)).GetProperty("code").GetString().Should().Be("template_match_conflict");

        using var resolve = await _fx.SendAsync(HttpMethod.Get, $"/api/onboarding/hire-templates/resolve?companyId={_fx.CompanyId}&designationId={designation}", null, token);
        (await Json(resolve)).GetProperty("suggestedId").GetGuid().Should().Be(id);

        using var removed = await _fx.SendAsync(HttpMethod.Delete, $"/api/onboarding/hire-templates/{id}", null, token);
        var outcome = await Json(removed);
        outcome.GetProperty("outcome").GetString().Should().Be("deleted");
        outcome.GetProperty("used").GetInt32().Should().Be(0);
        using var resolveAfter = await _fx.SendAsync(HttpMethod.Get, $"/api/onboarding/hire-templates/resolve?companyId={_fx.CompanyId}&designationId={designation}", null, token);
        (await Json(resolveAfter)).GetProperty("candidates").GetArrayLength().Should().Be(0);
        using var restored = await _fx.SendAsync(HttpMethod.Post, $"/api/onboarding/hire-templates/{id}/restore", null, token);
        restored.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(restored)).GetProperty("isActive").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task HireTemplate_Used_IsTurnedOff_NotDeleted_AndRestorable()
    {
        var token = await _fx.NewUserAsync("used", "HR Manager", Manage, "employees.write", "employees.read");
        var (templateId, checklistId) = await StandardTemplateAsync(token, "used");
        using var hired = await _fx.SendAsync(HttpMethod.Post, "/api/employees", Hire("Used Hire", "SA", templateId), token);
        hired.StatusCode.Should().Be(HttpStatusCode.Created);
        var t = await Json(await _fx.SendAsync(HttpMethod.Delete, $"/api/onboarding/hire-templates/{templateId}", null, token));
        t.GetProperty("outcome").GetString().Should().Be("turnedOff");
        t.GetProperty("used").GetInt32().Should().BeGreaterThan(0);
        var c = await Json(await _fx.SendAsync(HttpMethod.Delete, $"/api/onboarding/checklist-templates/{checklistId}", null, token));
        c.GetProperty("outcome").GetString().Should().Be("turnedOff");
        using var restored = await _fx.SendAsync(HttpMethod.Post, $"/api/onboarding/checklist-templates/{checklistId}/restore", null, token);
        (await Json(restored)).GetProperty("isActive").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task CopyToSisterCompany_CopiesTasksAndMatchKeys_NoSharedRows()
    {
        var token = await _fx.NewUserAsync("copy", "HR Manager", Manage);
        var checklist = await NewChecklistAsync(token, _fx.CompanyId, "To copy", false, Task_("Copied A", "IT", -1), Task_("Copied B", "Gov", 3, "expat"));
        var desig = await _fx.SeedAsync(db => { var d = new Designation { TenantId = _fx.TenantId, CompanyId = _fx.CompanyId, TitleEn = "Driver", Code = "DRV", IsActive = true }; db.Designations.Add(d); return d.Id; });
        var sisterDesig = await _fx.SeedAsync(db => { var d = new Designation { TenantId = _fx.TenantId, CompanyId = _fx.OtherCompanyId, TitleEn = "Driver", Code = "DRV", IsActive = true }; db.Designations.Add(d); return d.Id; });
        var grade = await _fx.SeedAsync(db => { var g = new Grade { TenantId = _fx.TenantId, CompanyId = _fx.CompanyId, Name = "OnlyHere", Code = "ONLY", IsActive = true }; db.Grades.Add(g); return g.Id; });
        using var tpl = await _fx.SendAsync(HttpMethod.Post, "/api/onboarding/hire-templates", new { name = "Driver hire", companyId = _fx.CompanyId, matchDesignationId = desig, matchGradeId = grade, checklistId = checklist }, token);
        var tplId = (await Json(tpl)).GetProperty("id").GetGuid();

        using var copyList = await _fx.SendAsync(HttpMethod.Post, $"/api/onboarding/checklist-templates/{checklist}/copy-to/{_fx.OtherCompanyId}", null, token);
        copyList.StatusCode.Should().Be(HttpStatusCode.OK, await copyList.Content.ReadAsStringAsync());
        var copiedChecklist = await Json(copyList);
        copiedChecklist.GetProperty("id").GetGuid().Should().NotBe(checklist);
        copiedChecklist.GetProperty("companyId").GetGuid().Should().Be(_fx.OtherCompanyId);
        var copiedTaskIds = copiedChecklist.GetProperty("tasks").EnumerateArray().Select(t => t.GetProperty("id").GetGuid()).ToList();
        copiedTaskIds.Should().HaveCount(2);
        using var original = await _fx.SendAsync(HttpMethod.Get, $"/api/onboarding/checklist-templates/{checklist}", null, token);
        (await Json(original)).GetProperty("tasks").EnumerateArray().Select(t => t.GetProperty("id").GetGuid()).Should().NotIntersectWith(copiedTaskIds);

        using var copy = await _fx.SendAsync(HttpMethod.Post, $"/api/onboarding/hire-templates/{tplId}/copy-to/{_fx.OtherCompanyId}", null, token);
        copy.StatusCode.Should().Be(HttpStatusCode.OK, await copy.Content.ReadAsStringAsync());
        var body = await Json(copy);
        var t = body.GetProperty("template");
        t.GetProperty("id").GetGuid().Should().NotBe(tplId);
        t.GetProperty("companyId").GetGuid().Should().Be(_fx.OtherCompanyId);
        t.GetProperty("matchDesignationId").GetGuid().Should().Be(sisterDesig, "the designation is mapped to the sister company's own record by code");
        t.GetProperty("matchGradeId").ValueKind.Should().Be(JsonValueKind.Null, "a grade with no counterpart is dropped, never carried across");
        body.GetProperty("dropped").EnumerateArray().Select(x => x.GetString()).Should().Contain("matchGradeId");
        t.GetProperty("checklistId").GetGuid().Should().NotBe(checklist, "the checklist is copied too");
    }

    [Fact]
    public async Task KsaStarter_CopyCreates15TasksInTheChosenCompany_AndPreviewHonoursNationality()
    {
        var token = await _fx.NewUserAsync("ksa", "HR Manager", Manage);
        using var starters = await _fx.SendAsync(HttpMethod.Get, "/api/onboarding/checklist-templates/starters", null, token);
        (await Json(starters))[0].GetProperty("tasks").GetArrayLength().Should().Be(15);
        using var copy = await _fx.SendAsync(HttpMethod.Post, $"/api/onboarding/checklist-templates/starters/ksa/copy-to/{_fx.CompanyId}", null, token);
        copy.StatusCode.Should().Be(HttpStatusCode.OK, await copy.Content.ReadAsStringAsync());
        var doc = await Json(copy);
        doc.GetProperty("tasks").GetArrayLength().Should().Be(15);
        doc.GetProperty("tasks").EnumerateArray().Should().OnlyContain(t => !t.GetProperty("blocksActivation").GetBoolean());
        var id = doc.GetProperty("id").GetGuid();

        async Task<JsonElement> Preview(string cls) => await Json(await _fx.SendAsync(HttpMethod.Post, $"/api/onboarding/checklist-templates/{id}/preview", new { nationalityClass = cls, startDate = "2026-11-08" }, token));
        var saudi = await Preview("saudi");
        saudi.GetProperty("tasks").GetArrayLength().Should().Be(12);
        saudi.GetProperty("tasks").EnumerateArray().Select(t => t.GetProperty("title").GetString()).Should().NotContain(x => x!.Contains("Muqeem"));
        (await Preview("expat")).GetProperty("tasks").GetArrayLength().Should().Be(15);
        var noStart = await Json(await _fx.SendAsync(HttpMethod.Post, $"/api/onboarding/checklist-templates/{id}/preview", new { nationalityClass = "saudi" }, token));
        noStart.GetProperty("tasks").EnumerateArray().Should().OnlyContain(t => t.GetProperty("dueWhenStartSet").GetBoolean());
    }

    // AC66
    [Fact]
    public async Task Worklist_OnlyOwnerRolesOpenTasks_AcrossEmployees()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        var a = await _fx.SeedEmployeeAsync($"WL-A-{tag}", _fx.CompanyId);
        var b = await _fx.SeedEmployeeAsync($"WL-B-{tag}", _fx.CompanyId);
        await _fx.SeedTasksAsync(a, ("Payroll for A " + tag, "Payroll", "Pending"), ("IT for A " + tag, "IT", "Pending"), ("Payroll done " + tag, "Payroll", "Completed"));
        await _fx.SeedTasksAsync(b, ("Payroll for B " + tag, "Payroll", "InProgress"), ("HR for B " + tag, "HR", "Pending"));

        var payroll = await _fx.NewUserAsync("wl-pay", "Payroll Officer", "employees.read");
        using var r = await _fx.SendAsync(HttpMethod.Get, "/api/onboarding/worklist", null, payroll);
        r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        var titles = (await Json(r)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()!).Where(t => t.EndsWith(tag)).ToList();
        titles.Should().BeEquivalentTo(new[] { "Payroll for A " + tag, "Payroll for B " + tag }, "only their role's open tasks, across both employees");

        var hr = await _fx.NewUserAsync("wl-hr", "HR Officer", "employees.read");
        var hrTitles = (await Json(await _fx.SendAsync(HttpMethod.Get, "/api/onboarding/worklist", null, hr))).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()!).Where(t => t.EndsWith(tag)).ToList();
        hrTitles.Should().BeEquivalentTo(new[] { "HR for B " + tag });

        var admin = await _fx.NewUserAsync("wl-admin", "Admin", "employees.read");
        (await Json(await _fx.SendAsync(HttpMethod.Get, $"/api/onboarding/worklist?ownerRole=IT", null, admin))).GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("title").GetString()!).Where(t => t.EndsWith(tag)).Should().Equal("IT for A " + tag);
        using var badRole = await _fx.SendAsync(HttpMethod.Get, "/api/onboarding/worklist?ownerRole=Janitor", null, admin);
        badRole.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        using var noPerm = await _fx.SendAsync(HttpMethod.Get, "/api/onboarding/worklist", null, await _fx.NewUserAsync("wl-none", "HR Officer"));
        noPerm.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Worklist_ScopedUser_NoOtherCompanyTasks()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        var mine = await _fx.SeedEmployeeAsync($"SC-A-{tag}", _fx.CompanyId);
        var far = await _fx.SeedEmployeeAsync($"SC-B-{tag}", _fx.OtherCompanyId);
        await _fx.SeedTasksAsync(mine, ("Near " + tag, "HR", "Pending"));
        await _fx.SeedTasksAsync(far, ("Far " + tag, "HR", "Pending"));
        var scoped = await _fx.NewScopedUserAsync("wl-scoped", _fx.CompanyId, "HR Officer", "employees.read");
        var titles = (await Json(await _fx.SendAsync(HttpMethod.Get, "/api/onboarding/worklist", null, scoped))).GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("title").GetString()!).Where(t => t.EndsWith(tag)).ToList();
        titles.Should().Equal("Near " + tag);
        // Updating another company's task is the same 404 as a task that does not exist.
        var farTask = await _fx.TaskIdAsync("Far " + tag);
        using var patch = await _fx.SendAsync(HttpMethod.Patch, $"/api/onboarding/tasks/{farTask}", new { status = "Completed" }, scoped);
        patch.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CompleteTask_EvidenceRequired_Refused_AndDueOverrideNeedsReason()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        var e = await _fx.SeedEmployeeAsync($"EV-{tag}", _fx.CompanyId);
        await _fx.SeedTasksAsync(e, ("Needs proof " + tag, "HR", "Pending", true), ("Plain " + tag, "HR", "Pending", false));
        var token = await _fx.NewUserAsync("ev", "HR Officer", "employees.read", "employees.write");
        var proof = await _fx.TaskIdAsync("Needs proof " + tag);
        var plain = await _fx.TaskIdAsync("Plain " + tag);

        using var refused = await _fx.SendAsync(HttpMethod.Patch, $"/api/onboarding/tasks/{proof}", new { status = "Completed" }, token);
        refused.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await Json(refused)).GetProperty("code").GetString().Should().Be("task_evidence_required");

        var doc = await _fx.SeedDocumentAsync(e);
        var foreignDoc = await _fx.SeedDocumentAsync(await _fx.SeedEmployeeAsync($"EV2-{tag}", _fx.CompanyId));
        using var wrongDoc = await _fx.SendAsync(HttpMethod.Patch, $"/api/onboarding/tasks/{proof}", new { status = "Completed", evidenceDocumentId = foreignDoc }, token);
        wrongDoc.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, "evidence must be a document of the same employee");
        using var ok = await _fx.SendAsync(HttpMethod.Patch, $"/api/onboarding/tasks/{proof}", new { status = "Completed", evidenceDocumentId = doc }, token);
        ok.StatusCode.Should().Be(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync());

        using var noReason = await _fx.SendAsync(HttpMethod.Patch, $"/api/onboarding/tasks/{plain}", new { dueDate = "2027-01-01" }, token);
        noReason.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await Json(noReason)).GetProperty("code").GetString().Should().Be("due_override_reason_required");
        using var shortReason = await _fx.SendAsync(HttpMethod.Patch, $"/api/onboarding/tasks/{plain}", new { dueDate = "2027-01-01", dueOverrideReason = "no" }, token);
        shortReason.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        using var good = await _fx.SendAsync(HttpMethod.Patch, $"/api/onboarding/tasks/{plain}", new { dueDate = "2027-01-01", dueOverrideReason = "Agreed with the manager" }, token);
        good.StatusCode.Should().Be(HttpStatusCode.OK);
        // A person who neither owns the role nor may write cannot touch the task.
        var outsider = await _fx.NewUserAsync("ev-out", "Payroll Officer", "employees.read");
        using var nope = await _fx.SendAsync(HttpMethod.Patch, $"/api/onboarding/tasks/{plain}", new { status = "Completed" }, outsider);
        nope.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task LegacyRecruitmentOnboardingRoutes_Unchanged()
    {
        var token = await _fx.NewUserAsync("legacy", "HR Manager", Manage);
        using var list = await _fx.SendAsync(HttpMethod.Get, "/api/recruitment/onboarding/checklists", null, token);
        list.StatusCode.Should().Be(HttpStatusCode.OK, await list.Content.ReadAsStringAsync());
        (await Json(list)).ValueKind.Should().Be(JsonValueKind.Array);
        using var create = await _fx.SendAsync(HttpMethod.Post, "/api/recruitment/onboarding/checklists", new { code = "LEG-" + Guid.NewGuid().ToString("N")[..6], name = "Legacy checklist", description = "", applicableTo = "All" }, token);
        create.IsSuccessStatusCode.Should().BeTrue(await create.Content.ReadAsStringAsync());
    }
}

public sealed class OnboardingTemplatesFixture : IAsyncLifetime
{
    private SqliteConnection _anchor = null!;
    private HttpClient _client = null!;
    public AuthorizationPipelineHost Host { get; private set; } = null!;
    public Guid TenantId { get; private set; }
    public Guid CompanyId { get; private set; }
    public Guid OtherCompanyId { get; private set; }
    public Guid OtherTenantId { get; private set; }
    public Guid ForeignCompanyId { get; private set; }

    public async Task InitializeAsync()
    {
        var connection = $"Data Source=file:onb-templates-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _anchor = new SqliteConnection(connection);
        await _anchor.OpenAsync();
        Host = new AuthorizationPipelineHost(connection);
        _client = Host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        await db.Database.EnsureCreatedAsync();
        (TenantId, CompanyId, OtherCompanyId) = await SeedTenantAsync(db, "Onboarding templates A", true);
        (OtherTenantId, ForeignCompanyId, _) = await SeedTenantAsync(db, "Onboarding templates B", false);
        await TestApprovalConfig.EnsureDeciderAsync(db, TenantId);
    }

    private static async Task<(Guid Tenant, Guid Company, Guid Second)> SeedTenantAsync(ZayraDbContext db, string name, bool two)
    {
        var tenant = new Tenant { Name = name, Slug = $"onb-{Guid.NewGuid():N}", IsActive = true };
        db.Tenants.Add(tenant);
        var main = new Company { TenantId = tenant.Id, LegalNameEn = name, TradeName = name, CountryCode = "SA", IsActive = true, DefaultCurrency = "SAR" };
        db.Companies.Add(main);
        var second = new Company { TenantId = tenant.Id, LegalNameEn = name + " Two", TradeName = name + " Two", CountryCode = "SA", IsActive = true, DefaultCurrency = "SAR" };
        if (two) db.Companies.Add(second);
        db.TenantSubscriptions.Add(new TenantSubscription { TenantId = tenant.Id, Plan = "Enterprise", Status = "Active", MaxEmployees = 10_000 });
        await db.SaveChangesAsync();
        return (tenant.Id, main.Id, two ? second.Id : Guid.Empty);
    }

    public Task<string> NewUserAsync(string name, string role, params string[] permissions) => MintAsync(TenantId, name, role, null, permissions);
    public Task<string> NewScopedUserAsync(string name, Guid companyId, string role, params string[] permissions) => MintAsync(TenantId, name, role, companyId, permissions);
    public Task<string> NewOtherTenantUserAsync(string name, string role, params string[] permissions) => MintAsync(OtherTenantId, name, role, null, permissions);

    private async Task<string> MintAsync(Guid tenantId, string name, string roleName, Guid? scopedCompany, string[] permissions)
    {
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var email = $"{name}-{Guid.NewGuid():N}@onb.local";
        var role = await db.Roles.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Name == roleName);
        if (role is null) { role = new Role { TenantId = tenantId, Name = roleName, NormalizedName = roleName.ToUpperInvariant() }; db.Roles.Add(role); }
        var user = new User
        {
            TenantId = tenantId, Email = email, NormalizedEmail = email.ToUpperInvariant(), FullName = name, PasswordHash = "no-login-path",
            Status = "Active", AccessMode = "FullPortal", IsActive = true, IsEmailConfirmed = true, IsGroupScope = scopedCompany is null,
        };
        db.Users.Add(user);
        db.Set<UserRole>().Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        foreach (var p in permissions)
            db.Set<UserPermissionOverride>().Add(new UserPermissionOverride { TenantId = tenantId, UserId = user.Id, PermissionKey = p, Effect = "Allow", IsActive = true, Reason = "onboarding templates test" });
        if (scopedCompany is Guid cid)
            db.Set<UserEntityAccess>().Add(new UserEntityAccess { TenantId = tenantId, UserId = user.Id, CompanyId = cid, Role = roleName });
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

    public async Task<Guid> SeedAsync(Func<ZayraDbContext, Guid> add)
    {
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var id = add(db);
        await db.SaveChangesAsync();
        return id;
    }

    public async Task<Employee> SeedEmployeeAsync(string name, Guid companyId)
    {
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var e = new Employee { TenantId = TenantId, CompanyId = companyId, EmployeeCode = $"W-{Guid.NewGuid():N}"[..14], FullName = name, EnglishName = name, Status = "Draft", JoiningDate = DateTime.UtcNow.Date };
        db.Employees.Add(e);
        await db.SaveChangesAsync();
        return e;
    }

    public Task SeedTasksAsync(Employee e, params (string Title, string Owner, string Status)[] tasks) =>
        SeedTasksAsync(e, tasks.Select(t => (t.Title, t.Owner, t.Status, false)).ToArray());

    public async Task SeedTasksAsync(Employee e, params (string Title, string Owner, string Status, bool Evidence)[] tasks)
    {
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var i = 0;
        foreach (var t in tasks)
            db.OnboardingTasks.Add(new OnboardingTask { TenantId = TenantId, EmployeeId = e.PublicId, TaskTitle = t.Title, OwnerRole = t.Owner, Status = t.Status, EvidenceRequired = t.Evidence, OrderIndex = i++, DueDate = new DateOnly(2026, 1, 1) });
        await db.SaveChangesAsync();
    }

    public async Task<Guid> TaskIdAsync(string title)
    {
        using var scope = Host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ZayraDbContext>().OnboardingTasks.IgnoreQueryFilters().Where(t => t.TaskTitle == title).Select(t => t.Id).SingleAsync();
    }

    public async Task<Guid> SeedDocumentAsync(Employee e)
    {
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var d = new EmployeeDocument { TenantId = TenantId, CompanyId = e.CompanyId, EmployeeId = e.Id, DocumentType = "Evidence", FileName = "proof.pdf" };
        db.EmployeeDocuments.Add(d);
        await db.SaveChangesAsync();
        return d.Id;
    }

    public async Task<int> CountEmployeesNamedAsync(string name)
    {
        using var scope = Host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ZayraDbContext>().Employees.IgnoreQueryFilters().CountAsync(e => e.EnglishName == name);
    }

    public async Task DisposeAsync() { _client.Dispose(); await Host.DisposeAsync(); await _anchor.DisposeAsync(); }
}
