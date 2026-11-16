using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// List reads are limited to the caller's own companies. Through the real pipeline: a one-company Admin
/// sees only their own company's overtime pay lines, overtime budgets, end-of-service calculations, payroll validation results,
/// employee risk scores, Qiwa sync logs and onboarding tasks; a group user still sees all of them; assessment invitation links never
/// appear in the list.
/// </summary>
public sealed class CompanyScopedReadsHttpTests(LaunchSecurityFixture f) : IClassFixture<LaunchSecurityFixture>
{
    private sealed class Seed
    {
        public int EmployeeA, EmployeeB;
        public Guid PublicA, PublicB;
        public Guid ImpactA, ImpactB, BudgetA, BudgetB, EosbA, EosbB, RiskA, RiskB, SyncA, SyncB, RunA, RunB, TaskA, TaskB, TaskOrphan;
        public Guid DeptA, DeptB;
    }

    private Task<Seed> SeedAsync() => f.OnceAsync("company-scoped-reads", async () =>
    {
        var s = new Seed
        {
            ImpactA = Guid.NewGuid(), ImpactB = Guid.NewGuid(), BudgetA = Guid.NewGuid(), BudgetB = Guid.NewGuid(), EosbA = Guid.NewGuid(), EosbB = Guid.NewGuid(),
            RiskA = Guid.NewGuid(), RiskB = Guid.NewGuid(), SyncA = Guid.NewGuid(), SyncB = Guid.NewGuid(), RunA = Guid.NewGuid(), RunB = Guid.NewGuid(),
            TaskA = Guid.NewGuid(), TaskB = Guid.NewGuid(), TaskOrphan = Guid.NewGuid(), DeptA = Guid.NewGuid(), DeptB = Guid.NewGuid(),
        };
        await f.SeedAsync(async db =>
        {
            Employee Emp(string code, Guid company) => new()
            {
                TenantId = f.TenantId, CompanyId = company, EmployeeCode = code, FullName = code, EnglishName = code, Status = EmployeeStatuses.Active,
            };
            var a = Emp("SCOPED-A", f.CompanyA);
            var b = Emp("SCOPED-B", f.CompanyB);
            db.Employees.AddRange(a, b);
            await db.SaveChangesAsync();
            s.EmployeeA = a.Id; s.EmployeeB = b.Id; s.PublicA = a.PublicId; s.PublicB = b.PublicId;

            db.Departments.Add(new Department { Id = s.DeptA, TenantId = f.TenantId, CompanyId = f.CompanyA, Code = "LA", NameEn = "Scoped A", IsActive = true });
            db.Departments.Add(new Department { Id = s.DeptB, TenantId = f.TenantId, CompanyId = f.CompanyB, Code = "LB", NameEn = "Scoped B", IsActive = true });
            db.OvertimePayrollImpacts.Add(new OvertimePayrollImpact { Id = s.ImpactA, TenantId = f.TenantId, EmployeeId = a.Id, Minutes = 60, Amount = 10m, OvertimeRequestId = Guid.NewGuid() });
            db.OvertimePayrollImpacts.Add(new OvertimePayrollImpact { Id = s.ImpactB, TenantId = f.TenantId, EmployeeId = b.Id, Minutes = 90, Amount = 20m, OvertimeRequestId = Guid.NewGuid() });
            db.OvertimeBudgets.Add(new OvertimeBudget { Id = s.BudgetA, TenantId = f.TenantId, DepartmentId = s.DeptA, Year = 2026, Month = 11, BudgetAmount = 100m, Currency = "SAR" });
            db.OvertimeBudgets.Add(new OvertimeBudget { Id = s.BudgetB, TenantId = f.TenantId, DepartmentId = s.DeptB, Year = 2026, Month = 11, BudgetAmount = 900m, Currency = "SAR" });
            db.EOSBCalculations.Add(new EOSBCalculation { Id = s.EosbA, TenantId = f.TenantId, EmployeeId = a.Id, CalculationDate = new DateOnly(2026, 11, 1), CalculatedAmount = 1m });
            db.EOSBCalculations.Add(new EOSBCalculation { Id = s.EosbB, TenantId = f.TenantId, EmployeeId = b.Id, CalculationDate = new DateOnly(2026, 11, 1), CalculatedAmount = 2m });
            db.EmployeeRiskScores.Add(new EmployeeRiskScore { Id = s.RiskA, TenantId = f.TenantId, EmployeeId = a.Id, EmployeeName = "SCOPED-A", ChurnRiskScore = 10 });
            db.EmployeeRiskScores.Add(new EmployeeRiskScore { Id = s.RiskB, TenantId = f.TenantId, EmployeeId = b.Id, EmployeeName = "SCOPED-B", ChurnRiskScore = 90 });
            db.QiwaSyncLogs.Add(new QiwaSyncLog { Id = s.SyncA, TenantId = f.TenantId, EmployeeId = a.Id });
            db.QiwaSyncLogs.Add(new QiwaSyncLog { Id = s.SyncB, TenantId = f.TenantId, EmployeeId = b.Id });
            db.PayrollRuns.Add(new PayrollRun { Id = s.RunA, TenantId = f.TenantId, CompanyId = f.CompanyA, Year = 2026, Month = 10 });
            db.PayrollRuns.Add(new PayrollRun { Id = s.RunB, TenantId = f.TenantId, CompanyId = f.CompanyB, Year = 2026, Month = 10 });
            db.PayrollValidationResults.Add(new PayrollValidationResult { TenantId = f.TenantId, PayrollRunId = s.RunA, Code = "A_ONLY", Message = "a" });
            db.PayrollValidationResults.Add(new PayrollValidationResult { TenantId = f.TenantId, PayrollRunId = s.RunB, Code = "B_ONLY", Message = "b" });
            db.OnboardingTasks.Add(new OnboardingTask { Id = s.TaskA, TenantId = f.TenantId, EmployeeId = a.PublicId, TaskTitle = "Task A" });
            db.OnboardingTasks.Add(new OnboardingTask { Id = s.TaskB, TenantId = f.TenantId, EmployeeId = b.PublicId, TaskTitle = "Task B" });
            db.OnboardingTasks.Add(new OnboardingTask { Id = s.TaskOrphan, TenantId = f.TenantId, TaskTitle = "No owner" });
        });
        await f.EnsureUserAsync("ScopedAdminA", "Admin", groupScope: false, f.CompanyA);
        await f.EnsureUserAsync("ScopedAdminGroup", "Admin", groupScope: true, null);
        return s;
    });

