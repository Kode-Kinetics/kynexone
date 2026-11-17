using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Benefits;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// A tenant on Benefits by grade (release_a) may still hold enrolments from the previous benefits system. Three rules:
/// no new legacy enrolment can be made; a legacy enrolment that carries payment terms never stops the company's payroll
/// (it is not paid, and the run says so, naming the employee and the enrolment); and HR can list what is left to clean up.
/// </summary>
public class LegacyEnrollmentUnderReleaseATests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // ── 1. Enrol is refused like its sibling writers ──────────────────────────────────────────────

    [Fact]
    public async Task Enroll_IsRefusedWithMovedToBenefitsByGrade_WhenReleaseAIsOn_AndWritesNothing()
    {
        await using var db = InMemory();
        var (tid, _, employee, plan) = await SeedLegacyAsync(db, releaseA: true);

        var result = await Benefits(db, tid).Enroll(new BenefitEnrollmentRequest(plan.Id, employee.Id, null, new DateOnly(2026, 6, 1), null), default);

        var conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
        JsonSerializer.SerializeToElement(conflict.Value, Web).GetProperty("error").GetString().Should().Be("moved_to_benefits_by_grade");
        (await db.BenefitEnrollments.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Enroll_IsNotRefusedAsMoved_WhenReleaseAIsOff()
    {
        await using var db = InMemory();
        var (tid, _, employee, plan) = await SeedLegacyAsync(db, releaseA: false);

        var result = await Benefits(db, tid).Enroll(new BenefitEnrollmentRequest(plan.Id, employee.Id, null, new DateOnly(2026, 6, 1), null), default);

        if (result is ConflictObjectResult c)
            JsonSerializer.Serialize(c.Value, Web).Should().NotContain("moved_to_benefits_by_grade");
    }

    // ── 2. Payroll skips a legacy enrolment with payment terms, and says so ───────────────────────

    [Theory]
    [InlineData("SalaryAllowance")]
    [InlineData("PayrollDeduction")]
    [InlineData("Reimbursement")]
    public async Task ARunWithALegacyPayingEnrolment_UnderReleaseA_Completes_PaysNothingFromIt_AndWarnsNamingTheEmployeeAndEnrolment(string delivery)
    {
        var (db, conn) = PayrollProrationArrearsTests.CreateDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var (companyId, a, b) = await PayrollProrationArrearsTests.SeedAsync(db, tid);
        db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = tid, FeatureKey = FeatureKeys.ReleaseA, IsEnabled = true });
        var plan = Plan(tid, companyId);
        db.BenefitPlans.Add(plan);
        var enrolment = Enrolment(tid, companyId, a, plan, delivery);
        db.BenefitEnrollments.Add(enrolment);
        var run = PayrollProrationArrearsTests.AddRun(db, tid, companyId, 2026, 6);
        await db.SaveChangesAsync();

        var result = await PayrollProrationArrearsTests.Ctrl(db, tid).Process(run.Id, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>("a retired benefit's terms must not stop the whole company's payroll");
        (await db.PayrollAdjustments.AsNoTracking().AnyAsync(x => x.PayrollRunId == run.Id)).Should().BeFalse("the legacy terms are never paid under release_a");
        var slips = await db.PayrollSlips.AsNoTracking().Where(s => s.RunId == run.Id).ToListAsync();
        slips.Should().HaveCount(2);
        slips.Should().OnlyContain(s => s.GrossSalary == 13_000m);

        var warning = (await db.PayrollValidationResults.AsNoTracking().Where(v => v.PayrollRunId == run.Id
            && v.Code == BenefitPayroll.LegacyEnrollmentSkippedCode).ToListAsync()).Should().ContainSingle().Subject;
        warning.Severity.Should().Be("Warning");
        warning.EmployeeId.Should().Be(a.Id);
        warning.Message.Should().Contain(a.EmployeeCode).And.Contain(a.FullName).And.Contain(enrolment.Id.ToString()).And.Contain(plan.Name);
    }

    [Fact]
    public async Task TheSkipWarning_SurvivesAValidateRefresh()
    {
        var (db, conn) = PayrollProrationArrearsTests.CreateDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var (companyId, a, _) = await PayrollProrationArrearsTests.SeedAsync(db, tid);
        db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = tid, FeatureKey = FeatureKeys.ReleaseA, IsEnabled = true });
        var plan = Plan(tid, companyId);
        db.BenefitPlans.Add(plan);
        db.BenefitEnrollments.Add(Enrolment(tid, companyId, a, plan, "SalaryAllowance"));
        var run = PayrollProrationArrearsTests.AddRun(db, tid, companyId, 2026, 6);
        await db.SaveChangesAsync();
        var ctrl = PayrollProrationArrearsTests.Ctrl(db, tid);
        (await ctrl.Process(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        (await ctrl.Validate(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        (await db.PayrollValidationResults.AsNoTracking().CountAsync(v => v.PayrollRunId == run.Id
            && v.Code == BenefitPayroll.LegacyEnrollmentSkippedCode && v.EmployeeId == a.Id)).Should().Be(1);
    }

    [Fact]
    public async Task AnEndedOrCoverageOnlyLegacyEnrolment_RaisesNoRunWarning()
    {
        var (db, conn) = PayrollProrationArrearsTests.CreateDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var (companyId, a, b) = await PayrollProrationArrearsTests.SeedAsync(db, tid);
        db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = tid, FeatureKey = FeatureKeys.ReleaseA, IsEnabled = true });
        var plan = Plan(tid, companyId);
        db.BenefitPlans.Add(plan);
        var ended = Enrolment(tid, companyId, a, plan, "SalaryAllowance");
        ended.EffectiveTo = new DateOnly(2026, 4, 30);
        db.BenefitEnrollments.AddRange(ended, Enrolment(tid, companyId, b, plan, "Coverage"));
        var run = PayrollProrationArrearsTests.AddRun(db, tid, companyId, 2026, 6);
        await db.SaveChangesAsync();

        (await PayrollProrationArrearsTests.Ctrl(db, tid).Process(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        (await db.PayrollValidationResults.AsNoTracking().AnyAsync(v => v.PayrollRunId == run.Id && v.Code == BenefitPayroll.LegacyEnrollmentSkippedCode))
            .Should().BeFalse();
    }

    // ── 3. The reconcile report ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReconcileReport_ListsEveryLegacyEnrolment_UnderReleaseA_WithWhetherItCarriesPayTerms_AndIsReadOnly()
    {
        await using var db = InMemory();
        var (tid, companyId, employee, plan) = await SeedLegacyAsync(db, releaseA: true);
        var paying = Enrolment(tid, companyId, employee, plan, "SalaryAllowance");
        var coverage = Enrolment(tid, companyId, employee, plan, "Coverage");
        coverage.EffectiveFrom = new DateOnly(2025, 1, 1);
        coverage.EffectiveTo = new DateOnly(2025, 12, 31);
        coverage.Status = "Ended";
        var otherTenant = Enrolment(Guid.NewGuid(), companyId, employee, plan, "SalaryAllowance");
        db.BenefitEnrollments.AddRange(paying, coverage, otherTenant);
        await db.SaveChangesAsync();
        var before = await db.BenefitEnrollments.AsNoTracking().OrderBy(e => e.Id).Select(e => new { e.Id, e.Status, e.EffectiveTo }).ToListAsync();

        var report = Ok<LegacyBenefitReconcileReport>(await Benefits(db, tid).LegacyReconcile(default));

        report.ReleaseA.Should().BeTrue();
        report.Items.Select(i => i.EnrollmentId).Should().BeEquivalentTo([paying.Id, coverage.Id], "another tenant's rows never appear");
        var row = report.Items.Single(i => i.EnrollmentId == paying.Id);
        (row.EmployeeId, row.EmployeeCode, row.EmployeeName, row.PlanCode, row.PlanName, row.Delivery, row.CarriesPayTerms, row.Status)
            .Should().Be((employee.Id, employee.EmployeeCode, employee.FullName, plan.Code, plan.Name, "SalaryAllowance", true, "Active"));
        report.Items.Single(i => i.EnrollmentId == coverage.Id).CarriesPayTerms.Should().BeFalse();
        (await db.BenefitEnrollments.AsNoTracking().OrderBy(e => e.Id).Select(e => new { e.Id, e.Status, e.EffectiveTo }).ToListAsync())
            .Should().Equal(before, "the report changes nothing");
    }

    [Fact]
    public async Task ReconcileReport_IsEmpty_WhenReleaseAIsOff_BecauseTheLegacySystemIsStillTheAuthority()
    {
        await using var db = InMemory();
        var (tid, companyId, employee, plan) = await SeedLegacyAsync(db, releaseA: false);
        db.BenefitEnrollments.Add(Enrolment(tid, companyId, employee, plan, "SalaryAllowance"));
        await db.SaveChangesAsync();

        var report = Ok<LegacyBenefitReconcileReport>(await Benefits(db, tid).LegacyReconcile(default));

        report.ReleaseA.Should().BeFalse();
        report.Items.Should().BeEmpty();
    }

    [Fact]
    public void ReconcileReport_HasTheSameGateAsTheEnrolmentList()
    {
        var list = typeof(BenefitsController).GetMethod(nameof(BenefitsController.ListEnrollments))!;
        var report = typeof(BenefitsController).GetMethod(nameof(BenefitsController.LegacyReconcile))!;
        static string Gate(System.Reflection.MethodInfo m) => string.Join("|", m.GetCustomAttributes(true)
            .Select(a => a switch
            {
                Zayra.Api.Infrastructure.Authorization.HasPermissionAttribute p => "perm:" + string.Join(",", p.Permissions),
                Microsoft.AspNetCore.Authorization.AuthorizeAttribute z => "roles:" + z.Roles,
                _ => null,
            }).Where(x => x is not null).OrderBy(x => x));
        Gate(report).Should().Be(Gate(list));
        report.GetCustomAttributes(typeof(HttpGetAttribute), true).Should().ContainSingle("the report is read-only");
    }

    // ── harness ───────────────────────────────────────────────────────────────────────────────────

    private static T Ok<T>(IActionResult result) => (T)result.Should().BeOfType<OkObjectResult>().Subject.Value!;

    private static ZayraDbContext InMemory() => new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static BenefitsController Benefits(ZayraDbContext db, Guid tid)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tid.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new(ClaimTypes.Role, "HR Manager"),
            new("permission", "employees.write"), new("permission", "employees.approve"),
        };
        return new BenefitsController(db)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) } },
        };
    }

    private static async Task<(Guid Tid, Guid CompanyId, Employee Employee, BenefitPlan Plan)> SeedLegacyAsync(ZayraDbContext db, bool releaseA)
    {
        var tid = Guid.NewGuid();
        var company = new Company { TenantId = tid, LegalNameEn = "Legacy Benefits Co", CountryCode = "SAU", IsActive = true, DefaultCurrency = "SAR" };
        var employee = new Employee { TenantId = tid, CompanyId = company.Id, EmployeeCode = "LB01", FullName = "Synthetic Legacy Holder",
            Status = "Active", JoiningDate = new DateTime(2024, 1, 1) };
        var plan = Plan(tid, company.Id);
        db.AddRange(company, employee, plan);
        if (releaseA) db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = tid, FeatureKey = FeatureKeys.ReleaseA, IsEnabled = true });
        await db.SaveChangesAsync();
        return (tid, company.Id, employee, plan);
    }

    private static BenefitPlan Plan(Guid tid, Guid companyId) => new()
    {
        TenantId = tid, CompanyId = companyId, Code = "LEG-CAR", Name = "Legacy car benefit", PlanType = "Allowance", Currency = "SAR",
        EffectiveFrom = new DateOnly(2024, 1, 1), IsActive = true,
    };

    private static BenefitEnrollment Enrolment(Guid tid, Guid companyId, Employee employee, BenefitPlan plan, string delivery) => new()
    {
        TenantId = tid, CompanyId = companyId, EmployeeId = employee.Id, EmployeeName = employee.FullName, BenefitPlanId = plan.Id,
        AssignmentSource = "Manual", EffectiveFrom = new DateOnly(2026, 1, 1), Status = "Active", LimitPeriod = "Annual",
        PaymentPolicySnapshotJson = JsonSerializer.Serialize(new BenefitPayrollPolicyEnvelope(1, plan.Id, "SAR",
            new BenefitPayrollPolicy(delivery, delivery == "Coverage" ? null : 750m),
            new BenefitPayrollComponent(Guid.NewGuid(), delivery == "PayrollDeduction" ? "BEN_DED" : "BEN_CAR", "Legacy car",
                delivery == "PayrollDeduction" ? "Deduction" : "Earning", false)), Web),
    };
}
