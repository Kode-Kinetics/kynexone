using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Learning;
using Zayra.Api.Infrastructure.Modules;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Infrastructure.Succession;
using Zayra.Api.Models;
using Zayra.Api.Tests.Learning;

namespace Zayra.Api.Tests.Succession;

/// <summary>
/// One disposable succession world on the shared Postgres container, wiring the REAL succession, learning, approval,
/// audit, notification, module-state and data-scope services as Program.cs does, with the DbContext's tenant/company
/// filters driven by the principal each scope runs as.
///
/// <para>Tenant A, company A1: HR Director (Hana), HR Manager (Hamid), the accountable executive (Eman), the incumbent of
/// the CFO position (Ines), a line manager (Majid) with two competing candidates (Cara, high performer; Cody, moderate) and
/// Rana who reports to Cara; Pete a peer with no appraisal (Unrated); Uma an unrelated manager with her own report (Ulf).
/// Company A2: Remy. Tenant B: Bilal and his own critical-position candidate. Succession and learning are on in both.</para>
/// </summary>
public sealed class SuccessionTestWorld : IAsyncDisposable
{
    private readonly PostgresFixture _fx;
    public ServiceProvider Services { get; }
    public ShiftableClock Clock { get; } = new(DateTimeOffset.UtcNow);

    public Guid TenantA { get; private set; }
    public Guid TenantB { get; private set; }
    public Guid CompanyA1 { get; private set; }
    public Guid CompanyA2 { get; private set; }
    public Guid CompanyB { get; private set; }
    public Guid DeptFinance { get; private set; }

    public int HrDirector { get; private set; }
    public int HrManager { get; private set; }
    public int Exec { get; private set; }
    public int Incumbent { get; private set; }
    public int Manager { get; private set; }
    public int Cara { get; private set; }
    public int Cody { get; private set; }
    public int Rana { get; private set; }
    public int Pete { get; private set; }
    public int Uma { get; private set; }
    public int Ulf { get; private set; }
    public int Remy { get; private set; }
    public int Bilal { get; private set; }

    public Guid CfoPosition { get; private set; }
    public Guid ControllerPosition { get; private set; }
    public Guid A2Position { get; private set; }
    public Guid NoCompanyPosition { get; private set; }
    public Guid BPosition { get; private set; }

    public Guid CaraReview { get; private set; }

    public readonly Dictionary<int, Guid> UserOf = new();

    public static readonly string[] HrDirectorPermissions =
        [.. SuccessionPermissions.All, "employees.read", "employees.write", "approvals.read", "approvals.decide", "approvals.write", "learning.read", "learning.assign"];

    public static readonly string[] HrManagerPermissions =
        [.. SuccessionPermissions.All.Where(p => p != SuccessionPermissions.Calibrate), "employees.read", "employees.write", "approvals.read",
            "approvals.decide", "approvals.write", "learning.read", "learning.assign"];

    /// <summary>The seeded Admin's succession reach (F2): configuration only.</summary>
    public static readonly string[] AdminPermissions =
        [SuccessionPermissions.Read, SuccessionPermissions.PositionsManage, SuccessionPermissions.PoolsManage,
            "employees.read", "employees.write", "approvals.read", "approvals.decide", "approvals.override", "audit.read"];

    /// <summary>A custom role someone might build: slate management without slate reading (F2 says that is not enough).</summary>
    public static readonly string[] SlatesManageWithoutReadPermissions =
        [SuccessionPermissions.Read, SuccessionPermissions.SlatesManage, "employees.read", "employees.write"];

    public static readonly string[] ManagerPermissions =
        ["employees.read", "manager.read", "approvals.read", "approvals.decide", "succession.nominate", "succession.devplans.read",
            "succession.devplans.manage", "succession.self", "ess.read", "ess.write"];

    public static readonly string[] EmployeePermissions = ["ess.read", "ess.write", "succession.self"];

