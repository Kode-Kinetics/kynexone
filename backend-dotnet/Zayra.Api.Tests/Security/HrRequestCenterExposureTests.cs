using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Models;
using static Zayra.Api.Tests.Security.SeededRoleBundles;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// The HR Request Center returned raw HRRequest rows. The dashboard's role gate resolved to employees.read, so
/// every staff role read the latest requests org-wide, and the bare-[Authorize] list gave any role with an
/// org-wide data scope (Recruiter, Finance, Payroll, Compliance, Auditor...) every request in the tenant,
/// including the free-text description and the Jawazat travel data.
/// </summary>
public sealed class HrRequestCenterExposureTests
{
    private const string Secret = "medical reason for leave";
    private const string Travel = "{\"destination\":\"Cairo\"}";
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Theory]
    [InlineData("Recruiter")]
    [InlineData("Finance")]
    [InlineData("Auditor")]
    [InlineData("Payroll Officer")]
    [InlineData("HR Assistant")]
    [InlineData("Compliance Officer")]
    [InlineData("Manager")]
    public async Task Dashboard_IsClosedToNonHrRoles(string role)
    {
        var (db, tenantId) = await NewTenantAsync("hrrc-dash");
        var status = await ProductionAuthorizationGate.StatusAsync<HRRequestCenterController>(
            await CallerAsync(db, tenantId, role), nameof(HRRequestCenterController.Dashboard), () => Task.FromResult<IActionResult>(new OkResult()));
        status.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Theory]
    [InlineData("HR Officer")]
    [InlineData("HR Manager")]
    [InlineData("Admin")]
    public async Task Dashboard_StaysOpenToHr_WithFullDetail(string role)
    {
        var (db, tenantId, requesterId, _) = await SeedAsync();
        var caller = await CallerAsync(db, tenantId, role);
        (await ProductionAuthorizationGate.StatusAsync<HRRequestCenterController>(
            caller, nameof(HRRequestCenterController.Dashboard), () => Task.FromResult<IActionResult>(new OkResult())))
            .Should().Be(StatusCodes.Status200OK);

        var json = Json(await Controller(db, caller, Org()).Dashboard(Ct));
        json.Should().Contain(Secret).And.Contain("Cairo");
    }

    [Theory]
    [InlineData("Recruiter")]
    [InlineData("Finance")]
    [InlineData("Auditor")]
    [InlineData("Payroll Manager")]
    public async Task OrgScopedNonHrRoles_ListOnlyTheirOwnRequests(string role)
    {
        var (db, tenantId, requesterId, callerEmployeeId) = await SeedAsync();
        var caller = WithEmployee(await CallerAsync(db, tenantId, role), callerEmployeeId);

        var page = Page(await Controller(db, caller, Org()).List(null, null, null, ct: Ct));

        page.Items.Should().ContainSingle().Which.EmployeeId.Should().Be(callerEmployeeId);
        page.Items.Single().Description.Should().Be("my own request text", "a requester always sees their own request in full");
        Json(page).Should().NotContain(Secret).And.NotContain("Cairo");

        var requesterTicket = db.HRRequests.Single(r => r.EmployeeId == requesterId);
        (await Controller(db, caller, Org()).Get(requesterTicket.Id, Ct)).Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task ALineManager_SeesTheTeamQueue_ButNotTheFreeTextOrTravelData()
    {
        var (db, tenantId, requesterId, managerEmployeeId) = await SeedAsync();
        var caller = WithEmployee(await CallerAsync(db, tenantId, "Manager"), managerEmployeeId);
        var team = Team(managerEmployeeId, requesterId);

        var page = Page(await Controller(db, caller, team).List(null, null, null, ct: Ct));

        var report = page.Items.Single(i => i.EmployeeId == requesterId);
        report.Subject.Should().Be("Leave question");
        report.DetailsRedacted.Should().BeTrue();
        report.IsJawazatRequest.Should().BeTrue();
        Json(page).Should().NotContain(Secret).And.NotContain("Cairo");

        var ticket = db.HRRequests.Single(r => r.EmployeeId == requesterId);
        var detail = Json(await Controller(db, caller, team).Get(ticket.Id, Ct));
        detail.Should().NotContain(Secret).And.NotContain("Cairo").And.NotContain("hr reply");
    }

    [Fact]
    public async Task TheRequester_SeesTheirOwnRequestInFull()
    {
        var (db, tenantId, requesterId, _) = await SeedAsync();
        var caller = WithEmployee(await CallerAsync(db, tenantId, "Employee"), requesterId);

        var page = Page(await Controller(db, caller, Team(requesterId)).List(null, null, null, ct: Ct));

        page.Items.Should().ContainSingle().Which.Description.Should().Be(Secret);
        page.Items.Single().JawazatDataJson.Should().Be(Travel);
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────────

    private static async Task<(ZayraDbContext Db, Guid TenantId, int RequesterId, int CallerEmployeeId)> SeedAsync()
    {
        var (db, tenantId) = await NewTenantAsync("hrrc");
        var requester = new Employee { TenantId = tenantId, EmployeeCode = "E-1", FullName = "Requester", Status = "Active" };
        var other = new Employee { TenantId = tenantId, EmployeeCode = "E-2", FullName = "Caller", Status = "Active" };
        db.Employees.AddRange(requester, other);
        await db.SaveChangesAsync();
        var requestRow = new HRRequest
        {
            TenantId = tenantId, EmployeeId = requester.Id, Subject = "Leave question", Description = Secret,
            JawazatDataJson = Travel, CompanyId = Guid.NewGuid(), Status = "Open", DueAtUtc = DateTime.UtcNow.AddDays(2),
        };
        db.HRRequests.AddRange(requestRow,
            new HRRequest { TenantId = tenantId, EmployeeId = other.Id, Subject = "Payslip copy", Description = "my own request text", Status = "Open", DueAtUtc = DateTime.UtcNow.AddDays(2) });
        db.HRRequestComments.Add(new HRRequestComment { TenantId = tenantId, HRRequestId = requestRow.Id, EmployeeId = requester.Id, Comment = "hr reply" });
        await db.SaveChangesAsync();
        return (db, tenantId, requester.Id, other.Id);
    }

    private static ClaimsPrincipal WithEmployee(ClaimsPrincipal caller, int employeeId)
    {
        var identity = (ClaimsIdentity)caller.Identity!;
        identity.AddClaim(new Claim("employee_id", employeeId.ToString()));
        return caller;
    }

    private static HRRequestCenterController Controller(ZayraDbContext db, ClaimsPrincipal caller, IDataScopeService scope) =>
        Bind(new HRRequestCenterController(db, scope), caller);

    private static PagedResult<HrRequestDto> Page(IActionResult result) =>
        (PagedResult<HrRequestDto>)((OkObjectResult)result).Value!;

    private static string Json(object result) =>
        JsonSerializer.Serialize(result is OkObjectResult ok ? ok.Value : result);

    private static IDataScopeService Org() => new FixedScope(null, null);
    private static IDataScopeService Team(int self, params int[] reports) => new FixedScope(self, new[] { self }.Concat(reports).ToArray());

    private sealed class FixedScope(int? self, int[]? allowed) : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(allowed is null
                ? new DataScope { Level = DataScopeLevel.Organization }
                : new DataScope { Level = DataScopeLevel.Team, CallerEmployeeId = self, AllowedEmployeeIds = allowed });
    }
}
