using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Compensation;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Compensation;

/// <summary>
/// The CMP golden world: one SAR company, two departments, three grades, two line managers with seven reports each,
/// HR and Finance users, published ratings, and one employee paid in AED. Every number the golden test asserts is
/// derived by hand from the constants below (see CompensationGoldenReconciliationTests), never through MeritMath.
/// </summary>
public sealed class MeritWorld
{
    public Guid Tenant, Company, CompanyB, Eng, Ops, Corp, G1, G2, G3, DesigEng, DesigOps;
    public readonly Dictionary<string, int> E = new(StringComparer.Ordinal);
    public Guid UserManagerA = Guid.NewGuid(), UserManagerB = Guid.NewGuid(), UserHr = Guid.NewGuid(), UserHr2 = Guid.NewGuid(),
        UserFinance = Guid.NewGuid(), UserStranger = Guid.NewGuid();
    public int ManagerA => E["MGR-A"];
    public int ManagerB => E["MGR-B"];
}

internal static class MeritTestKit
{
    public static readonly DateOnly PeriodStart = new(2025, 11, 1);
    public static readonly DateOnly PeriodEnd = new(2026, 10, 31);   // 365 days
    public static readonly DateOnly Effective = new(2026, 11, 1);
    public static readonly DateOnly Cutoff = new(2026, 8, 31);

    // One key ring for apply (seals the baseline) and the job (opens it).
    public static readonly IDataProtectionProvider Keys = new EphemeralDataProtectionProvider();

    public static readonly string[] HrPermissions =
    [
        CompPermissions.Read, CompPermissions.Manage, CompPermissions.Approve, CompPermissions.BenchmarksManage, CompPermissions.EquityRead,
        "approvals.read", "approvals.decide", "employees.read",
    ];

    // (code, dept, grade, basic, housing, transport, currency, joined, rating, manager)
    private static readonly (string Code, string Dept, string Grade, decimal Basic, decimal Housing, decimal Transport, string Currency, DateTime Joined, string? Rating, string? Manager, string Gender)[] People =
    [
        ("MGR-A", "Eng", "G3", 20000, 5000, 1000, "SAR", new(2018, 1, 1), "Exceeds", null, "Male"),
        ("MGR-B", "Ops", "G3", 20000, 5000, 1000, "SAR", new(2018, 1, 1), "Meets", null, "Female"),
        ("HR-1", "Corp", "G3", 18000, 4500, 1000, "SAR", new(2018, 1, 1), null, null, "Female"),
        ("HR-2", "Corp", "G3", 18000, 4500, 1000, "SAR", new(2018, 1, 1), null, null, "Male"),
        ("FIN-1", "Corp", "G3", 18000, 4500, 1000, "SAR", new(2018, 1, 1), null, null, "Male"),
        ("A1", "Eng", "G1", 6000, 1500, 500, "SAR", new(2020, 1, 1), "Exceeds", "MGR-A", "Male"),
        ("A2", "Eng", "G1", 7000, 1750, 500, "SAR", new(2020, 1, 1), "Meets", "MGR-A", "Female"),
        ("A3", "Eng", "G1", 8000, 2000, 500, "SAR", new(2020, 1, 1), "Meets", "MGR-A", "Male"),
        ("A4", "Eng", "G1", 6500, 1625, 500, "SAR", new(2020, 1, 1), "Exceeds", "MGR-A", "Female"),
        ("A5", "Eng", "G1", 6000, 1500, 500, "SAR", new(2026, 5, 1), null, "MGR-A", "Male"),       // joiner: 184/365 days
        ("A6", "Eng", "G1", 6000, 1500, 500, "AED", new(2020, 1, 1), "Meets", "MGR-A", "Female"),  // paid in AED
        ("A7", "Eng", "G1", 6000, 1500, 500, "SAR", new(2026, 10, 1), null, "MGR-A", "Male"),      // after the hire cutoff
        ("B1", "Ops", "G2", 9000, 2250, 750, "SAR", new(2019, 1, 1), "Exceeds", "MGR-B", "Male"),
        ("B2", "Ops", "G2", 10000, 2500, 1000, "SAR", new(2019, 1, 1), "Meets", "MGR-B", "Female"),
        ("B3", "Ops", "G1", 7500, 1875, 500, "SAR", new(2019, 1, 1), "Meets", "MGR-B", "Male"),
        ("B4", "Ops", "G1", 6000, 1500, 0, "SAR", new(2019, 1, 1), "Exceeds", "MGR-B", "Female"),
        ("B5", "Ops", "G1", 6800, 1700, 500, "SAR", new(2019, 1, 1), null, "MGR-B", "Male"),
        ("B6", "Ops", "G1", 7200, 1800, 500, "SAR", new(2019, 1, 1), "Meets", "MGR-B", "Female"),
    ];

