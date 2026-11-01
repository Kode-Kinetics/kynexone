using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers.Employees;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Employees.Onboarding;
using Zayra.Api.Models;
using Zayra.Api.Tests.Onboarding;

namespace Zayra.Api.Tests.Security;

public sealed partial class EmployeeAccessTests
{
    private async Task<int> HireWithPayAsync(World w, Guid creator, int seed, Guid? companyId = null)
    {
        await using var db = _fx.CreateDb();
        var request = Hire($"Dead End Person {seed}", $"deadend.{seed}.{Guid.NewGuid():N}@{w.Domain}", companyId ?? w.CompanyId) with {
            Nationality = "Indian",
            ComplianceRecords = [new EmployeeComplianceRecordRequest("SA", "iqama_number", "Iqama", TestIds.ValidIqama(seed),
                null, DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)), true, true)],
            SalaryBreakdown = new EmployeeSalaryBreakdownRequest(9100m + seed, 0, 0, 0, 0, 0, 0, null, null, "SAR")
        };
        return (await Employees(db).CreateAsync(w.TenantId, request, Ctx(creator, w.TenantId), default)).Id;
    }

    private async Task SyncAsync(World w, Guid? companyId = null, Guid? actor = null)
    {
        await using var db = _fx.CreateDb();
        await PayApprovalDeadEnds.SyncTaskAsync(db, w.TenantId, companyId ?? w.CompanyId, actor ?? w.AdminId, "test",
            Ctx(actor ?? w.AdminId, w.TenantId), null, default);
    }

    private async Task<List<Notification>> DeadEndNoticesAsync(World w, Guid? companyId = null)
    {
        await using var db = _fx.CreateDb();
        var id = (companyId ?? w.CompanyId).ToString();
        return await db.Notifications.IgnoreQueryFilters().AsNoTracking()
            .Where(n => n.TenantId == w.TenantId && n.EntityName == PayApprovalDeadEnds.EntityName && n.EntityId == id).ToListAsync();
    }

    private async Task<List<string>> CompanyAuditAsync(World w, Guid? companyId = null) =>
        await AuditActionsAsync(w, "Company", (companyId ?? w.CompanyId).ToString());

    [Fact]
    public async Task DeadEnd_Opens_WhenCreatorIsTheOnlyReviewer_AndStaysOneNotificationAfterTwoSyncs()
    {
        var w = await SeedAsync();
        var id = await HireWithPayAsync(w, w.AdminId, 1);
        await using (var db = _fx.CreateDb())
        {
            var result = await PayApprovalDeadEnds.EvaluateCompanyAsync(db, w.TenantId, w.CompanyId, default);
            result.StrandedEmployeeIds.Should().Equal(id);
            result.StrandedCount.Should().Be(1);
            result.HasMore.Should().BeFalse();
        }
        await SyncAsync(w);
        await SyncAsync(w);
        var notices = await DeadEndNoticesAsync(w);
        notices.Should().ContainSingle(n => n.UserId == w.AdminId && n.Status == "Unread");
        notices.Should().OnlyContain(n => n.Status != PayApprovalDeadEnds.ResolvedStatus);
        (await CompanyAuditAsync(w)).Count(a => a == PayApprovalDeadEnds.OpenedAction).Should().Be(1);
    }

    [Fact]
    public async Task DeadEnd_AutoResolves_WhenAnIndependentReviewerIsAdded()
    {
        var w = await SeedAsync();
        await HireWithPayAsync(w, w.AdminId, 2);
        await SyncAsync(w);
        (await DeadEndNoticesAsync(w)).Should().NotBeEmpty();
        await AddUserAsync(w, $"fin.{Guid.NewGuid():N}@{w.Domain}", ["Payroll Approver"], true, staff: true);
        await SyncAsync(w);
        (await DeadEndNoticesAsync(w)).Should().OnlyContain(n => n.Status == PayApprovalDeadEnds.ResolvedStatus);
        (await CompanyAuditAsync(w)).Should().Contain(PayApprovalDeadEnds.ResolvedAction);
        await SyncAsync(w);
        (await CompanyAuditAsync(w)).Count(a => a == PayApprovalDeadEnds.ResolvedAction).Should().Be(1);
    }

    [Fact]
    public async Task DeadEnd_NoTask_WhenANonBarredReviewerExists()
    {
        var w = await SeedAsync();
        await AddUserAsync(w, $"fin.{Guid.NewGuid():N}@{w.Domain}", ["Payroll Approver"], true, staff: true);
        await HireWithPayAsync(w, w.AdminId, 3);
        await SyncAsync(w);
        (await DeadEndNoticesAsync(w)).Should().BeEmpty();
        (await CompanyAuditAsync(w)).Should().NotContain(PayApprovalDeadEnds.OpenedAction);
    }

    [Fact]
    public async Task DeadEnd_UnrelatedEditByAReviewer_DoesNotBarThemOrDuplicateTheTask()
    {
        var w = await SeedAsync();
        var reviewer = await AddUserAsync(w, $"fin.{Guid.NewGuid():N}@{w.Domain}", ["Payroll Approver"], true, staff: true);
        var id = await HireWithPayAsync(w, w.AdminId, 4);
        await UpdateEmployeeAsync(id, e => e.UpdatedBy = reviewer);
        await SyncAsync(w);
        await SyncAsync(w);
        (await DeadEndNoticesAsync(w)).Should().BeEmpty();
        await using var db = _fx.CreateDb();
        (await PayApprovalDeadEnds.EvaluateCompanyAsync(db, w.TenantId, w.CompanyId, default)).StrandedCount.Should().Be(0);
    }

    [Fact]
    public async Task DeadEnd_Message_CarriesCountsAndLinkOnly_NoPayFigures()
    {
        var w = await SeedAsync();
        await HireWithPayAsync(w, w.AdminId, 5);
        await SyncAsync(w);
        var notice = (await DeadEndNoticesAsync(w)).Single(n => n.UserId == w.AdminId);
        notice.Message.Should().Contain($"/setup?tab=users&companyId={w.CompanyId}").And.Contain("1 pending");
        (notice.Title + notice.Message).Should().NotContain("9105").And.NotContain("SAR");
        await using var db = _fx.CreateDb();
        var meta = await db.AuditLogs.IgnoreQueryFilters().Where(a => a.TenantId == w.TenantId && a.Action == PayApprovalDeadEnds.OpenedAction)
            .Select(a => a.Metadata).SingleAsync();
        meta.Should().NotContain("9105").And.NotContain("Dead End Person");
    }

    [Fact]
    public async Task DeadEnd_IsScopedToTheCompany()
    {
        var w = await SeedAsync();
        await HireWithPayAsync(w, w.AdminId, 6);
        await SyncAsync(w, w.OtherCompanyId);
        (await DeadEndNoticesAsync(w, w.OtherCompanyId)).Should().BeEmpty();
        await using (var db = _fx.CreateDb())
            (await PayApprovalDeadEnds.EvaluateCompanyAsync(db, w.TenantId, w.OtherCompanyId, default)).StrandedCount.Should().Be(0);
        await SyncAsync(w);
        (await DeadEndNoticesAsync(w)).Should().NotBeEmpty();
        (await DeadEndNoticesAsync(w, w.OtherCompanyId)).Should().BeEmpty();
    }

    [Fact]
    public async Task DeadEnd_Sync_NeverThrowsAndNeverApproves()
    {
        var w = await SeedAsync();
        var id = await HireWithPayAsync(w, w.AdminId, 7);
        await using var db = _fx.CreateDb();
        await PayApprovalDeadEnds.SyncTaskAsync(db, w.TenantId, Guid.NewGuid(), w.AdminId, "test", null, null, default);
        await SyncAsync(w);
        await using var verify = _fx.CreateDb();
        (await CompensationApprovalGate.NeedsApprovalAsync(verify, w.TenantId,
            await verify.Employees.IgnoreQueryFilters().SingleAsync(e => e.Id == id), default)).Should().BeTrue();
    }

    [Fact]
    public async Task DeadEnd_Decide_ResolvesTheNoticeThroughTheController()
    {
        var w = await SeedAsync();
        var id = await HireWithPayAsync(w, w.AdminId, 8);
        await SyncAsync(w);
        var reviewer = await AddUserAsync(w, $"fin.{Guid.NewGuid():N}@{w.Domain}", ["Payroll Approver"], true, staff: true);
        await using (var db = _fx.CreateDb())
        {
            var claims = new List<Claim> { new("tenant_id", w.TenantId.ToString()), new(ClaimTypes.NameIdentifier, reviewer.ToString()), new("permission", "payroll.approve") };
            claims.AddRange(EntityScopeClaims.Build(EntityScopeDescriptor.Group, Array.Empty<EntityAccessGrant>()));
            var c = new PayApprovalsController(db, new AuditService(db)) { ControllerContext = new() { HttpContext = new DefaultHttpContext {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) } } };
            var detail = (OkObjectResult)await c.Detail(id, default);
            var fp = System.Text.Json.JsonSerializer.SerializeToElement(detail.Value).GetProperty("fingerprint").GetString()!;
            (await c.Decide(id, new(fp, "approve", "Checked fixture evidence"), default)).Should().BeOfType<OkObjectResult>();
        }
        (await DeadEndNoticesAsync(w)).Should().OnlyContain(n => n.Status == PayApprovalDeadEnds.ResolvedStatus);
    }
}
