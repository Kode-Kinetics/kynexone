using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;
using static Zayra.Api.Tests.Security.SeededRoleBundles;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// One API instance serves every tenant, so what a tenant can make it do is bounded:
///  - a device "Pull" calls a tenant-chosen endpoint: the wait is clamped, the body is read through a byte cap, and what
///    is written back is our own text, never the remote's status line, body or exception;
///  - the attendance reports need attendance.read, cover at most 366 days, and apply the caller's scope in SQL;
///  - an attendance-correction notice goes to the people it concerns, never to the whole tenant.
/// </summary>
public sealed class AttendancePullAndReportLimitsTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    // A literal public address: the SSRF pre-check passes without DNS, and the scripted handler answers for it.
    private const string DeviceUrl = "https://8.8.8.8/att";

    // ── 1. Device pull ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task APullThatStreamsMoreThanTheCap_FailsCleanly_AndReadsNoMoreThanTheCap()
    {
        var (db, tenantId) = await NewTenantAsync("pull-cap");
        var device = await SeedPullDeviceAsync(db, tenantId);
        var endless = new CountingStream(totalBytes: 2L * 1024 * 1024 * 1024); // 2 GB on offer, chunked (no Content-Length)
        var service = Service(db, _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(endless) });

        var log = (await service.SyncDeviceAsync(tenantId, device.Id, Context(tenantId), Ct))!;

        log.Status.Should().Be("Failed");
        log.ErrorMessage.Should().Contain("more than 4 MB").And.Contain("poll_path");
        log.RawEventsReceived.Should().Be(0);
        endless.BytesServed.Should().BeLessThanOrEqualTo(AttendanceService.SyncResponseByteCap + 81920,
            "reading stops at the cap, whatever the device keeps sending");
    }

    [Fact]
    public async Task APullThatDeclaresALengthOverTheCap_IsRefusedWithoutReadingTheBody()
    {
        var (db, tenantId) = await NewTenantAsync("pull-declared");
        var device = await SeedPullDeviceAsync(db, tenantId);
        var body = new CountingStream(totalBytes: 10);
        var service = Service(db, _ =>
        {
            var content = new StreamContent(body);
            content.Headers.ContentLength = 3L * 1024 * 1024 * 1024;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        var log = (await service.SyncDeviceAsync(tenantId, device.Id, Context(tenantId), Ct))!;

        log.Status.Should().Be("Failed");
        body.BytesServed.Should().Be(0);
    }

    [Fact]
    public async Task APullWithinTheCap_StillCountsTheRecords()
    {
        var (db, tenantId) = await NewTenantAsync("pull-ok");
        var device = await SeedPullDeviceAsync(db, tenantId);
        var service = Service(db, _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[{\"id\":1},{\"id\":2},{\"id\":3}]", Encoding.UTF8, "application/json"),
        });

        var log = (await service.SyncDeviceAsync(tenantId, device.Id, Context(tenantId), Ct))!;

        log.Status.Should().Be("Completed");
        log.RawEventsReceived.Should().Be(3);
    }

    [Theory]
    [InlineData("3600", 60, true)]
    [InlineData("0", 5, true)]
    [InlineData("-10", 5, true)]
    [InlineData("45", 45, false)]
    [InlineData("abc", 30, true)]
    [InlineData(null, 30, false)]
    public void TimeoutSeconds_IsClampedToFiveToSixty(string? configured, int expected, bool noted)
    {
        var parameters = configured is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["timeout_seconds"] = configured };

        AttendanceService.ResolveDeviceTimeoutSeconds(parameters, out var note).Should().Be(expected);
        (note is not null).Should().Be(noted);
    }

    [Fact]
    public async Task ATimeoutOf3600_IsClamped_AndTheSyncLogSaysSo()
    {
        var (db, tenantId) = await NewTenantAsync("pull-clamp");
        var device = await SeedPullDeviceAsync(db, tenantId, "{\"timeout_seconds\":\"3600\"}");
        var service = Service(db, _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });

        var log = (await service.SyncDeviceAsync(tenantId, device.Id, Context(tenantId), Ct))!;

        log.ErrorMessage.Should().Contain("timeout_seconds 3600 is outside the allowed 5-60 seconds, so 60 seconds was used.");
        log.ErrorMessage.Should().NotContain("Increase timeout_seconds");
    }

    [Fact]
    public async Task ADeviceThatSendsHeadersThenStalls_TimesOutOnTheBodyToo()
    {
        var (db, tenantId) = await NewTenantAsync("pull-stall");
        var device = await SeedPullDeviceAsync(db, tenantId, "{\"timeout_seconds\":\"5\"}");
        var service = Service(db, _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) });

        var started = DateTime.UtcNow;
        var log = (await service.SyncDeviceAsync(tenantId, device.Id, Context(tenantId), Ct))!;

        log.Status.Should().Be("Failed");
        log.ErrorMessage.Should().StartWith("Sync timed out after 5 seconds.");
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task ARefusingDevice_GetsGenericText_WithNoRemoteStatusReasonOrBody()
    {
        var (db, tenantId) = await NewTenantAsync("pull-generic");
        var device = await SeedPullDeviceAsync(db, tenantId);
        var service = Service(db, _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            ReasonPhrase = "redis-6379-internal",
            Content = new StringContent("secret-upstream-body"),
        });

        var sync = (await service.SyncDeviceAsync(tenantId, device.Id, Context(tenantId), Ct))!;
        var test = (await service.TestConnectionAsync(tenantId, device.Id, Context(tenantId), Ct))!;

        foreach (var log in new[] { sync, test })
        {
            log.Status.Should().Be("Failed");
            log.ErrorMessage.Should().Be(AttendanceService.DeviceRefusedMessage);
            foreach (var leak in new[] { "503", "ServiceUnavailable", "redis-6379-internal", "secret-upstream-body" })
                (log.Status + log.ErrorMessage).Should().NotContain(leak);
        }
        db.AttendanceDevices.AsNoTracking().Single(d => d.Id == device.Id).ErrorLog.Should().Be(AttendanceService.DeviceRefusedMessage);
    }

    [Fact]
    public async Task AConnectionError_IsGeneric_AndNeverQuotesTheExceptionText()
    {
        var (db, tenantId) = await NewTenantAsync("pull-exception");
        var device = await SeedPullDeviceAsync(db, tenantId);
        var service = Service(db, _ => throw new HttpRequestException("Connection refused (10.0.0.5:6379)"));

        var sync = (await service.SyncDeviceAsync(tenantId, device.Id, Context(tenantId), Ct))!;
        var test = (await service.TestConnectionAsync(tenantId, device.Id, Context(tenantId), Ct))!;

        foreach (var log in new[] { sync, test })
        {
            log.Status.Should().Be("Failed");
            log.ErrorMessage.Should().StartWith("Could not connect to the device.").And.NotContain("refused").And.NotContain("10.0.0.5");
        }
    }

    [Fact]
    public async Task AConnectionTest_ReadsHeadersOnly()
    {
        var (db, tenantId) = await NewTenantAsync("pull-test-headers");
        var device = await SeedPullDeviceAsync(db, tenantId);
        var endless = new CountingStream(totalBytes: 2L * 1024 * 1024 * 1024);
        var service = Service(db, _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(endless) });

        var log = (await service.TestConnectionAsync(tenantId, device.Id, Context(tenantId), Ct))!;

        log.Status.Should().Be("Success");
        endless.BytesServed.Should().Be(0);
    }

    [Fact]
    public void DeviceHeaders_CannotSetHostOrHopByHopHeaders()
    {
        var device = new AttendanceDevice
        {
            AuthType = "CustomHeader",
            AuthCredentialsJson = "{\"headerName\":\"Host\",\"headerValue\":\"internal.service\"}",
            CustomHeadersJson = "{\"Host\":\"metadata.google.internal\",\"Connection\":\"Upgrade\",\"Upgrade\":\"h2c\",\"Transfer-Encoding\":\"chunked\","
                                + "\"Proxy-Authorization\":\"Basic x\",\"TE\":\"trailers\",\"X-Api-Key\":\"k-1\"}",
        };

        using var request = AttendanceService.BuildDeviceRequest(device, DeviceUrl);

        request.Headers.Host.Should().BeNull();
        foreach (var name in new[] { "Host", "Connection", "Upgrade", "Transfer-Encoding", "Proxy-Authorization", "TE" })
            request.Headers.Contains(name).Should().BeFalse($"{name} is not the device configuration's to set");
        request.Headers.GetValues("X-Api-Key").Should().Equal("k-1");
    }

    [Theory]
    [InlineData("http://127.0.0.1:9/")]
    [InlineData("http://localhost:9/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    public async Task TheGuardedHandler_RefusesToConnectToABlockedAddress_AtConnectTime(string url)
    {
        // The pre-check (ValidateOutboundUrlAsync) is skipped here on purpose: this is what a DNS answer that changed
        // between the check and the connect meets.
        using var http = new HttpClient(SsrfGuard.CreateGuardedClientHandler()) { Timeout = TimeSpan.FromSeconds(10) };

        var act = () => http.GetAsync(url);

        var thrown = (await act.Should().ThrowAsync<HttpRequestException>()).Which;
        Chain(thrown).Should().Contain(e => e is SsrfGuard.BlockedAddressException);
    }

    // ── 2. Reports ─────────────────────────────────────────────────────────────────────────────────

    public static TheoryData<string> ReportActions => new()
    {
        nameof(AttendanceController.ReportDaily), nameof(AttendanceController.ReportMonthly), nameof(AttendanceController.ReportLate),
        nameof(AttendanceController.ReportAbsence), nameof(AttendanceController.ReportMissingPunch), nameof(AttendanceController.ReportPayroll),
    };

    [Theory]
    [MemberData(nameof(ReportActions))]
    public async Task AnEssUser_IsRefusedTheReports_AndAnAttendanceReader_IsAdmitted(string action)
    {
        var (db, tenantId) = await NewTenantAsync("report-gate");
        var gate = typeof(AttendanceController).GetMethod(action)!.GetCustomAttribute<HasPermissionAttribute>();
        gate.Should().NotBeNull($"{action} reads a date range of every employee's attendance");
        gate!.Permissions.Should().Equal("attendance.read");
        var requirement = new PermissionRequirement(gate.Permissions);

        (await Evaluate(requirement, await CallerAsync(db, tenantId, "Employee"))).Should().BeFalse("an ESS user gets 403");
        foreach (var reader in new[] { "Manager", "HR Manager", "Payroll Officer", "Auditor" })
            (await Evaluate(requirement, await CallerAsync(db, tenantId, reader))).Should().BeTrue($"{reader} reads attendance");
    }

    [Theory]
    [InlineData(366, false)]
    [InlineData(365, true)]
    [InlineData(-1, false)]
    [InlineData(5000, false)]
    public async Task AReportRange_IsAtMost366Days(int extraDays, bool allowed)
    {
        var (db, tenantId) = await NewTenantAsync("report-range");
        var controller = ReportController(db, tenantId, allowed: null);
        var from = new DateOnly(2026, 1, 1);
        var to = from.AddDays(extraDays);

        var results = new Microsoft.AspNetCore.Mvc.Infrastructure.IConvertToActionResult[]
        {
            await controller.ReportDaily(from, to, Ct), await controller.ReportLate(from, to, Ct), await controller.ReportAbsence(from, to, Ct),
            await controller.ReportMissingPunch(from, to, Ct), await controller.ReportPayroll(from, to, Ct),
        };

        foreach (var result in results.Select(r => r.Convert()))
            if (allowed) result.Should().BeOfType<OkObjectResult>();
            else ((BadRequestObjectResult)result).Value!.ToString().Should().Contain("at most 366 days");
    }

    [Fact]
    public async Task AScopedManager_GetsOnlyInScopeRows_AndAnOrgWideCaller_GetsTheSameRowsAsBefore()
    {
        var (db, tenantId) = await NewTenantAsync("report-scope");
        var day = new DateOnly(2026, 9, 1);
        foreach (var (id, status, missing) in new[] { (1, "Late", false), (2, "Absent", true), (3, "Late", true), (3, "Absent", false) })
            db.AttendanceDailyRecords.Add(new AttendanceDailyRecord
            {
                TenantId = tenantId, EmployeeId = id, EmployeeName = $"E{id}", WorkDate = status == "Late" ? day : day.AddDays(1),
                Status = status, MissingPunch = missing, LateMinutes = status == "Late" ? 10 : 0,
            });
        // Another tenant's record in range is never anyone's.
        db.AttendanceDailyRecords.Add(new AttendanceDailyRecord { TenantId = Guid.NewGuid(), EmployeeId = 1, EmployeeName = "X", WorkDate = day, Status = "Late" });
        await db.SaveChangesAsync();

        var scoped = ReportController(db, tenantId, allowed: [1, 2]);
        var orgWide = ReportController(db, tenantId, allowed: null);
        var (from, to) = (day, day.AddDays(1));

        Ids(await scoped.ReportDaily(from, to, Ct)).Should().BeEquivalentTo([1, 2]);
        Ids(await scoped.ReportLate(from, to, Ct)).Should().BeEquivalentTo([1]);
        Ids(await scoped.ReportAbsence(from, to, Ct)).Should().BeEquivalentTo([2]);
        Ids(await scoped.ReportMissingPunch(from, to, Ct)).Should().BeEquivalentTo([2]);
        Value(await scoped.ReportPayroll(from, to, Ct)).Select(r => r.EmployeeId).Should().BeEquivalentTo([1, 2]);
        Value(await scoped.ReportMonthly(2026, 9, Ct)).Select(r => r.EmployeeId).Should().BeEquivalentTo([1, 2]);

        Ids(await orgWide.ReportDaily(from, to, Ct)).Should().BeEquivalentTo([1, 2, 3, 3]);
        Ids(await orgWide.ReportLate(from, to, Ct)).Should().BeEquivalentTo([1, 3]);
        Ids(await orgWide.ReportMissingPunch(from, to, Ct)).Should().BeEquivalentTo([2, 3]);
        Value(await orgWide.ReportPayroll(from, to, Ct)).Select(r => r.EmployeeId).Should().BeEquivalentTo([1, 2, 3]);

        Ids(await ReportController(db, tenantId, allowed: []).ReportDaily(from, to, Ct)).Should().BeEmpty("an empty scope sees nothing");
    }

    [Fact]
    public void TheReportQuery_AppliesTheScopeInSql()
    {
        var options = new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql("Host=unused;Database=unused;Username=u;Password=p").Options;
        using var db = new ZayraDbContext(options);
        var service = new AttendanceService(db, new RecordingNotifications(), new NullHttpClientFactory());
        var (tenantId, from, to) = (Guid.NewGuid(), new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        var scopedSql = service.ReportRecords(tenantId, from, to, [1, 2]).ToQueryString();
        var orgSql = service.ReportRecords(tenantId, from, to, null).ToQueryString();

        scopedSql.Should().MatchRegex("(?i)employee_?id\"? = ANY \\(", "the scope is a WHERE predicate, not a filter after materialising");
        orgSql.Should().NotContain("ANY (");
    }

    // ── 3. Correction notification ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ASubmittedCorrection_NotifiesTheEmployeeAndTheirApprover_NotTheTenant()
    {
        var (db, tenantId) = await NewTenantAsync("reg-notify");
        var manager = await AddEmployeeAsync(db, tenantId, "Manager Person", managerId: null);
        var employee = await AddEmployeeAsync(db, tenantId, "Worker Person", managerId: manager.Id);
        var bystander = await AddEmployeeAsync(db, tenantId, "Bystander", managerId: null);
        var employeeUser = await LinkLoginAsync(db, tenantId, employee.Id);
        var managerUser = await LinkLoginAsync(db, tenantId, manager.Id);
        await LinkLoginAsync(db, tenantId, bystander.Id);
        var notifications = new RecordingNotifications();
        var service = new AttendanceService(db, notifications, new NullHttpClientFactory());

        await service.CreateRegularizationAsync(tenantId, Correction(employee.Id), new RequestContext(null, null, employeeUser, tenantId), Ct);

        notifications.Sent.Should().NotContain(n => n.UserId == null, "a null recipient is a broadcast to the whole tenant");
        notifications.Sent.Select(n => n.UserId).Should().BeEquivalentTo(new Guid?[] { employeeUser, managerUser });
        notifications.Sent.Single(n => n.UserId == managerUser).Message.Should().Contain("Worker Person").And.Contain("your decision");
    }

    [Fact]
    public async Task ASubmittedCorrection_WithNobodyResolved_SendsNothing()
    {
        var (db, tenantId) = await NewTenantAsync("reg-nobody");
        var employee = await AddEmployeeAsync(db, tenantId, "Unlinked Person", managerId: null);
        var notifications = new RecordingNotifications();
        var service = new AttendanceService(db, notifications, new NullHttpClientFactory());

        await service.CreateRegularizationAsync(tenantId, Correction(employee.Id), new RequestContext(null, null, Guid.NewGuid(), tenantId), Ct);

        notifications.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task AManagerApprovedCorrection_IsNotBroadcastWhenItMovesToHr()
    {
        var (db, tenantId) = await NewTenantAsync("reg-hr-stage");
        var manager = await AddEmployeeAsync(db, tenantId, "Manager Person", managerId: null);
        var employee = await AddEmployeeAsync(db, tenantId, "Worker Person", managerId: manager.Id);
        var employeeUser = await LinkLoginAsync(db, tenantId, employee.Id);
        var managerUser = await LinkLoginAsync(db, tenantId, manager.Id);
        var notifications = new RecordingNotifications();
        var service = new AttendanceService(db, notifications, new NullHttpClientFactory());
        var reg = await service.CreateRegularizationAsync(tenantId, Correction(employee.Id), new RequestContext(null, null, employeeUser, tenantId), Ct);
        notifications.Sent.Clear();

        await service.ApproveRegularizationAsync(tenantId, reg.Id, new RegularizationDecisionRequest("ok"), new RequestContext(null, null, managerUser, tenantId), Ct);

        notifications.Sent.Should().NotBeEmpty().And.NotContain(n => n.UserId == null);
        notifications.Sent.Select(n => n.UserId).Should().BeEquivalentTo(new Guid?[] { employeeUser });
    }

    [Fact]
    public async Task AManagerApprovedCorrection_ReachesTheHrDecisionQueue_AndNoOneElse()
    {
        var (db, tenantId) = await NewTenantAsync("reg-hr-queue");
        var manager = await AddEmployeeAsync(db, tenantId, "Manager Person", managerId: null);
        var employee = await AddEmployeeAsync(db, tenantId, "Worker Person", managerId: manager.Id);
        var employeeUser = await LinkLoginAsync(db, tenantId, employee.Id);
        var managerUser = await LinkLoginAsync(db, tenantId, manager.Id);
        var hrManager = await RoleHolderAsync(db, tenantId, "HR Manager");
        var hrDirector = await RoleHolderAsync(db, tenantId, "HR Director");
        await RoleHolderAsync(db, tenantId, "HR Officer"); // cannot decide the HR step, so is not told
        var notifications = new RecordingNotifications();
        var service = new AttendanceService(db, notifications, new NullHttpClientFactory());
        var reg = await service.CreateRegularizationAsync(tenantId, Correction(employee.Id), new RequestContext(null, null, employeeUser, tenantId), Ct);
        notifications.Sent.Clear();

        await service.ApproveRegularizationAsync(tenantId, reg.Id, new RegularizationDecisionRequest("ok"), new RequestContext(null, null, managerUser, tenantId), Ct);

        notifications.Sent.Should().NotContain(n => n.UserId == null);
        notifications.Sent.Select(n => n.UserId).Should().BeEquivalentTo(new Guid?[] { employeeUser, hrManager, hrDirector });
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────────

    private static RegularizationRequestDto Correction(int employeeId) => new(employeeId, new DateOnly(2026, 9, 15), "Missed punch",
        new DateTime(2026, 9, 15, 8, 0, 0, DateTimeKind.Utc), new DateTime(2026, 9, 15, 16, 0, 0, DateTimeKind.Utc), "Forgot to clock");

    private static async Task<Employee> AddEmployeeAsync(ZayraDbContext db, Guid tenantId, string name, int? managerId)
    {
        var employee = new Employee
        {
            TenantId = tenantId, EmployeeCode = $"E-{Guid.NewGuid():N}"[..12], FullName = name, Status = EmployeeStatuses.Active,
            ManagerEmployeeId = managerId, JoiningDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        return employee;
    }

    /// <summary>An active, group-scope login holding <paramref name="roleName"/> (so its access covers every company).</summary>
    private static async Task<Guid> RoleHolderAsync(ZayraDbContext db, Guid tenantId, string roleName)
    {
        var role = new Role { TenantId = tenantId, Name = roleName, NormalizedName = roleName.ToUpperInvariant(), IsActive = true };
        var user = new User { TenantId = tenantId, Email = $"{Guid.NewGuid():N}@example.com", FullName = roleName, IsActive = true, IsGroupScope = true };
        db.AddRange(role, user);
        db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<Guid> LinkLoginAsync(ZayraDbContext db, Guid tenantId, int employeeId)
    {
        var user = new User { TenantId = tenantId, Email = $"{Guid.NewGuid():N}@example.com", FullName = $"User {employeeId}" };
        db.Users.Add(user);
        db.EmployeeUserAccounts.Add(new EmployeeUserAccount { TenantId = tenantId, EmployeeId = employeeId, UserId = user.Id, Status = "Active" });
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<AttendanceDevice> SeedPullDeviceAsync(ZayraDbContext db, Guid tenantId, string parameters = "{}")
    {
        var device = new AttendanceDevice
        {
            TenantId = tenantId, DeviceName = "Gate", DeviceType = "Biometric", Vendor = "ZK", SerialNumber = "SN",
            SyncMethod = "Pull API", EndpointUrl = DeviceUrl, IsActive = true, AuthType = "None",
            DeviceParametersJson = parameters,
        };
        db.AttendanceDevices.Add(device);
        await db.SaveChangesAsync();
        return device;
    }

    private static AttendanceService Service(ZayraDbContext db, Func<HttpRequestMessage, HttpResponseMessage> device) =>
        new(db, new RecordingNotifications(), new NullHttpClientFactory()) { DeviceHandlerFactory = () => new ScriptedHandler(device) };

    private static RequestContext Context(Guid tenantId) => new(null, null, Guid.NewGuid(), tenantId);

    private static AttendanceController ReportController(ZayraDbContext db, Guid tenantId, int[]? allowed)
    {
        var caller = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("tenant_id", tenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim("permission", "attendance.read")],
            "Test"));
        return Bind(new AttendanceController(new AttendanceService(db, new RecordingNotifications(), new NullHttpClientFactory()),
            new FixedScope(allowed), new HrmHierarchyService(db, new NullAudit()), db), caller);
    }

    private static T Value<T>(ActionResult<T> result) => (T)((OkObjectResult)result.Result!).Value!;

    private static IEnumerable<int> Ids(ActionResult<IReadOnlyCollection<AttendanceDailyDto>> result) => Value(result).Select(r => r.EmployeeId);

    private static async Task<bool> Evaluate(PermissionRequirement requirement, ClaimsPrincipal user)
    {
        var context = new AuthorizationHandlerContext([requirement], user, null);
        await new PermissionAuthorizationHandler().HandleAsync(context);
        return context.HasSucceeded;
    }

    private static IEnumerable<Exception> Chain(Exception? e)
    {
        for (; e is not null; e = e.InnerException) yield return e;
    }

    private sealed class FixedScope(int[]? allowed) : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = allowed is null ? DataScopeLevel.Organization : DataScopeLevel.Team, AllowedEmployeeIds = allowed });
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }

    /// <summary>A non-seekable body of <c>totalBytes</c> that counts what was actually read from it.</summary>
    private sealed class CountingStream(long totalBytes) : Stream
    {
        public long BytesServed { get; private set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, totalBytes - BytesServed);
            if (n <= 0) return 0;
            buffer.AsSpan(offset, n).Fill((byte)'{');
            BytesServed += n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A body that never sends a byte until the reader gives up.</summary>
    private sealed class StallingStream : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("async only");
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class RecordingNotifications : INotificationService
    {
        public List<(Guid? UserId, string Title, string Message)> Sent { get; } = [];
        public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct)
        {
            Sent.Add((userId, title, message));
            return Task.CompletedTask;
        }
        public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NullHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class NullAudit : IAuditService
    {
        public Task WriteAsync(string action, string entityName, string? entityId, RequestContext context, string? metadata, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
