using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Models;
using static Zayra.Api.Tests.OrganizationPerCompanyBackfillPostgresTests;

namespace Zayra.Api.Tests;

/// <summary>
/// One container: the per-company backfill seed (grade GS kept by A and copied for B; department DC kept by B and
/// copied for A; its parent DP kept by A and copied for B), one workspace-wide rule of every family naming those rows,
/// then BackfillOrganisationPerCompany and CopyWorkspaceRulesPerCompany run once. Each test checks one rule family.
/// </summary>
public sealed class RuleCopyWorld : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
    public DbContextOptions<ZayraDbContext> Options { get; private set; } = null!;
    internal Seeded S { get; private set; } = null!;
    public Guid GradeB { get; private set; }
    public Guid ChildA { get; private set; }
    public Guid ParentB { get; private set; }
    public Guid BranchA { get; private set; }

    public static readonly Guid Policy = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    public static readonly Guid Workflow = Guid.Parse("a1000000-0000-0000-0000-000000000001");
    public static readonly Guid AttendanceOnBranchA = Guid.Parse("a2000000-0000-0000-0000-000000000001");
    public static readonly Guid AttendanceOnParent = Guid.Parse("a2000000-0000-0000-0000-000000000002");
    public static readonly Guid Overtime = Guid.Parse("a3000000-0000-0000-0000-000000000001");
    public static readonly Guid Checklist = Guid.Parse("a4000000-0000-0000-0000-000000000001");

    public ZayraDbContext Db() => new(Options);

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        Options = new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(_container.GetConnectionString()).Options;
        await using (var db = Db()) await db.Database.EnsureCreatedAsync();
        await using (var db = Db())
        {
            S = await SeedAsync(db);
            BranchA = await db.Branches.Where(x => x.TenantId == S.Tenant && x.CompanyId == S.A).Select(x => x.Id).SingleAsync();
            var t = S.Tenant;
            db.ApprovalPolicies.Add(new ApprovalPolicy { Id = Policy, TenantId = t, WorkflowType = "Leave", Name = "Leave DC/GS", DepartmentId = S.ChildDept, GradeId = S.SharedGrade, IsActive = true });
            db.ApprovalPolicySteps.AddRange(
                new ApprovalPolicyStep { TenantId = t, PolicyId = Policy, StepOrder = 1, StepName = "Manager" },
                new ApprovalPolicyStep { TenantId = t, PolicyId = Policy, StepOrder = 2, StepName = "HR" });
            db.ApprovalWorkflows.Add(new ApprovalWorkflow { Id = Workflow, TenantId = t, Code = "WF1", Name = "Grade GS", GradeId = S.SharedGrade, IsActive = true });
            db.ApprovalWorkflowSteps.Add(new ApprovalWorkflowStep { TenantId = t, WorkflowId = Workflow, StepOrder = 1, StepName = "HR", ApproverRole = "HR Manager" });
            db.AttendancePolicies.AddRange(
                new AttendancePolicy { Id = AttendanceOnBranchA, TenantId = t, Code = "ATT-HQ", Name = "HQ parent", DepartmentId = S.ParentDept, BranchId = BranchA },
                new AttendancePolicy { Id = AttendanceOnParent, TenantId = t, Code = "ATT-DP", Name = "Parent dept", DepartmentId = S.ParentDept });
            db.AttendanceRules.Add(new AttendanceRule { TenantId = t, AttendancePolicyId = AttendanceOnParent, RuleType = "Grace", RuleValueJson = "{}" });
            db.OvertimePolicies.Add(new OvertimePolicy { Id = Overtime, TenantId = t, Code = "OT-GS", Name = "OT for GS", GradeId = S.SharedGrade });
            db.OvertimeMultipliers.Add(new OvertimeMultiplier { TenantId = t, OvertimePolicyId = Overtime, DayCategory = "Weekend", Multiplier = 1.5m });
            db.OvertimeRules.Add(new OvertimeRule { TenantId = t, OvertimePolicyId = Overtime, RuleType = "Cap", RuleValueJson = "{}" });
            // October: the stored 300 is exactly the approved overtime of DC's people (A1 100 in A, B2 200 in B), so it
            // can be re-derived per company. September: 500 with no overtime behind it, so the copy starts at 0.
            db.OvertimeBudgets.AddRange(
                new OvertimeBudget { TenantId = t, DepartmentId = S.ChildDept, Year = 2026, Month = 10, BudgetAmount = 1000, ConsumedAmount = 300, Currency = "SAR" },
                new OvertimeBudget { TenantId = t, DepartmentId = S.ChildDept, Year = 2026, Month = 9, BudgetAmount = 800, ConsumedAmount = 500, Currency = "SAR" });
            foreach (var (emp, amount) in new[] { (S.EmpA1, 100m), (S.EmpB2, 200m) })
            {
                var req = new OvertimeRequest { TenantId = t, EmployeeId = emp, WorkDate = new DateOnly(2026, 10, 5), Status = "Approved", RequestedMinutes = 60, ApprovedMinutes = 60 };
                db.OvertimeRequests.Add(req);
                db.OvertimeCalculations.Add(new OvertimeCalculation { TenantId = t, OvertimeRequestId = req.Id, EmployeeId = emp, ApprovedMinutes = 60, Amount = amount, Currency = "SAR" });
            }
            db.OnboardingChecklists.Add(new OnboardingChecklist { Id = Checklist, TenantId = t, Code = "ONB-DC", Name = "DC onboarding", DepartmentId = S.ChildDept });
            db.OnboardingChecklistTemplateTasks.Add(new OnboardingChecklistTemplateTask { TenantId = t, ChecklistId = Checklist, TaskTitle = "Laptop" });
            db.LeavePolicyEligibilities.AddRange(
                new LeavePolicyEligibility { TenantId = t, LeavePolicyId = Guid.NewGuid(), GradeId = S.SharedGrade },
                new LeavePolicyEligibility { TenantId = t, LeavePolicyId = Guid.NewGuid(), CompanyId = S.A, GradeId = S.SharedGrade });
            db.BenefitEligibilityRules.Add(new BenefitEligibilityRule { TenantId = t, BenefitPlanId = Guid.NewGuid(), GradeId = S.SharedGrade, EffectiveFrom = new DateOnly(2026, 1, 1) });
            db.JobOpenings.Add(new JobOpening { TenantId = t, JobCode = "JOB-1", Title = "Engineer", DepartmentId = S.ChildDept });
            await db.SaveChangesAsync();
        }
        await using (var db = Db()) await RunSqlAsync(db, Zayra.Api.Migrations.BackfillOrganisationPerCompany.BackfillSql);
        await using (var db = Db()) await RunSqlAsync(db, Zayra.Api.Migrations.CopyWorkspaceRulesPerCompany.CopyRulesSql);
        await using (var db = Db())
        {
            GradeB = await db.Grades.Where(x => x.TenantId == S.Tenant && x.Code == "GS" && x.CompanyId == S.B).Select(x => x.Id).SingleAsync();
            ChildA = await db.Departments.Where(x => x.TenantId == S.Tenant && x.Code == "DC" && x.CompanyId == S.A).Select(x => x.Id).SingleAsync();
            ParentB = await db.Departments.Where(x => x.TenantId == S.Tenant && x.Code == "DP" && x.CompanyId == S.B).Select(x => x.Id).SingleAsync();
        }
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[Trait("Category", "Integration")]
public sealed class OrganizationRuleCopyPostgresTests(RuleCopyWorld w) : IClassFixture<RuleCopyWorld>
{
    [Fact]
    public async Task ApprovalPolicy_GetsOneCopyPerCompany_PointedAtThatCompanysRows_WithItsSteps()
    {
        await using var db = w.Db();
        var policies = await db.ApprovalPolicies.Where(x => x.TenantId == w.S.Tenant && x.WorkflowType == "Leave").ToListAsync();
        policies.Should().HaveCount(3, "the original stays; A gets (its DC copy, GS) and B gets (DC, its GS copy)");
        policies.Should().ContainSingle(x => x.DepartmentId == w.ChildA && x.GradeId == w.S.SharedGrade);
        policies.Should().ContainSingle(x => x.DepartmentId == w.S.ChildDept && x.GradeId == w.GradeB);
        foreach (var copy in policies.Where(x => x.Id != RuleCopyWorld.Policy))
            (await db.ApprovalPolicySteps.Where(x => x.PolicyId == copy.Id).OrderBy(x => x.StepOrder).Select(x => x.StepName).ToListAsync())
                .Should().Equal("Manager", "HR");
    }

