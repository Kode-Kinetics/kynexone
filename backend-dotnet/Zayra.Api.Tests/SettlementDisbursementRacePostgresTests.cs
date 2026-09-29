using System.Data.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// F10 against REAL PostgreSQL: an approved final settlement can be paid two ways — recorded as paid by
/// bank transfer (OffboardingController.RecordExternalSettlementPayment) or disbursed by a settlement
/// payroll run (PayrollController.Process). Racing, they must leave exactly ONE outcome: Paid with no
/// disbursement, or Disbursing with the payment refused. Never both.
///
/// <para>The payment side here follows the external-payment protocol of #124 exactly — one transaction
/// that locks the offboarding row and then the settlement row FOR UPDATE, stages the discharge journal
/// through the real <see cref="FinalSettlementExternalDischarge"/> and writes Paid — so this proves the
/// run's half of the contract independently of which PR lands first. The payroll run is the real
/// Process endpoint.</para>
///
/// <para>Each test forces one interleaving with interceptors. Before the run took the lock, both
/// interleavings ended with the discharge journal posted AND the settlement stamped Disbursing by the
/// run: the leaver paid twice.</para>
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class SettlementDisbursementRacePostgresTests
{
    private readonly PostgresFixture _fx;
    public SettlementDisbursementRacePostgresTests(PostgresFixture fx) => _fx = fx;

    private sealed record World(Guid TenantId, Guid RunId, Guid OffboardingId, Guid SettlementId, decimal Gross);

    /// <summary>The payment locks and writes Paid first; the run reaches its claim while the payment is still open.</summary>
    [Fact]
    public async Task PaymentFirst_TheRunWritesNothing_AndTheSettlementStaysPaidOutsidePayroll()
    {
        var w = await SeedAsync();
        var runAtClaim = new Signal();
        var paymentHoldsLocks = new Signal();

        var gate = new RunGate(beforeClaim: async () => { runAtClaim.Set(); await paymentHoldsLocks.WaitAsync(); });
        var run = ProcessAsync(w, gate);
        var payment = RecordPaymentAsync(w, before: runAtClaim.WaitAsync,
            afterWrite: async () => { paymentHoldsLocks.Set(); await Task.Delay(1500); });

        var (runResult, paymentOutcome) = (await run, await payment);

        paymentOutcome.Should().Be("Paid");
        var conflict = runResult.Should().BeOfType<ObjectResult>().Subject;
        conflict.StatusCode.Should().Be(409, "the run must not disburse a settlement that was just paid by bank transfer");
        System.Text.Json.JsonSerializer.Serialize(conflict.Value).Should().Contain("settlement_consumed_concurrently")
            .And.Contain("paid outside payroll");
        await AssertExactlyOneOutcomeAsync(w, expectPaidOutside: true);
    }

    /// <summary>The run claims the settlement first; the payment arrives while the run is still open.</summary>
    [Fact]
    public async Task RunFirst_ThePaymentIsRefused_AndTheSettlementIsDisbursedOnce()
    {
        var w = await SeedAsync();
        var runClaimed = new Signal();

        var gate = new RunGate(afterClaim: async () => { runClaimed.Set(); await Task.Delay(1500); });
        var run = ProcessAsync(w, gate);
        var payment = RecordPaymentAsync(w, before: runClaimed.WaitAsync, afterWrite: () => Task.CompletedTask);

        var (runResult, paymentOutcome) = (await run, await payment);

        runResult.Should().BeOfType<OkObjectResult>();
        paymentOutcome.Should().Be("settlement_not_approved", "a Disbursing settlement cannot also be paid by bank transfer");
        await AssertExactlyOneOutcomeAsync(w, expectPaidOutside: false);
    }

    private async Task AssertExactlyOneOutcomeAsync(World w, bool expectPaidOutside)
    {
        await using var db = _fx.CreateDb();
        var s = await db.EmployeeFinalSettlements.AsNoTracking().SingleAsync(x => x.Id == w.SettlementId);
        var externalPayments = await db.FinanceGlEntries.AsNoTracking()
            .CountAsync(x => x.TenantId == w.TenantId && x.SourceEntityId == w.SettlementId
                          && x.EventType == GlEventTypes.SettlementExternalPayment && !x.IsReversed);
        var runSlips = await db.PayrollSlips.AsNoTracking().CountAsync(x => x.RunId == w.RunId);
        var runStatus = (await db.PayrollRuns.AsNoTracking().SingleAsync(x => x.Id == w.RunId)).Status;

        if (expectPaidOutside)
        {
            s.Status.Should().Be(FinalSettlementStatuses.Paid);
            s.PaidOutsidePayroll.Should().BeTrue();
            s.PayrollRunId.Should().BeNull("the run wrote nothing");
            externalPayments.Should().BeGreaterThan(0);
            runSlips.Should().Be(0);
            runStatus.Should().Be("Draft");
        }
        else
        {
            s.Status.Should().Be(FinalSettlementStatuses.Disbursing);
            s.PayrollRunId.Should().Be(w.RunId);
            s.PaidOutsidePayroll.Should().BeFalse();
            externalPayments.Should().Be(0, "the refused payment posted no discharge");
            runSlips.Should().Be(1);
        }
    }

    // ── The two contenders ─────────────────────────────────────────────────────────────────────────

    private async Task<IActionResult> ProcessAsync(World w, RunGate gate)
    {
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(_fx.ConnectionString, o => o.EnableRetryOnFailure(5, TimeSpan.FromSeconds(5), null))
            .AddInterceptors(RowLockingInterceptor.Instance, gate)
            .Options);
        return await FinalSettlementMakerCheckerTests.Controller(db, w.TenantId, Guid.NewGuid(), "Payroll Operator")
            .Process(w.RunId, CancellationToken.None);
    }

    /// <summary>
    /// The external-payment protocol of #124: one transaction, the offboarding then the settlement locked
    /// FOR UPDATE, the real discharge staged, Paid written. Returns "Paid" or the refusal code.
    /// </summary>
    private async Task<string> RecordPaymentAsync(World w, Func<Task> before, Func<Task> afterWrite)
    {
        await before();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(_fx.ConnectionString)
            .AddInterceptors(RowLockingInterceptor.Instance)
            .Options);
        await using var tx = await db.Database.BeginTransactionAsync();
        var off = await db.EmployeeOffboardings.TagWith(RowLockingInterceptor.ForUpdateTag)
            .SingleAsync(x => x.TenantId == w.TenantId && x.Id == w.OffboardingId);
        var settlement = await db.EmployeeFinalSettlements.TagWith(RowLockingInterceptor.ForUpdateTag)
            .Where(x => x.TenantId == w.TenantId && x.OffboardingId == off.Id && x.Status != FinalSettlementStatuses.Cancelled)
            .OrderBy(x => x.Id)
            .FirstAsync();
        var paidOn = DateOnly.FromDateTime(DateTime.UtcNow);
        var (_, refusal) = await FinalSettlementExternalDischarge.StageAsync(
            db, settlement, paidOn, "BankTransfer", "TRF-RACE-1", Guid.NewGuid(), "Finance Recorder", CancellationToken.None);
        if (refusal is not null)
        {
            await tx.RollbackAsync();
            return refusal.Error;
        }
        settlement.Status = FinalSettlementStatuses.Paid;
        settlement.PaidAtUtc = DateTime.UtcNow;
        settlement.PaidOutsidePayroll = true;
        settlement.ExternalPaymentMethod = "BankTransfer";
        settlement.ExternalPaymentReference = "TRF-RACE-1";
        settlement.ExternalPaymentDate = paidOn;
        off.FinalSettlementDone = true;
        await db.SaveChangesAsync();
        await afterWrite();
        await tx.CommitAsync();
        return "Paid";
    }

    // ── World ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A leaver whose settlement is Approved (accrued to 2320, approver recorded, not yet disbursed), and a
    /// Draft off-cycle settlement run for the following month that names them.
    /// </summary>
    private async Task<World> SeedAsync()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = "Race KSA Co", CountryCode = "SAU", Jurisdiction = "KSA-mainland",
            RegistrationNumber = $"RC-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true,
            CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var employee = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = $"RC-{Guid.NewGuid():N}"[..14],
            FullName = "Leaving Racer", Nationality = "Indian", Status = EmployeeStatuses.Offboarded,
            JoiningDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.AddRange(company, employee);
        await db.SaveChangesAsync();

        var lastDay = new DateOnly(2026, 5, 31);
        var offboarding = new EmployeeOffboarding
        {
            TenantId = tenantId, EmployeeId = employee.Id, EmployeeCode = employee.EmployeeCode,
            EmployeeName = employee.FullName, SeparationType = "Termination", Status = "InProgress",
            NoticeDate = lastDay.AddDays(-30), NoticePeriodDays = 30, LastWorkingDay = lastDay,
        };
        const decimal gross = 12_000m;
        var settlement = new EmployeeFinalSettlement
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeId = employee.Id, EmployeeCode = employee.EmployeeCode,
            EmployeeName = employee.FullName, OffboardingId = offboarding.Id, LastWorkingDay = lastDay,
            ServiceStartDate = new DateOnly(2020, 1, 1), SettlementDueDate = lastDay.AddDays(7),
            TerminationReason = "Termination", Currency = "SAR",
            GratuityAmount = gross, GrossPayable = gross, TotalDeductions = 0m, NetPayable = gross,
            Status = FinalSettlementStatuses.Approved, CreatedByUserId = Guid.NewGuid(), SubmittedByUserId = Guid.NewGuid(),
            ApprovedByUserId = Guid.NewGuid(), ApprovedByName = "Independent Approver",
            ApprovedAtUtc = DateTime.UtcNow, GlPostedAtUtc = DateTime.UtcNow, GlPeriod = "2026-06",
        };
        var run = new PayrollRun
        {
            TenantId = tenantId, CompanyId = company.Id, Year = 2026, Month = 6, Status = "Draft",
            RunType = PayrollRunTypes.OffCycle, IncludesRecurringPay = false, SettlesFinalSettlements = true,
            CreatedAtUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.AddRange(offboarding, settlement, run);
        db.FinalSettlementLines.Add(new FinalSettlementLine
        {
            TenantId = tenantId, SettlementId = settlement.Id, ComponentCode = FinalSettlementComponents.Gratuity,
            ComponentName = "End-of-service gratuity", LineType = FinalSettlementLineTypes.Earning,
            Source = FinalSettlementComponents.SettlementSource, Amount = gross, SortOrder = 0,
        });
        // The live accrual the approval posted: 2320 credited at the gross.
        db.FinanceGlEntries.Add(new FinanceGlEntry
        {
            TenantId = tenantId, CompanyId = company.Id, SourceModule = FinalSettlementGlDescriptions.SourceModule,
            SourceEntityId = settlement.Id, SourceEntityRef = FinalSettlementGlDescriptions.SettlementRef(settlement.Id),
            EventType = GlEventTypes.SettlementAccrual, DebitAccount = string.Empty,
            CreditAccount = "2320 - Final Settlement Payable", Amount = gross, Currency = "SAR",
            EntryDate = new DateOnly(2026, 6, 1), Period = "2026-06",
            Description = FinalSettlementGlDescriptions.AccrualPrefix + settlement.EmployeeCode,
        });
        db.PayrollRunEmployeeSelections.Add(new PayrollRunEmployeeSelection
        {
            TenantId = tenantId, CompanyId = company.Id, PayrollRunId = run.Id, EmployeeId = employee.Id,
            Mode = PayrollRunSelectionModes.Include,
        });
        await db.SaveChangesAsync();
        return new World(tenantId, run.Id, offboarding.Id, settlement.Id, gross);
    }

    // ── Choreography ───────────────────────────────────────────────────────────────────────────────

    private sealed class Signal
    {
        private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Set() => _tcs.TrySetResult();
        /// <summary>Bounded, so a broken choreography fails the assertions instead of hanging the suite.</summary>
        public Task WaitAsync() => Task.WhenAny(_tcs.Task, Task.Delay(TimeSpan.FromSeconds(20)));
    }

    /// <summary>
    /// Hooks the run's settlement claim: <c>beforeClaim</c> runs once before the run's first command that
    /// claims the selected settlements (the offboarding lock, or on the old code the unlocked settlement
    /// re-read); <c>afterClaim</c> runs once after the settlement claim itself has executed, while the run's
    /// transaction is still open.
    /// </summary>
    private sealed class RunGate(Func<Task>? beforeClaim = null, Func<Task>? afterClaim = null) : DbCommandInterceptor
    {
        private int _before, _after;

        private static bool IsClaim(DbCommand c) =>
            c.CommandText.Contains("disbursedIds", StringComparison.Ordinal)
            || c.CommandText.Contains("disbursedOffboardingIds", StringComparison.Ordinal);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (beforeClaim is not null && IsClaim(command) && Interlocked.Exchange(ref _before, 1) == 0)
                await beforeClaim();
            return result;
        }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (afterClaim is not null && command.CommandText.Contains("disbursedIds", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _after, 1) == 0)
                await afterClaim();
            return result;
        }
    }
}
