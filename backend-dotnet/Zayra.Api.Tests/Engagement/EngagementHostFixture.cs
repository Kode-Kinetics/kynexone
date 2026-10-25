using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Engagement;

[CollectionDefinition("Engagement")]
public sealed class EngagementCollection : ICollectionFixture<EngagementHostFixture>;

/// <summary>
/// The REAL Program.cs pipeline (JWT, [HasPermission], the opt-in guard, company filters, DataProtection, the
/// notification outbox) over a REAL PostgreSQL 16 built by running EVERY EF migration — AddEngagementSurveys and its
/// triggers included. Background workers are removed from the host; tests run the engagement and report workers'
/// one-shot methods themselves. Email is captured, never sent.
///
/// <para>Golden dataset (tenant A, company C1, k = 5, anonymous): 33 invited across Operations (22), Finance (8) and
/// Legal (3); 30 respond with the fixed answers in <see cref="Golden"/>. Expected values are hand-computed in
/// EngagementGoldenReconciliationTests, not recomputed by code.</para>
/// </summary>
public sealed class EngagementHostFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();

    public EngagementHost Host { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public RecordingEmail Email { get; } = new();
    public string ConnectionString { get; private set; } = string.Empty;

    public Guid TenantA { get; } = Guid.NewGuid();
    public Guid TenantB { get; } = Guid.NewGuid();
    public Guid TenantC { get; } = Guid.NewGuid();
    public Guid CompanyA1 { get; } = Guid.NewGuid();
    public Guid CompanyA2 { get; } = Guid.NewGuid();
    public Guid CompanyB1 { get; } = Guid.NewGuid();
    public Guid CompanyC1 { get; } = Guid.NewGuid();
    public Guid DeptOps { get; } = Guid.NewGuid();
    public Guid DeptFin { get; } = Guid.NewGuid();
    public Guid DeptLegal { get; } = Guid.NewGuid();
    public Guid DeptMgmt { get; } = Guid.NewGuid();
    public Guid DesigEngineer { get; } = Guid.NewGuid();
    public Guid DesigAnalyst { get; } = Guid.NewGuid();
    public Guid DesigSupervisor { get; } = Guid.NewGuid();
    public Guid DesigManager { get; } = Guid.NewGuid();

    public int MgrEngId { get; private set; }
    public int MgrOtherId { get; private set; }
    public int MgrFinId { get; private set; }
    public int MgrLegalId { get; private set; }
    public int OutsiderEmployeeId { get; private set; }
    /// <summary>Index i (0..32) → employee id. 0..29 respond (Golden order), 30..32 do not.</summary>
    public List<int> Invitees { get; } = new();
    public Dictionary<int, string> EmployeeTokens { get; } = new();

    public string TokenHr { get; private set; } = null!;
    public string TokenHrManagerC2 { get; private set; } = null!;
    public string TokenMgrEng { get; private set; } = null!;
    public string TokenMgrLegal { get; private set; } = null!;
    public string TokenHrB { get; private set; } = null!;
    public string TokenHrC { get; private set; } = null!;
    public Guid HrUserId { get; private set; }

    public Guid TemplateId { get; private set; }
    /// <summary>The golden survey: 30 responses, then CLOSED (anonymous results exist only after close).</summary>
    public Guid GoldenSurveyId { get; private set; }
    /// <summary>A second anonymous survey left OPEN: drafts, the double submit and reminders happen here.</summary>
    public Guid LiveSurveyId { get; private set; }
    public HttpStatusCode SecondSubmitStatus { get; private set; }
    public string? SecondSubmitError { get; private set; }
    public JsonElement ReopenedDraft { get; private set; }

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        ConnectionString = _pg.GetConnectionString();
        await using (var migrate = CreateDb())
            await migrate.Database.MigrateAsync();

        Host = new EngagementHost(ConnectionString, Email);
        Client = Host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        await SeedAsync();
        await RunGoldenScenarioAsync();
    }

    public Task DisposeAsync()
    {
        Client?.Dispose();
        Host?.Dispose();
        return _pg.DisposeAsync().AsTask();
    }

    public ZayraDbContext CreateDb() => new(new DbContextOptionsBuilder<ZayraDbContext>()
        .UseNpgsql(ConnectionString, PostgresFixture.ProductionProviderOptions)
        .AddInterceptors(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.Instance)
        .AddInterceptors(Zayra.Api.Infrastructure.Data.AdvisoryXactLockGuardInterceptor.Instance)
        .Options);

    // ── Seed ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task SeedAsync()
    {
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var seeder = scope.ServiceProvider.GetRequiredService<IAuthSeeder>();

        db.Tenants.AddRange(
            new Tenant { Id = TenantA, Name = "Engagement A", Slug = $"eng-a-{TenantA:N}", AccountType = TenantAccountTypes.Group },
            new Tenant { Id = TenantB, Name = "Engagement B", Slug = $"eng-b-{TenantB:N}" },
            new Tenant { Id = TenantC, Name = "Engagement C (flag off)", Slug = $"eng-c-{TenantC:N}" });
        db.Companies.AddRange(
            new Company { Id = CompanyA1, TenantId = TenantA, LegalNameEn = "Acme Riyadh", LegalNameAr = "أكمي الرياض", CountryCode = "SA", IsActive = true },
            new Company { Id = CompanyA2, TenantId = TenantA, LegalNameEn = "Acme Jeddah", LegalNameAr = "أكمي جدة", CountryCode = "SA", IsActive = true },
            new Company { Id = CompanyB1, TenantId = TenantB, LegalNameEn = "Beta Co", CountryCode = "SA", IsActive = true },
            new Company { Id = CompanyC1, TenantId = TenantC, LegalNameEn = "Gamma Co", CountryCode = "SA", IsActive = true });
        db.Departments.AddRange(
            new Department { Id = DeptOps, TenantId = TenantA, Code = "OPS", NameEn = "Operations", NameAr = "العمليات" },
            new Department { Id = DeptFin, TenantId = TenantA, Code = "FIN", NameEn = "Finance", NameAr = "المالية" },
            new Department { Id = DeptLegal, TenantId = TenantA, Code = "LEG", NameEn = "Legal", NameAr = "الشؤون القانونية" },
            new Department { Id = DeptMgmt, TenantId = TenantA, Code = "MGT", NameEn = "Management", NameAr = "الإدارة" });
        db.Designations.AddRange(
            new Designation { Id = DesigEngineer, TenantId = TenantA, Code = "ENG", TitleEn = "Engineer", TitleAr = "مهندس" },
            new Designation { Id = DesigAnalyst, TenantId = TenantA, Code = "ANA", TitleEn = "Analyst", TitleAr = "محلل" },
            new Designation { Id = DesigSupervisor, TenantId = TenantA, Code = "SUP", TitleEn = "Supervisor", TitleAr = "مشرف" },
            new Designation { Id = DesigManager, TenantId = TenantA, Code = "MGR", TitleEn = "Manager", TitleAr = "مدير" });
        foreach (var t in new[] { TenantA, TenantB })
            db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = t, FeatureKey = FeatureKeys.Engagement, IsEnabled = true });
        await db.SaveChangesAsync();

        foreach (var t in new[] { TenantA, TenantB, TenantC }) await seeder.EnsureTenantRolesAsync(t);

        // Managers (Management department — outside the survey audience).
        MgrEngId = await AddEmployeeAsync(db, TenantA, CompanyA1, DeptMgmt, DesigManager, "MGR-ENG", "Mona Engineering Lead", null);
        MgrOtherId = await AddEmployeeAsync(db, TenantA, CompanyA1, DeptMgmt, DesigManager, "MGR-OPS", "Omar Operations Lead", null);
        MgrFinId = await AddEmployeeAsync(db, TenantA, CompanyA1, DeptMgmt, DesigManager, "MGR-FIN", "Fatima Finance Lead", null);
        MgrLegalId = await AddEmployeeAsync(db, TenantA, CompanyA1, DeptMgmt, DesigManager, "MGR-LEG", "Layla Legal Lead", null);
        OutsiderEmployeeId = await AddEmployeeAsync(db, TenantA, CompanyA2, DeptOps, DesigEngineer, "C2-001", "Jeddah Engineer", null);

        // The 33 invitees in Golden order, then the three non-respondents.
        var plan = Golden.Groups.Concat(new[] { ("Ops", "Eng"), ("Ops", "Ana"), ("Fin", "Ana") }).ToList();
        for (var i = 0; i < plan.Count; i++)
        {
            var (dept, desig) = plan[i];
            var deptId = dept switch { "Ops" => DeptOps, "Fin" => DeptFin, _ => DeptLegal };
            var desigId = desig switch { "Eng" => DesigEngineer, "Ana" => DesigAnalyst, _ => DesigSupervisor };
            var manager = dept switch { "Ops" when desig == "Eng" => MgrEngId, "Ops" => MgrOtherId, "Fin" => MgrFinId, _ => MgrLegalId };
            Invitees.Add(await AddEmployeeAsync(db, TenantA, CompanyA1, deptId, desigId, $"E{i:D3}", $"Employee {i:D2}", manager));
        }

        // Logins.
        var hr = await AddUserAsync(db, TenantA, "hr-director", "HR Director", groupScope: true, employeeId: null, grantCompany: null);
        HrUserId = hr;
        await AddUserAsync(db, TenantA, "hr-manager-c2", "HR Manager", groupScope: false, employeeId: null, grantCompany: CompanyA2);
        await AddUserAsync(db, TenantA, "mgr-eng", "Manager", groupScope: false, employeeId: MgrEngId, grantCompany: CompanyA1, AccessModes.ManagerPortal);
        await AddUserAsync(db, TenantA, "mgr-legal", "Manager", groupScope: false, employeeId: MgrLegalId, grantCompany: CompanyA1, AccessModes.ManagerPortal);
        await AddUserAsync(db, TenantB, "hr-b", "HR Director", groupScope: true, employeeId: null, grantCompany: null);
        await AddUserAsync(db, TenantC, "hr-c", "HR Director", groupScope: true, employeeId: null, grantCompany: null);
        for (var i = 0; i < Invitees.Count; i++)
            await AddUserAsync(db, TenantA, $"emp-{i:D2}", "Employee", groupScope: false, employeeId: Invitees[i], grantCompany: CompanyA1, AccessModes.EssOnly);

        TokenHr = await MintAsync(TenantA, "hr-director");
        TokenHrManagerC2 = await MintAsync(TenantA, "hr-manager-c2");
        TokenMgrEng = await MintAsync(TenantA, "mgr-eng");
        TokenMgrLegal = await MintAsync(TenantA, "mgr-legal");
        TokenHrB = await MintAsync(TenantB, "hr-b");
        TokenHrC = await MintAsync(TenantC, "hr-c");
        for (var i = 0; i < Invitees.Count; i++) EmployeeTokens[i] = await MintAsync(TenantA, $"emp-{i:D2}");
    }

    internal static async Task<int> AddEmployeeAsync(ZayraDbContext db, Guid tenant, Guid company, Guid dept, Guid desig, string code, string name, int? manager)
    {
        var e = new Employee
        {
            TenantId = tenant, CompanyId = company, DepartmentId = dept, DesignationId = desig, EmployeeCode = code,
            FullName = name, Status = EmployeeStatuses.Active, JoiningDate = DateTime.UtcNow.AddYears(-2), ManagerEmployeeId = manager,
        };
        db.Employees.Add(e);
        await db.SaveChangesAsync();
        return e.Id;
    }

    internal static async Task<Guid> AddUserAsync(ZayraDbContext db, Guid tenant, string name, string role, bool groupScope, int? employeeId,
        Guid? grantCompany, string accessMode = AccessModes.FullPortal)
    {
        var user = new User
        {
            TenantId = tenant, Email = $"{name}@engagement.test", NormalizedEmail = $"{name}@engagement.test".ToUpperInvariant(),
            FullName = name, PasswordHash = "no-login-path", Status = "Active", AccessMode = accessMode, IsActive = true,
            IsEmailConfirmed = true, IsGroupScope = groupScope,
        };
        db.Users.Add(user);
        var roleRow = await db.Roles.IgnoreQueryFilters().FirstAsync(r => r.TenantId == tenant && r.Name == role);
        db.Set<UserRole>().Add(new UserRole { UserId = user.Id, RoleId = roleRow.Id });
        if (grantCompany is Guid company)
            db.UserEntityAccesses.Add(new UserEntityAccess { TenantId = tenant, UserId = user.Id, CompanyId = company, GrantMode = EntityGrantModes.SelectedCompanies, Role = role, IsActive = true });
        if (employeeId is int emp)
            db.EmployeeUserAccounts.Add(new EmployeeUserAccount
            {
                TenantId = tenant, EmployeeId = emp, UserId = user.Id, AccessMode = accessMode, IsPrimary = true, Status = "Active", RequiresPasswordSetup = false,
            });
        await db.SaveChangesAsync();
        return user.Id;
    }

    public async Task<string> MintAsync(Guid tenantId, string name)
    {
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var user = await db.Users.IgnoreQueryFilters().AsNoTracking().Include(u => u.Tenant)
            .Include(u => u.UserRoles).ThenInclude(r => r.Role).ThenInclude(r => r!.RolePermissions).ThenInclude(p => p.Permission)
            .Include(u => u.PermissionOverrides).Include(u => u.EmployeeUserAccounts).Include(u => u.EntityAccesses)
            .SingleAsync(u => u.TenantId == tenantId && u.Email == $"{name}@engagement.test");
        var grants = user.EntityAccesses.Where(g => g.IsActive).Select(g => new EntityAccessGrant(g.CompanyId, g.Role, g.GrantMode)).ToList();
        var companies = await db.Companies.IgnoreQueryFilters().Where(c => c.TenantId == tenantId && c.IsActive).Select(c => c.Id).ToListAsync();
        return scope.ServiceProvider.GetRequiredService<ITokenService>().CreateAccessToken(user, AuthService.GetRoles(user),
            AuthService.GetPermissions(user), user.Tenant!, grants, EntityScopeClaims.Resolve(user.IsGroupScope, grants, companies), out _);
    }

    // ── HTTP ─────────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await Client.SendAsync(request);
    }

    public async Task<JsonElement> JsonAsync(HttpMethod method, string path, string token, object? body = null, HttpStatusCode expect = HttpStatusCode.OK)
    {
        using var response = await SendAsync(method, path, token, body);
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(expect, $"{method} {path}: {text}");
        return string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    // ── Golden scenario ──────────────────────────────────────────────────────────────────────────────────────────

    public static object TemplateBody(string name) => new
    {
        nameEn = name, nameAr = "استبيان المشاركة", descriptionEn = "Quarterly pulse", descriptionAr = "نبض ربع سنوي",
        drivers = new[]
        {
            new { key = "recognition", labelEn = "Recognition", labelAr = "التقدير" },
            new { key = "enablement", labelEn = "Enablement", labelAr = "التمكين" },
        },
        questions = new object[]
        {
            new { key = "q1", type = "likert5", textEn = "I feel recognised for my work.", textAr = "أشعر بالتقدير لعملي.", required = true, driver = "recognition" },
            new { key = "q2", type = "likert5", textEn = "My manager supports my growth.", textAr = "يدعم مديري تطوري.", required = true, driver = "enablement" },
            new { key = "q3", type = "likert5", textEn = "I have the tools I need.", textAr = "لدي الأدوات التي أحتاجها.", required = false, driver = "enablement" },
            new { key = "q4", type = "enps", textEn = "How likely are you to recommend working here?", textAr = "ما مدى احتمال أن توصي بالعمل هنا؟", required = true },
            new
            {
                key = "q5", type = "single", textEn = "Where do you prefer to work?", textAr = "أين تفضل أن تعمل؟", required = false,
                options = new[]
                {
                    new { key = "office", labelEn = "Office", labelAr = "المكتب" },
                    new { key = "hybrid", labelEn = "Hybrid", labelAr = "مختلط" },
                    new { key = "remote", labelEn = "Remote", labelAr = "عن بعد" },
                },
            },
            new { key = "q6", type = "text", textEn = "What one thing should we change?", textAr = "ما الشيء الذي يجب أن نغيره؟", required = false },
        },
    };

    public async Task<Guid> CreatePublishedTemplateAsync(string name)
    {
        var created = await JsonAsync(HttpMethod.Post, "/api/engagement/templates", TokenHr, TemplateBody(name), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        await JsonAsync(HttpMethod.Post, $"/api/engagement/templates/{id}/publish", TokenHr, new { version = created.GetProperty("version").GetInt32() });
        return id;
    }

    public async Task<Guid> CreateAndLaunchSurveyAsync(Guid templateId, string title, string mode, Guid[]? departments = null,
        DateTime? opensAtUtc = null, int closesInDays = 10, string recurrence = "None", int[]? reminders = null, int? k = null)
    {
        var opens = opensAtUtc ?? DateTime.UtcNow.AddDays(-4);
        var created = await JsonAsync(HttpMethod.Post, "/api/engagement/surveys", TokenHr, new
        {
            templateId, titleEn = title, titleAr = "استبيان " + title, mode, companyId = CompanyA1,
            audience = new { departmentIds = departments ?? [DeptOps, DeptFin, DeptLegal] },
            opensAtUtc = opens, closesAtUtc = DateTime.UtcNow.AddDays(closesInDays), recurrence,
            minCohortSize = k, reminderOffsetsDays = reminders ?? [3],
        }, HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        await JsonAsync(HttpMethod.Post, $"/api/engagement/surveys/{id}/launch", TokenHr, new { version = created.GetProperty("version").GetInt32() });
        return id;
    }

    public async Task<(string Token, JsonElement Session)> OpenAsync(int invitee, Guid surveyId)
    {
        var session = await JsonAsync(HttpMethod.Post, "/api/ess/surveys/open", EmployeeTokens[invitee], new { surveyId });
        return (session.GetProperty("token").GetString()!, session);
    }

    public async Task<HttpResponseMessage> SubmitAsync(int invitee, Guid surveyId, string token, object[] answers) =>
        await SendAsync(HttpMethod.Post, "/api/ess/surveys/submit", EmployeeTokens[invitee], new { surveyId, token, answers });

    private async Task RunGoldenScenarioAsync()
    {
        TemplateId = await CreatePublishedTemplateAsync("Golden engagement pulse");
        GoldenSurveyId = await CreateAndLaunchSurveyAsync(TemplateId, "Golden pulse", "Anonymous");
        for (var i = 0; i < Golden.Groups.Length; i++)
        {
            var (token, _) = await OpenAsync(i, GoldenSurveyId);
            using var submitted = await SubmitAsync(i, GoldenSurveyId, token, Golden.Answers(i));
            submitted.StatusCode.Should().Be(HttpStatusCode.OK, await submitted.Content.ReadAsStringAsync());
        }
        await CloseAsync(GoldenSurveyId);

        // The live survey: same audience, left open.
        LiveSurveyId = await CreateAndLaunchSurveyAsync(TemplateId, "Live pulse", "Anonymous");
        var (t0, _) = await OpenAsync(0, LiveSurveyId);
        // Draft first (a different q1), reopen: the draft comes back to its owner, then the real answers.
        using (var draft = await SendAsync(HttpMethod.Put, "/api/ess/surveys/draft", EmployeeTokens[0],
            new { surveyId = LiveSurveyId, token = t0, answers = new object[] { new { questionKey = "q1", value = 1 }, new { questionKey = "q6", text = "DRAFT-ONLY-TEXT" } } }))
            draft.StatusCode.Should().Be(HttpStatusCode.OK, await draft.Content.ReadAsStringAsync());
        (t0, var reopened) = await OpenAsync(0, LiveSurveyId);
        ReopenedDraft = reopened.GetProperty("draft").Clone();
        (await SubmitAsync(0, LiveSurveyId, t0, Golden.Answers(0))).StatusCode.Should().Be(HttpStatusCode.OK);
        var (t1, _) = await OpenAsync(1, LiveSurveyId);
        (await SubmitAsync(1, LiveSurveyId, t1, Golden.Answers(1))).StatusCode.Should().Be(HttpStatusCode.OK);
        using (var again = await SubmitAsync(1, LiveSurveyId, t1, Golden.Answers(1)))
        {
            SecondSubmitStatus = again.StatusCode;
            SecondSubmitError = JsonDocument.Parse(await again.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString();
        }
        // Non-respondent 31 opens and keeps a draft; 30 and 32 never open.
        var (t31, _) = await OpenAsync(31, LiveSurveyId);
        using var d31 = await SendAsync(HttpMethod.Put, "/api/ess/surveys/draft", EmployeeTokens[31],
            new { surveyId = LiveSurveyId, token = t31, answers = new object[] { new { questionKey = "q1", value = 2 }, new { questionKey = "q6", text = "UNSUBMITTED-DRAFT-TEXT" } } });
        d31.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    public async Task CloseAsync(Guid surveyId)
    {
        var detail = await JsonAsync(HttpMethod.Get, $"/api/engagement/surveys/{surveyId}", TokenHr);
        await JsonAsync(HttpMethod.Post, $"/api/engagement/surveys/{surveyId}/close", TokenHr,
            new { version = detail.GetProperty("survey").GetProperty("version").GetInt32() });
    }
}

/// <summary>The fixed answers of the 30 golden respondents (index order = <see cref="Groups"/>).</summary>
public static class Golden
{
    public static readonly (string Dept, string Desig)[] Groups =
        Enumerable.Repeat(("Ops", "Eng"), 10).Concat(Enumerable.Repeat(("Ops", "Ana"), 4)).Concat(Enumerable.Repeat(("Ops", "Sup"), 6))
            .Concat(Enumerable.Repeat(("Fin", "Ana"), 6)).Concat(Enumerable.Repeat(("Fin", "Eng"), 1)).Concat(Enumerable.Repeat(("Leg", "Ana"), 3)).ToArray();

    public static readonly int[] Q1 = [5, 4, 4, 3, 5, 2, 4, 5, 3, 4, 4, 3, 2, 5, 5, 5, 4, 4, 3, 4, 2, 3, 4, 4, 5, 1, 4, 1, 2, 1];
    public static readonly int[] Q2 = [4, 4, 5, 5, 3, 3, 4, 4, 4, 5, 3, 3, 4, 4, 5, 4, 4, 5, 5, 4, 3, 3, 2, 4, 4, 3, 5, 2, 2, 3];
    public static readonly int?[] Q3 = [4, null, 5, 4, 3, 4, 4, null, 5, 4, 3, 4, null, 4, 4, 4, 5, 5, 4, 3, 2, 3, 3, null, 4, 3, 4, 2, null, 3];
    public static readonly int[] Q4 = [9, 10, 8, 7, 9, 6, 10, 9, 8, 9, 7, 6, 5, 9, 10, 9, 9, 8, 7, 10, 6, 7, 8, 5, 9, 4, 9, 3, 5, 2];
    public static readonly string?[] Q5 = ["office", "hybrid", "hybrid", null, "office", "hybrid", "hybrid", "office", "remote", "hybrid", "hybrid", "remote", "office", "hybrid", "office", "office", "hybrid", "hybrid", "remote", "hybrid", "remote", null, "office", "hybrid", "hybrid", "remote", "office", "remote", "hybrid", "office"];
    public static readonly int[] WithComment = [0, 2, 4, 6, 8, 10, 15, 20, 23, 28];

    public static string CommentOf(int i) => $"Golden comment number {i:D2}";

    public static object[] Answers(int i)
    {
        var list = new List<object>
        {
            new { questionKey = "q1", value = Q1[i] },
            new { questionKey = "q2", value = Q2[i] },
            new { questionKey = "q4", value = Q4[i] },
        };
        if (Q3[i] is int q3) list.Add(new { questionKey = "q3", value = q3 });
        if (Q5[i] is string q5) list.Add(new { questionKey = "q5", choices = new[] { q5 } });
        if (WithComment.Contains(i)) list.Add(new { questionKey = "q6", text = CommentOf(i) });
        return list.ToArray();
    }
}

public sealed class EngagementHost : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly RecordingEmail _email;

    public EngagementHost(string connectionString, RecordingEmail email)
    {
        _connectionString = connectionString;
        _email = email;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.UseSetting("ConnectionStrings:Default", _connectionString);
        builder.UseSetting("Database:RunMigrationsOnStartup", "false");
        builder.ConfigureTestServices(services =>
        {
            // Not a licence test: an unlimited licence, so no request is refused 402 for want of one.
            Zayra.Api.Tests.Licensing.LicenceTestServices.UseUnlimitedLicence(services);
            var apiAssembly = typeof(ZayraDbContext).Assembly;
            foreach (var worker in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType?.Assembly == apiAssembly).ToList())
                services.Remove(worker);
            foreach (var email in services.Where(d => d.ServiceType == typeof(IEmailService)).ToList()) services.Remove(email);
            services.AddSingleton<IEmailService>(_email);
        });
    }
}

/// <summary>Captures every email (and its attachments) instead of sending it.</summary>
public sealed class RecordingEmail : IEmailService
{
    public List<(string To, string Subject, IReadOnlyList<EmailAttachment> Attachments)> Sent { get; } = new();

    public Task SendAsync(string toAddress, string toName, string subject, string htmlBody, IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken cancellationToken = default)
    {
        lock (Sent) Sent.Add((toAddress, subject, attachments ?? []));
        return Task.CompletedTask;
    }

    public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
}