    public static async Task<MeritWorld> SeedAsync(PostgresFixture fx)
    {
        var w = new MeritWorld();
        await using var db = fx.CreateDb();
        w.Tenant = await PostgresFixture.SeedMinimalTenant(db);
        var company = new Company
        {
            Id = Guid.NewGuid(), TenantId = w.Tenant, LegalNameEn = "Golden Merit Co", CountryCode = "SA", Jurisdiction = "KSA-mainland",
            RegistrationNumber = $"GM-{Guid.NewGuid():N}"[..20], DefaultCurrency = "SAR", IsActive = true, CreatedAtUtc = new DateTime(2018, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var companyB = new Company
        {
            Id = Guid.NewGuid(), TenantId = w.Tenant, LegalNameEn = "Other Co", CountryCode = "AE", Jurisdiction = "UAE-mainland",
            RegistrationNumber = $"OC-{Guid.NewGuid():N}"[..20], DefaultCurrency = "AED", IsActive = true, CreatedAtUtc = new DateTime(2018, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Companies.AddRange(company, companyB);
        w.Company = company.Id; w.CompanyB = companyB.Id;
        var eng = new Department { TenantId = w.Tenant, Code = "ENG", NameEn = "Engineering", NameAr = "الهندسة" };
        var ops = new Department { TenantId = w.Tenant, Code = "OPS", NameEn = "Operations", NameAr = "العمليات" };
        var corp = new Department { TenantId = w.Tenant, Code = "CORP", NameEn = "Corporate", NameAr = "الإدارة" };
        db.Departments.AddRange(eng, ops, corp);
        w.Eng = eng.Id; w.Ops = ops.Id; w.Corp = corp.Id;
        var g1 = new Grade { TenantId = w.Tenant, Code = "G1", Name = "Grade 1", MinSalary = 8000, MidSalary = 10000, MaxSalary = 12000, Currency = "SAR" };
        var g2 = new Grade { TenantId = w.Tenant, Code = "G2", Name = "Grade 2", MinSalary = 12000, MidSalary = 15000, MaxSalary = 18000, Currency = "SAR" };
        var g3 = new Grade { TenantId = w.Tenant, Code = "G3", Name = "Grade 3", MinSalary = 20000, MidSalary = 26000, MaxSalary = 40000, Currency = "SAR" };
        db.Grades.AddRange(g1, g2, g3);
        w.G1 = g1.Id; w.G2 = g2.Id; w.G3 = g3.Id;
        var dEng = new Designation { TenantId = w.Tenant, Code = "SWE", TitleEn = "Software Engineer", TitleAr = "مهندس برمجيات", DepartmentId = eng.Id };
        var dOps = new Designation { TenantId = w.Tenant, Code = "OPA", TitleEn = "Operations Analyst", TitleAr = "محلل عمليات", DepartmentId = ops.Id };
        db.Designations.AddRange(dEng, dOps);
        w.DesigEng = dEng.Id; w.DesigOps = dOps.Id;
        var structures = new Dictionary<string, SalaryStructure>();
        foreach (var g in new[] { g1, g2, g3 })
        {
            var s = new SalaryStructure { TenantId = w.Tenant, CompanyId = company.Id, Code = $"GRADE-{g.Code}", Name = $"{g.Name} structure", Currency = "SAR", EffectiveDate = new DateOnly(2018, 1, 1) };
            structures[g.Code] = s;
            db.SalaryStructures.Add(s);
        }
        await db.SaveChangesAsync();

        var users = new Dictionary<string, Guid> { ["MGR-A"] = w.UserManagerA, ["MGR-B"] = w.UserManagerB, ["HR-1"] = w.UserHr, ["HR-2"] = w.UserHr2, ["FIN-1"] = w.UserFinance };
        var grades = new Dictionary<string, Grade> { ["G1"] = g1, ["G2"] = g2, ["G3"] = g3 };
        var depts = new Dictionary<string, Department> { ["Eng"] = eng, ["Ops"] = ops, ["Corp"] = corp };
        foreach (var p in People)
        {
            var e = new Employee
            {
                TenantId = w.Tenant, CompanyId = company.Id, EmployeeCode = $"{p.Code}-{Guid.NewGuid():N}"[..12], FullName = $"Person {p.Code}",
                Status = "Active", JoiningDate = DateTime.SpecifyKind(p.Joined, DateTimeKind.Utc), Gender = p.Gender, Nationality = "Saudi",
                DepartmentId = depts[p.Dept].Id, Department = depts[p.Dept].NameEn, GradeId = grades[p.Grade].Id, Grade = p.Grade,
                DesignationId = p.Dept == "Ops" ? dOps.Id : dEng.Id, UserAccountId = users.GetValueOrDefault(p.Code),
                Salary = p.Basic + p.Housing + p.Transport,
            };
            db.Employees.Add(e);
            await db.SaveChangesAsync();
            w.E[p.Code] = e.Id;
        }
        foreach (var p in People.Where(x => x.Manager is not null))
            (await db.Employees.SingleAsync(e => e.Id == w.E[p.Code])).ManagerEmployeeId = w.E[p.Manager!];
        foreach (var p in People)
            db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
            {
                TenantId = w.Tenant, EmployeeId = w.E[p.Code], SalaryStructureId = structures[p.Grade].Id, BasicSalary = p.Basic,
                HousingAllowance = p.Housing, TransportAllowance = p.Transport, EffectiveDate = new DateOnly(2024, 1, 1), Currency = p.Currency,
            });
        // Published ratings from a review period that ended inside the merit performance period.
        var perf = new PerformanceCycle { TenantId = w.Tenant, Name = "FY2026 mid-year", ReviewPeriodStart = new DateOnly(2026, 1, 1), ReviewPeriodEnd = new DateOnly(2026, 6, 30), Status = "Published" };
        db.PerformanceCycles.Add(perf);
        foreach (var p in People.Where(x => x.Rating is not null))
            db.AppraisalReviews.Add(new AppraisalReview
            {
                TenantId = w.Tenant, CycleId = perf.Id, EmployeeId = w.E[p.Code], FinalRating = p.Rating!, Status = "Published",
                PublishedAt = new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc),
            });
        await db.SaveChangesAsync();
        return w;
    }

    public static MeritPlanningService Service(ZayraDbContext db) =>
        new(db, new AuditService(db), Approvals(db), null, Keys, NullLogger<MeritPlanningService>.Instance);

    public static ApprovalWorkflowService Approvals(ZayraDbContext db) =>
        new(db, new AuditService(db), new HrmHierarchyService(db, new AuditService(db)), dataProtection: Keys);

    public static RequestContext Context(Guid tenant, Guid? user, IEnumerable<string> roles, IEnumerable<string> permissions) =>
        new("127.0.0.1", "cmp-test", user, tenant, roles.ToList(), permissions.ToList());

    public static CompCaller Hr(MeritWorld w, bool second = false) => Caller(w, second ? w.UserHr2 : w.UserHr, second ? "HR Two" : "HR One",
        w.E[second ? "HR-2" : "HR-1"], ["HR Manager"], HrPermissions);

    public static CompCaller Finance(MeritWorld w) => Caller(w, w.UserFinance, "Finance One", w.E["FIN-1"], ["Finance Approver"],
        [CompPermissions.Read, CompPermissions.Approve, "approvals.read", "approvals.decide"]);

    public static CompCaller Manager(MeritWorld w, bool b = false) => Caller(w, b ? w.UserManagerB : w.UserManagerA, b ? "Manager B" : "Manager A",
        b ? w.ManagerB : w.ManagerA, ["Manager"], [CompPermissions.Plan, "approvals.read", "approvals.decide", "approvals.write"]);

    public static CompCaller Caller(MeritWorld w, Guid user, string name, int? employeeId, string[] roles, string[] permissions) =>
        new(w.Tenant, user, name, permissions.ToHashSet(StringComparer.OrdinalIgnoreCase), employeeId, Context(w.Tenant, user, roles, permissions));

    public static CompEligibilityInput Eligibility(MeritWorld w) =>
        new(Cutoff, ["Active"], null, [w.G3], null, null, 30, CompProrationRules.EligibleDays);

    public static readonly CompGuidelineInput[] Matrix =
    [
        new("Exceeds", 0m, 0.9m, 4m, 6m, 8m), new("Exceeds", 0.9m, null, 3m, 4m, 6m),
        new("Meets", 0m, 0.9m, 2m, 3m, 5m), new("Meets", 0.9m, null, 1m, 2m, 4m),
        new("Unrated", 0m, null, 0m, 2m, 3m),
    ];

    /// <summary>Draft cycle with matrix and budgets (Eng 4 % of eligible basic, Ops fixed 1,500.00 SAR), FX AED 1.02.</summary>
    public static async Task<CompCycleDto> ConfiguredCycleAsync(PostgresFixture fx, MeritWorld w, bool withFx = true, DateOnly? effective = null)
    {
        await using var db = fx.CreateDb();
        var svc = Service(db);
        var hr = Hr(w);
        var cycle = await svc.CreateCycleAsync(hr, new CreateCompCycleRequest(w.Company, $"Merit {Guid.NewGuid():N}"[..14], PeriodStart, PeriodEnd, effective ?? Effective, Eligibility(w)), default);
        await svc.SaveGuidelinesAsync(hr, cycle.Id, new SaveCompGuidelinesRequest(Matrix), default);
        cycle = await svc.SaveBudgetsAsync(hr, cycle.Id, new SaveCompBudgetsRequest([
            new(w.Eng, CompBudgetPoolTypes.PercentOfEligibleBasic, 4m, null),
            new(w.Ops, CompBudgetPoolTypes.FixedAmount, null, 1500m),
        ]), default);
        if (withFx) cycle = await svc.SaveFxAsync(hr, cycle.Id, new SaveCompFxRequest([new("AED", 1.02m, "SAMA reference rate", new DateOnly(2026, 10, 1))]), default);
        return cycle;
    }

    public static async Task<CompCycleDto> OpenAsync(PostgresFixture fx, MeritWorld w, CompCycleDto cycle)
    {
        await using var db = fx.CreateDb();
        return await Service(db).OpenPlanningAsync(Hr(w), cycle.Id, new CompCycleTransitionRequest(cycle.Version), default);
    }

    public static async Task<CompWorksheetDto> WorksheetOfAsync(PostgresFixture fx, MeritWorld w, Guid cycleId, bool managerB = false, bool asHr = false)
    {
        await using var db = fx.CreateDb();
        var managerId = managerB ? w.ManagerB : w.ManagerA;
        var id = await db.CompWorksheets.Where(s => s.CycleId == cycleId && s.ManagerEmployeeId == managerId && s.IsPrimary).Select(s => s.Id).SingleAsync();
        return await Service(db).GetWorksheetAsync(asHr ? Hr(w) : Manager(w, managerB), id, default);
    }

    public static CompLineInput Pct(MeritWorld w, string code, decimal pct, string? why = null) => new(w.E[code], null, pct, why);
    public static CompLineInput Amt(MeritWorld w, string code, decimal amount, string? why = null) => new(w.E[code], amount, null, why);

    public static readonly string Justification = "Retention risk: two competing offers on file; agreed with HRBP.";

    public static IReadOnlyList<CompLineInput> ProposalsA(MeritWorld w) =>
    [
        Pct(w, "A1", 6m), Pct(w, "A2", 3m), Pct(w, "A3", 5m, Justification), Amt(w, "A4", 390m), Pct(w, "A5", 2m), Pct(w, "A6", 3m),
    ];

    public static IReadOnlyList<CompLineInput> ProposalsB(MeritWorld w) =>
    [
        Pct(w, "B1", 4m), Pct(w, "B2", 2m), Pct(w, "B3", 2.5m), Amt(w, "B4", 500m, "Brings pay to the grade minimum."), Pct(w, "B5", 0m), Pct(w, "B6", 1m),
    ];

    /// <summary>Opens planning, saves both managers' proposals (A with an overspend justification) and submits both.</summary>
    public static async Task<(CompCycleDto Cycle, CompWorksheetDto A, CompWorksheetDto B)> SubmittedAsync(PostgresFixture fx, MeritWorld w, DateOnly? effective = null)
    {
        var cycle = await OpenAsync(fx, w, await ConfiguredCycleAsync(fx, w, effective: effective));
        var a = await WorksheetOfAsync(fx, w, cycle.Id);
        var b = await WorksheetOfAsync(fx, w, cycle.Id, managerB: true);
        await using var db = fx.CreateDb();
        var svc = Service(db);
        a = await svc.SaveWorksheetAsync(Manager(w), a.Id, new SaveCompWorksheetRequest(a.Version, "Engineering retention plan approved by the CTO.", ProposalsA(w)), default);
        a = await svc.SubmitWorksheetAsync(Manager(w), a.Id, new SubmitCompWorksheetRequest(a.Version), default);
        b = await svc.SaveWorksheetAsync(Manager(w, true), b.Id, new SaveCompWorksheetRequest(b.Version, null, ProposalsB(w)), default);
        b = await svc.SubmitWorksheetAsync(Manager(w, true), b.Id, new SubmitCompWorksheetRequest(b.Version), default);
        return (cycle, a, b);
    }

    /// <summary>Both approval steps (HR Manager → Finance Approver) through the shared engine.</summary>
    public static async Task ApproveBothStepsAsync(PostgresFixture fx, MeritWorld w, Guid approvalId)
    {
        await DecideAsync(fx, w, approvalId, w.UserHr2, ["HR Manager"], "Approve", "HR checked");
        await DecideAsync(fx, w, approvalId, w.UserFinance, ["Finance Approver"], "Approve", "Within plan");
    }

    public static async Task<ApprovalRequestDto?> DecideAsync(PostgresFixture fx, MeritWorld w, Guid approvalId, Guid user, string[] roles, string decision, string? comments,
        string[]? permissions = null)
    {
        await using var db = fx.CreateDb();
        return await Approvals(db).DecideAsync(w.Tenant, approvalId, new ApprovalDecisionRequest(decision, comments),
            Context(w.Tenant, user, roles, permissions ?? ["approvals.decide", CompPermissions.Approve]), default);
    }

    /// <summary>Submitted → both worksheets approved → review → cycle approved (by a different user) → applied.</summary>
    public static async Task<CompCycleDto> AppliedAsync(PostgresFixture fx, MeritWorld w, DateOnly? effective = null)
    {
        var (cycle, a, b) = await SubmittedAsync(fx, w, effective);
        await ApproveBothStepsAsync(fx, w, a.ApprovalRequestId!.Value);
        await ApproveBothStepsAsync(fx, w, b.ApprovalRequestId!.Value);
        await using var db = fx.CreateDb();
        var svc = Service(db);
        var current = await svc.GetCycleAsync(Hr(w), cycle.Id, default);
        current = await svc.StartReviewAsync(Hr(w), cycle.Id, new CompCycleTransitionRequest(current.Version), default);
        current = await svc.ApproveCycleAsync(Finance(w), cycle.Id, new CompCycleTransitionRequest(current.Version), default);
        return await svc.ApplyAsync(Hr(w), cycle.Id, default);
    }

    /// <summary>The effective-date job's own decision for one change, in its own transaction (as the queue runs it).</summary>
    public static async Task<EffectiveChangeResult> RunJobAsync(PostgresFixture fx, Guid tenant, Guid changeId, DateOnly localToday)
    {
        await using var db = fx.CreateDb();
        var handler = new EffectiveChangeJobHandler(new EffectiveChangeOptions(), NullLogger<EffectiveChangeJobHandler>.Instance, Keys);
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            // The services the app registers for the job: CMP's effective-date gate (AddCompensation) included.
            var services = new ServiceCollection().AddSingleton<IEffectiveChangeGate, MeritEffectiveChangeGate>().BuildServiceProvider();
            var result = await handler.ProcessAsync(db, services, tenant, changeId, localToday, null, default);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return result;
        });
    }

