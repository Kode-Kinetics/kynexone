using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
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

    /// <summary>
    /// A context that presents what the <c>[AllowAnonymous]</c> device webhook actually presents: an
    /// HttpContext whose principal is NOT authenticated, under strict mode (which Production forces via
    /// <c>EntityScopeOptions.ResolveStrictMode</c>).
    ///
    /// <para>The resolver's unauthenticated branch returns <c>IsGroupLevel:false</c> with an EMPTY
    /// company list, so <c>ZayraDbContext._isGroupScope</c> is false and <c>_companyScopeIds</c> is
    /// empty — while <c>_isSystemScope</c> is true, because an unauthenticated request is treated as
    /// pre-HTTP/system context for the TENANT clause. That split is the whole defect: the tenant filter
    /// steps aside and the company filter does not.</para>
    /// </summary>
    private static ZayraDbContext AnonymousDeviceWebhookDb(string store) =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(store).Options,
            new _AttendanceHttpAccessor(new DefaultHttpContext()),
            logger: null,
            scopeOptions: Options.Create(new EntityScopeOptions { StrictMode = true }));

    /// <summary>The plaintext device API key used by the ingest tests. The service stores only its hash.</summary>
    private const string DeviceKey = "knx_ingest_key_for_the_company_scope_test";

    /// <summary>The day both ingest tests punch on.</summary>
    private static readonly DateOnly PunchDay = new(2026, 8, 26);

    /// <summary>Adds an active device owned by <see cref="Tenant"/> and authenticated by <see cref="DeviceKey"/>.</summary>
    private static async Task SeedDeviceAsync(string store)
    {
        await using var seed = new ZayraDbContext(
            new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(store).Options);
        seed.AttendanceDevices.Add(new AttendanceDevice
        {
            TenantId = Tenant,
            DeviceName = "Gate A",
            DeviceType = "Biometric",
            Vendor = "Test",
            SerialNumber = "SN-A-001",
            LocationName = "Main Entrance",
            // Only the hash is stored; the plaintext key IS the credential presented by the webhook.
            ApiKeyReference = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DeviceKey))),
            IsActive = true,
        });
        await seed.SaveChangesAsync();
    }

    /// <summary>
    /// One in-punch and one out-punch for the Company-A employee, auto-processed — the shape a real
    /// device sends. Shared by both ingest tests so the two regressions are always exercised against
    /// exactly the same request; they are two failures of one call, not two scenarios.
    /// </summary>
    private static Task<DeviceIngestResult?> IngestADayOfPunchesAsync(ZayraDbContext db)
    {
        var punchIn = PunchDay.ToDateTime(new TimeOnly(6, 0));
        return Service(db).IngestByDeviceKeyAsync(
            DeviceKey,
            new DeviceIngestRequest(
                Punches: new[]
                {
                    new DeviceIngestPunch("A-001", DateTime.SpecifyKind(punchIn, DateTimeKind.Utc),
                        "In", null, null, null, null, null, null),
                    new DeviceIngestPunch("A-001", DateTime.SpecifyKind(punchIn.AddHours(9), DateTimeKind.Utc),
                        "Out", null, null, null, null, null, null),
                },
                AutoProcess: true),
            "127.0.0.1",
            CancellationToken.None);
    }

    /// <summary>
    /// THE P1 THIS BRANCH EXISTS TO FIX — and, until now, the fix nobody was testing.
    ///
    /// <para>Mutation-tested: reverting <c>bypassCompanyFilter: true</c> to <c>false</c> at the
    /// <c>IngestByDeviceKeyAsync</c> call site left the entire suite green. Every device punch silently
    /// resolved to no employee, so <c>Unmatched</c> equalled the punch count and <c>Processed</c> stayed
    /// 0 — a total, silent loss of device attendance in Production. The existing coverage could not see
    /// it: <c>CrossTenantQueryFilterTests.DeviceIngest_RawEventsCreated_AlwaysTaggedWithDeviceTenant</c>
    /// builds its DbContext with NO IHttpContextAccessor, which resolves to group scope and therefore
    /// never engages the company filter at all, and it asserts <c>Unmatched == 1</c> for a deliberately
    /// non-matching code — the exact value the bug produces.</para>
    ///
    /// <para>This test asserts the OPPOSITE of the bug: a punch carrying a code that DOES exist in the
    /// device's tenant must match, and auto-processing must produce a day.</para>
    /// </summary>
    [Fact]
    public async Task AnAnonymousDeviceWebhookMatchesTheEmployeeAndProcessesTheDay()
    {
        var (store, _) = await SeedAsync();
        await SeedDeviceAsync(store);

        await using var db = AnonymousDeviceWebhookDb(store);

        // PREMISE — without this, the test could pass for the wrong reason. Under the anonymous
        // webhook's scope the AMBIENT read filter hides every employee, because Employee is
        // ICompanyScopedOperational and the company clause resolves to an empty set. The rows are
        // there; the filter is what is hiding them. If this ever stops holding, the assertions below
        // no longer prove the bypass is doing anything and must be re-derived, not deleted.
        (await db.Employees.CountAsync()).Should().Be(0,
            "an anonymous request resolves to an EMPTY company scope, so the ambient filter must hide "
            + "both seeded employees — this is the condition that made device ingest match nothing");
        (await db.Employees.IgnoreQueryFilters().CountAsync()).Should().Be(2,
            "...and the rows must actually exist, or the assertion above is vacuous");

        var result = await IngestADayOfPunchesAsync(db);
        result.Should().NotBeNull("the device key is valid, so the webhook must not reject the batch");

        // One scope so a failure reports BOTH halves of the symptom. They are not independent —
        // Processed is 0 precisely because matchedEmployees stayed empty — and an engineer reading a
        // CI log needs the whole shape of the regression, not just the first assertion to trip.
        using (new AssertionScope())
        {
            result!.Accepted.Should().Be(2, "both punches are new");
            result.Unmatched.Should().Be(0,
                "employee A-001 exists in the device's tenant. If this is 2, ResolveEmployee is being "
                + "run through the company filter again on the anonymous path and every device punch "
                + "in Production is being discarded as unmatched.");
            result.Processed.Should().BeGreaterThan(0,
                "auto-processing must produce a day for the matched employee. This is 0 whenever the "
                + "employee does not resolve, because matchedEmployees stays empty.");
        }

        await using var check = new ZayraDbContext(
            new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(store).Options);
        var daily = await check.AttendanceDailyRecords.ToListAsync();
        daily.Should().ContainSingle("one employee, one day")
            .Which.WorkDate.Should().Be(PunchDay);
    }

    /// <summary>
    /// THE SECOND HALF OF THE SAME DEFECT — the one the first fix created rather than cured.
    ///
    /// <para><c>AttendanceRecord</c> is <c>ICompanyScopedOperational</c>, so a null <c>CompanyId</c>
    /// makes the row invisible to every company-scoped user: the poison default the operational tier
    /// exists to prevent. <c>UpsertLegacyRecord</c> used to resolve that company by re-querying
    /// <c>_db.Employees</c> through the ambient filter — which, on this path, denies everything. So the
    /// row was written with a null company.</para>
    ///
    /// <para>Nothing downstream rescued it: <c>ZayraDbContext.EnforceCompanyScopeOnWritesAsync</c>
    /// returns early when there is no tenant claim, and an anonymous device webhook has none, so the
    /// server-side stamping — including the branch that follows the owning employee's company — never
    /// ran here at all.</para>
    ///
    /// <para>This was LATENT before the ingest fix: nothing matched, so no legacy rows were written.
    /// Fixing the match is what started writing them, which is why the two fixes ship together.</para>
    /// </summary>
    [Fact]
    public async Task TheLegacyRecordFromADeviceIngestCarriesTheEmployeesCompany()
    {
        var (store, _) = await SeedAsync();
        await SeedDeviceAsync(store);

        await using (var db = AnonymousDeviceWebhookDb(store))
        {
            var result = await IngestADayOfPunchesAsync(db);
            result!.Unmatched.Should().Be(0, "the punch must match before there is a legacy row to check");
        }

        using (new AssertionScope())
        {
            // The STORED VALUE, read with no filter in the way at all.
            await using var raw = new ZayraDbContext(
                new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(store).Options);
            var legacy = await raw.AttendanceRecords.IgnoreQueryFilters().ToListAsync();
            legacy.Should().ContainSingle("one employee, one day");
            legacy[0].CompanyId.Should().Be(CompanyA,
                "the legacy row must carry the company of the employee the punch matched. A null here "
                + "is the poison default: AttendanceRecord is ICompanyScopedOperational, so the row is "
                + "written invisible to every company-scoped user and only group scope can see it.");

            // ...and the CONSEQUENCE, which is what actually harms a customer: an ordinary Company-A
            // HR user must be able to see the attendance their own device recorded.
            await using var scoped = CompanyScopedDb(store);
            (await scoped.AttendanceRecords.CountAsync()).Should().Be(1,
                "a Company-A-scoped user must see the row their Company-A device produced. If this is "
                + "0 the attendance exists in the database but has vanished from the product for every "
                + "scoped user, while group-scope admins still see it — a silent split-brain.");
        }
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
