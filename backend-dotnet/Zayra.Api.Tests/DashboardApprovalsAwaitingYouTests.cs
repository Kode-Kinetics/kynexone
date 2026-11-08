using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The home dashboard's "N approvals waiting for you". It used to count every pending approval about people in the viewer's scope, including
/// items routed to somebody else, so a caller who could decide nothing was told approvals were waiting for them. It now counts only the pending
/// items the caller can decide right now, by the SAME check the decision endpoint runs (routed to them or their role, not their own request,
/// not about them, not a step they already decided), and only for someone who holds approvals.decide.
/// </summary>
public class DashboardApprovalsAwaitingYouTests
{
    private static readonly Guid Tid = Guid.NewGuid();
    private static readonly Guid Me = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    private static ZayraDbContext CreateDb()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Tenants.Add(new Tenant { Id = Tid, Name = "Awaiting", Slug = $"aw-{Tid:N}"[..20] });
        db.SaveChanges();
        return db;
    }

    private static DashboardController Ctrl(ZayraDbContext db, IEnumerable<string> permissions, params string[] roles)
    {
        var claims = new List<Claim> { new("tenant_id", Tid.ToString()), new(ClaimTypes.NameIdentifier, Me.ToString()) };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        return new DashboardController(db, new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), new OrgScope())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) } },
        };
    }

    private static ApprovalRequest Pending(string title, DateTime created, Guid? approverUser = null, string approverRole = "", Guid? requestedBy = null)
    {
        var r = new ApprovalRequest
        {
            TenantId = Tid, WorkflowId = Guid.NewGuid(), EntityName = "SalaryChange", EntityId = Guid.NewGuid().ToString(), Title = title, Status = "Pending",
            CreatedAtUtc = created, RequestedByUserId = requestedBy ?? Guid.NewGuid(),
            CurrentApproverUserId = approverUser, CurrentApproverRole = approverRole, CurrentApproverType = approverUser is not null ? "user" : "role",
        };
        return r;
    }

    private static async Task<DashboardOverviewDto> OverviewAsync(DashboardController ctrl)
    {
        var result = await ctrl.Full();
        return ((DashboardFullDto)((OkObjectResult)result).Value!).Overview;
    }

    private static readonly string[] Decide = ["approvals.decide"];
    private static readonly string[] DecideAndOverride = ["approvals.decide", "approvals.override"];

    [Fact]
    public async Task ACallerWhoIsNotTheCurrentDecider_SeesZeroWaitingForThem()
    {
        var db = CreateDb();
        var t = DateTime.UtcNow;
        db.ApprovalRequests.AddRange(
            Pending("To someone else", t, approverUser: Other),
            Pending("To Finance", t.AddMinutes(1), approverRole: "Finance Approver"),
            Pending("To HR", t.AddMinutes(2), approverRole: "HR Manager"));
        await db.SaveChangesAsync();

        var overview = await OverviewAsync(Ctrl(db, Decide, "Manager"));

        overview.PendingApprovals.Should().Be(0, "none of the three is routed to this caller");
        overview.ApprovalQueue.Should().BeEmpty();
    }

    [Fact]
    public async Task OnlyTheItemsTheCallerCanDecideNow_AreCountedAndListed()
    {
        var db = CreateDb();
        var t = DateTime.UtcNow;
        var mine = Pending("Routed to me", t, approverUser: Me);
        var myRole = Pending("Routed to my role", t.AddMinutes(1), approverRole: "HR Manager");
        var ownRequest = Pending("I requested it", t.AddMinutes(2), approverUser: Me, requestedBy: Me);
        var decidedEarlier = Pending("I decided step 1", t.AddMinutes(3), approverUser: Me);
        decidedEarlier.Decisions.Add(new ApprovalDecision { TenantId = Tid, ApprovalRequestId = decidedEarlier.Id, StepOrder = 1, Decision = "Approved", DecidedByUserId = Me });
        var elsewhere = Pending("Someone else's", t.AddMinutes(4), approverUser: Other);
        var financeRole = Pending("Finance's", t.AddMinutes(5), approverRole: "Finance Approver");
        db.ApprovalRequests.AddRange(mine, myRole, ownRequest, decidedEarlier, elsewhere, financeRole);
        await db.SaveChangesAsync();

        var overview = await OverviewAsync(Ctrl(db, Decide, "HR Manager"));

        overview.PendingApprovals.Should().Be(2);
        overview.ApprovalQueue.Select(q => q.Title).Should().Equal("Routed to my role", "Routed to me");
    }

    [Fact]
    public async Task WithoutApprovalsDecide_NothingIsWaitingForTheCaller_EvenWhenItIsRoutedToThem()
    {
        var db = CreateDb();
        db.ApprovalRequests.Add(Pending("Routed to me", DateTime.UtcNow, approverUser: Me));
        await db.SaveChangesAsync();

        var overview = await OverviewAsync(Ctrl(db, Array.Empty<string>(), "Employee"));

        overview.PendingApprovals.Should().Be(0, "the decision endpoint needs approvals.decide, so the dashboard must not promise a decision the caller cannot take");
        overview.ApprovalQueue.Should().BeEmpty();
    }

    [Fact]
    public async Task AnOverrideHolder_CanDecideAnyPendingItemThatIsNotTheirOwnRequest()
    {
        var db = CreateDb();
        var t = DateTime.UtcNow;
        db.ApprovalRequests.AddRange(
            Pending("A", t, approverUser: Other),
            Pending("B", t.AddMinutes(1), approverRole: "Finance Approver"),
            Pending("Mine to withdraw", t.AddMinutes(2), approverUser: Other, requestedBy: Me));
        await db.SaveChangesAsync();

        var overview = await OverviewAsync(Ctrl(db, DecideAndOverride, "Admin"));

        overview.PendingApprovals.Should().Be(2, "approvals.override reaches every step, but never the caller's own request");
    }

    [Fact]
    public async Task TheQueueShowsTheEightNewestItemsTheCallerCanDecide_WhileTheCountIsTheTotal()
    {
        var db = CreateDb();
        var t = DateTime.UtcNow;
        for (var i = 0; i < 10; i++) db.ApprovalRequests.Add(Pending($"Mine {i}", t.AddMinutes(i), approverUser: Me));
        for (var i = 0; i < 5; i++) db.ApprovalRequests.Add(Pending($"Other {i}", t.AddMinutes(20 + i), approverUser: Other));
        await db.SaveChangesAsync();

        var overview = await OverviewAsync(Ctrl(db, Decide, "HR Manager"));

        overview.PendingApprovals.Should().Be(10);
        overview.ApprovalQueue.Select(q => q.Title).Should().Equal(Enumerable.Range(2, 8).Reverse().Select(i => $"Mine {i}"));
    }

    [Fact]
    public async Task UnderTheScanLimit_TheCountIsExact_AndNotMarkedCapped()
    {
        var db = CreateDb();
        db.ApprovalRequests.Add(Pending("Mine", DateTime.UtcNow, approverUser: Me));
        await db.SaveChangesAsync();

        var overview = await OverviewAsync(Ctrl(db, Decide, "HR Manager"));

        overview.PendingApprovals.Should().Be(1);
        overview.ApprovalsCapped.Should().BeFalse();
    }

    [Fact]
    public async Task PastTheScanLimit_TheCountIsAFloorAndSaysSo_SoTheScreenCanShowFiveHundredPlus()
    {
        var db = CreateDb();
        var t = DateTime.UtcNow;
        for (var i = 0; i <= DecidableApprovals.ScanLimit; i++) db.ApprovalRequests.Add(Pending($"Mine {i}", t.AddSeconds(i), approverUser: Me));
        await db.SaveChangesAsync();

        var overview = await OverviewAsync(Ctrl(db, Decide, "HR Manager"));

        overview.PendingApprovals.Should().Be(DecidableApprovals.ScanLimit, "the newest 500 were examined");
        overview.ApprovalsCapped.Should().BeTrue("more pending requests exist than were examined, so the figure understates");
        overview.ApprovalQueue.Should().HaveCount(8);
    }

    private sealed class OrgScope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
            => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization, AllowedEmployeeIds = null });
    }
}