    // ── payroll (the PayrollController.Process path) ──

    public static PayrollController Payroll(ZayraDbContext db, Guid tenantId)
    {
        var rules = new StubRuleReader()
            .Set("gosi.saudi_employee_rate", 0.09m).Set("gosi.saudi_employer_rate", 0.09m).Set("gosi.saned_rate", 0.0075m)
            .Set("gosi.expat_occupational_hazard_rate", 0.02m).Set("gosi.covered_wage_ceiling_sar", 45_000m)
            .Set("ot.standard_multiplier", 1.5m).Set("ot.standard_monthly_hours", 240m)
            .Set("lop.monthly_day_divisor", 30m).Set("lop.standard_work_minutes_per_day", 480m);
        var ctrl = new PayrollController(db, new DataScopeService(db), new HttpContextAccessor(), new QuietNotifications(),
            new PlainPackResolver(rules), rules, new NoLetters(), new NullDocumentStorage(), new Zayra.Api.Infrastructure.Documents.PdfRenderGate(8));
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", tenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.Role, "Admin"), new Claim("permission", "payroll.read"), new Claim("permission", "payroll.write"),
                    new Claim("permission", "employees.read"), new Claim("is_group_scope", "true"),
                }, "Test")),
            },
        };
        return ctrl;
    }

    public static string Json(object o) => JsonSerializer.Serialize(o);
}

internal sealed class PlainPackResolver(IStatutoryRuleReader rules) : ICountryPackResolver
{
    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j) =>
        cc is "SAU" or "SA" ? new Zayra.Api.Infrastructure.CountryPack.Ksa.KsaDeductionCalculator(rules) : new DefaultStatutoryDeductionCalculator();
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}

internal sealed class QuietNotifications : INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class NoLetters : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
}
