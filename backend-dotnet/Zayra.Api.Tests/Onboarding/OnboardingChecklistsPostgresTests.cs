using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Onboarding;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Models;
using Xunit;

namespace Zayra.Api.Tests.Onboarding;

/// <summary>WP-P3 on the real Postgres provider: the guarantees are unique indexes, so they are proven where the index lives.</summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class OnboardingChecklistsPostgresTests
{
    private readonly PostgresFixture _fixture;
    public OnboardingChecklistsPostgresTests(PostgresFixture fixture) => _fixture = fixture;

    private sealed record Seeded(Guid TenantId, Guid CompanyId, Guid ChecklistId, Employee Employee);

    private async Task<Seeded> SeedAsync(int taskCount = 3, string nationality = "SA", bool blocking = false)
    {
        await using var db = _fixture.CreateDb();
        var tenant = new Tenant { Name = "P3 " + Guid.NewGuid().ToString("N")[..6], Slug = "p3-" + Guid.NewGuid().ToString("N"), IsActive = true };
        db.Tenants.Add(tenant);
        var company = new Company { TenantId = tenant.Id, LegalNameEn = "P3 Co", TradeName = "P3 Co", CountryCode = "SA", IsActive = true, DefaultCurrency = "SAR" };
        db.Companies.Add(company);
        var checklist = new OnboardingChecklist { TenantId = tenant.Id, CompanyId = company.Id, Code = "CL-" + Guid.NewGuid().ToString("N")[..10], Name = "KSA", ApplicableTo = "Company", IsDefault = true };
        db.OnboardingChecklists.Add(checklist);
        for (var i = 0; i < taskCount; i++)
            db.OnboardingChecklistTemplateTasks.Add(new OnboardingChecklistTemplateTask { TenantId = tenant.Id, ChecklistId = checklist.Id, TaskTitle = "Task " + i, OrderIndex = i, DueOffsetDays = i, BlocksActivation = blocking && i == 0 });
        var employee = new Employee
        {
            TenantId = tenant.Id, CompanyId = company.Id, EmployeeCode = "P3-" + Guid.NewGuid().ToString("N")[..8], FullName = "New Hire", EnglishName = "New Hire",
            Status = "Draft", Nationality = nationality, JoiningDate = new DateTime(2026, 11, 8, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        return new(tenant.Id, company.Id, checklist.Id, employee);
    }

    [Fact]
    public async Task Generate_Concurrent_TwoCallers_ExactlyOneSetOfTasks()
    {
        var s = await SeedAsync(taskCount: 6);
        for (var round = 0; round < 3; round++)
        {
            var employee = new Employee { TenantId = s.TenantId, CompanyId = s.CompanyId, EmployeeCode = "R" + round + Guid.NewGuid().ToString("N")[..8], FullName = "Racer", EnglishName = "Racer", Nationality = "SA", Status = "Draft", JoiningDate = s.Employee.JoiningDate };
            await using (var seed = _fixture.CreateDb()) { seed.Employees.Add(employee); await seed.SaveChangesAsync(); }
            var gate = new TaskCompletionSource();
            async Task Caller()
            {
                await using var db = _fixture.CreateDb();
                await gate.Task;
                await new ChecklistGenerator(db).EnsureAsync(s.TenantId, employee, s.ChecklistId, true, default);
            }
            var callers = Enumerable.Range(0, 4).Select(_ => Task.Run(Caller)).ToArray();
            gate.SetResult();
            await Task.WhenAll(callers);
            await using var verify = _fixture.CreateDb();
            (await verify.OnboardingTasks.CountAsync(t => t.TenantId == s.TenantId && t.EmployeeId == employee.PublicId)).Should().Be(6, "one set, never two");
        }
    }

    [Fact]
    public async Task UniqueIndex_RefusesASecondInstanceOfTheSameTemplateTask()
    {
        var s = await SeedAsync(taskCount: 1);
        await using var db = _fixture.CreateDb();
        var template = await db.OnboardingChecklistTemplateTasks.AsNoTracking().SingleAsync(t => t.ChecklistId == s.ChecklistId);
        db.OnboardingTasks.Add(new OnboardingTask { TenantId = s.TenantId, EmployeeId = s.Employee.PublicId, TemplateTaskId = template.Id, TaskTitle = "a" });
        db.OnboardingTasks.Add(new OnboardingTask { TenantId = s.TenantId, EmployeeId = s.Employee.PublicId, TemplateTaskId = template.Id, TaskTitle = "b" });
        var act = () => db.SaveChangesAsync();
        (await act.Should().ThrowAsync<DbUpdateException>()).Which.Should().Match<DbUpdateException>(e => ChecklistGenerator.IsUniqueViolation(e));
        // Manual tasks (no template) are never limited.
        await using var db2 = _fixture.CreateDb();
        db2.OnboardingTasks.AddRange(
            new OnboardingTask { TenantId = s.TenantId, EmployeeId = s.Employee.PublicId, TaskTitle = "m1" },
            new OnboardingTask { TenantId = s.TenantId, EmployeeId = s.Employee.PublicId, TaskTitle = "m2" });
        await db2.SaveChangesAsync();
    }

    // The NULL-in-UNIQUE trap: two templates that each leave a key empty must still collide on the keys they share.
    [Fact]
    public async Task HireTemplates_MatchUnique_CoalesceNullTrap()
    {
        var s = await SeedAsync(taskCount: 0);
        var desig = Guid.NewGuid();
        HireTemplate Make(string name, Guid? d, Guid? g = null, bool active = true, bool deleted = false) => new()
        {
            TenantId = s.TenantId, CompanyId = s.CompanyId, Name = name, NameKey = name.ToLowerInvariant(), MatchDesignationId = null, MatchGradeId = null,
            MatchKey = HireTemplate.Key(d, g, null), IsActive = active, IsDeleted = deleted,
        };
        await using var db = _fixture.CreateDb();
        // No keys at all: any number of "pick by hand" templates is fine.
        db.HireTemplates.AddRange(Make("Hand 1", null), Make("Hand 2", null));
        db.HireTemplates.Add(Make("Desig A", desig));
        await db.SaveChangesAsync();

        // Same designation, grade empty in both: a collision even though grade is NULL in both rows.
        await using var dup = _fixture.CreateDb();
        dup.HireTemplates.Add(Make("Desig B", desig));
        var act = () => dup.SaveChangesAsync();
        (await act.Should().ThrowAsync<DbUpdateException>()).Which.Should().Match<DbUpdateException>(e => ChecklistGenerator.IsUniqueViolation(e));

        // Off or removed templates do not hold the key; a different grade is a different key.
        await using var ok = _fixture.CreateDb();
        ok.HireTemplates.AddRange(Make("Off", desig, active: false), Make("Removed", desig, deleted: true), Make("Desig and grade", desig, Guid.NewGuid()));
        await ok.SaveChangesAsync();
    }

    [Fact]
    public async Task HireTemplates_Name_Unique_CaseInsensitive()
    {
        var s = await SeedAsync(taskCount: 0);
        HireTemplate Make(string name, Guid company, bool deleted = false) => new()
        { TenantId = s.TenantId, CompanyId = company, Name = name, NameKey = name.Trim().ToLowerInvariant(), MatchKey = HireTemplate.NoMatchKey, IsDeleted = deleted };
        await using (var db = _fixture.CreateDb()) { db.HireTemplates.Add(Make("Branch Cashier", s.CompanyId)); await db.SaveChangesAsync(); }
        await using var dup = _fixture.CreateDb();
        dup.HireTemplates.Add(Make("  branch CASHIER ", s.CompanyId));
        var act = () => dup.SaveChangesAsync();
        (await act.Should().ThrowAsync<DbUpdateException>()).Which.Should().Match<DbUpdateException>(e => ChecklistGenerator.IsUniqueViolation(e));

        // The same name in a sister company, or after the first was removed, is fine.
        var sister = new Company { TenantId = s.TenantId, LegalNameEn = "Sister", TradeName = "Sister", CountryCode = "SA", IsActive = true };
        await using var ok = _fixture.CreateDb();
        ok.Companies.Add(sister);
        await ok.SaveChangesAsync();
        ok.HireTemplates.Add(Make("Branch Cashier", sister.Id));
        await ok.SaveChangesAsync();
    }

    [Fact]
    public async Task OnePerCompanyDefaultChecklist_IndexRefusesASecond()
    {
        var s = await SeedAsync(taskCount: 0);   // seeded one default
        await using var db = _fixture.CreateDb();
        db.OnboardingChecklists.Add(new OnboardingChecklist { TenantId = s.TenantId, CompanyId = s.CompanyId, Code = "X" + Guid.NewGuid().ToString("N")[..8], Name = "Second", IsDefault = true });
        var act = () => db.SaveChangesAsync();
        (await act.Should().ThrowAsync<DbUpdateException>()).Which.Should().Match<DbUpdateException>(e => ChecklistGenerator.IsUniqueViolation(e));
    }

    // Expand-only: a row written the way every pre-P3 caller writes it (none of the new columns set) reads back with safe defaults.
    [Fact]
    public async Task Migration_P3_ExpandOnly_LegacyRowsReadable()
    {
        var s = await SeedAsync(taskCount: 0);
        await using (var db = _fixture.CreateDb())
        {
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO onboarding_tasks (id, tenant_id, employee_id, task_title, task_description, category, assigned_to_name, status, notes, order_index, is_mandatory, created_at_utc) " +
                "VALUES ({0}, {1}, {2}, 'Legacy', '', 'General', '', 'Pending', '', 0, true, now())", Guid.NewGuid(), s.TenantId, s.Employee.PublicId);
        }
        await using var verify = _fixture.CreateDb();
        var legacy = await verify.OnboardingTasks.AsNoTracking().SingleAsync(t => t.TenantId == s.TenantId && t.TaskTitle == "Legacy");
        (legacy.OwnerRole, legacy.BlocksActivation, legacy.Source, legacy.TemplateTaskId, legacy.DueOffsetDays).Should().Be(("HR", false, "manual", (Guid?)null, (int?)null));
        var open = await OnboardingTaskBlockers.OpenForAsync(verify, s.TenantId, s.Employee.PublicId, default);
        open.Should().BeEmpty("a legacy task never blocks activation");
    }

    // AC67 / CD4
    [Fact]
    public async Task Activation_BlockedByOpenBlockingTask_ListsTasks()
    {
        var s = await SeedAsync(taskCount: 3, blocking: true);
        await using var db = _fixture.CreateDb();
        var employee = await db.Employees.SingleAsync(e => e.Id == s.Employee.Id);
        await new ChecklistGenerator(db).EnsureAsync(s.TenantId, employee, s.ChecklistId, true, default);
        var guard = new EmployeeActivationGuard(db);
        var snapshot = (await guard.BuildSnapshotAsync(s.TenantId, employee.Id, default))!;

        var ex = await Assert.ThrowsAsync<EmployeeActivationBlockedException>(() =>
            guard.EnsureActivatableAsync(s.TenantId, s.CompanyId, snapshot, new RequestContext(null, "", Guid.NewGuid(), s.TenantId), default));
        var task = await db.OnboardingTasks.AsNoTracking().SingleAsync(t => t.EmployeeId == employee.PublicId && t.BlocksActivation);
        ex.Readiness.Blocking.Select(b => b.Key).Should().Contain(OnboardingTaskBlockers.KeyPrefix + task.Id);

        // Once the task is done, activation is no longer held by it.
        task = await db.OnboardingTasks.SingleAsync(t => t.Id == task.Id);
        task.Status = "Completed";
        await db.SaveChangesAsync();
        var act = () => guard.EnsureActivatableAsync(s.TenantId, s.CompanyId, snapshot, new RequestContext(null, "", Guid.NewGuid(), s.TenantId), default);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Activation_NonBlockingOpenTask_Allowed()
    {
        var s = await SeedAsync(taskCount: 3, blocking: false);
        await using var db = _fixture.CreateDb();
        var employee = await db.Employees.SingleAsync(e => e.Id == s.Employee.Id);
        await new ChecklistGenerator(db).EnsureAsync(s.TenantId, employee, s.ChecklistId, true, default);
        (await db.OnboardingTasks.CountAsync(t => t.EmployeeId == employee.PublicId && t.Status == "Pending")).Should().Be(3);
        var guard = new EmployeeActivationGuard(db);
        var snapshot = (await guard.BuildSnapshotAsync(s.TenantId, employee.Id, default))!;
        var act = () => guard.EnsureActivatableAsync(s.TenantId, s.CompanyId, snapshot, new RequestContext(null, "", Guid.NewGuid(), s.TenantId), default);
        await act.Should().NotThrowAsync("tasks never block activation unless the template opted in (CD4)");
    }

    [Fact]
    public void TableBudget_HireTemplatesLineMatchesDbSet()
    {
        var manifest = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "../../../Security/table-budget.txt"))
            .Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        manifest.Should().Contain("HireTemplates");
        typeof(ZayraDbContext).GetProperty("HireTemplates").Should().NotBeNull();
        manifest.Count(m => m == "HireTemplates").Should().Be(1);
    }
}
