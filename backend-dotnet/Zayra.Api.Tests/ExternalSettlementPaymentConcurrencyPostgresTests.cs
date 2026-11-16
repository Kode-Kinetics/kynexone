using System.Data.Common;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Recording a final settlement as paid outside payroll posts the discharge journal (DR payable /
/// CR cash) that closes the Final Settlement Payable. It read the settlement without a lock and wrote in
/// two separate saves, so two recordings that overlapped each saw an Approved, uncleared settlement and
/// each posted the discharge: the payable was cleared twice and cash credited twice. It now runs as one
/// transaction that starts by locking the settlement row and re-checks it under the lock, and a retried
/// commit is recognised by its own audit row instead of being posted again.
///
/// <para>Real Postgres, production's retrying strategy and the FOR UPDATE rewrite: the in-memory
/// provider has neither locks nor transactions, so it cannot show this.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class ExternalSettlementPaymentConcurrencyPostgresTests
{
    private const decimal Gross = 60_000m;
    private const decimal Deductions = 1_000m;
    private const string Payable = "2320 - Final Settlement Payable";

    private readonly PostgresFixture _fixture;
    public ExternalSettlementPaymentConcurrencyPostgresTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task TwoConcurrentRecordings_PostTheDischargeExactlyOnce()
    {
        var seed = await SeedApprovedSettlementAsync();
        // Both requests are held at their first save until the other arrives (or 3 s pass). Without the
        // row lock both reach it having read the same Approved, uncleared settlement; with it, the second
        // is still waiting on the lock, so the first goes on alone after the timeout.
        var rendezvous = new SaveRendezvous(participants: 2, timeout: TimeSpan.FromSeconds(3));

        async Task<IActionResult> RecordAsync(string reference)
        {
            await using var db = CreateDb(rendezvous.For());
            return await Controller(db, seed.TenantId, Guid.NewGuid()).RecordExternalSettlementPayment(
                seed.OffboardingId,
                new ExternalSettlementPaymentRequest("BankTransfer", reference, Gross - Deductions, null),
                CancellationToken.None);
        }

        var results = await Task.WhenAll(RecordAsync("TRF-RACE-A"), RecordAsync("TRF-RACE-B"));

        await AssertDischargedOnceAsync(seed);
        Assert.Single(results, r => r is OkObjectResult);
        // The loser re-reads the settlement under the lock, finds it Paid, and is refused.
        var refused = Assert.IsType<UnprocessableEntityObjectResult>(Assert.Single(results, r => r is not OkObjectResult));
        Assert.Contains("settlement_not_approved", System.Text.Json.JsonSerializer.Serialize(refused.Value));
    }

    [Fact]
    public async Task ACommitWhoseAcknowledgementIsLost_IsNotPostedAgainOnRetry()
    {
        var seed = await SeedApprovedSettlementAsync();
        var fault = new ThrowOnceAfterCommitInterceptor();

        IActionResult result;
        await using (var db = CreateDb(fault))
        {
            result = await Controller(db, seed.TenantId, Guid.NewGuid()).RecordExternalSettlementPayment(
                seed.OffboardingId,
                new ExternalSettlementPaymentRequest("BankTransfer", "TRF-RETRY", Gross - Deductions, null),
                CancellationToken.None);
        }

        Assert.Equal(1, fault.InjectedFaults);
        Assert.IsType<OkObjectResult>(result);
        await AssertDischargedOnceAsync(seed);
    }

    /// <summary>
    /// The leave-balance rows the settlement's encashment lines name are read FOR UPDATE inside the recording's transaction, so a
    /// payroll run or an encashment approval writing the same rows waits instead of overwriting Encashed with a stale value.
    /// </summary>
    [Fact]
    public async Task Recording_ReadsTheEncashedLeaveBalanceRowsForUpdate()
    {
        var seed = await SeedApprovedSettlementAsync();
        Guid balanceId;
        await using (var db = _fixture.CreateDb())
        {
            var settlement = await db.EmployeeFinalSettlements.SingleAsync(x => x.Id == seed.SettlementId);
            var row = new EmployeeLeaveBalance
            {
                TenantId = seed.TenantId, EmployeeId = settlement.EmployeeId, EmployeeName = settlement.EmployeeName,
                LeaveTypeId = Guid.NewGuid(), LeaveTypeName = "Annual leave", Year = 2026, Accrued = 12.25m, Used = 3m,
            };
            db.EmployeeLeaveBalances.Add(row);
            db.FinalSettlementLines.Add(new FinalSettlementLine
            {
                TenantId = seed.TenantId, SettlementId = settlement.Id, ComponentCode = FinalSettlementComponents.LeaveEncashment,
                ComponentName = "Leave encashment — Annual leave", LineType = FinalSettlementLineTypes.Earning,
                Source = FinalSettlementComponents.SettlementSource, Amount = 2_775m, Quantity = 9.25m, SourceEntityId = row.Id,
            });
            await db.SaveChangesAsync();
            balanceId = row.Id;
        }

        var capture = new SqlCapture();
        await using (var db = CreateDb(capture))
        {
            var result = await Controller(db, seed.TenantId, Guid.NewGuid()).RecordExternalSettlementPayment(
                seed.OffboardingId, new ExternalSettlementPaymentRequest("BankTransfer", "TRF-LOCK", Gross - Deductions, null),
                CancellationToken.None);
            Assert.IsType<OkObjectResult>(result);
        }

        var balanceReads = capture.Commands
            .Where(c => c.Contains("FROM employee_leave_balances", StringComparison.Ordinal) && c.Contains("SELECT", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(balanceReads);
        Assert.All(balanceReads, c => Assert.Contains("FOR UPDATE", c, StringComparison.Ordinal));
        await using var verify = _fixture.CreateDb();
        Assert.Equal(9.25m, (await verify.EmployeeLeaveBalances.AsNoTracking().SingleAsync(x => x.Id == balanceId)).Encashed);
    }

    /// <summary>Records every reader command's text, after <see cref="RowLockingInterceptor"/> has rewritten it.</summary>
    private sealed class SqlCapture : DbCommandInterceptor
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Commands { get; } = new();
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Enqueue(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private async Task AssertDischargedOnceAsync(Seed seed)
    {
        await using var verify = _fixture.CreateDb();
        var discharge = await verify.FinanceGlEntries.AsNoTracking()
            .Where(x => x.TenantId == seed.TenantId && x.SourceEntityId == seed.SettlementId
                     && x.EventType == GlEventTypes.SettlementExternalPayment)
            .ToListAsync();
        // One discharge debits the payable by the gross, once; cash is credited the net, once.
        Assert.Equal(Gross, discharge.Where(x => x.DebitAccount == Payable).Sum(x => x.Amount));
        Assert.Equal(Gross - Deductions, discharge.Where(x => x.CreditAccount.Contains("Cash")).Sum(x => x.Amount));

        var settlement = await verify.EmployeeFinalSettlements.AsNoTracking().SingleAsync(x => x.Id == seed.SettlementId);
        Assert.Equal(FinalSettlementStatuses.Paid, settlement.Status);
        Assert.True(settlement.PaidOutsidePayroll);
        Assert.Equal(1, await verify.AuditLogs.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(x => x.TenantId == seed.TenantId && x.EntityId == seed.SettlementId.ToString()
                          && x.Action == "payroll.final_settlement.paid_outside_payroll"));
        Assert.True((await verify.EmployeeOffboardings.AsNoTracking().SingleAsync(x => x.Id == seed.OffboardingId))
            .FinalSettlementDone);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────────────────────────────

    private sealed record Seed(Guid TenantId, Guid OffboardingId, Guid SettlementId);

    private async Task<Seed> SeedApprovedSettlementAsync()
    {
        await using var db = _fixture.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var company = new Company
        {
            Id = Guid.NewGuid(), TenantId = tenantId, LegalNameEn = "Settlement Race Co",
            TradeName = "Race", CountryCode = "SA", DefaultCurrency = "SAR",
        };
        db.Companies.Add(company);
        var employee = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "RACE-001", FullName = "Leaving Employee",
            Status = "Offboarded", JoiningDate = new DateTime(2019, 3, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        var lastDay = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
        var offboarding = new EmployeeOffboarding
        {
            TenantId = tenantId, EmployeeId = employee.Id, EmployeeCode = employee.EmployeeCode,
            EmployeeName = employee.FullName, SeparationType = "Resignation", Status = "InProgress",
            NoticeDate = lastDay.AddDays(-30), NoticePeriodDays = 30, LastWorkingDay = lastDay,
        };
        db.EmployeeOffboardings.Add(offboarding);
        var settlement = new EmployeeFinalSettlement
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeId = employee.Id,
            EmployeeCode = employee.EmployeeCode, EmployeeName = employee.FullName, OffboardingId = offboarding.Id,
            LastWorkingDay = lastDay, ServiceStartDate = new DateOnly(2019, 3, 1), SettlementDueDate = lastDay,
            TerminationReason = "Resignation", Currency = "SAR",
            GrossPayable = Gross, TotalDeductions = Deductions, NetPayable = Gross - Deductions,
            Status = FinalSettlementStatuses.Approved,
            ApprovedByUserId = Guid.NewGuid(), ApprovedByName = "Finance Approver", ApprovedAtUtc = DateTime.UtcNow,
            GlPostedAtUtc = DateTime.UtcNow, GlPeriod = $"{DateTime.UtcNow:yyyy-MM}",
        };
        db.EmployeeFinalSettlements.Add(settlement);
        db.FinanceGlEntries.Add(new FinanceGlEntry
        {
            TenantId = tenantId, CompanyId = company.Id,
            SourceModule = FinalSettlementGlDescriptions.SourceModule,
            SourceEntityId = settlement.Id,
            SourceEntityRef = FinalSettlementGlDescriptions.SettlementRef(settlement.Id),
            EventType = GlEventTypes.SettlementAccrual,
            DebitAccount = string.Empty, CreditAccount = Payable,
            Amount = Gross, Currency = "SAR",
            EntryDate = DateOnly.FromDateTime(DateTime.UtcNow), Period = $"{DateTime.UtcNow:yyyy-MM}",
            Description = FinalSettlementGlDescriptions.AccrualPrefix + employee.EmployeeCode,
        });
        await db.SaveChangesAsync();
        return new Seed(tenantId, offboarding.Id, settlement.Id);
    }

    /// <summary>The fixture's production provider configuration, plus one test interceptor.</summary>
    private ZayraDbContext CreateDb(IInterceptor interceptor) => new(
        new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(_fixture.ConnectionString, options => options.EnableRetryOnFailure(
                maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(5), errorCodesToAdd: null))
            .AddInterceptors(RowLockingInterceptor.Instance, interceptor)
            .Options);

    private static OffboardingController Controller(ZayraDbContext db, Guid tenantId, Guid actorId) =>
        new(db, new AuditService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("tenant_id", tenantId.ToString()),
                        new Claim(ClaimTypes.NameIdentifier, actorId.ToString()),
                        new Claim(ClaimTypes.Name, "Finance Recorder"),
                        new Claim("permission", "payroll.approve"),
                    }, "test")),
                },
            },
        };

    /// <summary>
    /// Holds each participating context at its first SaveChanges until every participant has arrived,
    /// or until the timeout, whichever is first. Each context gets its own interceptor instance.
    /// </summary>
    private sealed class SaveRendezvous(int participants, TimeSpan timeout)
    {
        private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public IInterceptor For() => new Participant(this);

        private async Task ArriveAsync()
        {
            if (Interlocked.Increment(ref _arrived) == participants) _allArrived.TrySetResult();
            await Task.WhenAny(_allArrived.Task, Task.Delay(timeout));
        }

        private sealed class Participant(SaveRendezvous owner) : SaveChangesInterceptor
        {
            private int _waited;

            public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
                DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
            {
                if (Interlocked.Exchange(ref _waited, 1) == 0) await owner.ArriveAsync();
                return result;
            }
        }
    }

    /// <summary>The first commit succeeds and then reports a timeout, as a dropped acknowledgement would.</summary>
    private sealed class ThrowOnceAfterCommitInterceptor : DbTransactionInterceptor
    {
        private int _armed = 1;
        private int _injectedFaults;

        public int InjectedFaults => Volatile.Read(ref _injectedFaults);

        public override Task TransactionCommittedAsync(
            DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _armed, 0) == 1)
            {
                Interlocked.Increment(ref _injectedFaults);
                throw new TimeoutException("Simulated lost commit acknowledgement.");
            }
            return Task.CompletedTask;
        }
    }
}
