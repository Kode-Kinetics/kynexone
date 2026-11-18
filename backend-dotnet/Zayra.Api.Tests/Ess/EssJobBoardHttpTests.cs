using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;
using Zayra.Api.Tests.Security;

namespace Zayra.Api.Tests.Ess;

/// <summary>
/// The employee internal job board (GET api/ess/jobs), through the REAL pipeline (real JWT, real permission gates, real
/// company query filters). A signed-in employee reads the OPEN openings of their OWN company only, read-only, with a
/// public-safe projection; the management endpoints stay closed to a plain Employee.
/// </summary>
public sealed class EssJobBoardHttpTests(EssJobBoardFixture f) : IClassFixture<EssJobBoardFixture>
{
    private static readonly string[] ForbiddenKeys =
    [
        "salaryFrom", "salaryTo", "assignedHrUserId", "assignedHrName", "requisitionId", "gradeId", "companyId", "tenantId",
        "headCount", "filledCount", "createdAtUtc", "status", "departmentId", "designationId",
    ];

    // ── Visibility ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Employee_SeesExactlyTheirOwnCompanysOpenOpenings()
    {
        var ids = await f.Ids("/api/ess/jobs", "EssA");
        ids.Should().BeEquivalentTo([f.AOpenNew, f.AOpenOld, f.AOpenUnpublished]);
    }

