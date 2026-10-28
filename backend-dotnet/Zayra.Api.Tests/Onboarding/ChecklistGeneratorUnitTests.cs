using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Onboarding;
using Zayra.Api.Data;
using Zayra.Api.Models;
using Xunit;

namespace Zayra.Api.Tests.Onboarding;

/// <summary>WP-P3 unit layer (G41): the pure rules that decide which onboarding tasks a hire gets and when they are due.</summary>
public class ChecklistGeneratorUnitTests
{
    private static OnboardingChecklistTemplateTask T(string title, int offset, string condition = "any", string owner = "HR", bool blocks = false, int order = 0) => new()
    {
        Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), ChecklistId = Guid.NewGuid(), TaskTitle = title, DueOffsetDays = offset,
        ConditionNationalityClass = condition, OwnerRole = owner, BlocksActivation = blocks, OrderIndex = order, IsActive = true,
    };

    private static ZayraDbContext Db() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    // AC64
    [Fact]
    public void ChecklistGenerator_DueIsStartPlusOffset_NegativeOffsetsAllowed()
    {
        var start = new DateOnly(2026, 11, 8);
        var plan = ChecklistPlanner.Plan(new[] { T("Contract", -7, order: 0), T("Badge", 0, order: 1), T("Probation review", 76, order: 2) }, NationalityClasses.Saudi, start);
        plan.Select(p => p.DueDate).Should().Equal(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 8), new DateOnly(2027, 1, 23));
    }

    // AC65
    [Fact]
    public void ChecklistGenerator_SaudiOmitsExpatTasks_ExpatOmitsSaudiOnly_BlankNationalityOnlyAny()
    {
        var tasks = new[] { T("Everyone", 0), T("Iqama", 30, "expat"), T("Saudi-only", 0, "saudi"), T("GCC-only", 0, "gcc") };
        var start = new DateOnly(2026, 11, 8);

        ChecklistPlanner.Plan(tasks, NationalityClasses.Of("SA"), start).Select(p => p.Template.TaskTitle).Should().Equal("Everyone", "Saudi-only");
        ChecklistPlanner.Plan(tasks, NationalityClasses.Of("Saudi Arabia"), start).Select(p => p.Template.TaskTitle).Should().NotContain("Iqama");
        ChecklistPlanner.Plan(tasks, NationalityClasses.Of("PK"), start).Select(p => p.Template.TaskTitle).Should().Equal("Everyone", "Iqama");
        ChecklistPlanner.Plan(tasks, NationalityClasses.Of("AE"), start).Select(p => p.Template.TaskTitle).Should().Equal("Everyone", "GCC-only");
        // Unknown is never "expat": a blank or unrecognised nationality only gets the tasks that apply to everyone.
        ChecklistPlanner.Plan(tasks, NationalityClasses.Of(""), start).Select(p => p.Template.TaskTitle).Should().Equal("Everyone");
        ChecklistPlanner.Plan(tasks, NationalityClasses.Of(null), start).Select(p => p.Template.TaskTitle).Should().Equal("Everyone");
        ChecklistPlanner.Plan(tasks, NationalityClasses.Of("not a country"), start).Select(p => p.Template.TaskTitle).Should().Equal("Everyone");
    }

    [Theory]
    [InlineData("SA", "saudi")] [InlineData("سعودي", "saudi")] [InlineData("Saudi", "saudi")]
    [InlineData("AE", "gcc")] [InlineData("Kuwait", "gcc")]
    [InlineData("IN", "expat")] [InlineData("Pakistan", "expat")]
    [InlineData("", "unknown")] [InlineData("   ", "unknown")] [InlineData("xyzzy plugh", "unknown")]
    public void NationalityClass_IsDerivedFromTheStoredNationality(string stored, string expected) =>
        NationalityClasses.Of(stored).Should().Be(expected);

    [Fact]
    public void ChecklistGenerator_InactiveTemplateTasksAreNotRaised() =>
        ChecklistPlanner.Plan(new[] { T("Live", 0), new OnboardingChecklistTemplateTask { TaskTitle = "Retired", IsActive = false } }, "saudi", new DateOnly(2026, 11, 8))
            .Should().ContainSingle().Which.Template.TaskTitle.Should().Be("Live");

    // Idempotent on retry: the plan is the same, and instances are keyed by template task id (the unique index enforces it on Postgres).
    [Fact]
    public async Task ChecklistGenerator_IdempotentOnRetry()
    {
        await using var db = Db();
        var tenant = Guid.NewGuid();
        var checklist = new OnboardingChecklist { TenantId = tenant, Code = "C1", Name = "KSA" };
        db.OnboardingChecklists.Add(checklist);
        var tasks = new[] { T("A", 0), T("B", 3) };
        foreach (var t in tasks) { t.TenantId = tenant; t.ChecklistId = checklist.Id; }
        db.OnboardingChecklistTemplateTasks.AddRange(tasks);
        await db.SaveChangesAsync();
        var employee = new Employee { TenantId = tenant, Nationality = "SA", JoiningDate = new DateTime(2026, 11, 8, 0, 0, 0, DateTimeKind.Utc) };

        var gen = new ChecklistGenerator(db);
        (await gen.EnsureAsync(tenant, employee, checklist.Id, true, default)).Created.Should().Be(2);
        var again = await gen.EnsureAsync(tenant, employee, checklist.Id, true, default);
        again.Created.Should().Be(0);
        again.AlreadyThere.Should().Be(2);
        (await db.OnboardingTasks.CountAsync(t => t.EmployeeId == employee.PublicId)).Should().Be(2);
    }

    // CD54: no joining date -> no due date; set later -> re-based; a hand-set (overridden) or finished task keeps its date.
    [Fact]
    public async Task ChecklistGenerator_NullStartDate_DueNull_RebasedWhenSet_OverriddenDueKept()
    {
        await using var db = Db();
        var tenant = Guid.NewGuid();
        var checklist = new OnboardingChecklist { TenantId = tenant, Code = "C2", Name = "KSA" };
        db.OnboardingChecklists.Add(checklist);
        var tasks = new[] { T("Follows", 5), T("Overridden", 5), T("Done", 5) };
        foreach (var t in tasks) { t.TenantId = tenant; t.ChecklistId = checklist.Id; }
        db.OnboardingChecklistTemplateTasks.AddRange(tasks);
        await db.SaveChangesAsync();
        var employee = new Employee { TenantId = tenant, Nationality = "SA" };

        var gen = new ChecklistGenerator(db);
        await gen.EnsureAsync(tenant, employee, checklist.Id, startDateKnown: false, default);
        var raised = await db.OnboardingTasks.Where(t => t.EmployeeId == employee.PublicId).ToListAsync();
        raised.Should().HaveCount(3).And.OnlyContain(t => t.DueDate == null);

        var overridden = raised.Single(t => t.TaskTitle == "Overridden");
        overridden.DueDate = new DateOnly(2026, 12, 25); overridden.DueOverrideReason = "Agreed with the manager";
        var done = raised.Single(t => t.TaskTitle == "Done");
        done.Status = "Completed";
        await db.SaveChangesAsync();

        await gen.RebaseAsync(tenant, employee.PublicId, new DateOnly(2026, 11, 8), default);
        var after = await db.OnboardingTasks.AsNoTracking().Where(t => t.EmployeeId == employee.PublicId).ToDictionaryAsync(t => t.TaskTitle);
        after["Follows"].DueDate.Should().Be(new DateOnly(2026, 11, 13));
        after["Overridden"].DueDate.Should().Be(new DateOnly(2026, 12, 25));
        after["Done"].DueDate.Should().BeNull("a finished task is not re-dated");

        await gen.RebaseAsync(tenant, employee.PublicId, new DateOnly(2026, 11, 15), default);
        (await db.OnboardingTasks.AsNoTracking().SingleAsync(t => t.TaskTitle == "Follows")).DueDate.Should().Be(new DateOnly(2026, 11, 20), "moving the joining date moves the due date");
    }

    [Fact]
    public async Task ChecklistGenerator_TaskIsACopy_TemplateEditDoesNotChangeIt()
    {
        await using var db = Db();
        var tenant = Guid.NewGuid();
        var checklist = new OnboardingChecklist { TenantId = tenant, Code = "C3", Name = "KSA" };
        db.OnboardingChecklists.Add(checklist);
        var t = T("Open the account", 0, owner: "IT", blocks: true); t.TenantId = tenant; t.ChecklistId = checklist.Id; t.TitleAr = "فتح الحساب";
        db.OnboardingChecklistTemplateTasks.Add(t);
        await db.SaveChangesAsync();
        var employee = new Employee { TenantId = tenant, Nationality = "SA", JoiningDate = DateTime.UtcNow.Date };
        await new ChecklistGenerator(db).EnsureAsync(tenant, employee, checklist.Id, true, default);

        t.TaskTitle = "Renamed"; t.OwnerRole = "HR"; t.BlocksActivation = false; t.DueOffsetDays = 99;
        await db.SaveChangesAsync();

        var task = await db.OnboardingTasks.AsNoTracking().SingleAsync();
        (task.TaskTitle, task.TitleAr, task.OwnerRole, task.BlocksActivation).Should().Be(("Open the account", "فتح الحساب", "IT", true));
        task.TemplateTaskId.Should().Be(t.Id);
    }

    // CD4
    [Fact]
    public void BlocksActivation_DefaultFalse()
    {
        new OnboardingChecklistTemplateTask().BlocksActivation.Should().BeFalse();
        new OnboardingTask().BlocksActivation.Should().BeFalse();
        KsaStarterTemplate.Tasks.Should().OnlyContain(t => !t.BlocksActivation, "no starter task blocks activation unless the company opts in");
    }

    [Theory]
    [InlineData("any", true)] [InlineData("saudi", true)] [InlineData("gcc", true)] [InlineData("expat", true)]
    [InlineData("", false)] [InlineData("Expat", false)] [InlineData("foreigner", false)] [InlineData(null, false)]
    public void TemplateTaskCondition_UnknownValue_Rejected(string? value, bool valid) =>
        NationalityClasses.IsValidCondition(value).Should().Be(valid);

    [Theory]
    [InlineData("HR", true)] [InlineData("Gov", true)] [InlineData("Employee", true)] [InlineData("hr", false)] [InlineData("Janitor", false)] [InlineData(null, false)]
    public void OwnerRole_OnlyTheKnownRoles(string? value, bool valid) => OnboardingOwnerRoles.IsValid(value).Should().Be(valid);

    private static HireTemplate H(string name, Guid? d = null, Guid? g = null, Guid? b = null) => new()
    {
        Name = name, MatchDesignationId = d, MatchGradeId = g, MatchBranchId = b, IsActive = true, MatchKey = HireTemplate.Key(d, g, b),
    };

    [Fact]
    public void HireTemplateResolver_Specificity_TieSuggestsNothing()
    {
        Guid desig = Guid.NewGuid(), grade = Guid.NewGuid(), branch = Guid.NewGuid();
        var byDesignation = H("By designation", d: desig);
        var byGrade = H("By grade", g: grade);
        var byBranch = H("By branch", b: branch);
        var other = H("Other designation", d: Guid.NewGuid());
        var noKeys = H("Picked by hand");

        var r = HireTemplateResolver.Resolve(new[] { byBranch, byGrade, byDesignation, other, noKeys }, desig, grade, branch);
        r.Candidates.Select(c => c.Name).Should().Equal("By designation", "By grade", "By branch");
        r.SuggestedId.Should().Be(byDesignation.Id, "designation (4) beats grade (2) beats branch (1)");

        // designation 4 vs grade 2 + branch 1 = 3: designation still wins.
        HireTemplateResolver.Resolve(new[] { byDesignation, H("Grade and branch", g: grade, b: branch) }, desig, grade, branch).SuggestedId.Should().Be(byDesignation.Id);

        // Two templates of equal specificity: no suggestion, HR picks.
        var tie = HireTemplateResolver.Resolve(new[] { H("Tie A", g: grade), H("Tie B", g: grade) }, null, grade, null);
        tie.Candidates.Should().HaveCount(2);
        tie.SuggestedId.Should().BeNull();

        // A template naming a key the hire does not have never matches; inactive and deleted ones are ignored.
        HireTemplateResolver.Resolve(new[] { byDesignation }, null, grade, branch).Candidates.Should().BeEmpty();
        var inactive = H("Off", d: desig); inactive.IsActive = false;
        var deleted = H("Gone", d: desig); deleted.IsDeleted = true;
        HireTemplateResolver.Resolve(new[] { inactive, deleted }, desig, null, null).Candidates.Should().BeEmpty();
    }

    [Fact]
    public void HireTemplateDefaults_ParseAndValidate()
    {
        HireTemplateDefaults.Parse("{}").Ok.Should().BeTrue();
        HireTemplateDefaults.Parse(null).Values!.ManagerRule.Should().Be("none");
        var ok = HireTemplateDefaults.Parse("""{"employmentType":"Full-time","probationDays":90,"managerRule":"department-head","workLocation":"HQ"}""");
        ok.Ok.Should().BeTrue();
        ok.Values!.ProbationDays.Should().Be(90);
        HireTemplateDefaults.Parse("""{"salary":9000}""").Errors.Should().Contain("unknown_key:salary", "money never lives in a template");
        HireTemplateDefaults.Parse("""{"probationDays":400}""").Errors.Should().Contain("invalid:probationDays");
        HireTemplateDefaults.Parse("""{"managerRule":"ceo"}""").Errors.Should().Contain("invalid:managerRule");
        HireTemplateDefaults.Parse("""{"departmentId":"not-a-guid"}""").Errors.Should().Contain("invalid:departmentId");
        HireTemplateDefaults.Parse("[1,2]").Errors.Should().Contain("defaults_not_object");
        HireTemplateDefaults.Parse("{broken").Errors.Should().Contain("defaults_not_object");
    }

    [Fact]
    public async Task DefaultsJson_ForeignReferencesRejected()
    {
        await using var db = Db();
        Guid tenant = Guid.NewGuid(), other = Guid.NewGuid();
        var company = new Company { TenantId = tenant, LegalNameEn = "A", TradeName = "A", CountryCode = "SA" };
        var sister = new Company { TenantId = tenant, LegalNameEn = "B", TradeName = "B", CountryCode = "SA" };
        var foreign = new Company { TenantId = other, LegalNameEn = "F", TradeName = "F", CountryCode = "SA" };
        db.Companies.AddRange(company, sister, foreign);
        var mine = new Department { TenantId = tenant, CompanyId = company.Id, NameEn = "Ops", IsActive = true };
        var sisters = new Department { TenantId = tenant, CompanyId = sister.Id, NameEn = "Sis", IsActive = true };
        var foreigners = new Department { TenantId = other, CompanyId = foreign.Id, NameEn = "Far", IsActive = true };
        var branch = new Branch { TenantId = tenant, CompanyId = sister.Id, NameEn = "Sis HQ", IsActive = true };
        db.AddRange(mine, sisters, foreigners, branch);
        await db.SaveChangesAsync();

        async Task<IReadOnlyList<string>> Check(Guid? dept, Guid? branchId = null) =>
            await HireTemplateDefaults.ReferenceErrorsAsync(db, tenant, company.Id,
                new HireTemplateDefaultValues(dept, null, null, null, null, "none"), null, null, branchId, null, default);

        (await Check(mine.Id)).Should().BeEmpty();
        (await Check(sisters.Id)).Should().Equal("invalid_reference:departmentId");
        (await Check(foreigners.Id)).Should().Equal("invalid_reference:departmentId");
        (await Check(Guid.NewGuid())).Should().Equal("invalid_reference:departmentId");
        (await Check(null, branch.Id)).Should().Equal("invalid_reference:matchBranchId");
    }

    // 10-KSA section 5
    [Fact]
    public void KsaStarterTemplate_Has15Tasks_WithOwnersAndOffsets()
    {
        KsaStarterTemplate.Tasks.Should().HaveCount(15);
        KsaStarterTemplate.Tasks.Should().OnlyContain(t => OnboardingOwnerRoles.IsValid(t.OwnerRole) && NationalityClasses.IsValidCondition(t.NationalityClass));
        KsaStarterTemplate.Tasks.Should().OnlyContain(t => t.TitleAr.Length > 0 && t.DescriptionAr.Length > 0 && t.Description.Length > 0);
        KsaStarterTemplate.Tasks.Select(t => t.Title).Should().OnlyHaveUniqueItems();
        KsaStarterTemplate.Tasks.Where(t => t.NationalityClass == "expat").Select(t => t.Title)
            .Should().Contain(x => x.Contains("Iqama")).And.Contain(x => x.Contains("Muqeem")).And.Contain(x => x.Contains("fitness"));
        KsaStarterTemplate.Tasks.Where(t => t.OwnerRole == "Gov").Should().HaveCountGreaterThanOrEqualTo(4);
        KsaStarterTemplate.Tasks.Select(t => t.OwnerRole).Distinct().Should().Contain(new[] { "HR", "IT", "Manager", "Payroll", "Gov" });
        // No government integration is implied: every Gov step is a person's task.
        KsaStarterTemplate.Tasks.Select(t => t.Title + t.Description).Should().OnlyContain(s => !s.Contains("API"));

        // A Saudi hire gets none of the Iqama/Muqeem/medical-fitness tasks; an expat does.
        var templates = KsaStarterTemplate.Tasks.Select((t, i) => new OnboardingChecklistTemplateTask
        { TaskTitle = t.Title, ConditionNationalityClass = t.NationalityClass, DueOffsetDays = t.DueOffsetDays, OrderIndex = i }).ToList();
        var saudi = ChecklistPlanner.Plan(templates, NationalityClasses.Saudi, new DateOnly(2026, 11, 8)).Select(p => p.Template.TaskTitle).ToList();
        saudi.Should().HaveCount(12).And.NotContain(x => x.Contains("Muqeem") || x.Contains("Iqama issuance") || x.Contains("fitness"));
        ChecklistPlanner.Plan(templates, NationalityClasses.Expat, new DateOnly(2026, 11, 8)).Should().HaveCount(15);
    }

    // Train wiring: P3's template asks P2's section requester (contract C-1) once the hire exists.
    [Fact]
    public async Task HireTemplate_WithEssSections_RaisesTheCompletionRequestForThoseSections()
    {
        await using var db = Db();
        var tenant = Guid.NewGuid();
        var company = new Company { TenantId = tenant, LegalNameEn = "Co", TradeName = "Co", CountryCode = "SA", Jurisdiction = "t", RegistrationNumber = "RC-1", DefaultCurrency = "SAR", IsActive = true };
        db.Companies.Add(company);
        var employee = new Employee { TenantId = tenant, CompanyId = company.Id, EmployeeCode = "E1", FullName = "A B", EnglishName = "A B", Status = "Draft" };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        var template = new HireTemplate { TenantId = tenant, CompanyId = company.Id, Name = "T", EssCompletionSections = "id,bank", IsActive = true };

        await HireTemplateHook.GenerateAsync(db, tenant, employee, template, startDateKnown: true, default);

        var item = await db.EmployeeActionItems.IgnoreQueryFilters().SingleAsync(x => x.EmployeeId == employee.Id);
        item.RequestedSections.Should().Be("id,bank");
    }
}
