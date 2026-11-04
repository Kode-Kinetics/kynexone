using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;
using static Zayra.Api.Tests.Security.SeededRoleBundles;

namespace Zayra.Api.Tests;

/// <summary>
/// B1 — Processing for everyone the caller may, or a selection. The 403 on "All employees" came from the controller
/// demanding an UNRESTRICTED scope: a company-scoped HR user has an Organization-level scope whose allowed set is
/// their company's employees, so it is never "unrestricted". "All" is now allowed to Organization level and processes
/// exactly the allowed set; a Team/Department/Own scope is still refused; every named employee must be in scope.
/// </summary>
public sealed class AttendanceBulkProcessingTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateOnly Day = new(2026, 6, 10); // a Wednesday

    // ── sync: Process ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task All_UnderAnOrganizationScopeLimitedToACompany_Is200_AndProcessesExactlyThatSet()
    {
        var f = await SeedAsync(4);
        var controller = f.Controller("HR Manager", DataScopeLevel.Organization, f.Ids[0], f.Ids[1]);

        var result = await controller.Process(new ProcessAttendanceRequest(Day, Day, null), Ct);

        result.Result.Should().BeOfType<OkObjectResult>();
        (await ProcessedEmployeesAsync(f)).Should().BeEquivalentTo(new[] { f.Ids[0], f.Ids[1] });
    }

    [Fact]
    public async Task All_UnderAnOrganizationScopeWithNoEmployees_ProcessesNobody_NeverEverybody()
    {
        var f = await SeedAsync(3);
        var controller = f.Controller("HR Manager", DataScopeLevel.Organization /* allowed = empty */);

        var result = await controller.Process(new ProcessAttendanceRequest(Day, Day, null), Ct);

        Assert.Equal(0, Assert.IsType<OkObjectResult>(result.Result).Value);
        (await ProcessedEmployeesAsync(f)).Should().BeEmpty();
    }

    [Fact]
    public async Task All_UnderAnUnrestrictedScope_ProcessesEveryActiveEmployee_AndSkipsTheInactive()
    {
        var f = await SeedAsync(3, withInactive: true);
        var controller = f.Controller("HR Manager", DataScopeLevel.Organization, unrestricted: true);

        var result = await controller.Process(new ProcessAttendanceRequest(Day, Day, null), Ct);

        result.Result.Should().BeOfType<OkObjectResult>();
        (await ProcessedEmployeesAsync(f)).Should().BeEquivalentTo(f.Ids);
    }

    [Theory]
    [InlineData(DataScopeLevel.Team)]
    [InlineData(DataScopeLevel.Department)]
    [InlineData(DataScopeLevel.DirectReports)]
    [InlineData(DataScopeLevel.Own)]
    public async Task All_UnderATeamDepartmentOrOwnScope_IsForbidden_AndWritesNothing(DataScopeLevel level)
    {
        var f = await SeedAsync(3);
        var controller = f.Controller("HR Manager", level, f.Ids[0], f.Ids[1]);

        var sync = await controller.Process(new ProcessAttendanceRequest(Day, Day, null), Ct);
        var background = await controller.ProcessInBackground(new ProcessAttendanceRequest(Day, Day, null), f.Store(), null, Ct);

        sync.Result.Should().BeOfType<ForbidResult>();
        background.Result.Should().BeOfType<ForbidResult>();
        (await ProcessedEmployeesAsync(f)).Should().BeEmpty();
        (await f.Db.BackgroundJobs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ASelectionWithOneEmployeeOutOfScope_IsForbidden_AndNothingIsWritten()
    {
        var f = await SeedAsync(3);
        var controller = f.Controller("HR Manager", DataScopeLevel.Team, f.Ids[0], f.Ids[1]);

        var sync = await controller.Process(new ProcessAttendanceRequest(Day, Day, null, new[] { f.Ids[0], f.Ids[2] }), Ct);
        var background = await controller.ProcessInBackground(
            new ProcessAttendanceRequest(Day, Day, null, new[] { f.Ids[0], f.Ids[2] }), f.Store(), null, Ct);

        sync.Result.Should().BeOfType<ForbidResult>();
        background.Result.Should().BeOfType<ForbidResult>();
        (await ProcessedEmployeesAsync(f)).Should().BeEmpty();
        (await f.Db.BackgroundJobs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ASelectionInScope_ProcessesOnlyThoseEmployees()
    {
        var f = await SeedAsync(4);
        var controller = f.Controller("HR Manager", DataScopeLevel.Organization, unrestricted: true);

        var result = await controller.Process(new ProcessAttendanceRequest(Day, Day, null, new[] { f.Ids[1], f.Ids[3] }), Ct);

        result.Result.Should().BeOfType<OkObjectResult>();
        (await ProcessedEmployeesAsync(f)).Should().BeEquivalentTo(new[] { f.Ids[1], f.Ids[3] });
    }

    [Fact]
    public async Task AnEmptySelection_IsA400_NotEveryone()
    {
        var f = await SeedAsync(3);
        var controller = f.Controller("HR Manager", DataScopeLevel.Organization, unrestricted: true);

        var sync = await controller.Process(new ProcessAttendanceRequest(Day, Day, null, Array.Empty<int>()), Ct);
        var background = await controller.ProcessInBackground(
            new ProcessAttendanceRequest(Day, Day, null, Array.Empty<int>()), f.Store(), null, Ct);

        sync.Result.Should().BeOfType<BadRequestObjectResult>();
        background.Result.Should().BeOfType<BadRequestObjectResult>();
        (await ProcessedEmployeesAsync(f)).Should().BeEmpty();
        (await f.Db.BackgroundJobs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ASelectionOverTheCap_IsA400WithAnExplanation()
    {
        var f = await SeedAsync(1);
        var controller = f.Controller("HR Manager", DataScopeLevel.Organization, unrestricted: true);
        var tooMany = Enumerable.Range(1, 2001).ToArray();

        var sync = await controller.Process(new ProcessAttendanceRequest(Day, Day, null, tooMany), Ct);
        var background = await controller.ProcessInBackground(new ProcessAttendanceRequest(Day, Day, null, tooMany), f.Store(), null, Ct);

        foreach (var refused in new[] { sync.Result!, background.Result! })
            JsonSerializer.Serialize(Assert.IsType<BadRequestObjectResult>(refused).Value).Should().Contain("2,000");
        (await f.Db.BackgroundJobs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ASingleEmployeeId_StillWorks_ForTheSeeds()
    {
        var f = await SeedAsync(3);
        var controller = f.Controller("HR Manager", DataScopeLevel.Organization, unrestricted: true);

        var result = await controller.Process(new ProcessAttendanceRequest(Day, Day, f.Ids[2]), Ct);

        result.Result.Should().BeOfType<OkObjectResult>();
        (await ProcessedEmployeesAsync(f)).Should().BeEquivalentTo(new[] { f.Ids[2] });
    }

    // ── background: ProcessInBackground ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Async_ASelection_QueuesAJobCarryingExactlyThoseIds()
    {
        var f = await SeedAsync(4);
        var controller = f.Controller("HR Manager", DataScopeLevel.Organization, unrestricted: true);

        var response = await EnqueueAsync(controller, f, new ProcessAttendanceRequest(Day, Day, null, new[] { f.Ids[3], f.Ids[1] }), null);

        response.Deduplicated.Should().BeFalse();
        response.StatusUrl.Should().Be($"/api/jobs/{response.JobId}");
        PayloadOf(f, response).EmployeeIds.Should().BeEquivalentTo(new[] { f.Ids[1], f.Ids[3] });
    }

    [Fact]
    public async Task Async_AllUnderACompanyScopedOrganizationScope_QueuesTheCallersSetAsTheCompleteTargetList()
    {
        var f = await SeedAsync(4);
        var controller = f.Controller("HR Manager", DataScopeLevel.Organization, f.Ids[0], f.Ids[2]);

        var response = await EnqueueAsync(controller, f, new ProcessAttendanceRequest(Day, Day, null), null);

        PayloadOf(f, response).EmployeeIds.Should().BeEquivalentTo(new[] { f.Ids[0], f.Ids[2] });
    }

    [Fact]
    public async Task Async_AllUnderAnEmptyOrganizationSet_QueuesAnEmptyList_NotNull()
    {
        var f = await SeedAsync(2);
        var controller = f.Controller("HR Manager", DataScopeLevel.Organization /* allowed = empty */);

        var response = await EnqueueAsync(controller, f, new ProcessAttendanceRequest(Day, Day, null), null);

        PayloadOf(f, response).EmployeeIds.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public async Task Async_AllUnderAnUnrestrictedScope_QueuesNoTargetList()
    {
        var f = await SeedAsync(2);
        var controller = f.Controller("HR Manager", DataScopeLevel.Organization, unrestricted: true);

        var response = await EnqueueAsync(controller, f, new ProcessAttendanceRequest(Day, Day, null), null);

        PayloadOf(f, response).EmployeeIds.Should().BeNull();
    }

    [Theory]
    [InlineData(false, null)]          // an organisation-wide caller, no client key
    [InlineData(false, "client-key")]  // an organisation-wide caller with a client Idempotency-Key
    [InlineData(true, null)]           // a company-limited caller (their set is a list)
    public async Task TheJobKey_IsStableForTheSameRequest_AndDiffersByRangeAndByTargetSet(bool limited, string? clientKey)
    {
        var f = await SeedAsync(4);
        var controller = limited
            ? f.Controller("HR Manager", DataScopeLevel.Organization, f.Ids[0], f.Ids[1], f.Ids[2])
            : f.Controller("HR Manager", DataScopeLevel.Organization, unrestricted: true);
        var baseRequest = new ProcessAttendanceRequest(Day, Day.AddDays(1), null, new[] { f.Ids[0], f.Ids[1] });

        var first = await EnqueueAsync(controller, f, baseRequest, clientKey);
        var same = await EnqueueAsync(controller, f, baseRequest with { EmployeeIds = new[] { f.Ids[1], f.Ids[0] } }, clientKey); // same set, any order
        var otherRange = await EnqueueAsync(controller, f, baseRequest with { ToDate = Day.AddDays(2) }, clientKey);
        var otherSelection = await EnqueueAsync(controller, f, baseRequest with { EmployeeIds = new[] { f.Ids[0], f.Ids[2] } }, clientKey);
        var singleEmployee = await EnqueueAsync(controller, f, baseRequest with { EmployeeIds = null, EmployeeId = f.Ids[0] }, clientKey);

        same.JobId.Should().Be(first.JobId);
        same.Deduplicated.Should().BeTrue();
        new[] { otherRange.JobId, otherSelection.JobId, singleEmployee.JobId }.Should().OnlyHaveUniqueItems()
            .And.NotContain(first.JobId);
        (await f.Db.BackgroundJobs.CountAsync()).Should().Be(4);
    }

    [Fact]
    public async Task TheJobKey_OfACompanyLimitedCaller_NamesThemselves_SoOneCallerNeverReceivesAnotherCallersJob()
    {
        var f = await SeedAsync(3);
        var request = new ProcessAttendanceRequest(Day, Day, null, new[] { f.Ids[0] });
        var one = f.Controller("HR Manager", DataScopeLevel.Organization, f.Ids[0], f.Ids[1], f.Ids[2]);
        var other = f.Controller("HR Manager", DataScopeLevel.Organization, f.Ids[0], f.Ids[1], f.Ids[2]);

        var a = await EnqueueAsync(one, f, request, null);
        var b = await EnqueueAsync(other, f, request, null);

        b.JobId.Should().NotBe(a.JobId);
    }

    [Fact]
    public async Task AVeryLongClientKey_IsStillAcceptedAndKeyedByTheRequest()
    {
        var f = await SeedAsync(2);
        var controller = f.Controller("HR Manager", DataScopeLevel.Organization, unrestricted: true);
        var key = new string('k', 200);

        var a = await EnqueueAsync(controller, f, new ProcessAttendanceRequest(Day, Day, null, new[] { f.Ids[0] }), key);
        var b = await EnqueueAsync(controller, f, new ProcessAttendanceRequest(Day, Day, null, new[] { f.Ids[1] }), key);

        b.JobId.Should().NotBe(a.JobId);
    }

    // ── the job handler's employee query ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheJobHandler_HonoursTheSelection_TheCompanyScope_AndTheActiveFilter()
    {
        var f = await SeedAsync(4, withInactive: true);
        var inactive = f.Db.Employees.Single(e => e.Status != EmployeeStatuses.Active).Id;
        var payload = new AttendanceProcessingJobPayload(Day, Day, null, GroupScope: true, CompanyIds: Array.Empty<Guid>(),
            RequestedByUserId: null, IpAddress: null, UserAgent: null);

        Ids(f, payload).Should().BeEquivalentTo(f.Ids, "no selection: every ACTIVE employee, as the synchronous run does");
        Ids(f, payload with { EmployeeIds = new[] { f.Ids[0], f.Ids[1], inactive } })
            .Should().BeEquivalentTo(new[] { f.Ids[0], f.Ids[1] }, "a selection is honoured and an inactive employee is never processed");
        Ids(f, payload with { EmployeeIds = Array.Empty<int>() }).Should().BeEmpty("an empty list is the complete set: nobody");
        Ids(f, payload with { EmployeeId = f.Ids[2], EmployeeIds = new[] { f.Ids[1] } }).Should().BeEmpty("both filters apply");
        Ids(f, payload with { GroupScope = false, CompanyIds = new[] { f.CompanyA }, EmployeeIds = new[] { f.Ids[0], f.Ids[3] } })
            .Should().BeEquivalentTo(new[] { f.Ids[0] }, "the caller's company scope still applies to a selection");
    }

    [Fact]
    public void AJobQueuedBeforeSelectionsExisted_StillDeserialises_WithNoSelection()
    {
        const string legacy = """{"FromDate":"2026-06-10","ToDate":"2026-06-10","EmployeeId":5,"GroupScope":true,"CompanyIds":[],"RequestedByUserId":null,"IpAddress":null,"UserAgent":null}""";

        var payload = JsonSerializer.Deserialize<AttendanceProcessingJobPayload>(legacy)!;

        payload.EmployeeIds.Should().BeNull();
        payload.EmployeeId.Should().Be(5);
    }

    private static List<int> Ids(Fixture f, AttendanceProcessingJobPayload payload) =>
        AttendanceProcessingJobHandler.EmployeesFor(f.Db, f.TenantId, payload).Select(e => e.Id).ToList();

    // ── fixture ──────────────────────────────────────────────────────────────────────────────────────────────

    private static async Task<List<int>> ProcessedEmployeesAsync(Fixture f) =>
        await f.Db.AttendanceDailyRecords.AsNoTracking().Where(r => r.WorkDate == Day).Select(r => r.EmployeeId).Distinct().ToListAsync();

    private static async Task<BackgroundJobEnqueueResponse> EnqueueAsync(AttendanceController controller, Fixture f, ProcessAttendanceRequest request, string? key) =>
        (BackgroundJobEnqueueResponse)((AcceptedResult)(await controller.ProcessInBackground(request, f.Store(), key, Ct)).Result!).Value!;

    private static AttendanceProcessingJobPayload PayloadOf(Fixture f, BackgroundJobEnqueueResponse response) =>
        JsonSerializer.Deserialize<AttendanceProcessingJobPayload>(f.Db.BackgroundJobs.Single(j => j.Id == response.JobId).PayloadJson)!;

    private static async Task<Fixture> SeedAsync(int employees, bool withInactive = false)
    {
        var (db, tenantId) = await NewTenantAsync("att-bulk-b1");
        var companyA = Guid.NewGuid();
        var list = Enumerable.Range(1, employees)
            .Select(i => new Employee { TenantId = tenantId, CompanyId = companyA, EmployeeCode = $"E-{i}", FullName = $"Employee {i}", Status = EmployeeStatuses.Active })
            .ToList();
        // The last active employee belongs to another company, for the company-scope assertion.
        if (list.Count >= 4) list[3].CompanyId = Guid.NewGuid();
        if (withInactive) list.Add(new Employee { TenantId = tenantId, CompanyId = companyA, EmployeeCode = "E-GONE", FullName = "Left", Status = "Terminated" });
        db.Employees.AddRange(list);
        await db.SaveChangesAsync();
        foreach (var e in list)
            db.AttendanceRawEvents.AddRange(
                new AttendanceRawEvent { TenantId = tenantId, EmployeeId = e.Id, EmployeeCode = e.EmployeeCode, PunchDirection = "In", PunchTimestampUtc = Day.ToDateTime(new TimeOnly(5, 0), DateTimeKind.Utc) },
                new AttendanceRawEvent { TenantId = tenantId, EmployeeId = e.Id, EmployeeCode = e.EmployeeCode, PunchDirection = "Out", PunchTimestampUtc = Day.ToDateTime(new TimeOnly(14, 0), DateTimeKind.Utc) });
        await db.SaveChangesAsync();
        return new Fixture(db, tenantId, companyA, list.Where(e => e.Status == EmployeeStatuses.Active).Select(e => e.Id).ToList());
    }

    private sealed record Fixture(ZayraDbContext Db, Guid TenantId, Guid CompanyA, List<int> Ids)
    {
        public BackgroundJobStore Store() =>
            new(Db, new BackgroundJobTypeRegistry(new[] { AttendanceProcessingJobHandler.Descriptor }));

        public AttendanceController Controller(string role, DataScopeLevel level, params int[] allowed) =>
            ControllerCore(role, level, allowed, unrestricted: false);

        public AttendanceController Controller(string role, DataScopeLevel level, bool unrestricted) =>
            ControllerCore(role, level, Array.Empty<int>(), unrestricted);

        private AttendanceController ControllerCore(string role, DataScopeLevel level, int[] allowed, bool unrestricted)
        {
            var caller = CallerAsync(Db, TenantId, role).GetAwaiter().GetResult();
            return Bind(new AttendanceController(
                new AttendanceService(Db, new NullNotifications(), new NullHttpClientFactory()),
                new FixedScope(level, unrestricted ? null : allowed),
                new HrmHierarchyService(Db, new NullAudit()), Db), caller);
        }
    }

    private sealed class FixedScope(DataScopeLevel level, int[]? allowed) : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = level, CallerEmployeeId = allowed?.FirstOrDefault(), AllowedEmployeeIds = allowed });
    }

    private sealed class NullNotifications : INotificationService
    {
        public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
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