    [Theory]
    [InlineData("AClosed")]
    [InlineData("AOnHold")]
    [InlineData("AInProgress")]
    [InlineData("ACancelled")]
    public async Task Employee_DoesNotSeeAnOpeningThatIsNotOpen(string which)
    {
        var id = f.Id(which);
        (await f.Ids("/api/ess/jobs", "EssA")).Should().NotContain(id);
        (await f.Status("/api/ess/jobs/" + id, "EssA")).Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Employee_DoesNotSeeAnotherCompanysOpenOpening_AndTheOtherEmployeeSeesTheirs()
    {
        (await f.Ids("/api/ess/jobs", "EssA")).Should().NotContain(f.BOpen);
        (await f.Ids("/api/ess/jobs", "EssB")).Should().BeEquivalentTo([f.BOpen]);
    }

    [Fact]
    public async Task Employee_NeverSeesAnotherTenantsOpening_EvenWithTheSameCompanyId()
    {
        (await f.Ids("/api/ess/jobs", "EssA")).Should().NotContain([f.OtherTenantOpen, f.OtherTenantSameCompanyIdOpen]);
        (await f.Status("/api/ess/jobs/" + f.OtherTenantSameCompanyIdOpen, "EssA")).Should().Be(HttpStatusCode.NotFound);
        (await f.Status("/api/ess/jobs/" + f.OtherTenantOpen, "EssA")).Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GroupScopeUser_LinkedToACompanyAEmployee_SeesOnlyCompanyA()
    {
        var ids = await f.Ids("/api/ess/jobs", "GroupA");
        ids.Should().BeEquivalentTo([f.AOpenNew, f.AOpenOld, f.AOpenUnpublished]);
        ids.Should().NotContain([f.BOpen, f.NullCompanyOpen]);
    }

    [Fact]
    public async Task GroupScopeUser_CannotWidenOrSwitchTheBoardWithTheCompanySwitcherHeader()
    {
        // Fails closed or stays on company A; never company B's board.
        using var r = await f.Send("/api/ess/jobs", "GroupA", f.CompanyB);
        var text = await r.Content.ReadAsStringAsync();
        text.Should().NotContain(f.BOpen.ToString());
        (await f.Status("/api/ess/jobs/" + f.BOpen, "GroupA", f.CompanyB)).Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task TenantWideOpening_IsVisibleOnlyToAnEmployeeWhoseCompanyIsAlsoNull()
    {
        (await f.Ids("/api/ess/jobs", "EssNull")).Should().BeEquivalentTo([f.NullCompanyOpen]);
        (await f.Ids("/api/ess/jobs", "EssA")).Should().NotContain(f.NullCompanyOpen);
        (await f.Status("/api/ess/jobs/" + f.NullCompanyOpen, "EssA")).Should().Be(HttpStatusCode.NotFound);
        (await f.Status("/api/ess/jobs/" + f.AOpenNew, "EssNull")).Should().Be(HttpStatusCode.NotFound);
    }

    // ── Detail ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Detail_OwnOpening_Is200_WithTheDisplayFields()
    {
        var item = await f.Json("/api/ess/jobs/" + f.AOpenNew, "EssA");
        item.GetProperty("id").GetGuid().Should().Be(f.AOpenNew);
        item.GetProperty("jobCode").GetString().Should().Be("JOB-A-NEW");
        item.GetProperty("title").GetString().Should().Be("Alpha Engineer");
        item.GetProperty("departmentName").GetString().Should().Be("Platform");
        item.GetProperty("designationTitle").GetString().Should().Be("Senior Engineer");
        item.GetProperty("employmentType").GetString().Should().Be("Full-Time");
        item.GetProperty("location").GetString().Should().Be("Riyadh");
        item.GetProperty("description").GetString().Should().Be("Build things");
        item.GetProperty("requirements").GetString().Should().Be("Five years");
        item.GetProperty("responsibilities").GetString().Should().Be("Ship");
        item.GetProperty("publishedAtUtc").ValueKind.Should().Be(JsonValueKind.String);
    }

    [Fact]
    public async Task Detail_OtherCompanyClosedOtherTenantAndMissing_AreTheSameNotFound()
    {
        var bodies = new List<string>();
        foreach (var id in new[] { f.BOpen, f.AClosed, f.OtherTenantOpen, Guid.NewGuid() })
        {
            using var r = await f.Send("/api/ess/jobs/" + id, "EssA");
            r.StatusCode.Should().Be(HttpStatusCode.NotFound);
            bodies.Add(await r.Content.ReadAsStringAsync());
        }
        bodies.Distinct().Should().HaveCount(1, "an invisible opening must not be distinguishable from a missing one");
    }

    // ── Projection ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Json_CarriesNoManagementFields_ListOrDetail_AtAnyDepth()
    {
        foreach (var path in new[] { "/api/ess/jobs", "/api/ess/jobs/" + f.AOpenNew })
        {
            using var r = await f.Send(path, "EssA");
            r.StatusCode.Should().Be(HttpStatusCode.OK);
            var text = await r.Content.ReadAsStringAsync();
            var keys = new List<string>();
            Collect(JsonDocument.Parse(text).RootElement, keys);
            keys.Should().NotBeEmpty();
            keys.Select(k => k.ToLowerInvariant()).Should().NotContain(ForbiddenKeys.Select(k => k.ToLowerInvariant()),
                $"{path} must not leak management fields");
            text.Should().NotContain("Secret HR Person").And.NotContain("9999").And.NotContain(f.RequisitionMarker.ToString())
                .And.NotContain(f.CompanyA.ToString()).And.NotContain(f.GradeMarker.ToString());
        }
    }

    [Fact]
    public async Task List_HasThePagedEnvelope_InCamelCase()
    {
        var json = await f.Json("/api/ess/jobs", "EssA");
        json.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["items", "total", "page", "pageSize"]);
        json.GetProperty("total").GetInt32().Should().Be(3);
        json.GetProperty("page").GetInt32().Should().Be(1);
        json.GetProperty("pageSize").GetInt32().Should().Be(25);
        json.GetProperty("items")[0].EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            ["id", "jobCode", "title", "departmentName", "designationTitle", "employmentType", "location", "description",
             "requirements", "responsibilities", "publishedAtUtc"]);
    }

    // ── Ordering, paging, search ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_IsOrderedByPublishedDescending_ThenUnpublishedLast()
    {
        (await f.Ids("/api/ess/jobs", "EssA")).Should().Equal(f.AOpenNew, f.AOpenOld, f.AOpenUnpublished);
    }

    [Fact]
    public async Task Paging_ClampsPageAndPageSize_AndTotalIsTheFullCount()
    {
        var def = await f.Json("/api/ess/jobs", "EssC");
        def.GetProperty("total").GetInt32().Should().Be(EssJobBoardFixture.BulkCount);
        def.GetProperty("pageSize").GetInt32().Should().Be(25);
        def.GetProperty("items").GetArrayLength().Should().Be(25);

        var big = await f.Json("/api/ess/jobs?pageSize=1000", "EssC");
        big.GetProperty("pageSize").GetInt32().Should().Be(100);
        big.GetProperty("items").GetArrayLength().Should().Be(100);
        big.GetProperty("total").GetInt32().Should().Be(EssJobBoardFixture.BulkCount);

        var zero = await f.Json("/api/ess/jobs?pageSize=0&page=0", "EssC");
        zero.GetProperty("pageSize").GetInt32().Should().Be(1);
        zero.GetProperty("page").GetInt32().Should().Be(1);
        zero.GetProperty("items").GetArrayLength().Should().Be(1);

        var last = await f.Json("/api/ess/jobs?pageSize=100&page=2", "EssC");
        last.GetProperty("items").GetArrayLength().Should().Be(EssJobBoardFixture.BulkCount - 100);

        var beyond = await f.Json("/api/ess/jobs?pageSize=100&page=9", "EssC");
        beyond.GetProperty("items").GetArrayLength().Should().Be(0);
        beyond.GetProperty("total").GetInt32().Should().Be(EssJobBoardFixture.BulkCount);
    }

    [Fact]
    public async Task Paging_PagesDoNotOverlap()
    {
        var p1 = await f.Ids("/api/ess/jobs?pageSize=60&page=1", "EssC");
        var p2 = await f.Ids("/api/ess/jobs?pageSize=60&page=2", "EssC");
        p1.Intersect(p2).Should().BeEmpty();
        p1.Concat(p2).Distinct().Count().Should().Be(EssJobBoardFixture.BulkCount);
    }

    [Theory]
    [InlineData("ALPHA ENG")]      // title, case-insensitive
    [InlineData("platform")]       // department
    [InlineData("senior engineer")] // designation
    [InlineData("riyadh")]         // location
    public async Task Search_MatchesTitleDepartmentDesignationAndLocation_CaseInsensitively(string q)
    {
        var json = await f.Json($"/api/ess/jobs?q={Uri.EscapeDataString(q)}", "EssA");
        json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).Should().Contain(f.AOpenNew);
    }