    private async Task<List<JsonElement>> ItemsAsync(string path, string persona)
    {
        var json = await f.Json(HttpMethod.Get, path, f.Token(persona));
        var items = json.ValueKind == JsonValueKind.Array ? json
            : json.TryGetProperty("items", out var i) ? i
            : json.TryGetProperty("data", out var d) ? d
            : json.GetProperty("warnings");
        return items.EnumerateArray().Select(x => x.Clone()).ToList();
    }

    private static HashSet<Guid> Ids(IEnumerable<JsonElement> items) => items.Select(x => x.GetProperty("id").GetGuid()).ToHashSet();

    [Fact]
    public async Task OvertimePayrollReview_OneCompanyAdmin_SeesOnlyOwnCompanysLines()
    {
        var s = await SeedAsync();
        var own = Ids(await ItemsAsync("/api/overtime/payroll-review", "ScopedAdminA"));
        own.Should().Contain(s.ImpactA).And.NotContain(s.ImpactB);
        Ids(await ItemsAsync("/api/overtime/payroll-review", "ScopedAdminGroup")).Should().Contain([s.ImpactA, s.ImpactB]);
    }

    [Fact]
    public async Task OvertimeBudgets_OneCompanyAdmin_SeesOnlyOwnCompanysDepartments()
    {
        var s = await SeedAsync();
        var own = Ids(await ItemsAsync("/api/overtime/budgets?year=2026", "ScopedAdminA"));
        own.Should().Contain(s.BudgetA).And.NotContain(s.BudgetB);
        Ids(await ItemsAsync("/api/overtime/budgets?year=2026", "ScopedAdminGroup")).Should().Contain([s.BudgetA, s.BudgetB]);
    }

    [Fact]
    public async Task EosbList_OneCompanyAdmin_SeesOnlyOwnCompanysCalculations()
    {
        var s = await SeedAsync();
        var own = Ids(await ItemsAsync("/api/payroll/eosb/list", "ScopedAdminA"));
        own.Should().Contain(s.EosbA).And.NotContain(s.EosbB);
        Ids(await ItemsAsync("/api/payroll/eosb/list", "ScopedAdminGroup")).Should().Contain([s.EosbA, s.EosbB]);
    }

