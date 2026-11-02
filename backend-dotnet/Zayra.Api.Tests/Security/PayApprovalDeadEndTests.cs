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
    /// <summary>
    /// A salaried hire is refused up front when nobody else could approve it (the second-approver guard), so a dead end can only
    /// arise afterwards. These tests build that state the way it really happens: a second approver exists while the hire is
    /// created, then loses the right (deactivated straight in the database, so no hook runs and the tests decide when to sync).
    /// </summary>
    private async Task<T> WithTransientApproverAsync<T>(World w, Func<Task<T>> create)
    {
        var approver = await AddUserAsync(w, $"transient.{Guid.NewGuid():N}@{w.Domain}", ["Payroll Approver"], true, staff: true, name: "Transient Approver");
        try { return await create(); }
        finally
        {
            await using var db = _fx.CreateDb();
            var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == approver);
            user.IsActive = false;
            await db.SaveChangesAsync();
        }
    }

    private Task<int> HireWithPayAsync(World w, Guid creator, int seed, Guid? companyId = null) =>
        WithTransientApproverAsync(w, () => HireWithPayCoreAsync(w, creator, seed, companyId));

    private async Task<int> HireWithPayCoreAsync(World w, Guid creator, int seed, Guid? companyId)
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
            var c = PayController(db, w, reviewer);
            var detail = (OkObjectResult)await c.Detail(id, default);
            var fp = System.Text.Json.JsonSerializer.SerializeToElement(detail.Value).GetProperty("fingerprint").GetString()!;
            (await c.Decide(id, new(fp, "approve", "Checked fixture evidence"), default)).Should().BeOfType<OkObjectResult>();
        }
        (await DeadEndNoticesAsync(w)).Should().OnlyContain(n => n.Status == PayApprovalDeadEnds.ResolvedStatus);
    }

    [Fact]
    public async Task DeadEnd_ZeroResultFromAPartialWindow_DoesNotResolveTheNotice_AndContinuesPastTheWindow()
    {
        // Package A can be approved by the one reviewer; package B is that reviewer's own pay, so they cannot.
        // With a window of one package the first read sees only A: zero stranded, but more left. That is not proof of resolution.
        var w = await SeedAsync();
        var reviewer = await AddUserAsync(w, $"fin.{Guid.NewGuid():N}@{w.Domain}", ["Payroll Approver"], true, staff: true);
        await HireWithPayAsync(w, w.AdminId, 71);
        var b = await HireWithPayAsync(w, w.AdminId, 72);
        await UpdateEmployeeAsync(b, e => e.UserAccountId = reviewer);   // B is the reviewer's own pay: they can never approve it
        await using (var dbg = _fx.CreateDb())
        {
            var all = await PayApprovalDeadEnds.EvaluateCompanyAsync(dbg, w.TenantId, w.CompanyId, default);
            all.StrandedEmployeeIds.Should().Equal(new[] { b }, $"pending={all.PendingCount}");
        }
        await SyncAsync(w);
        (await DeadEndNoticesAsync(w)).Should().Contain(n => n.Status != PayApprovalDeadEnds.ResolvedStatus);

        await using (var db = _fx.CreateDb())
        {
            var first = await PayApprovalDeadEnds.EvaluateCompanyAsync(db, w.TenantId, w.CompanyId, default, null, 0, 1);
            first.StrandedCount.Should().Be(0);
            first.HasMore.Should().BeTrue();
            first.NextAfterEmployeeId.Should().NotBeNull();
            var next = await PayApprovalDeadEnds.EvaluateCompanyAsync(db, w.TenantId, w.CompanyId, default, null, first.NextAfterEmployeeId!.Value, 1);
            next.StrandedEmployeeIds.Should().Equal(b);
        }

        await using (var db = _fx.CreateDb())
            await PayApprovalDeadEnds.SyncTaskAsync(db, w.TenantId, w.CompanyId, w.AdminId, "sweep", Ctx(w.AdminId, w.TenantId), null, default, null, 1);
        (await DeadEndNoticesAsync(w)).Should().Contain(n => n.Status != PayApprovalDeadEnds.ResolvedStatus, "the stranded package past the first window keeps the notice open");
        (await CompanyAuditAsync(w)).Should().NotContain(PayApprovalDeadEnds.ResolvedAction);
    }
}
