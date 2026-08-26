using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// WAVE 1 — attendance employee resolution and the company boundary.
///
/// <para><b>Two defects live here, in opposite directions, and the fix for one created the other.</b></para>
///
/// <para>FIRST: device ingest matched NOTHING in production. <c>/api/attendance/ingest</c> is
/// <c>[AllowAnonymous]</c> — the device API key is the credential, so there is no JWT — and
/// <c>Employee</c> is <c>ICompanyScopedOperational</c>. The company clause of the read filter derives
/// from the request principal and is independent of the system-scope bypass, so under strict mode
/// (which Production forces) an anonymous request resolved to an EMPTY company scope and the employee
/// lookup matched zero rows. Every punch was recorded unmatched and no daily record was ever created.</para>
///
/// <para>SECOND: the fix for that applied <c>IgnoreQueryFilters</c> unconditionally — and
/// <c>ResolveEmployee</c> has a second caller, the AUTHENTICATED <c>PushEventAsync</c>. Its controller
/// pre-check resolves the employee through a FILTERED query, so a target in another company came back
/// null, <c>employeeId is not null</c> was false, and the <c>Forbid()</c> was skipped — after which the
/// now-unfiltered lookup found the row and recorded a punch against another company's employee.
/// Employee ids are sequential integers, so that was trivially enumerable.</para>
///
/// <para>The bypass is therefore GATED: legitimate only where there is no principal to scope by.</para>
/// </summary>
public class AttendanceCompanyScopeTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid CompanyA = Guid.NewGuid();
    private static readonly Guid CompanyB = Guid.NewGuid();

    /// <summary>A context whose principal is scoped to Company A only — a real company-scoped caller.</summary>
    private static ZayraDbContext CompanyScopedDb(string store)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", Tenant.ToString()),
            new(EntityScopeContext.V2ClaimType,
                JsonSerializer.Serialize(new { v = 2, m = "companies", c = new[] { CompanyA.ToString() } })),
        };
        var httpCtx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")),
        };
        return new ZayraDbContext(
            new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(store).Options,
            new _AttendanceHttpAccessor(httpCtx));
    }

    private static async Task<(string Store, int CompanyBEmployeeId)> SeedAsync()
    {
        var store = Guid.NewGuid().ToString();
        await using var db = new ZayraDbContext(
            new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(store).Options);

        var inA = new Employee
        {
            TenantId = Tenant, CompanyId = CompanyA, EmployeeCode = "A-001",
            FullName = "Aisha In CompanyA", Status = "Active",
            JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var inB = new Employee
        {
            TenantId = Tenant, CompanyId = CompanyB, EmployeeCode = "B-001",
            FullName = "Bilal In CompanyB", Status = "Active",
            JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Employees.AddRange(inA, inB);
        await db.SaveChangesAsync();
        return (store, inB.Id);
    }

    private static AttendanceService Service(ZayraDbContext db) =>
        new(db, new _AttendanceNullNotifications(), new _AttendanceNullHttpClientFactory());

    /// <summary>
    /// THE REGRESSION. An authenticated Company-A caller must not be able to record a punch against a
    /// Company-B employee. Before the bypass was gated this succeeded and created an
    /// <c>AttendanceRawEvent</c> — which is <c>ITenantOwned</c> only, so the write-side company guard
    /// does not cover it either. There was no backstop.
    /// </summary>
    [Fact]
    public async Task AnAuthenticatedCompanyScopedCaller_CannotPushAPunchForAnotherCompanysEmployee()
    {
        var (store, companyBEmployeeId) = await SeedAsync();
        await using var db = CompanyScopedDb(store);
        var service = Service(db);

        var act = async () => await service.PushEventAsync(
            Tenant,
            new AttendanceRawEventRequest(
                EmployeeId: companyBEmployeeId, EmployeeCode: null, DeviceId: null, Source: "test",
                PunchTimestampUtc: new DateTime(2026, 8, 26, 8, 0, 0, DateTimeKind.Utc),
                PunchDirection: "In", LocationName: null, Latitude: null, Longitude: null,
                IpAddress: null, PhotoReference: null, RawPayloadJson: null, SyncBatchReference: null,
                VerificationMethod: null, ConfidenceScore: null),
            new Zayra.Api.Application.Auth.RequestContext("127.0.0.1", "tests", null, Tenant),
            default);

        // The company filter is the control on this path, so an out-of-scope employee must simply not
        // resolve — the service then refuses rather than writing.
        await act.Should().ThrowAsync<InvalidOperationException>();

        await using var check = new ZayraDbContext(
            new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(store).Options);
        (await check.AttendanceRawEvents.CountAsync())
            .Should().Be(0, "a denied cross-company push must write nothing at all");
    }

    /// <summary>The same caller acting within their OWN company must still work — otherwise the test
    /// above would pass on a service that simply never resolves anyone.</summary>
    [Fact]
    public async Task TheSameCallerCanStillPushForTheirOwnCompany()
    {
        var (store, _) = await SeedAsync();
        await using var db = CompanyScopedDb(store);
        var service = Service(db);

        var pushed = await service.PushEventAsync(
            Tenant,
            new AttendanceRawEventRequest(
                EmployeeId: null, EmployeeCode: "A-001", DeviceId: null, Source: "test",
                PunchTimestampUtc: new DateTime(2026, 8, 26, 8, 0, 0, DateTimeKind.Utc),
                PunchDirection: "In", LocationName: null, Latitude: null, Longitude: null,
                IpAddress: null, PhotoReference: null, RawPayloadJson: null, SyncBatchReference: null,
                VerificationMethod: null, ConfidenceScore: null),
            new Zayra.Api.Application.Auth.RequestContext("127.0.0.1", "tests", null, Tenant),
            default);

        pushed.Should().NotBeNull();
        await using var check = new ZayraDbContext(
            new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(store).Options);
        (await check.AttendanceRawEvents.CountAsync()).Should().Be(1);
    }
}

file sealed class _AttendanceHttpAccessor : IHttpContextAccessor
{
    public _AttendanceHttpAccessor(HttpContext ctx) => HttpContext = ctx;
    public HttpContext? HttpContext { get; set; }
}

file sealed class _AttendanceNullNotifications : Zayra.Api.Infrastructure.Notifications.INotificationService
{
    public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string entity, string? entityId, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
}

file sealed class _AttendanceNullHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}
