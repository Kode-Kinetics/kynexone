using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Infrastructure.Finance;
using static Zayra.Api.Tests.AdvanceTestKit;
using static Zayra.Api.Tests.AdvancePayoutLifecycleTests;

namespace Zayra.Api.Tests;

/// <summary>
/// Cancel is the unpaid exit of a salary advance, with a reason: HR or Finance cancel a Pending or Approved advance; its requester
/// and the employee it pays withdraw their own while it is Pending. Nothing was posted, so nothing is reversed. A paid (Active)
/// advance is recovered through payroll and cannot be cancelled here.
/// </summary>
public class AdvanceCancelTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly string[] Hr = { "HR Manager" };
    private static readonly string[] FinanceRole = { "Finance" };

    // ── Cancel: the unpaid exit ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cancel_OfAPendingAdvance_ByItsRequester_LeavesNoJournal_AndIsAudited()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var id = await SeedPendingAsync(db, f, 1_000m);
        var requester = (await db.SalaryAdvances.AsNoTracking().SingleAsync(x => x.Id == id)).CreatedBy!.Value;

        var result = await Advances(db, f, requester, new[] { "HR Officer" }, perms: new[] { "loans.write" })
            .Cancel(id, new AdvanceCancelRequest("raised by mistake"), Ct);

        Assert.IsType<OkObjectResult>(result);
        var adv = await db.SalaryAdvances.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal("Cancelled", adv.Status);
        Assert.Equal("raised by mistake", adv.CancelReason);
        Assert.NotNull(adv.CancelledAtUtc);
        Assert.Empty(await db.FinanceGlEntries.ToListAsync());
        Assert.Equal("AdvanceCancelled", (await db.AdvanceAuditLogs.AsNoTracking().SingleAsync()).Action);
    }

    [Fact]
    public async Task Cancel_OfAnApprovedAdvance_ByFinance_LeavesNoJournal_AndRetiresTheSchedule()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var (id, _) = await ApprovedAsync(db, f);

        Assert.IsType<OkObjectResult>(await Advances(db, f, Guid.NewGuid(), FinanceRole).Cancel(id, new AdvanceCancelRequest("employee no longer needs it"), Ct));

        var adv = await db.SalaryAdvances.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal("Cancelled", adv.Status);
        Assert.Equal(0m, adv.OutstandingBalance);
        Assert.Empty(await db.FinanceGlEntries.ToListAsync());
        Assert.All(await db.AdvanceInstallments.AsNoTracking().Where(x => x.AdvanceId == id).ToListAsync(), i => Assert.Equal("Cancelled", i.Status));
        AssertRefusal<ConflictObjectResult>(await PayAsync(db, f, id), "invalid_advance_state");
        Assert.Empty(await db.FinanceGlEntries.ToListAsync());
    }

    [Fact]
    public async Task Cancel_OfAnActiveAdvance_IsRefused_AndTheBalanceStands()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var (id, _) = await ApprovedAsync(db, f);
        Assert.IsType<OkObjectResult>(await PayAsync(db, f, id));
        db.ChangeTracker.Clear();

        var result = await Advances(db, f, Guid.NewGuid(), FinanceRole).Cancel(id, new AdvanceCancelRequest("too late"), Ct);

        AssertRefusal<ConflictObjectResult>(result, "invalid_advance_state");
        var adv = await db.SalaryAdvances.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal("Active", adv.Status);
        Assert.Equal(1_200m, adv.OutstandingBalance);
        Assert.Single(await db.FinanceGlEntries.ToListAsync());
    }

    [Fact]
    public async Task Cancel_BySomeoneWhoIsNeitherTheRequesterNorTheEmployeeNorHrOrFinance_IsForbidden()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var id = await SeedPendingAsync(db, f, 1_000m);

        var result = await Advances(db, f, Guid.NewGuid(), new[] { "Employee" }, perms: new[] { "loans.self" })
            .Cancel(id, new AdvanceCancelRequest("not mine"), Ct);

        Assert.IsType<ForbidResult>(result);
        Assert.Equal("Pending", (await db.SalaryAdvances.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task Cancel_ByTheEmployeeItPays_IsAccepted()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var id = await SeedPendingAsync(db, f, 1_000m);

        var result = await EmployeeSelf(db, f).Cancel(id, new AdvanceCancelRequest("withdrawing my request"), Ct);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("Cancelled", (await db.SalaryAdvances.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task TheEmployeeItPays_CannotCancelItOnceApproved_HrOrFinanceCan()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var (approved, _) = await ApprovedAsync(db, f, 1_000m, 2);

        AssertRefusal<ObjectResult>(await EmployeeSelf(db, f).Cancel(approved, new AdvanceCancelRequest("withdrawing"), Ct),
            "advance_cancel_own_pending_only");
        Assert.Equal(403, ((ObjectResult)await EmployeeSelf(db, f).Cancel(approved, new AdvanceCancelRequest("withdrawing"), Ct)).StatusCode);
        Assert.Equal("Approved", (await db.SalaryAdvances.AsNoTracking().SingleAsync()).Status);

        Assert.IsType<OkObjectResult>(await Advances(db, f, Guid.NewGuid(), Hr).Cancel(approved, new AdvanceCancelRequest("withdrawn on request"), Ct));
        Assert.Equal("Cancelled", (await db.SalaryAdvances.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task Cancel_WithoutAReason_IsRefused()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var id = await SeedPendingAsync(db, f, 1_000m);

        AssertRefusal<BadRequestObjectResult>(await Advances(db, f, Guid.NewGuid(), Hr).Cancel(id, new AdvanceCancelRequest(" "), Ct),
            "advance_cancel_reason_required");
        Assert.Equal("Pending", (await db.SalaryAdvances.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task ACancelledRequest_DoesNotUseTheYearlyQuota()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);             // two advances a year
        var first = await SeedPendingAsync(db, f, 500m, 1);
        await SeedPendingAsync(db, f, 500m, 1);
        Assert.IsType<OkObjectResult>(await Advances(db, f, Guid.NewGuid(), Hr).Cancel(first, new AdvanceCancelRequest("raised twice"), Ct));
        db.ChangeTracker.Clear();

        var again = await Advances(db, f, Guid.NewGuid(), Hr).Create(
            new CreateAdvanceRequest(f.EmployeePublicId, "Huda Saleh", 500m, "OneTime", 1, null, "rent", f.EmployeeId), Ct);

        Assert.IsType<OkObjectResult>(again);
    }

    [Fact]
    public async Task Cancel_TakesTheAdvanceLock()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var id = await SeedPendingAsync(db, f, 500m, 1);
        var log = new List<(string Scope, Guid TenantId, Guid AggregateId)>();
        FinanceDecisionSerializer.Recorder.Value = log;
        try { Assert.IsType<OkObjectResult>(await Advances(db, f, Guid.NewGuid(), Hr).Cancel(id, new AdvanceCancelRequest("not needed"), Ct)); }
        finally { FinanceDecisionSerializer.Recorder.Value = null; }

        Assert.Contains((FinanceDecisionSerializer.ScopeAdvance, f.TenantId, id), log);
    }

    [Fact]
    public void Cancel_IsOpenToTheSelfServiceAndLoanWriteKeys_TheBodyDecidesWho()
    {
        var attr = (Zayra.Api.Infrastructure.Authorization.HasPermissionAttribute)Attribute.GetCustomAttribute(
            typeof(AdvancesController).GetMethod(nameof(AdvancesController.Cancel))!, typeof(Zayra.Api.Infrastructure.Authorization.HasPermissionAttribute))!;
        Assert.Equal(new[] { "loans.self", "loans.write" }, attr.Permissions);
    }
}
