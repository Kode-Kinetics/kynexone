using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;
using static Zayra.Api.Tests.AdvanceTestKit;

namespace Zayra.Api.Tests;

/// <summary>
/// The salary-advance lifecycle, mirroring the loan payment flow: approval agrees the terms and the schedule and moves no money;
/// a Finance payout records the payment (reference, paid date) and posts Dr 1410 Employee Salary Advances / Cr 1000 Cash on the paid
/// date. Pending → Approved → Active → Closed, with Rejected and Cancelled as the unpaid exits (<see cref="AdvanceCancelTests"/>).
/// </summary>
public class AdvancePayoutLifecycleTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);
    private static readonly string[] Hr = { "HR Manager" };
    private static readonly string[] FinanceRole = { "Finance" };

    // ── Approve: terms and schedule, no money ───────────────────────────────────────────────────────

    [Fact]
    public async Task Approve_MovesPendingToApproved_SetsTermsAndSchedule_AndPostsNoJournal()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var id = await SeedPendingAsync(db, f, 1_000m, 3);

        var result = await Advances(db, f, Guid.NewGuid(), Hr).Approve(id, new AdvanceApproveRequest(1_000m, 3, null), Ct);

        Assert.IsType<OkObjectResult>(result);
        var adv = await db.SalaryAdvances.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal("Approved", adv.Status);
        Assert.Equal(1_000m, adv.ApprovedAmount);
        Assert.Equal(333.33m, adv.InstallmentAmount);
        Assert.Equal(0m, adv.OutstandingBalance);
        Assert.Null(adv.PaidAtUtc);
        var rows = await db.AdvanceInstallments.AsNoTracking().Where(x => x.AdvanceId == id).OrderBy(x => x.InstallmentNumber).ToListAsync();
        Assert.Equal(new[] { 333.33m, 333.33m, 333.34m }, rows.Select(r => r.AmountDue).ToArray());
        Assert.All(rows, r => Assert.Equal("Pending", r.Status));
        Assert.Empty(await db.FinanceGlEntries.ToListAsync());
        Assert.Equal("AdvanceApproved", (await db.AdvanceAuditLogs.SingleAsync()).Action);
    }

    // ── Payout: the cash moment ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Payout_PostsExactlyOneDr1410Cr1000_ForTheApprovedAmount_OnThePaidDateAndItsPeriod()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var (id, _) = await ApprovedAsync(db, f, 1_200m, 3, requestedMonthsAgo: 2);
        var paid = new DateOnly(Today.Year, Today.Month, 15).AddMonths(-1);
        var payer = Guid.NewGuid();

        var result = await PayAsync(db, f, id, "BANK-001", paid, payer);

        Assert.IsType<OkObjectResult>(result);
        var gl = Assert.Single(await db.FinanceGlEntries.AsNoTracking().ToListAsync());
        Assert.Equal("Advance", gl.SourceModule);
        Assert.Equal(id, gl.SourceEntityId);
        Assert.Equal("Disbursement", gl.EventType);
        Assert.Equal("1410 - Employee Salary Advances", gl.DebitAccount);
        Assert.Equal("1000 - Cash/Bank", gl.CreditAccount);
        Assert.Equal(1_200m, gl.Amount);
        Assert.Equal("SAR", gl.Currency);
        Assert.Equal(f.CompanyId, gl.CompanyId);
        Assert.Equal(paid, gl.EntryDate);
        Assert.Equal(paid.ToString("yyyy-MM"), gl.Period);

        var adv = await db.SalaryAdvances.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal("Active", adv.Status);
        Assert.Equal(1_200m, adv.OutstandingBalance);
        Assert.Equal("BANK-001", adv.PaymentReference);
        Assert.Equal(payer, adv.PaidByUserId);
        Assert.NotNull(adv.PaidAtUtc);
        Assert.Contains(await db.AdvanceAuditLogs.AsNoTracking().Select(x => x.Action).ToListAsync(), a => a == "AdvancePaidOut");
    }

    [Fact]
    public async Task Payout_RepeatedWithTheSameReference_IsIdempotent_AndADifferentReferenceIsRefused()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var (id, _) = await ApprovedAsync(db, f);

        Assert.IsType<OkObjectResult>(await PayAsync(db, f, id, "BANK-777"));
        db.ChangeTracker.Clear();
        Assert.IsType<OkObjectResult>(await PayAsync(db, f, id, " BANK-777 "));
        db.ChangeTracker.Clear();
        var different = await PayAsync(db, f, id, "BANK-888");

        AssertRefusal<ConflictObjectResult>(different, "advance_already_paid");
        Assert.Single(await db.FinanceGlEntries.AsNoTracking().ToListAsync());
        Assert.Equal("BANK-777", (await db.SalaryAdvances.AsNoTracking().SingleAsync()).PaymentReference);
    }

    [Fact]
    public async Task Payout_ByTheApprover_IsRefused_AndNothingMoves()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var (id, approver) = await ApprovedAsync(db, f);

        var result = await PayAsync(db, f, id, "BANK-001", payer: approver, roles: new[] { "Finance", "HR Manager" });

        AssertRefusal<BadRequestObjectResult>(result, "advance_payer_is_approver");
        await AssertStillApprovedAsync(db, id);
    }

    [Fact]
    public async Task Payout_ByTheEmployeeItPays_IsRefused_AndNothingMoves()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var (id, _) = await ApprovedAsync(db, f);

        var result = await PayAsync(db, f, id, "BANK-001", payer: f.EmployeeUserId);

        AssertRefusal<BadRequestObjectResult>(result, "advance_payer_is_subject");
        await AssertStillApprovedAsync(db, id);
    }

    [Fact]
    public async Task Payout_IntoAClosedPeriod_IsRefused_AndNothingMoves()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var (id, _) = await ApprovedAsync(db, f);
        db.GlPeriodCloses.Add(new GlPeriodClose { TenantId = f.TenantId, CompanyId = f.CompanyId, Period = Today.ToString("yyyy-MM"), Status = GlPeriodStatuses.Closed });
        await db.SaveChangesAsync();

        var result = await PayAsync(db, f, id, "BANK-001", Today);

        AssertRefusal<UnprocessableEntityObjectResult>(result, "advance_payout_period_closed");
        await AssertStillApprovedAsync(db, id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("R-0123456789-0123456789-0123456789-0123456789-0123456789-0123456789-0123456789-0123456789-0123456789-X")]
    public async Task Payout_WithoutAUsableReference_IsRefused(string? reference)
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var (id, _) = await ApprovedAsync(db, f);

        var result = await PayAsync(db, f, id, reference!);

        AssertRefusal<BadRequestObjectResult>(result, "advance_payout_reference_required");
        await AssertStillApprovedAsync(db, id);
    }

    /// <summary>Review P2-1: the payout form defaults the paid date to the browser's local today, which for a user east of UTC is
    /// already tomorrow in UTC. The date is accepted up to UTC today plus one (the same tolerance the first-repayment date has) and
    /// refused beyond it.</summary>
    [Fact]
    public async Task Payout_DatedUtcTodayPlusOne_IsAccepted_ForAUserEastOfUtc_AndPlusTwoIsRefused()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var (id, _) = await ApprovedAsync(db, f);

        AssertRefusal<BadRequestObjectResult>(await PayAsync(db, f, id, "BANK-001", Today.AddDays(2)), "advance_payout_date_invalid");
        await AssertStillApprovedAsync(db, id);
        db.ChangeTracker.Clear();

        var result = await PayAsync(db, f, id, "BANK-001", Today.AddDays(1));

        Assert.IsType<OkObjectResult>(result);
        var adv = await db.SalaryAdvances.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal("Active", adv.Status);
        Assert.Equal(Today.AddDays(1), (await db.FinanceGlEntries.AsNoTracking().SingleAsync()).EntryDate);
    }

    [Fact]
    public async Task Payout_DatedInTheFuture_IsRefused()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var (id, _) = await ApprovedAsync(db, f);

        AssertRefusal<BadRequestObjectResult>(await PayAsync(db, f, id, "BANK-001", Today.AddDays(2)), "advance_payout_date_invalid");
        await AssertStillApprovedAsync(db, id);
    }

    [Fact]
    public async Task Payout_OfAPendingAdvance_IsRefused()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var id = await SeedPendingAsync(db, f, 1_000m);

        AssertRefusal<ConflictObjectResult>(await PayAsync(db, f, id), "invalid_advance_state");
        Assert.Empty(await db.FinanceGlEntries.ToListAsync());
    }

    [Fact]
    public async Task Payout_OfALegacyActiveAdvance_IsRefusedAsAlreadyPaid_AndPostsNothing()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var id = await SeedPendingAsync(db, f, 2_000m);
        var adv = await db.SalaryAdvances.SingleAsync(x => x.Id == id);
        adv.Status = "Active"; adv.ApprovedAmount = 2_000m; adv.OutstandingBalance = 2_000m;   // the old flow: no PaidAt, no reference
        await db.SaveChangesAsync();

        AssertRefusal<ConflictObjectResult>(await PayAsync(db, f, id), "advance_already_paid");
        Assert.Empty(await db.FinanceGlEntries.ToListAsync());
    }

    [Fact]
    public async Task Payout_ByAnHrManagerWithoutTheFinanceRole_IsForbidden()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var (id, _) = await ApprovedAsync(db, f);

        Assert.IsType<ForbidResult>(await PayAsync(db, f, id, roles: Hr));
        await AssertStillApprovedAsync(db, id);
    }

    [Fact]
    public async Task Payout_OutsideTheCallersDataScope_IsForbidden()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var (id, _) = await ApprovedAsync(db, f);

        var result = await Advances(db, f, Guid.NewGuid(), FinanceRole, new FixedScope(new[] { f.EmployeeId + 999 }))
            .Payout(id, new AdvancePayoutRequest(null, "BANK-001", null), Ct);

        Assert.IsType<ForbidResult>(result);
        await AssertStillApprovedAsync(db, id);
    }

    [Fact]
    public void Payout_IsGatedOnLoansWrite_WithNoNewRoleGate_TheBodyKeepsItToFinance()
    {
        // Same audience as the loan payment outcome: loans.write (what that endpoint's role gate resolves to), then the Admin or
        // Finance role in the body (Payout_ByAnHrManagerWithoutTheFinanceRole_IsForbidden). No [Authorize(Roles)]: the
        // architecture ratchet allows no new role gate.
        var method = typeof(AdvancesController).GetMethod(nameof(AdvancesController.Payout))!;
        var permission = (Zayra.Api.Infrastructure.Authorization.HasPermissionAttribute)Attribute.GetCustomAttribute(
            method, typeof(Zayra.Api.Infrastructure.Authorization.HasPermissionAttribute))!;
        Assert.Equal(new[] { "loans.write" }, permission.Permissions);
        Assert.DoesNotContain(method.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>(), a => !string.IsNullOrEmpty(a.Roles));
        var route = method.GetCustomAttributes(typeof(HttpPostAttribute), true).Cast<HttpPostAttribute>().Single();
        Assert.Equal("{id:guid}/payout", route.Template);
    }

    [Fact]
    public async Task Payout_TakesTheAdvanceLock()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var (paidId, _) = await ApprovedAsync(db, f);
        var log = new List<(string Scope, Guid TenantId, Guid AggregateId)>();
        FinanceDecisionSerializer.Recorder.Value = log;
        try { Assert.IsType<OkObjectResult>(await PayAsync(db, f, paidId)); }
        finally { FinanceDecisionSerializer.Recorder.Value = null; }

        Assert.Contains((FinanceDecisionSerializer.ScopeAdvance, f.TenantId, paidId), log);
    }

    // ── The repayment start date (audit P2-1) ───────────────────────────────────────────────────────

    [Fact]
    public async Task Payout_WithoutAnAgreedStartDate_StartsTheScheduleOneMonthAfterPayment_AndPersistsThatDate()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var (id, _) = await ApprovedAsync(db, f, 1_200m, 3, requestedMonthsAgo: 1);
        var paid = Today.AddDays(-3);

        Assert.IsType<OkObjectResult>(await PayAsync(db, f, id, "BANK-001", paid));

        var adv = await db.SalaryAdvances.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal(paid.AddMonths(1), adv.RepaymentStartDate);
        var due = await db.AdvanceInstallments.AsNoTracking().Where(x => x.AdvanceId == id).OrderBy(x => x.InstallmentNumber).Select(x => x.DueDate).ToListAsync();
        Assert.Equal(new[] { paid.AddMonths(1), paid.AddMonths(2), paid.AddMonths(3) }, due);
    }

    [Fact]
    public async Task Payout_WithAnAgreedStartDate_KeepsItAndTheSchedule()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var start = Today.AddMonths(2);
        var (id, _) = await ApprovedAsync(db, f, 1_200m, 3, start: start);

        Assert.IsType<OkObjectResult>(await PayAsync(db, f, id, "BANK-001", Today));

        var adv = await db.SalaryAdvances.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal(start, adv.RepaymentStartDate);
        var due = await db.AdvanceInstallments.AsNoTracking().Where(x => x.AdvanceId == id).OrderBy(x => x.InstallmentNumber).Select(x => x.DueDate).ToListAsync();
        Assert.Equal(new[] { start, start.AddMonths(1), start.AddMonths(2) }, due);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────

    internal static async Task<(Guid AdvanceId, Guid ApproverId)> ApprovedAsync(
        ZayraDbContext db, Fx f, decimal amount = 1_200m, int installments = 3, DateOnly? start = null, int requestedMonthsAgo = 0)
    {
        var id = await SeedPendingAsync(db, f, amount, installments);
        if (requestedMonthsAgo > 0)
        {
            var seeded = await db.SalaryAdvances.SingleAsync(x => x.Id == id);
            seeded.CreatedAtUtc = DateTime.UtcNow.AddMonths(-requestedMonthsAgo);
            await db.SaveChangesAsync();
        }
        var approver = Guid.NewGuid();
        Assert.IsType<OkObjectResult>(await Advances(db, f, approver, Hr).Approve(id, new AdvanceApproveRequest(amount, installments, start), Ct));
        db.ChangeTracker.Clear();
        return (id, approver);
    }

    internal static Task<IActionResult> PayAsync(ZayraDbContext db, Fx f, Guid advanceId, string reference = "BANK-001",
        DateOnly? paidDate = null, Guid? payer = null, string[]? roles = null) =>
        Advances(db, f, payer ?? Guid.NewGuid(), roles ?? FinanceRole).Payout(advanceId, new AdvancePayoutRequest(paidDate, reference, null), Ct);

    internal static AdvancesController EmployeeSelf(ZayraDbContext db, Fx f)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", f.TenantId.ToString()), new(ClaimTypes.NameIdentifier, f.EmployeeUserId.ToString()),
            new(ClaimTypes.Name, "Huda Saleh"), new(ClaimTypes.Role, "Employee"), new("permission", "loans.self"),
            new("employee_id", f.EmployeeId.ToString()),
        };
        return new AdvancesController(db, new FixedScope(new[] { f.EmployeeId }))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) },
            },
        };
    }

    private static async Task AssertStillApprovedAsync(ZayraDbContext db, Guid id)
    {
        var adv = await db.SalaryAdvances.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal("Approved", adv.Status);
        Assert.Equal(0m, adv.OutstandingBalance);
        Assert.Null(adv.PaidAtUtc);
        Assert.Null(adv.PaymentReference);
        Assert.Empty(await db.FinanceGlEntries.AsNoTracking().Where(x => x.SourceEntityId == id).ToListAsync());
    }

    /// <summary>Every refusal names a machine code and carries the sentence in English and Arabic.</summary>
    internal static void AssertRefusal<T>(IActionResult result, string code) where T : ObjectResult
    {
        var body = JsonSerializer.SerializeToElement(Assert.IsType<T>(result).Value);
        Assert.Equal(code, body.GetProperty("error").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("message").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("messageAr").GetString()));
    }
}
