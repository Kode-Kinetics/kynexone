using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Learning;
using Zayra.Api.Infrastructure.Modules;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Learning;

/// <summary>A clock the test moves: the sweep's "today", token expiry and credential expiry all read it.</summary>
public sealed class ShiftableClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; private set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now = Now.Add(by);
    public DateOnly Today => DateOnly.FromDateTime(Now.UtcDateTime);
}

/// <summary>An IHttpContextAccessor whose context is per-instance (the framework one is AsyncLocal, i.e. shared).</summary>
internal sealed class FixedHttpContextAccessor : IHttpContextAccessor
{
    public HttpContext? HttpContext { get; set; }
}

/// <summary>Who a scope runs as. Null = the background system context (no HTTP principal).</summary>
internal sealed class TestPrincipal
{
    public ClaimsPrincipal? User { get; set; }
    public bool StrictScope { get; set; }
}

/// <summary>
/// One disposable learning world on the shared Postgres container: tenant A (two companies, two departments, a
/// manager with three reports, staff in another department and in the second company), tenant B for isolation, the
/// learning flag on for both, and a DI container that wires the REAL learning services, audit, notification outbox,
/// tenant module state and data scope exactly as Program.cs does — with the DbContext's company/tenant filters driven
/// by the principal each scope runs as.
/// </summary>
public sealed class LearningTestWorld : IAsyncDisposable
{
    private readonly PostgresFixture _fx;
    public ServiceProvider Services { get; }
    public ShiftableClock Clock { get; } = new(new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero));
    /// <summary>Interceptors added to every context created after they are set (e.g. an injected transient fault).</summary>
    public Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] ExtraInterceptors { get; set; } = [];

    public Guid TenantA { get; private set; }
    public Guid TenantB { get; private set; }
    public Guid CompanyA1 { get; private set; }
    public Guid CompanyA2 { get; private set; }
    public Guid CompanyB { get; private set; }
    public Guid DeptOps { get; private set; }
    public Guid DeptSales { get; private set; }
    public Guid DeptB { get; private set; }

    /// <summary>Manager in Ops (A1); reports Ops1, Ops2; Sales1, Sales2 in Sales (A1); Remote1 in A2 (Ops); B1 in tenant B.</summary>
    public int Manager { get; private set; }
    public int Ops1 { get; private set; }
    public int Ops2 { get; private set; }
    public int Sales1 { get; private set; }
    public int Sales2 { get; private set; }
    public int Remote1 { get; private set; }
    public int B1 { get; private set; }
    public Guid HrUserA { get; } = Guid.NewGuid();
    public Guid HrUserB { get; } = Guid.NewGuid();
    /// <summary>Logins linked (Employee.UserAccountId) to Ops1 and to the manager. Everyone else has no login.</summary>
    public Guid Ops1User { get; private set; }
    public Guid ManagerUser { get; private set; }

    public static readonly string[] HrPermissions =
        ["employees.read", "employees.write", "learning.read", "learning.manage", "learning.assign", "learning.report", "learning.records.correct"];

    private LearningTestWorld(PostgresFixture fx)
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
                HttpContext = principal.User is null ? null : new DefaultHttpContext { User = principal.User },
            };
        });
        services.AddScoped(sp =>
        {
            var principal = sp.GetRequiredService<TestPrincipal>();
            var options = new DbContextOptionsBuilder<ZayraDbContext>()
                .UseNpgsql(fx.ConnectionString, PostgresFixture.ProductionProviderOptions)
                .AddInterceptors(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.Instance)
                .AddInterceptors(Zayra.Api.Infrastructure.Data.AdvisoryXactLockGuardInterceptor.Instance)
                .AddInterceptors(ExtraInterceptors)
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
        services.AddScoped<ITenantClock, TenantClock>();
        services.AddLearning(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Learning:PublicApiBaseUrl"] = "https://api.kynexone.test",
            // A tiny page so every sweep test also proves the sweep pages through everything (no starvation).
            ["Learning:SweepPageSize"] = "2",
            ["Learning:MaxStatementsPerSession"] = "60",
        }).Build());
        Services = services.BuildServiceProvider();
    }

    public static async Task<LearningTestWorld> CreateAsync(PostgresFixture fx)
    {
        var world = new LearningTestWorld(fx);
        await world.SeedAsync();
        return world;
    }

    public ValueTask DisposeAsync() => Services.DisposeAsync();

    // ── Scopes ──────────────────────────────────────────────────────────────────────────────────────────────────────

    public AsyncServiceScope As(ClaimsPrincipal? user, bool strict = false)
    {
        var scope = Services.CreateAsyncScope();
        var principal = scope.ServiceProvider.GetRequiredService<TestPrincipal>();
        principal.User = user;
        principal.StrictScope = strict;
        return scope;
    }

    public AsyncServiceScope System() => As(null);

    public static ClaimsPrincipal Principal(Guid tenantId, Guid userId, int? employeeId, IEnumerable<string> permissions, IReadOnlyList<Guid>? companies = null)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, userId.ToString()),
        };
        if (employeeId is int id) claims.Add(new Claim("employee_id", id.ToString()));
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        claims.Add(new Claim(EntityScopeContext.V2ClaimType, companies is null
            ? JsonSerializer.Serialize(new { v = 2, m = "group" })
            : JsonSerializer.Serialize(new { v = 2, m = "companies", c = companies })));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    public ClaimsPrincipal HrA() => Principal(TenantA, HrUserA, null, HrPermissions);
    public ClaimsPrincipal HrA1Only() => Principal(TenantA, Guid.NewGuid(), null, HrPermissions, [CompanyA1]);
    public ClaimsPrincipal HrB() => Principal(TenantB, HrUserB, null, HrPermissions);
    public ClaimsPrincipal ManagerA() => Principal(TenantA, Guid.NewGuid(), Manager, ["employees.read", "manager.read", "learning.report", "ess.read", "ess.write"], [CompanyA1]);
    public ClaimsPrincipal EmployeeA(int employeeId, Guid company) => Principal(TenantA, Guid.NewGuid(), employeeId, ["ess.read", "ess.write"], [company]);
    public ClaimsPrincipal EmployeeB() => Principal(TenantB, Guid.NewGuid(), B1, ["ess.read", "ess.write"], [CompanyB]);

    /// <summary>An employee scope plus the actor the ESS controller would build for it.</summary>
    public (AsyncServiceScope Scope, LearningActor Actor) Employee(int employeeId, Guid? company = null)
    {
        var scope = As(EmployeeA(employeeId, company ?? CompanyA1));
        return (scope, new LearningActor(TenantA, Guid.NewGuid(), null, "test", new DataScope
        {
            Level = DataScopeLevel.Own, CallerEmployeeId = employeeId, AllowedEmployeeIds = [employeeId],
        }, employeeId));
    }

    /// <summary>The actor an HR/manager controller would build (DataScopeService over the scope's principal).</summary>
    public static async Task<LearningActor> ActorAsync(AsyncServiceScope scope, Guid tenantId)
    {
        var user = scope.ServiceProvider.GetRequiredService<TestPrincipal>().User!;
        var dataScope = await scope.ServiceProvider.GetRequiredService<IDataScopeService>().ResolveAsync(user, tenantId, CancellationToken.None);
        var userId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        int? self = int.TryParse(user.FindFirstValue("employee_id"), out var linked) ? linked : null;
        return new LearningActor(tenantId, userId, "127.0.0.1", "test", dataScope, self);
    }

    public static T Get<T>(AsyncServiceScope scope) where T : notnull => scope.ServiceProvider.GetRequiredService<T>();

    // ── Seed ────────────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task SeedAsync()
    {
        await using var db = _fx.CreateDb();
        TenantA = await PostgresFixture.SeedMinimalTenant(db);
        TenantB = await PostgresFixture.SeedMinimalTenant(db);
        var a1 = new Company { TenantId = TenantA, LegalNameEn = "Alpha Trading", CountryCode = "SA", IsActive = true };
        var a2 = new Company { TenantId = TenantA, LegalNameEn = "Alpha Logistics", CountryCode = "SA", IsActive = true };
        var b = new Company { TenantId = TenantB, LegalNameEn = "Beta Co", CountryCode = "SA", IsActive = true };
        db.Companies.AddRange(a1, a2, b);
        var ops = new Department { TenantId = TenantA, Code = "OPS", NameEn = "Operations", NameAr = "العمليات" };
        var sales = new Department { TenantId = TenantA, Code = "SAL", NameEn = "Sales", NameAr = "المبيعات" };
        var deptB = new Department { TenantId = TenantB, Code = "OPS", NameEn = "Operations", NameAr = "العمليات" };
        db.Departments.AddRange(ops, sales, deptB);
        await db.SaveChangesAsync();
        (CompanyA1, CompanyA2, CompanyB, DeptOps, DeptSales, DeptB) = (a1.Id, a2.Id, b.Id, ops.Id, sales.Id, deptB.Id);

        Employee Emp(Guid tenant, string name, Guid company, Guid dept, int? manager = null) => new()
        {
            TenantId = tenant, EmployeeCode = $"L-{Guid.NewGuid():N}"[..12], FullName = name, Status = EmployeeStatuses.Active,
            CompanyId = company, DepartmentId = dept, ManagerEmployeeId = manager, EmploymentType = "Full Time", Nationality = "Saudi",
            JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var manager = Emp(TenantA, "Maha Manager", CompanyA1, DeptOps);
        db.Employees.Add(manager);
        await db.SaveChangesAsync();
        var ops1 = Emp(TenantA, "Omar Ops", CompanyA1, DeptOps, manager.Id);
        var ops2 = Emp(TenantA, "Ola Ops", CompanyA1, DeptOps, manager.Id);
        var sales1 = Emp(TenantA, "Sami Sales", CompanyA1, DeptSales);
        var sales2 = Emp(TenantA, "Sara Sales", CompanyA1, DeptSales);
        var remote = Emp(TenantA, "Rami Remote", CompanyA2, DeptOps);
        var b1 = Emp(TenantB, "Bilal Beta", CompanyB, DeptB);
        db.Employees.AddRange(ops1, ops2, sales1, sales2, remote, b1);
        db.TenantFeatureFlags.AddRange(
            new TenantFeatureFlag { TenantId = TenantA, FeatureKey = FeatureKeys.Learning, IsEnabled = true },
            new TenantFeatureFlag { TenantId = TenantB, FeatureKey = FeatureKeys.Learning, IsEnabled = true });
        await db.SaveChangesAsync();
        (Manager, Ops1, Ops2, Sales1, Sales2, Remote1, B1) = (manager.Id, ops1.Id, ops2.Id, sales1.Id, sales2.Id, remote.Id, b1.Id);

        User Login(string name) => new()
        {
            TenantId = TenantA, Email = $"{name}-{Guid.NewGuid():N}@lrn.test", NormalizedEmail = $"{name}@LRN.TEST", FullName = name,
            PasswordHash = "no-login-path", Status = "Active", AccessMode = "FullPortal", IsActive = true, IsEmailConfirmed = true,
        };
        var ops1Login = Login("ops1");
        var managerLogin = Login("manager");
        db.Users.AddRange(ops1Login, managerLogin);
        await db.SaveChangesAsync();
        ops1.UserAccountId = ops1Login.Id;
        manager.UserAccountId = managerLogin.Id;
        await db.SaveChangesAsync();
        (Ops1User, ManagerUser) = (ops1Login.Id, managerLogin.Id);
    }

    // ── Catalogue builders ──────────────────────────────────────────────────────────────────────────────────────────

    public static CourseInput Lesson(string code, string completionRule = LearningCompletionRules.Acknowledgement, int? validityMonths = null,
        int maxAttempts = 3, decimal passMark = 70m) => new(
        code, $"{code} course", $"دورة {code}", "What everyone must know.", null, 15, LearningContentKinds.Lesson,
        "<h2>Exits</h2><p>Know your <strong>nearest exit</strong>.</p><script>alert(1)</script>", null, null, null, null, completionRule,
        completionRule == LearningCompletionRules.Assessment ? Questions() : null, completionRule == LearningCompletionRules.Assessment ? passMark : null,
        completionRule == LearningCompletionRules.Assessment ? maxAttempts : null, false, validityMonths, true, null);

    /// <summary>A cmi5 unit. Passed-family moveOn gets an 80% mastery score. Verification is off unless asked for; a
    /// NotApplicable unit grants no certificate (publishing would refuse it).</summary>
    public static CourseInput Cmi5(string code, string moveOn, int? validityMonths = null, bool verification = false) => new(
        code, $"{code} unit", null, null, null, 10, LearningContentKinds.Cmi5, null, null, "https://au.example.test:5411/index.html",
        $"https://au.example.test/{code.ToLowerInvariant()}", moveOn, LearningCompletionRules.Cmi5MoveOn, null,
        LearningCatalogService.UsesPassed(moveOn) ? 80m : null, null, null,
        moveOn == Cmi5MoveOnValues.NotApplicable ? null : validityMonths, moveOn != Cmi5MoveOnValues.NotApplicable, null, verification);

    /// <summary>Two questions: q1 single (correct = option 2), q2 true/false (correct = true).</summary>
    public static IReadOnlyList<AssessmentQuestionInput> Questions() =>
    [
        new(LearningQuestionKinds.Single, "Which exit is nearest?", null,
            [new AssessmentOptionInput("The lift", null), new AssessmentOptionInput("The stairs", null), new AssessmentOptionInput("The window", null)],
            [1], null),
        new(LearningQuestionKinds.TrueFalse, "Fire doors stay closed.", null, null, null, true),
    ];

    public static Dictionary<string, List<string>> Correct() => new() { ["q1"] = ["q1o2"], ["q2"] = ["true"] };
    public static Dictionary<string, List<string>> Wrong() => new() { ["q1"] = ["q1o1"], ["q2"] = ["false"] };

    /// <summary>Creates and publishes courses, builds an active path over them (item n+1 requires item n when chained).</summary>
    public async Task<(Guid PathId, IReadOnlyList<Guid> CourseIds)> PublishedPathAsync(string code, IReadOnlyList<CourseInput> courses,
        bool chainPrerequisites = false, LearningEligibilityCriteria? eligibility = null)
    {
        await using var scope = As(HrA());
        var actor = await ActorAsync(scope, TenantA);
        var catalog = Get<LearningCatalogService>(scope);
        var ids = new List<Guid>();
        foreach (var course in courses)
        {
            var created = await catalog.CreateCourseAsync(actor, course, default);
            ids.Add((await catalog.PublishCourseAsync(actor, created.Id, null, default)).Id);
        }
        var path = await catalog.CreatePathAsync(actor, new PathInput(code, $"{code} path", $"مسار {code}", null, eligibility, null, null, 30,
            ids.Select((id, i) => new PathItemInput(id, true, chainPrerequisites && i > 0 ? i : null)).ToList(), null), default);
        await catalog.ActivatePathAsync(actor, path.Id, default);
        return (path.Id, ids);
    }

    public async Task<AssignmentBulkResult> AssignAsync(Guid pathId, IReadOnlyList<int> employeeIds, DateOnly? due = null)
    {
        await using var scope = As(HrA());
        return await Get<LearningAssignmentService>(scope).AssignAsync(await ActorAsync(scope, TenantA),
            new AssignInput(pathId, "Employee", employeeIds, null, due, null, true), default);
    }

    public async Task<LearningEnrollment> EnrollmentAsync(int employeeId, Guid pathId, int sequence, int? cycle = null)
    {
        await using var db = _fx.CreateDb();
        var assignment = await db.LearningAssignments.AsNoTracking()
            .Where(a => a.EmployeeId == employeeId && a.PathId == pathId && (cycle == null || a.Cycle == cycle))
            .OrderByDescending(a => a.Cycle).FirstAsync();
        return await db.LearningEnrollments.AsNoTracking().FirstAsync(e => e.AssignmentId == assignment.Id && e.Sequence == sequence);
    }

    public ZayraDbContext Db() => _fx.CreateDb();
}