    [Fact]
    public async Task Search_NeverReachesAnotherCompanysOrANonOpenOpening()
    {
        var beta = await f.Json("/api/ess/jobs?q=beta", "EssA");
        beta.GetProperty("total").GetInt32().Should().Be(0);
        var closed = await f.Json("/api/ess/jobs?q=closed", "EssA");
        closed.GetProperty("total").GetInt32().Should().Be(0);
        var none = await f.Json("/api/ess/jobs?q=zzzzzz", "EssA");
        none.GetProperty("total").GetInt32().Should().Be(0);
        none.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    // ── Gates ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UnlinkedUser_Gets404_WithGuidanceInBothLanguages()
    {
        using var r = await f.Send("/api/ess/jobs", "Unlinked");
        r.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("message").GetString().Should().Be(EssLinkGuidance.En);
        body.GetProperty("messageAr").GetString().Should().Be(EssLinkGuidance.Ar);
        (await f.Status("/api/ess/jobs/" + f.AOpenNew, "Unlinked")).Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task OneCompanyEmployeeWithNoEntityGrant_IsTreatedAsUnlinked_ByTheSharedResolver_AndSeesNoJobs()
    {
        // The shared CallerEmployeeResolver reads Employees through the company filter: with no grant the employee row is hidden,
        // so self-service answers 404 with the link guidance (fails closed). Pinned so a change to that is deliberate.
        using var r = await f.Send("/api/ess/jobs", "EssNoGrant");
        r.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await r.Content.ReadAsStringAsync()).Should().NotContain(f.AOpenNew.ToString());
    }

    [Fact]
    public async Task UserWithoutEssRead_Is403()
    {
        (await f.Status("/api/ess/jobs", "NoEss")).Should().Be(HttpStatusCode.Forbidden);
        (await f.Status("/api/ess/jobs/" + f.AOpenNew, "NoEss")).Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task KioskOnlyAccessMode_IsRefusedByTheController_WithBadRequest()
    {
        (await f.Status("/api/ess/jobs", "KioskUser")).Should().Be(HttpStatusCode.BadRequest);
        (await f.Status("/api/ess/jobs/" + f.AOpenNew, "KioskUser")).Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task NoLoginAccessMode_IsRefused_BeforeTheControllerEvenRuns()
    {
        // The authentication pipeline already rejects a NoLogin session (401); the controller's own 400 is the second line.
        (await f.Status("/api/ess/jobs", "NoLoginUser")).Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.BadRequest);
        (await f.Status("/api/ess/jobs/" + f.AOpenNew, "NoLoginUser")).Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Unauthenticated_Is401()
    {
        using var a = await f.Client.GetAsync("/api/ess/jobs");
        a.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var b = await f.Client.GetAsync("/api/ess/jobs/" + f.AOpenNew);
        b.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RequestCannotNameAnotherEmployeeCompanyOrTenant()
    {
        // Extra identifiers on the query string are ignored: the caller's own employee decides the company.
        var ids = await f.Ids($"/api/ess/jobs?companyId={f.CompanyB}&employeeId=1&tenantId={f.OtherTenantId}", "EssA");
        ids.Should().BeEquivalentTo([f.AOpenNew, f.AOpenOld, f.AOpenUnpublished]);
    }

    // ── The management endpoints stay closed to a plain Employee ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("EssA")]
    [InlineData("GroupA")]
    public async Task PlainEmployee_StillGets403_OnTheManagementOpeningEndpoints(string user)
    {
        (await f.Status("/api/recruitment/openings", user)).Should().Be(HttpStatusCode.Forbidden);
        (await f.Status("/api/recruitment/openings/export", user)).Should().Be(HttpStatusCode.Forbidden);
        (await f.Status("/api/recruitment/openings/stats", user)).Should().Be(HttpStatusCode.Forbidden);
        (await f.Status($"/api/recruitment/openings/{f.AOpenNew}", user)).Should().Be(HttpStatusCode.Forbidden);
    }

    // ── What the global company query filter does (why the board ignores it, with explicit predicates) ─────────────────

    [Fact]
    public async Task QueryFilterProbe_OneCompanyUserWithNoGrants_IsHiddenFromTheirOwnCompanysRows()
    {
        // An employee with the read permission but NO entity-access grants resolves to scope None: the operational company
        // filter hides every company-stamped row, including their own company's.
        var json = await f.Json("/api/recruitment/openings", "EssAReader");
        json.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task QueryFilterProbe_GroupUser_SeesEveryCompanyInTheTenant_ButNotAnotherTenant()
    {
        var ids = await f.Ids("/api/recruitment/openings?pageSize=200", "GroupAReader");
        ids.Should().Contain([f.AOpenNew, f.BOpen, f.AClosed]).And.NotContain([f.OtherTenantOpen, f.OtherTenantSameCompanyIdOpen]);
    }

    private static void Collect(JsonElement e, List<string> keys)
    {
        if (e.ValueKind == JsonValueKind.Object)
            foreach (var p in e.EnumerateObject()) { keys.Add(p.Name); Collect(p.Value, keys); }
        else if (e.ValueKind == JsonValueKind.Array)
            foreach (var i in e.EnumerateArray()) Collect(i, keys);
    }
}

public sealed class EssJobBoardFixture : IAsyncLifetime
{
    public const int BulkCount = 105;
    private SqliteConnection _anchor = null!;
    private readonly Dictionary<string, string> _tokens = new();
    private readonly Dictionary<string, Guid> _ids = new();
    private readonly ErrorCapture _errors = new();
    public AuthorizationPipelineHost Host { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;

    public Guid TenantId { get; } = Guid.NewGuid();
    public Guid OtherTenantId { get; } = Guid.NewGuid();
    public Guid CompanyA { get; } = Guid.NewGuid();
    public Guid CompanyB { get; } = Guid.NewGuid();
    public Guid CompanyC { get; } = Guid.NewGuid();
    public Guid OtherTenantCompany { get; } = Guid.NewGuid();
    public Guid RequisitionMarker { get; } = Guid.NewGuid();
    public Guid GradeMarker { get; } = Guid.NewGuid();

    public Guid AOpenNew { get; } = Guid.NewGuid();
    public Guid AOpenOld { get; } = Guid.NewGuid();
    public Guid AOpenUnpublished { get; } = Guid.NewGuid();
    public Guid AClosed { get; } = Guid.NewGuid();
    public Guid BOpen { get; } = Guid.NewGuid();
    public Guid NullCompanyOpen { get; } = Guid.NewGuid();
    public Guid OtherTenantOpen { get; } = Guid.NewGuid();
    public Guid OtherTenantSameCompanyIdOpen { get; } = Guid.NewGuid();
    public Guid Id(string name) => name == "AClosed" ? AClosed : _ids[name];

    public async Task InitializeAsync()
    {
        var connection = $"Data Source=file:ess-jobs-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _anchor = new SqliteConnection(connection);
        await _anchor.OpenAsync();
        Host = new AuthorizationPipelineHost(connection, services => services.AddLogging(b => b.AddProvider(_errors)));
        Client = Host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        await db.Database.EnsureCreatedAsync();

        db.Tenants.Add(new Tenant { Id = TenantId, Name = "Board", Slug = $"board-{TenantId:N}", IsActive = true, AccountType = TenantAccountTypes.Group });
        db.Tenants.Add(new Tenant { Id = OtherTenantId, Name = "Other", Slug = $"other-{OtherTenantId:N}", IsActive = true });
        db.Companies.Add(new Company { Id = CompanyA, TenantId = TenantId, LegalNameEn = "Alpha", CountryCode = "SA", IsActive = true });
        db.Companies.Add(new Company { Id = CompanyB, TenantId = TenantId, LegalNameEn = "Beta", CountryCode = "SA", IsActive = true });
        db.Companies.Add(new Company { Id = CompanyC, TenantId = TenantId, LegalNameEn = "Gamma", CountryCode = "SA", IsActive = true });
        db.Companies.Add(new Company { Id = OtherTenantCompany, TenantId = OtherTenantId, LegalNameEn = "Delta", CountryCode = "SA", IsActive = true });

        var now = DateTime.UtcNow;
        JobOpening Opening(Guid id, Guid tenant, Guid? company, string code, string title, string status, DateTime? published, DateTime created,
            string dept = "Platform", string desig = "Senior Engineer", string location = "Riyadh") => new()
        {
            Id = id, TenantId = tenant, CompanyId = company, JobCode = code, Title = title, Status = status, PublishedAtUtc = published, CreatedAtUtc = created,
            DepartmentName = dept, DesignationTitle = desig, Location = location, EmploymentType = "Full-Time", Description = "Build things",
            Requirements = "Five years", Responsibilities = "Ship", SalaryFrom = 9999m, SalaryTo = 19999m, AssignedHrUserId = Guid.NewGuid(),
            AssignedHrName = "Secret HR Person", RequisitionId = RequisitionMarker, GradeId = GradeMarker, HeadCount = 7, FilledCount = 3,
        };
        db.JobOpenings.Add(Opening(AOpenNew, TenantId, CompanyA, "JOB-A-NEW", "Alpha Engineer", "Open", now.AddDays(-1), now.AddDays(-10)));
        db.JobOpenings.Add(Opening(AOpenOld, TenantId, CompanyA, "JOB-A-OLD", "Alpha Analyst", "Open", now.AddDays(-5), now.AddDays(-20), dept: "Finance", desig: "Analyst", location: "Jeddah"));
        db.JobOpenings.Add(Opening(AOpenUnpublished, TenantId, CompanyA, "JOB-A-UNP", "Alpha Clerk", "Open", null, now.AddDays(-30), dept: "Admin", desig: "Clerk", location: "Dammam"));
        db.JobOpenings.Add(Opening(AClosed, TenantId, CompanyA, "JOB-A-CLO", "Alpha Closed", "Closed", now.AddDays(-2), now.AddDays(-9)));
        foreach (var (name, status) in new[] { ("AOnHold", "OnHold"), ("AInProgress", "InProgress"), ("ACancelled", "Cancelled") })
        {
            var id = Guid.NewGuid();
            _ids[name] = id;
            db.JobOpenings.Add(Opening(id, TenantId, CompanyA, "JOB-A-" + name.ToUpperInvariant(), "Alpha " + status, status, now.AddDays(-3), now.AddDays(-8)));
        }
        db.JobOpenings.Add(Opening(BOpen, TenantId, CompanyB, "JOB-B-1", "Beta Engineer", "Open", now.AddDays(-1), now.AddDays(-4), dept: "Beta Dept", location: "Khobar"));
        db.JobOpenings.Add(Opening(NullCompanyOpen, TenantId, null, "JOB-N-1", "Tenant Wide Role", "Open", now.AddDays(-1), now.AddDays(-4), dept: "Shared", location: "Remote"));
        db.JobOpenings.Add(Opening(OtherTenantOpen, OtherTenantId, OtherTenantCompany, "JOB-X-1", "Other Tenant Role", "Open", now.AddDays(-1), now.AddDays(-4)));
        db.JobOpenings.Add(Opening(OtherTenantSameCompanyIdOpen, OtherTenantId, CompanyA, "JOB-X-2", "Other Tenant Same Company Id", "Open", now.AddDays(-1), now.AddDays(-4)));
        for (var i = 0; i < BulkCount; i++)
            db.JobOpenings.Add(Opening(Guid.NewGuid(), TenantId, CompanyC, $"JOB-C-{i:000}", $"Gamma Role {i:000}", "Open", now.AddMinutes(-i), now.AddDays(-40), dept: "Bulk", location: "Tabuk"));
        await db.SaveChangesAsync();
        await new AuthSeeder(db).EnsureTenantRolesAsync(TenantId, CancellationToken.None);

        Employee Emp(string code, Guid? company) => new()
        {
            TenantId = TenantId, CompanyId = company, EmployeeCode = code, FullName = code, EnglishName = code, Status = EmployeeStatuses.Active,
        };
        var empA = Emp("E-A", CompanyA); var empB = Emp("E-B", CompanyB); var empC = Emp("E-C", CompanyC); var empNull = Emp("E-N", null);
        var empNoGrant = Emp("E-NG", CompanyA); var empNoEss = Emp("E-NOESS", CompanyA); var empNoLogin = Emp("E-NOLOGIN", CompanyA); var empKiosk = Emp("E-KIOSK", CompanyA);
        var empAReader = Emp("E-AR", CompanyA); var empGroupReader = Emp("E-GR", CompanyA); var empGroupA = Emp("E-GA", CompanyA);
        db.Employees.AddRange(empNoGrant, empA, empB, empC, empNull, empNoEss, empNoLogin, empKiosk, empAReader, empGroupReader, empGroupA);
        await db.SaveChangesAsync();

        await AddUser(db, scope, "EssA", "Employee", false, empA.Id, grantCompany: CompanyA);
        await AddUser(db, scope, "EssB", "Employee", false, empB.Id, grantCompany: CompanyB);
        await AddUser(db, scope, "EssC", "Employee", false, empC.Id, grantCompany: CompanyC);
        // A company-less employee can only belong to a group-scope login (a one-company grant needs a company).
        await AddUser(db, scope, "EssNull", "Employee", true, empNull.Id);
        await AddUser(db, scope, "EssNoGrant", "Employee", false, empNoGrant.Id);
        await AddUser(db, scope, "GroupA", "Employee", true, empGroupA.Id);
        await AddUser(db, scope, "Unlinked", "Employee", false, null);
        await AddUser(db, scope, "NoEss", "Employee", false, empNoEss.Id, deny: ["ess.read", "ess.write"]);
        await AddUser(db, scope, "NoLoginUser", "Employee", false, empNoLogin.Id, linkMode: AccessModes.NoLogin);
        await AddUser(db, scope, "KioskUser", "Employee", false, empKiosk.Id, linkMode: AccessModes.KioskOnly, allow: ["ess.read"]);
        await AddUser(db, scope, "EssAReader", "Employee", false, empAReader.Id, allow: ["recruitment.read"]);
        await AddUser(db, scope, "GroupAReader", "Employee", true, empGroupReader.Id, allow: ["recruitment.read"]);
    }

    private async Task AddUser(ZayraDbContext db, IServiceScope scope, string key, string role, bool groupScope, int? employeeId,
        string[]? allow = null, string[]? deny = null, string linkMode = AccessModes.FullPortal, Guid? grantCompany = null)
    {
        var email = $"{key.ToLowerInvariant()}-{TenantId:N}@board.local";
        var user = new User
        {
            TenantId = TenantId, Email = email, NormalizedEmail = email.ToUpperInvariant(), FullName = key, PasswordHash = "no-login-path",
            Status = "Active", AccessMode = "FullPortal", IsActive = true, IsEmailConfirmed = true, IsGroupScope = groupScope,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var roleRow = await db.Roles.SingleAsync(r => r.TenantId == TenantId && r.Name == role);
        db.Set<UserRole>().Add(new UserRole { UserId = user.Id, RoleId = roleRow.Id });
        // Like a login provisioned from an employee record: one grant for the employee's own company.
        if (grantCompany is Guid gc) db.UserEntityAccesses.Add(new UserEntityAccess { TenantId = TenantId, UserId = user.Id, CompanyId = gc, Role = "Employee", IsActive = true });
        foreach (var k in allow ?? [])
            db.Set<UserPermissionOverride>().Add(new UserPermissionOverride { TenantId = TenantId, UserId = user.Id, PermissionKey = k, Effect = "Allow", Reason = "test" });
        foreach (var k in deny ?? [])
            db.Set<UserPermissionOverride>().Add(new UserPermissionOverride { TenantId = TenantId, UserId = user.Id, PermissionKey = k, Effect = "Deny", Reason = "test" });
        if (employeeId is int e)
            db.EmployeeUserAccounts.Add(new EmployeeUserAccount { TenantId = TenantId, EmployeeId = e, UserId = user.Id, AccessMode = linkMode, Status = "Active", RequiresPasswordSetup = false });
        await db.SaveChangesAsync();

        var loaded = await db.Users.IgnoreQueryFilters().AsNoTracking().Include(u => u.Tenant)
            .Include(u => u.UserRoles).ThenInclude(r => r.Role).ThenInclude(r => r!.RolePermissions).ThenInclude(p => p.Permission)
            .Include(u => u.PermissionOverrides).Include(u => u.EmployeeUserAccounts).Include(u => u.EntityAccesses)
            .SingleAsync(u => u.TenantId == TenantId && u.Email == email);
        var grants = loaded.EntityAccesses.Where(a => a.IsActive).Select(a => new EntityAccessGrant(a.CompanyId, a.Role, a.GrantMode)).ToList();
        _tokens[key] = scope.ServiceProvider.GetRequiredService<ITokenService>().CreateAccessToken(loaded, AuthService.GetRoles(loaded),
            AuthService.GetPermissions(loaded), loaded.Tenant!, grants,
            EntityScopeClaims.Resolve(loaded.IsGroupScope, grants, Array.Empty<Guid>()), out _);
    }

    public async Task<HttpResponseMessage> Send(string path, string user, Guid? companyHeader = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokens[user]);
        if (companyHeader is Guid c) request.Headers.Add(ZayraDbContext.CompanySelectionHeader, c.ToString());
        var response = await Client.SendAsync(request);
        if ((int)response.StatusCode >= 500)
            throw new Xunit.Sdk.XunitException($"GET {path} answered {(int)response.StatusCode}: {_errors.Last()}");
        return response;
    }

    public async Task<HttpStatusCode> Status(string path, string user, Guid? companyHeader = null)
    {
        using var r = await Send(path, user, companyHeader);
        return r.StatusCode;
    }

    public async Task<JsonElement> Json(string path, string user, Guid? companyHeader = null)
    {
        using var r = await Send(path, user, companyHeader);
        var text = await r.Content.ReadAsStringAsync();
        r.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {path} as {user}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    public async Task<Guid[]> Ids(string path, string user, Guid? companyHeader = null)
    {
        var json = await Json(path, user, companyHeader);
        var items = json.ValueKind == JsonValueKind.Array ? json : json.GetProperty("items");
        return items.EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToArray();
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (Host is not null) await Host.DisposeAsync();
        if (_anchor is not null) await _anchor.DisposeAsync();
    }
}