    [Fact]
    public async Task ApprovalWorkflow_IsCopiedWithAUniqueCodeAndItsSteps()
    {
        await using var db = w.Db();
        var copy = await db.ApprovalWorkflows.SingleAsync(x => x.TenantId == w.S.Tenant && x.Code.StartsWith("WF1-"));
        copy.GradeId.Should().Be(w.GradeB);
        copy.Name.Should().StartWith("Grade GS — ");
        (await db.ApprovalWorkflowSteps.CountAsync(x => x.WorkflowId == copy.Id)).Should().Be(1);
        (await db.ApprovalWorkflows.SingleAsync(x => x.Id == RuleCopyWorld.Workflow)).GradeId.Should().Be(w.S.SharedGrade, "the original keeps serving A");
    }

    [Fact]
    public async Task AttendancePolicy_IsCopiedWithItsRules_ButNotWhenItIsLimitedToAnotherCompanysBranch()
    {
        await using var db = w.Db();
        var copies = await db.AttendancePolicies.Where(x => x.TenantId == w.S.Tenant && x.DepartmentId == w.ParentB).ToListAsync();
        copies.Should().ContainSingle().Which.Code.Should().StartWith("ATT-DP-");
        (await db.AttendanceRules.CountAsync(x => x.AttendancePolicyId == copies[0].Id)).Should().Be(1);
        (await db.AttendancePolicies.CountAsync(x => x.TenantId == w.S.Tenant && x.Code.StartsWith("ATT-HQ"))).Should().Be(1,
            "a rule limited to A's branch never applied to B");
    }

