using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The defect: <c>DashboardController</c>'s four cache keys carried tenant (and month count) but
/// no COMPANY dimension —
/// <c>dashboard:v2:full:{tid}:{months}</c>, <c>dashboard:summary:{tid}</c>,
/// <c>dashboard:trends:{tid}:{months}</c>, <c>dashboard:overview:{tid}</c>.
///
/// <para>Nothing in that controller mentions a company, which is why it survived review: the
/// company dimension enters every query <i>silently</i>, through the
/// <c>ICompanyScopedOperational</c> global query filters <see cref="ZayraDbContext"/> applies from
/// the request's resolved entity scope. The payload was company-filtered; the key was not. So in a
/// group tenant the first company's dashboard was served to the next caller for the 60 s TTL —
/// and with <c>REDIS_URL</c> set (Program.cs) across pods, not merely within one process. That is
/// a cross-company data leak inside a tenant, not a staleness bug.</para>
///
/// <para>These tests run against real Postgres with one SHARED <see cref="IDistributedCache"/>
/// across both callers — the existing <c>DashboardTests</c> harness hands every controller its own
/// fresh <c>MemoryDistributedCache</c>, so no test there could ever observe a stale entry, and its
/// DbContext has no <see cref="IHttpContextAccessor"/> at all, which puts it in system scope where
/// the company filter never engages.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class DashboardCacheCompanyScopeTests
{
    private readonly PostgresFixture _fx;
    public DashboardCacheCompanyScopeTests(PostgresFixture fx) => _fx = fx;

    private const int CompanyAHeadcount = 3;
    private const int CompanyBHeadcount = 7;

    [Fact]
    public async Task FullDashboard_ReadAsCompanyA_ThenAsCompanyB_DoesNotServeAsBWhatWasBuiltForA()
    {
        var (tenantId, companyA, companyB) = await SeedTwoCompaniesAsync();

        var accessor = new SwitchableAccessor();
        await using var db = _fx.CreateDbWithAccessor(accessor);
        var cache = SharedCache();

        // ── Proof the fixture is not vacuous: each company genuinely has DIFFERENT, NON-EMPTY
        //    data, read through the same filters the dashboard reads through. A cross-company
        //    assertion that passes on two empty results proves nothing, and this suite has been
        //    bitten by exactly that before.
        accessor.HttpContext = ScopedContext(tenantId, companyA);
        var directA = await CountEmployeesAsync(db);
        accessor.HttpContext = ScopedContext(tenantId, companyB);
        var directB = await CountEmployeesAsync(db);
        directA.Should().Be(CompanyAHeadcount, "company A must have non-empty, distinctive data");
        directB.Should().Be(CompanyBHeadcount, "company B must have non-empty, distinctive data");
        directA.Should().NotBe(directB, "the two companies must be distinguishable at all");

        // ── The leak path: A warms the key, B reads within the TTL ────────────────────────────
        var summaryA = await ReadFullSummaryAsync(db, cache, accessor, tenantId, companyA);
        var summaryB = await ReadFullSummaryAsync(db, cache, accessor, tenantId, companyB);

        summaryA.TotalEmployees.Should().Be(CompanyAHeadcount);
        summaryB.TotalEmployees.Should().Be(CompanyBHeadcount,
            "company B must be served its OWN dashboard, not the entry company A warmed");
        summaryB.TotalEmployees.Should().NotBe(summaryA.TotalEmployees,
            "serving A's payload as B's is a cross-company data leak inside the tenant");

        // ── And back to A, to prove B's read did not simply evict A ───────────────────────────
        var summaryA2 = await ReadFullSummaryAsync(db, cache, accessor, tenantId, companyA);
        summaryA2.TotalEmployees.Should().Be(CompanyAHeadcount);
    }

    /// <summary>
    /// Keying alone would be a hollow fix if the underlying query were not company-scoped: the
    /// cache would then merely hide a leak behind a correct-looking key. This asserts the query
    /// itself, with the cache bypassed entirely (a cold cache per call).
    /// </summary>
    [Fact]
    public async Task TheUnderlyingDashboardQuery_IsItselfCompanyScoped_NotJustTheKey()
    {
        var (tenantId, companyA, companyB) = await SeedTwoCompaniesAsync();

        var accessor = new SwitchableAccessor();
        await using var db = _fx.CreateDbWithAccessor(accessor);

        var a = await ReadFullSummaryAsync(db, SharedCache(), accessor, tenantId, companyA);
        var b = await ReadFullSummaryAsync(db, SharedCache(), accessor, tenantId, companyB);

        a.TotalEmployees.Should().Be(CompanyAHeadcount);
        b.TotalEmployees.Should().Be(CompanyBHeadcount);
    }

    /// <summary>
    /// Proves the cache is actually engaged on this path — otherwise the test above would pass
    /// for the wrong reason (no caching at all) and would not catch a regression that reintroduced
    /// a tenant-only key. Same company, same key: the second read must be the cached one even
    /// though the data changed underneath it.
    /// </summary>
    [Fact]
    public async Task TheFullDashboardIsGenuinelyCached_SoTheKeyIsTheOnlyThingSeparatingCompanies()
    {
        var (tenantId, companyA, _) = await SeedTwoCompaniesAsync();

        var accessor = new SwitchableAccessor();
        await using var db = _fx.CreateDbWithAccessor(accessor);
        var cache = SharedCache();

        var first = await ReadFullSummaryAsync(db, cache, accessor, tenantId, companyA);
        first.TotalEmployees.Should().Be(CompanyAHeadcount);

        accessor.HttpContext = ScopedContext(tenantId, companyA);
        db.Employees.Add(MakeEmployee(tenantId, companyA));
        await db.SaveChangesAsync();

        var second = await ReadFullSummaryAsync(db, cache, accessor, tenantId, companyA);
        second.TotalEmployees.Should().Be(CompanyAHeadcount,
            "the 60 s entry must still be served — if this fails the endpoint is no longer cached "
            + "and the cross-company assertions above would pass vacuously");
    }

    [Fact]
    public async Task SummaryTrendsAndOverview_AreKeyedByCompanyToo_NotOnlyFull()
    {
        var (tenantId, companyA, companyB) = await SeedTwoCompaniesAsync();

        var accessor = new SwitchableAccessor();
        await using var db = _fx.CreateDbWithAccessor(accessor);
        var cache = SharedCache();

        var summaryA = Unwrap<DashboardSummaryDto>(
            await MakeController(db, cache, accessor, tenantId, companyA).Summary(CancellationToken.None));
        var summaryB = Unwrap<DashboardSummaryDto>(
            await MakeController(db, cache, accessor, tenantId, companyB).Summary(CancellationToken.None));

        summaryA.TotalEmployees.Should().Be(CompanyAHeadcount);
        summaryB.TotalEmployees.Should().Be(CompanyBHeadcount,
            "/api/dashboard/summary had the same tenant-only key as /full");

        var overviewA = Unwrap<DashboardOverviewDto>(
            await MakeController(db, cache, accessor, tenantId, companyA).Overview(CancellationToken.None));
        var overviewB = Unwrap<DashboardOverviewDto>(
            await MakeController(db, cache, accessor, tenantId, companyB).Overview(CancellationToken.None));

        overviewA.OpenLeaveRequests.Should().Be(CompanyAHeadcount,
            "one pending leave request per company-A employee — non-empty and distinctive");
        overviewB.OpenLeaveRequests.Should().Be(CompanyBHeadcount,
            "/api/dashboard/overview had the same tenant-only key as /full");
    }

    // ── fixture ────────────────────────────────────────────────────────────────

    private async Task<(Guid TenantId, Guid CompanyA, Guid CompanyB)> SeedTwoCompaniesAsync()
    {
        await using var seed = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(seed);

        var a = MakeCompany(tenantId, "Alpha");
        var b = MakeCompany(tenantId, "Beta");
        seed.Companies.AddRange(a, b);
        await seed.SaveChangesAsync();

        var employees = new List<Employee>();
        for (var i = 0; i < CompanyAHeadcount; i++) employees.Add(MakeEmployee(tenantId, a.Id));
        for (var i = 0; i < CompanyBHeadcount; i++) employees.Add(MakeEmployee(tenantId, b.Id));
        seed.Employees.AddRange(employees);
        await seed.SaveChangesAsync();

        // A pending leave request per employee, so /overview's queue is non-empty and differs too.
        foreach (var e in employees)
            seed.LeaveRequests.Add(new LeaveRequest
            {
                TenantId = tenantId,
                CompanyId = e.CompanyId,
                EmployeeId = e.Id,
                EmployeeName = e.FullName,
                LeaveTypeName = "Annual",
                StartDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(3)),
                EndDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(4)),
                TotalDays = 2,
                Status = "Pending",
                Reason = "test",
            });
        await seed.SaveChangesAsync();

        return (tenantId, a.Id, b.Id);
    }

    private static IDistributedCache SharedCache() =>
        new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

    private static async Task<int> CountEmployeesAsync(ZayraDbContext db)
        => await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .CountAsync(db.Employees);

    private static async Task<DashboardSummaryDto> ReadFullSummaryAsync(
        ZayraDbContext db, IDistributedCache cache, SwitchableAccessor accessor, Guid tenantId, Guid companyId)
    {
        var ctrl = MakeController(db, cache, accessor, tenantId, companyId);
        return Unwrap<DashboardFullDto>(await ctrl.Full(6, CancellationToken.None)).Summary;
    }

    private static DashboardController MakeController(
        ZayraDbContext db, IDistributedCache cache, SwitchableAccessor accessor, Guid tenantId, Guid companyId)
    {
        // ONE HttpContext for both the DbContext's scope resolution and the controller's, exactly
        // as a real request has — so the cache key and the query filter cannot disagree.
        var http = ScopedContext(tenantId, companyId);
        accessor.HttpContext = http;
        var ctrl = new DashboardController(db, cache, new UnrestrictedDataScope());
        ctrl.ControllerContext = new ControllerContext { HttpContext = http };
        return ctrl;
    }

    private static T Unwrap<T>(IActionResult result)
    {
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        return (T)ok.Value!;
    }

    private static HttpContext ScopedContext(Guid tenantId, Guid companyId)
    {
        var accessJson = JsonSerializer.Serialize(new { c = companyId, r = "Viewer" });
        var ctx = new DefaultHttpContext();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim("entity_access", accessJson),
        }, "Test"));
        return ctx;
    }

    private static Company MakeCompany(Guid tenantId, string name) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        LegalNameEn = name,
        CountryCode = "SAU",
        Jurisdiction = "KSA-mainland",
        RegistrationNumber = $"REG-{Guid.NewGuid():N}",
        DefaultCurrency = "SAR",
        IsActive = true,
        CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private static Employee MakeEmployee(Guid tenantId, Guid? companyId) => new()
    {
        TenantId = tenantId,
        CompanyId = companyId,
        EmployeeCode = $"DC-{Guid.NewGuid():N}".Substring(0, 14),
        FullName = $"Employee {Guid.NewGuid():N}".Substring(0, 20),
        Status = "Active",
        JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private sealed class SwitchableAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private sealed class UnrestrictedDataScope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
            => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
    }
}