    private SuccessionTestWorld(PostgresFixture fx)
    {
        _fx = fx;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<TimeProvider>(Clock);
        services.AddScoped<TestPrincipal>();
        services.AddScoped<IHttpContextAccessor>(sp =>
        {
            var principal = sp.GetRequiredService<TestPrincipal>();
            return new FixedHttpContextAccessor
            {
                HttpContext = principal.User is null ? null : new DefaultHttpContext { User = principal.User, RequestServices = sp },
            };
        });
        services.AddScoped(sp =>
        {
            var principal = sp.GetRequiredService<TestPrincipal>();
            var options = new DbContextOptionsBuilder<ZayraDbContext>()
                .UseNpgsql(fx.ConnectionString, PostgresFixture.ProductionProviderOptions)
                .AddInterceptors(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.Instance)
                .AddInterceptors(Zayra.Api.Infrastructure.Data.AdvisoryXactLockGuardInterceptor.Instance)
                .Options;
            return new ZayraDbContext(options, sp.GetRequiredService<IHttpContextAccessor>(), null,
                Options.Create(new EntityScopeOptions { StrictMode = principal.StrictScope }));
        });
        services.AddScoped<IDataScopeService>(sp => new DataScopeService(sp.GetRequiredService<ZayraDbContext>(), null,
            sp.GetRequiredService<IHttpContextAccessor>()));
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<INotificationRecipientResolver, NotificationRecipientResolver>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<ITenantModuleService, TenantModuleService>();
        services.AddScoped<ITenantClock, Zayra.Api.Infrastructure.Common.TenantClock>();
        services.AddScoped<IApprovalWorkflowService>(sp =>
        {
            var db = sp.GetRequiredService<ZayraDbContext>();
            var audit = sp.GetRequiredService<IAuditService>();
            return new ApprovalWorkflowService(db, audit, new HrmHierarchyService(db, audit), null, null, null, null,
                sp.GetRequiredService<IDataScopeService>(), sp.GetRequiredService<IHttpContextAccessor>());
        });
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Learning:PublicApiBaseUrl"] = "https://api.kynexone.test",
        }).Build();
        services.AddLearning(configuration);
        services.AddSuccession(configuration);
        Services = services.BuildServiceProvider();
    }

    public static async Task<SuccessionTestWorld> CreateAsync(PostgresFixture fx)
    {
        var world = new SuccessionTestWorld(fx);
        await world.SeedAsync();
        return world;
    }

    public ValueTask DisposeAsync() => Services.DisposeAsync();

    // ── Scopes and actors ───────────────────────────────────────────────────────────────────────────────────────

    public AsyncServiceScope As(ClaimsPrincipal? user)
    {
        var scope = Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TestPrincipal>().User = user;
        return scope;
    }

    public AsyncServiceScope System() => As(null);

    public static ClaimsPrincipal Principal(Guid tenantId, Guid userId, int? employeeId, IEnumerable<string> permissions, IEnumerable<string> roles,
        IReadOnlyList<Guid>? companies = null)
    {
        var claims = new List<Claim> { new("tenant_id", tenantId.ToString()), new(ClaimTypes.NameIdentifier, userId.ToString()) };
        if (employeeId is int id) claims.Add(new Claim("employee_id", id.ToString()));
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        claims.Add(new Claim(EntityScopeContext.V2ClaimType, companies is null
            ? JsonSerializer.Serialize(new { v = 2, m = "group" })
            : JsonSerializer.Serialize(new { v = 2, m = "companies", c = companies })));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    public ClaimsPrincipal HrDirectorP() => Principal(TenantA, UserOf[HrDirector], HrDirector, HrDirectorPermissions, ["HR Director"]);
    public ClaimsPrincipal HrManagerP() => Principal(TenantA, UserOf[HrManager], HrManager, HrManagerPermissions, ["HR Manager"]);
    public ClaimsPrincipal HrManagerA2Only() => Principal(TenantA, UserOf[Remy], Remy, HrManagerPermissions, ["HR Manager"], [CompanyA2]);
    public ClaimsPrincipal AdminP() => Principal(TenantA, Guid.NewGuid(), null, AdminPermissions, ["Admin"]);
    public ClaimsPrincipal ManagerP(int manager) => Principal(TenantA, UserOf[manager], manager, ManagerPermissions, ["Manager"], [CompanyA1]);
    public ClaimsPrincipal EmployeeP(int employee) => Principal(TenantA, UserOf[employee], employee, EmployeePermissions, ["Employee"], [CompanyA1]);
    public ClaimsPrincipal HrDirectorB() => Principal(TenantB, UserOf[Bilal], Bilal, HrDirectorPermissions, ["HR Director"]);
    /// <summary>A second HR Director whose own employee record is <paramref name="employee"/> (a candidate, or someone reporting to one).</summary>
    public ClaimsPrincipal HrDirectorAs(int employee) => Principal(TenantA, UserOf[employee], employee, HrDirectorPermissions, ["HR Director"]);

    /// <summary>The actor a succession controller builds for the scope's principal.</summary>
    public static async Task<SuccessionActor> ActorAsync(AsyncServiceScope scope)
    {
        var user = scope.ServiceProvider.GetRequiredService<TestPrincipal>().User!;
        var tenantId = Guid.Parse(user.FindFirstValue("tenant_id")!);
        var dataScope = await scope.ServiceProvider.GetRequiredService<IDataScopeService>().ResolveAsync(user, tenantId, CancellationToken.None);
        int? employee = int.TryParse(user.FindFirstValue("employee_id"), out var e) ? e : null;
        return new SuccessionActor(tenantId, Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!), employee,
            user.Claims.Where(c => c.Type == "permission").Select(c => c.Value).ToHashSet(StringComparer.OrdinalIgnoreCase),
            user.Claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value).ToList(), dataScope, "127.0.0.1", "test");
    }

    public static T Get<T>(AsyncServiceScope scope) where T : notnull => scope.ServiceProvider.GetRequiredService<T>();

    /// <summary>Runs <paramref name="work"/> as <paramref name="user"/> with its service of type T and its actor.</summary>
    public async Task<TResult> Run<T, TResult>(ClaimsPrincipal user, Func<T, SuccessionActor, Task<TResult>> work) where T : notnull
    {
        await using var scope = As(user);
        return await work(Get<T>(scope), await ActorAsync(scope));
    }

    public static RequestContext Context(ClaimsPrincipal user) => new("127.0.0.1", "test",
        Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!), Guid.Parse(user.FindFirstValue("tenant_id")!),
        user.Claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value).ToList(),
        user.Claims.Where(c => c.Type == "permission").Select(c => c.Value).ToList());

    /// <summary>Decides an approval request as <paramref name="user"/> through the real engine.</summary>
    public async Task<ApprovalRequestDto?> DecideAsync(ClaimsPrincipal user, Guid approvalId, string decision, string? comments)
    {
        await using var scope = As(user);
        var tenantId = Guid.Parse(user.FindFirstValue("tenant_id")!);
        return await Get<IApprovalWorkflowService>(scope).DecideAsync(tenantId, approvalId, new ApprovalDecisionRequest(decision, comments),
            Context(user), CancellationToken.None);
    }

    public ZayraDbContext Db() => _fx.CreateDb();

    // ── Seed ────────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task SeedAsync()
    {
        await using var db = _fx.CreateDb();
        TenantA = await PostgresFixture.SeedMinimalTenant(db);
        TenantB = await PostgresFixture.SeedMinimalTenant(db);
        var a1 = new Company { TenantId = TenantA, LegalNameEn = "Alpha Trading", CountryCode = "SA", IsActive = true };
        var a2 = new Company { TenantId = TenantA, LegalNameEn = "Alpha Logistics", CountryCode = "SA", IsActive = true };
        var b = new Company { TenantId = TenantB, LegalNameEn = "Beta Co", CountryCode = "SA", IsActive = true };
        db.Companies.AddRange(a1, a2, b);
        var finance = new Department { TenantId = TenantA, Code = "FIN", NameEn = "Finance", NameAr = "المالية" };
        var deptB = new Department { TenantId = TenantB, Code = "FIN", NameEn = "Finance", NameAr = "المالية" };
        db.Departments.AddRange(finance, deptB);
        await db.SaveChangesAsync();
        (CompanyA1, CompanyA2, CompanyB, DeptFinance) = (a1.Id, a2.Id, b.Id, finance.Id);

        Employee Emp(Guid tenant, string name, string arabic, Guid company, Guid dept, int? manager = null)
        {
            var user = Guid.NewGuid();
            return new Employee
            {
                TenantId = tenant, EmployeeCode = $"S-{Guid.NewGuid():N}"[..12], FullName = name, EnglishName = name, ArabicName = arabic,
                Status = EmployeeStatuses.Active, CompanyId = company, DepartmentId = dept, ManagerEmployeeId = manager, EmploymentType = "Full Time",
                Nationality = "Saudi", Gender = "Female", DateOfBirth = new DateOnly(1990, 1, 1), MaritalStatus = "Single", UserAccountId = user,
                JoiningDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            };
        }
        async Task<int> Add(Employee e)
        {
            db.Employees.Add(e);
            await db.SaveChangesAsync();
            UserOf[e.Id] = e.UserAccountId!.Value;
            return e.Id;
        }
        HrDirector = await Add(Emp(TenantA, "Hana Director", "هناء", CompanyA1, DeptFinance));
        HrManager = await Add(Emp(TenantA, "Hamid HR", "حامد", CompanyA1, DeptFinance, HrDirector));
        Exec = await Add(Emp(TenantA, "Eman Exec", "إيمان", CompanyA1, DeptFinance));
        Incumbent = await Add(Emp(TenantA, "Ines Incumbent", "إيناس", CompanyA1, DeptFinance, Exec));
        Manager = await Add(Emp(TenantA, "Majid Manager", "ماجد", CompanyA1, DeptFinance, Incumbent));
        Cara = await Add(Emp(TenantA, "Cara Candidate", "كارا", CompanyA1, DeptFinance, Manager));
        Cody = await Add(Emp(TenantA, "Cody Candidate", "كودي", CompanyA1, DeptFinance, Manager));
        Rana = await Add(Emp(TenantA, "Rana Report", "رنا", CompanyA1, DeptFinance, Cara));
        Pete = await Add(Emp(TenantA, "Pete Peer", "بيت", CompanyA1, DeptFinance, Exec));
        Uma = await Add(Emp(TenantA, "Uma Unrelated", "أمة", CompanyA1, DeptFinance, Exec));
        Ulf = await Add(Emp(TenantA, "Ulf Report", "أولف", CompanyA1, DeptFinance, Uma));
        Remy = await Add(Emp(TenantA, "Remy Remote", "رامي", CompanyA2, DeptFinance));
        Bilal = await Add(Emp(TenantB, "Bilal Beta", "بلال", CompanyB, deptB.Id));

        var cfo = new Position { TenantId = TenantA, CompanyId = CompanyA1, DepartmentId = DeptFinance, Code = "CFO", Title = "Chief Financial Officer",
            Status = PositionStatuses.Filled, IncumbentEmployeeId = Incumbent };
        var controller = new Position { TenantId = TenantA, CompanyId = CompanyA1, DepartmentId = DeptFinance, Code = "FC", Title = "Financial Controller",
            Status = PositionStatuses.Open };
        var a2Pos = new Position { TenantId = TenantA, CompanyId = CompanyA2, Code = "OPS", Title = "Operations Director", Status = PositionStatuses.Open };
        var noCompany = new Position { TenantId = TenantA, CompanyId = null, Code = "FLOAT", Title = "Floating role", Status = PositionStatuses.Open };
        var bPos = new Position { TenantId = TenantB, CompanyId = CompanyB, Code = "CFO", Title = "Beta CFO", Status = PositionStatuses.Open };
        db.Positions.AddRange(cfo, controller, a2Pos, noCompany, bPos);

        // Appraisals: Cara High (82), Cody Moderate (65), Ulf Low (50). Pete has none (Unrated); Rana's is under appeal.
        AppraisalReview Review(int employee, decimal score, bool appealed = false) => new()
        {
            TenantId = TenantA, CycleId = Guid.NewGuid(), CycleName = "FY2025", EmployeeId = employee, FinalScore = score, Status = appealed ? "Appealed" : "Published",
            IsAppealed = appealed, PublishedAt = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), ScorecardTemplateId = Guid.NewGuid(),
        };
        var caraReview = Review(Cara, 82m);
        db.AppraisalReviews.AddRange(caraReview, Review(Cody, 65m), Review(Ulf, 50m), Review(Rana, 90m, appealed: true));
        db.TenantFeatureFlags.AddRange(
            new TenantFeatureFlag { TenantId = TenantA, FeatureKey = FeatureKeys.Succession, IsEnabled = true },
            new TenantFeatureFlag { TenantId = TenantB, FeatureKey = FeatureKeys.Succession, IsEnabled = true },
            new TenantFeatureFlag { TenantId = TenantA, FeatureKey = FeatureKeys.Learning, IsEnabled = true });
        await db.SaveChangesAsync();
        (CfoPosition, ControllerPosition, A2Position, NoCompanyPosition, BPosition, CaraReview) =
            (cfo.Id, controller.Id, a2Pos.Id, noCompany.Id, bPos.Id, caraReview.Id);
    }

    // ── Builders for the common path ────────────────────────────────────────────────────────────────────────────

    public Task<CriticalPositionSummary> DesignateCfoAsync(int? accountable = null) =>
        Run<SuccessionPositionService, CriticalPositionSummary>(HrManagerP(), (s, a) => s.DesignateAsync(a,
            new DesignateInput(CfoPosition, "High", "Critical", "Signs the statutory accounts; no internal cover today.", accountable ?? Exec, null), default));

    public Task<NominationDto> NominateAsync(Guid criticalPositionId, int employee, string? readiness = null, ClaimsPrincipal? by = null) =>
        Run<SuccessionNominationService, NominationDto>(by ?? HrManagerP(), async (s, a) => (NominationDto)await s.NominateAsync(a,
            new NominateInput(employee, criticalPositionId, null, readiness, "Led the FY2025 close and the audit response.", null), default));

    public Task<TalentAssessmentDto> AssessAsync(int employee, int potential, string period = "2026-H2", ClaimsPrincipal? by = null) =>
        Run<TalentAssessmentService, TalentAssessmentDto>(by ?? HrManagerP(), (s, a) => s.CreateAsync(a,
            new AssessInput(employee, period, potential, "Took on the treasury project and grew it without being asked.", null, null), default));

    public Task<TalentAssessmentDto> CalibrateAssessmentAsync(Guid id, int potential, string? reason, int version, ClaimsPrincipal? by = null) =>
        Run<TalentAssessmentService, TalentAssessmentDto>(by ?? HrDirectorP(), (s, a) => s.CalibrateAsync(a, id, new CalibrateInput(potential, reason, version), default));

    /// <summary>Nomination → in review → assessed → assessment calibrated → readiness confirmed (Calibrated).</summary>
    public async Task<NominationDto> CalibratedCandidateAsync(Guid criticalPositionId, int employee, int potential, string readiness)
    {
        var n = await NominateAsync(criticalPositionId, employee);
        n = await Run<SuccessionNominationService, NominationDto>(HrManagerP(), (s, a) => s.StartReviewAsync(a, n.Id, default));
        var assessment = await AssessAsync(employee, potential);
        await CalibrateAssessmentAsync(assessment.Id, potential, null, assessment.Version);
        return await Run<SuccessionNominationService, NominationDto>(HrDirectorP(), (s, a) => s.CalibrateAsync(a, n.Id,
            new ReadinessInput(readiness, null, n.Version), default));
    }

    public Task<SlateVersionDto> DraftSlateAsync(Guid criticalPositionId, params (Guid NominationId, int Order)[] candidates) =>
        Run<SuccessionSlateService, SlateVersionDto>(HrManagerP(), (s, a) => s.CreateDraftAsync(a, criticalPositionId,
            new SlateDraftInput(candidates.Select(c => new SlateCandidateInput(c.NominationId, c.Order, $"Order {c.Order}: strongest evidence of delivery.")).ToList(), null),
            default));

    public Task<SlateVersionDto> SubmitSlateAsync(Guid slateId) =>
        Run<SuccessionSlateService, SlateVersionDto>(HrManagerP(), (s, a) => s.SubmitAsync(a, slateId, null, default));

    public async Task<SuccessionSlateVersion> SlateRowAsync(Guid id)
    {
        await using var db = Db();
        return await db.SuccessionSlateVersions.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == id);
    }

    public async Task<SuccessionNomination> NominationRowAsync(Guid id)
    {
        await using var db = Db();
        return await db.SuccessionNominations.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == id);
    }
}
