using System.Data.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// F10 against REAL PostgreSQL, under production's retrying execution strategy and row-lock
/// interceptor: two independent approvers approving the same final settlement at the same moment.
///
/// <para>Each approval reads the settlement, checks that no accrual has posted yet, then posts the
/// accrual journal. Unserialized, both pass the check before either commits and the payable is booked
/// twice. The test holds each request at its first ledger query until both have arrived (or, once the
/// row is locked and the second request cannot arrive, for a few seconds), which makes that
/// interleaving certain instead of lucky.</para>
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class FinalSettlementApprovalConcurrencyPostgresTests
{
    private readonly PostgresFixture _fx;
    public FinalSettlementApprovalConcurrencyPostgresTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task TwoApproversAtOnce_PostTheAccrualExactlyOnce()
    {
        var maker = Guid.NewGuid();
        FinalSettlementMakerCheckerTests.SettlementFixture fx;
        await using (var seed = _fx.CreateDb())
            fx = await FinalSettlementMakerCheckerTests.SeedPendingSettlementAsync(seed, createdBy: maker, submittedBy: Guid.NewGuid());

        var meetAtLedger = new Rendezvous(parties: 2, timeout: TimeSpan.FromSeconds(4));
        async Task<IActionResult> ApproveAs(Guid approver)
        {
            await using var db = CreateDbPausingAtLedger(meetAtLedger);
            return await FinalSettlementMakerCheckerTests.Controller(db, fx.TenantId, approver)
                .ApproveFinalSettlement(fx.Settlement.Id, FinalSettlementMakerCheckerTests.ApproveBody(), CancellationToken.None);
        }

        var results = await Task.WhenAll(ApproveAs(Guid.NewGuid()), ApproveAs(Guid.NewGuid()));

        results.Count(r => r is OkObjectResult).Should().Be(1, "exactly one of two concurrent approvals may succeed");
        var refused = results.Single(r => r is not OkObjectResult).Should().BeOfType<BadRequestObjectResult>().Subject;
        System.Text.Json.JsonSerializer.Serialize(refused.Value).Should().Contain("invalid_transition");

        await using var verify = _fx.CreateDb();
        var accrualRows = await verify.FinanceGlEntries.AsNoTracking()
            .Where(x => x.TenantId == fx.TenantId && x.SourceEntityId == fx.Settlement.Id
                     && x.EventType == GlEventTypes.SettlementAccrual && !x.IsReversed)
            .ToListAsync();
        accrualRows.Where(x => x.CreditAccount != "").Sum(x => x.Amount)
            .Should().Be(fx.Settlement.GrossPayable, "the payable is credited once, for the settlement's gross");
        var stored = await verify.EmployeeFinalSettlements.AsNoTracking().SingleAsync(x => x.Id == fx.Settlement.Id);
        stored.Status.Should().Be(FinalSettlementStatuses.Approved);
        stored.ApprovedByUserId.Should().NotBeNull().And.NotBe(maker);
        (await verify.PayrollAuditLogs.AsNoTracking()
            .CountAsync(x => x.TenantId == fx.TenantId && x.Action == "payroll.final_settlement.approved"
                          && x.EntityId == fx.Settlement.Id.ToString()))
            .Should().Be(1);
    }

    /// <summary>
    /// The lifecycle on real PostgreSQL through every locked transition: submit, a refused self-approval,
    /// an independent approval, and a cancel that contras exactly what the approval posted.
    /// </summary>
    [Fact]
    public async Task Submit_Approve_Cancel_RunUnderTheRetryingStrategyAndKeepTheLedgerBalanced()
    {
        var maker = Guid.NewGuid();
        var submitter = Guid.NewGuid();
        FinalSettlementMakerCheckerTests.SettlementFixture fx;
        await using (var seed = _fx.CreateDb())
            fx = await FinalSettlementMakerCheckerTests.SeedPendingSettlementAsync(seed, createdBy: maker, submittedBy: null);

        await using (var db = _fx.CreateDb())
            (await FinalSettlementMakerCheckerTests.Controller(db, fx.TenantId, submitter)
                .SubmitFinalSettlement(fx.Settlement.Id, new PayrollReasonRequest("ready"), CancellationToken.None))
                .Should().BeOfType<OkObjectResult>();

        await using (var db = _fx.CreateDb())
            (await FinalSettlementMakerCheckerTests.Controller(db, fx.TenantId, submitter)
                .ApproveFinalSettlement(fx.Settlement.Id, FinalSettlementMakerCheckerTests.ApproveBody(), CancellationToken.None))
                .Should().BeOfType<ConflictObjectResult>("the submitter cannot approve");

        await using (var db = _fx.CreateDb())
            (await FinalSettlementMakerCheckerTests.Controller(db, fx.TenantId, Guid.NewGuid())
                .ApproveFinalSettlement(fx.Settlement.Id, FinalSettlementMakerCheckerTests.ApproveBody(), CancellationToken.None))
                .Should().BeOfType<OkObjectResult>();

        await using (var db = _fx.CreateDb())
            (await FinalSettlementMakerCheckerTests.Controller(db, fx.TenantId, Guid.NewGuid())
                .CancelFinalSettlement(fx.Settlement.Id, new PayrollReasonRequest("Resignation withdrawn"), CancellationToken.None))
                .Should().BeOfType<OkObjectResult>();

        await using var verify = _fx.CreateDb();
        var stored = await verify.EmployeeFinalSettlements.AsNoTracking().SingleAsync(x => x.Id == fx.Settlement.Id);
        stored.Status.Should().Be(FinalSettlementStatuses.Cancelled);
        stored.SubmittedByUserId.Should().Be(submitter);
        var gl = await verify.FinanceGlEntries.AsNoTracking()
            .Where(x => x.TenantId == fx.TenantId && x.SourceEntityId == fx.Settlement.Id).ToListAsync();
        gl.Should().Contain(x => x.EventType == GlEventTypes.SettlementAccrual);
        gl.Where(x => x.EventType == GlEventTypes.SettlementAccrual).Should().OnlyContain(x => x.IsReversed);
        gl.Where(x => x.EventType == GlEventTypes.SettlementAccrualReversal).Sum(x => x.Amount)
            .Should().Be(gl.Where(x => x.EventType == GlEventTypes.SettlementAccrual).Sum(x => x.Amount));
    }

    private ZayraDbContext CreateDbPausingAtLedger(Rendezvous rendezvous) => new(
        new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(_fx.ConnectionString, o => o.EnableRetryOnFailure(5, TimeSpan.FromSeconds(5), null))
            .AddInterceptors(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.Instance, new PauseAtFirstLedgerRead(rendezvous))
            .Options);

    /// <summary>Everyone waits here until all parties arrive, or the timeout passes.</summary>
    private sealed class Rendezvous(int parties, TimeSpan timeout)
    {
        private int _arrived;
        private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ArriveAsync()
        {
            if (Interlocked.Increment(ref _arrived) >= parties) _all.TrySetResult();
            await Task.WhenAny(_all.Task, Task.Delay(timeout));
        }
    }

    /// <summary>Pauses a context once, at its first read of the GL table (the post-once check).</summary>
    private sealed class PauseAtFirstLedgerRead(Rendezvous rendezvous) : DbCommandInterceptor
    {
        private int _fired;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("finance_gl_entries", StringComparison.Ordinal)
                && command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                && Interlocked.Exchange(ref _fired, 1) == 0)
                await rendezvous.ArriveAsync();
            return result;
        }
    }
}