    [Fact]
    public async Task OvertimePolicyAndBudget_AreCopied()
    {
        await using var db = w.Db();
        var policy = await db.OvertimePolicies.SingleAsync(x => x.TenantId == w.S.Tenant && x.Code.StartsWith("OT-GS-"));
        policy.GradeId.Should().Be(w.GradeB);
        (await db.OvertimeMultipliers.CountAsync(x => x.OvertimePolicyId == policy.Id)).Should().Be(1);
        (await db.OvertimeRules.CountAsync(x => x.OvertimePolicyId == policy.Id)).Should().Be(1);
        var budgets = await db.OvertimeBudgets.Where(x => x.TenantId == w.S.Tenant && x.Month == 10).ToListAsync();
        budgets.Select(x => x.DepartmentId).Should().BeEquivalentTo(new Guid?[] { w.S.ChildDept, w.ChildA });
    }

    [Fact]
    public async Task OvertimeBudgetCopy_NeverCarriesTheKeepersConsumption()
    {
        await using var db = w.Db();
        var budgets = await db.OvertimeBudgets.Where(x => x.TenantId == w.S.Tenant).ToListAsync();
        // DC is kept by B; A got the copy. Budget envelopes are duplicated unchanged (splitting them is the owner's call).
        var octB = budgets.Single(x => x.Month == 10 && x.DepartmentId == w.S.ChildDept);
        var octA = budgets.Single(x => x.Month == 10 && x.DepartmentId == w.ChildA);
        (octB.BudgetAmount, octA.BudgetAmount).Should().Be((1000m, 1000m));
        (octB.ConsumedAmount, octA.ConsumedAmount).Should().Be((200m, 100m), "re-derived from each company's approved overtime");
        var sepB = budgets.Single(x => x.Month == 9 && x.DepartmentId == w.S.ChildDept);
        var sepA = budgets.Single(x => x.Month == 9 && x.DepartmentId == w.ChildA);
        (sepB.ConsumedAmount, sepA.ConsumedAmount).Should().Be((500m, 0m), "not derivable: the keeper keeps its figure, the copy starts at 0");
        var methods = (await db.AuditLogs.Where(x => x.TenantId == w.S.Tenant && x.EntityName == "overtime_budgets").ToListAsync())
            .Select(x => JsonDocument.Parse(x.Metadata!).RootElement.GetProperty("consumedMethod").GetString()).ToList();
        methods.Should().BeEquivalentTo(new[] { "recomputed_from_overtime", "copies_zeroed" });
    }

