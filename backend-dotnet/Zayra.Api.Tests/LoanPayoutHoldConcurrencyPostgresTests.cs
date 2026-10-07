using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

[Trait("Category", "Integration")]
[Collection("Integration")]
public class LoanPayoutHoldConcurrencyPostgresTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PayoutWaitingForLoanLockHonorsHrHoldCommittedBeforeLockAcquisition(bool individualLine)
    {
        var (tid, loanId, batchId, lineId) = await SeedApprovedBatch();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var payoutDb = fixture.CreateDb();
        await payoutDb.Database.OpenConnectionAsync(timeout.Token);
        var payoutPid = ((NpgsqlConnection)payoutDb.Database.GetDbConnection()).ProcessID;
        await using var hrDb = fixture.CreateDb();

        await hrDb.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var hold = await hrDb.Database.BeginTransactionAsync(timeout.Token);
            await FinanceDecisionSerializer.AcquireAsync(hrDb, FinanceDecisionSerializer.ScopeLoan, tid, loanId, timeout.Token);
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var controller = Controller(payoutDb, tid);
            var payout = individualLine
                ? controller.RecordPaymentLineOutcome(batchId, lineId, new("Paid", "HELD-PAYOUT", today), timeout.Token)
                : controller.ConfirmPaymentBatchPaid(batchId, new("HELD-PAYOUT", today), timeout.Token);

            // Observe this exact payout connection at the advisory lock. This proves its
            // authorization read happened before HR changes the loan, without a timing race.
            var waiting = false;
            await using (var observer = new NpgsqlConnection(fixture.ConnectionString))
            {
                await observer.OpenAsync(timeout.Token);
                for (var attempt = 0; attempt < 200 && !waiting; attempt++)
                {
                    await using var command = new NpgsqlCommand("""
                        SELECT EXISTS (
                            SELECT 1 FROM pg_stat_activity
                            WHERE pid = @pid AND datname = current_database()
                              AND wait_event_type = 'Lock' AND wait_event = 'advisory'
                        )
                        """, observer);
                    command.Parameters.AddWithValue("pid", payoutPid);
                    waiting = (bool)(await command.ExecuteScalarAsync(timeout.Token))!;
                    if (!waiting) await Task.Delay(25, timeout.Token);
                }
            }

            var loan = await hrDb.EmployeeLoans.SingleAsync(x => x.TenantId == tid && x.Id == loanId, timeout.Token);
            loan.CollectionStatus = "OnHold";
            loan.ReviewRequired = true;
            loan.ReviewReason = "HR hold committed while Finance waits for the loan lock.";
            await hrDb.SaveChangesAsync(timeout.Token);
            await hold.CommitAsync(timeout.Token);
            var result = await payout;
            Assert.True(waiting, "The specific payout connection must reach the loan advisory lock before HR commits.");
            Assert.IsType<ConflictObjectResult>(result);
        });

        await using var verify = fixture.CreateDb();
        var after = await verify.EmployeeLoans.AsNoTracking().SingleAsync(x => x.Id == loanId);
        Assert.Equal("Approved", after.Status);
        Assert.Equal("OnHold", after.CollectionStatus);
        Assert.True(after.ReviewRequired);
        Assert.Null(after.DisbursementDate);
        Assert.Equal(0m, after.OutstandingBalance);
        Assert.Equal("Approved", (await verify.LoanDisbursementBatches.AsNoTracking().SingleAsync(x => x.Id == batchId)).Status);
        var line = await verify.LoanDisbursementLines.AsNoTracking().SingleAsync(x => x.Id == lineId);
        Assert.Equal("Pending", line.Status);
        Assert.Null(line.PaidDate);
        Assert.Empty(await verify.FinanceGlEntries.Where(x => x.SourceEntityId == loanId).ToListAsync());
        Assert.Empty(await verify.LoanInstallments.Where(x => x.LoanId == loanId).ToListAsync());
    }

    private async Task<(Guid Tid, Guid LoanId, Guid BatchId, Guid LineId)> SeedApprovedBatch()
    {
        await using var seed = fixture.CreateDb();
        var tid = await PostgresFixture.SeedMinimalTenant(seed);
        var company = new Company { TenantId = tid, LegalNameEn = "Loan Hold Co", CountryCode = "SAU", DefaultCurrency = "SAR", IsActive = true };
        var employee = new Employee { TenantId = tid, CompanyId = company.Id, EmployeeCode = "HOLD", FullName = "Loan Hold Borrower", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-2) };
        var type = new LoanType { TenantId = tid, Code = "HOLD", NameEn = "Hold", MaxAmount = 10000, MaxInstallments = 12 };
        seed.AddRange(company, employee, type);
        await seed.SaveChangesAsync();
        seed.EmployeePayrollProfiles.Add(new EmployeePayrollProfile { TenantId = tid, EmployeeId = employee.Id, Iban = "SA4420000001234567891234", BankName = "Hold Bank", SalaryCurrency = "SAR" });
        var loan = new EmployeeLoan
        {
            TenantId = tid, CompanyId = company.Id, EmployeeIntId = employee.Id, EmployeeId = employee.PublicId,
            EmployeeName = employee.FullName, LoanNumber = $"HOLD-{Guid.NewGuid():N}", Status = "Approved", LoanTypeId = type.Id,
            RepaymentMethod = "BankTransfer", Currency = "SAR", RequestedAmount = 100m, ApprovedAmount = 100m,
            RequestedInstallments = 3, ApprovedInstallments = 3, InstallmentAmount = 33.33m
        };
        seed.EmployeeLoans.Add(loan);
        await seed.SaveChangesAsync();
        Assert.IsType<OkObjectResult>(await Controller(seed, tid).CreatePaymentBatch(new(new[] { loan.Id }), default));
        var batch = await seed.LoanDisbursementBatches.AsNoTracking().SingleAsync(x => x.TenantId == tid);
        await using var approve = fixture.CreateDb();
        Assert.IsType<OkObjectResult>(await Controller(approve, tid).ApprovePaymentBatch(batch.Id, default));
        var line = await approve.LoanDisbursementLines.AsNoTracking().SingleAsync(x => x.BatchId == batch.Id);
        return (tid, loan.Id, batch.Id, line.Id);
    }

    private static LoansController Controller(ZayraDbContext db, Guid tid) => new(db, new Scope())
    {
        ControllerContext = new()
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[] {
                    new Claim("tenant_id", tid.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.Role, "Finance"), new Claim(ClaimTypes.Role, "HR Manager") }, "test"))
            }
        }
    };

    private sealed class Scope : IDataScopeService
    { public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization }); }
}