    [Fact]
    public async Task AiValidation_ForAnotherCompanysRun_IsNotFound_AndOwnRunStillAnswers()
    {
        var s = await SeedAsync();
        (await f.Status(HttpMethod.Get, $"/api/payroll/ai-validation?runId={s.RunB}", f.Token("ScopedAdminA"))).Should().Be(HttpStatusCode.NotFound);
        var own = await f.Json(HttpMethod.Get, $"/api/payroll/ai-validation?runId={s.RunA}", f.Token("ScopedAdminA"));
        own.GetProperty("warnings").EnumerateArray().Select(w => w.GetProperty("code").GetString()).Should().Contain("A_ONLY");
        (await f.Status(HttpMethod.Get, $"/api/payroll/ai-validation?runId={s.RunB}", f.Token("ScopedAdminGroup"))).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RiskScores_OneCompanyAdmin_SeesOnlyOwnCompanysEmployees()
    {
        var s = await SeedAsync();
        var own = Ids(await ItemsAsync("/api/ai/risk-scores", "ScopedAdminA"));
        own.Should().Contain(s.RiskA).And.NotContain(s.RiskB);
        Ids(await ItemsAsync("/api/ai/risk-scores", "ScopedAdminGroup")).Should().Contain([s.RiskA, s.RiskB]);
    }

    [Fact]
    public async Task QiwaSyncLogs_OneCompanyAdmin_SeesOnlyOwnCompanysEmployees()
    {
        var s = await SeedAsync();
        var own = Ids(await ItemsAsync("/api/qiwa/sync-logs", "ScopedAdminA"));
        own.Should().Contain(s.SyncA).And.NotContain(s.SyncB);
        Ids(await ItemsAsync("/api/qiwa/sync-logs", "ScopedAdminGroup")).Should().Contain([s.SyncA, s.SyncB]);
    }

    [Fact]
    public async Task OnboardingTasks_OneCompanyAdmin_SeesOnlyOwnCompanysEmployeesTasks_AndSummaryAgrees()
    {
        var s = await SeedAsync();
        var own = Ids(await ItemsAsync("/api/recruitment/onboarding/tasks?pageSize=100", "ScopedAdminA"));
        own.Should().Contain(s.TaskA).And.NotContain([s.TaskB, s.TaskOrphan], "another company's tasks and tasks with no owner are group-level only");
        Ids(await ItemsAsync("/api/recruitment/onboarding/tasks?pageSize=100", "ScopedAdminGroup")).Should().Contain([s.TaskA, s.TaskB, s.TaskOrphan]);

        var summaryOwn = await f.Json(HttpMethod.Get, $"/api/recruitment/onboarding/summary?employeeId={s.PublicB}", f.Token("ScopedAdminA"));
        summaryOwn.GetProperty("total").GetInt32().Should().Be(0, "another company's employee's onboarding must not be counted");
    }

    [Fact]
    public async Task OnboardingTaskStatus_OneCompanyAdmin_UpdatesOnlyOwnCompanysTasks()
    {
        var s = await SeedAsync();
        (await f.Status(HttpMethod.Patch, $"/api/recruitment/onboarding/tasks/{s.TaskB}/status", f.Token("ScopedAdminA"), new { status = "InProgress", notes = "x" }))
            .Should().Be(HttpStatusCode.NotFound, "another company's employee's task is not in the caller's scope");
        (await f.Status(HttpMethod.Patch, $"/api/recruitment/onboarding/tasks/{s.TaskOrphan}/status", f.Token("ScopedAdminA"), new { status = "InProgress", notes = "x" }))
            .Should().Be(HttpStatusCode.NotFound, "a task with no owner is group-level");
        (await f.ReadAsync(db => db.OnboardingTasks.IgnoreQueryFilters().Where(t => t.Id == s.TaskB || t.Id == s.TaskOrphan).Select(t => t.Status).ToListAsync()))
            .Should().NotContain("InProgress");

        (await f.Status(HttpMethod.Patch, $"/api/recruitment/onboarding/tasks/{s.TaskA}/status", f.Token("ScopedAdminA"), new { status = "InProgress", notes = "x" }))
            .Should().Be(HttpStatusCode.OK, "the caller's own company's task can still be updated");
    }

    [Fact]
    public async Task OnboardingTasks_GroupUserWithASelectedCompany_SeesThatCompanyOnly()
    {
        var s = await SeedAsync();
        async Task<HashSet<Guid>> ListAs(Guid? selected)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/recruitment/onboarding/tasks?pageSize=100");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", f.Token("ScopedAdminGroup"));
            if (selected is Guid c) request.Headers.Add(ZayraDbContext.CompanySelectionHeader, c.ToString());
            using var response = await f.Client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, text);
            return Ids(JsonDocument.Parse(text).RootElement.GetProperty("items").EnumerateArray().Select(x => x.Clone()));
        }

        (await ListAs(f.CompanyA)).Should().Contain(s.TaskA).And.NotContain([s.TaskB, s.TaskOrphan]);
        (await ListAs(null)).Should().Contain([s.TaskA, s.TaskB, s.TaskOrphan], "with no company selected the group user still sees everything");
    }

    [Fact]
    public async Task AssessmentList_NeverCarriesTheCandidatesInvitationToken()
    {
        await SeedAsync();
        var applicationId = Guid.NewGuid();
        var assessmentId = Guid.NewGuid();
        await f.SeedAsync(async db =>
        {
            db.CandidateAssessments.Add(new CandidateAssessment
            {
                Id = assessmentId, TenantId = f.TenantId, CompanyId = f.CompanyA, ApplicationId = applicationId, CandidateId = Guid.NewGuid(),
                TemplateId = Guid.NewGuid(), TemplateName = "Aptitude", Status = "Sent", InvitationToken = "SECRET-INVITE-TOKEN-0001",
            });
            await Task.CompletedTask;
        });
        using var response = await f.Send(HttpMethod.Get, "/api/recruitment/assessments", f.Token("ScopedAdminA"));
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        text.Should().Contain(assessmentId.ToString());
        text.Should().NotContain("SECRET-INVITE-TOKEN-0001").And.NotContainEquivalentOf("invitationToken");
    }
}