    [Fact]
    public async Task OnboardingChecklist_IsCopiedWithItsTasks()
    {
        await using var db = w.Db();
        var copy = await db.OnboardingChecklists.SingleAsync(x => x.TenantId == w.S.Tenant && x.DepartmentId == w.ChildA);
        copy.Code.Should().StartWith("ONB-DC-");
        (await db.OnboardingChecklistTemplateTasks.Where(x => x.ChecklistId == copy.Id).Select(x => x.TaskTitle).ToListAsync()).Should().Equal("Laptop");
    }

    [Fact]
    public async Task CompanylessEligibilityRows_AreCopiedForTheCompany_CompanyRowsAreNot()
    {
        await using var db = w.Db();
        var leave = await db.LeavePolicyEligibilities.Where(x => x.TenantId == w.S.Tenant).ToListAsync();
        leave.Should().HaveCount(3);
        leave.Should().ContainSingle(x => x.CompanyId == w.S.B && x.GradeId == w.GradeB);
        leave.Should().ContainSingle(x => x.CompanyId == w.S.A && x.GradeId == w.S.SharedGrade, "a company row is not copied");
        var benefit = await db.BenefitEligibilityRules.Where(x => x.TenantId == w.S.Tenant).ToListAsync();
        benefit.Should().HaveCount(2);
        benefit.Should().ContainSingle(x => x.CompanyId == w.S.B && x.GradeId == w.GradeB);
    }

    [Fact]
    public async Task JobOpenings_StayOnTheKeepersRow_AndEveryCopyIsAuditedInAVerifiedChain()
    {
        await using var db = w.Db();
        (await db.JobOpenings.CountAsync(x => x.TenantId == w.S.Tenant)).Should().Be(1);
        (await db.JobOpenings.SingleAsync(x => x.TenantId == w.S.Tenant)).DepartmentId.Should().Be(w.S.ChildDept);
        var audits = await db.AuditLogs.Where(x => x.TenantId == w.S.Tenant).OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).ToListAsync();
        audits.Count(x => x.Action == "organization.company_backfill_rule_copied").Should().Be(10);
        AuditService.VerifyChain(audits).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task ReRunningTheBackfillAndTheRuleCopy_ChangesNothing()
    {
        string Snapshot(ZayraDbContext db) => JsonSerializer.Serialize(new
        {
            ap = db.ApprovalPolicies.OrderBy(x => x.Id).Select(x => new { x.Id, x.DepartmentId, x.GradeId }).ToList(),
            aps = db.ApprovalPolicySteps.Count(), aw = db.ApprovalWorkflows.Count(), aws = db.ApprovalWorkflowSteps.Count(),
            att = db.AttendancePolicies.Count(), ar = db.AttendanceRules.Count(), ot = db.OvertimePolicies.Count(),
            om = db.OvertimeMultipliers.Count(), orl = db.OvertimeRules.Count(), ob = db.OvertimeBudgets.OrderBy(x => x.Id).Select(x => new { x.Id, x.ConsumedAmount, x.BudgetAmount }).ToList(),
            oc = db.OnboardingChecklists.Count(), oct = db.OnboardingChecklistTemplateTasks.Count(),
            lpe = db.LeavePolicyEligibilities.Count(), ber = db.BenefitEligibilityRules.Count(), audit = db.AuditLogs.Count(),
        });
        string before;
        await using (var db = w.Db()) before = Snapshot(db);
        await using (var db = w.Db()) await RunSqlAsync(db, Zayra.Api.Migrations.BackfillOrganisationPerCompany.BackfillSql);
        await using (var db = w.Db()) await RunSqlAsync(db, Zayra.Api.Migrations.CopyWorkspaceRulesPerCompany.CopyRulesSql);
        await using (var db = w.Db()) Snapshot(db).Should().Be(before);
    }
}
