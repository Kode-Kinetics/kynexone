using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>Cash must move only on evidenced payout/receipt, independently of approval and payroll.</summary>
public class StandaloneLoanWorkflowTests
{
    [Theory]
    [InlineData("Weekly")]
    [InlineData("BiWeekly")]
    [InlineData("Quarterly")]
    public async Task NonmonthlyLoanType_RefusesPayrollDeduction_AndAcceptsStandaloneRepayment(string frequency)
    {
        await using var h = await Harness.Create();
        var type = await h.Db.LoanTypes.SingleAsync();
        type.RepaymentFrequency = frequency;
        await h.Db.SaveChangesAsync();
        var employee = await h.Db.Employees.SingleAsync();
        var request = new CreateLoanRequest(employee.PublicId, employee.FullName, type.Id, 100m, 3, null, employee.Id, "PayrollDeduction");
        Assert.IsType<BadRequestObjectResult>(await h.Maker.CreateLoan(request, CancellationToken.None));
        Assert.Empty(await h.Db.EmployeeLoans.ToListAsync());
        Assert.Empty(await h.Db.LoanApprovals.ToListAsync());
        Assert.Empty(await h.Db.LoanAuditLogs.ToListAsync());

        Assert.IsType<OkObjectResult>(await h.Maker.CreateLoan(request with { RepaymentMethod = "BankTransfer" }, CancellationToken.None));
        var loan = await h.Db.EmployeeLoans.SingleAsync();
        Assert.Equal("BankTransfer", loan.RepaymentMethod);
        Assert.Equal(frequency, loan.RepaymentFrequency);
        Assert.Equal("Pending", loan.Status);
    }

    [Fact]
    public async Task RequestAndApproval_DefaultToStandalone_AndDoNotMoveCash()
    {
        await using var h = await Harness.Create();
        var loan = await h.Request();
        Assert.Equal("BankTransfer", loan.RepaymentMethod);
        var step = await h.Db.LoanApprovals.SingleAsync(x => x.LoanId == loan.Id);
        Assert.Equal("Pending", step.Status);
        Assert.IsType<OkObjectResult>(await h.Checker.DecideApproval(loan.Id, step.Id,
            new ApprovalDecisionRequest("Approved", "approved", null, null, null), CancellationToken.None));

        Assert.Equal("Approved", loan.Status);
        Assert.Equal(100m, loan.ApprovedAmount);
        Assert.Equal(0m, loan.OutstandingBalance);
        Assert.Null(loan.DisbursementDate);
        Assert.Empty(await h.Db.LoanInstallments.ToListAsync());
        Assert.Empty(await h.Db.FinanceGlEntries.ToListAsync());
        Assert.NotEmpty(await h.Db.LoanAuditLogs.Where(x => x.LoanId == loan.Id).ToListAsync());
    }

    [Fact]
    public async Task LegacyAutomaticType_StillRequiresHrApprovalAndSeparateCashDisbursement()
    {
        await using var h = await Harness.Create(requiresApproval: false);
        var loan = await h.Request();
        Assert.Equal("Pending", loan.Status);
        Assert.Equal(0m, loan.OutstandingBalance);
        Assert.Null(loan.DisbursementDate);
        Assert.Empty(await h.Db.LoanInstallments.ToListAsync());
        Assert.Empty(await h.Db.FinanceGlEntries.ToListAsync());
    }

    [Fact]
    public async Task Batch_RequiresIndependentApproval_ThenPaysOnceWithExactSchedule()
    {
        await using var h = await Harness.Create(requiresApproval: false);
        var loan = await h.Request();
        var batchId = await h.Batch(loan);
        Assert.IsNotType<OkObjectResult>(await h.Maker.ConfirmPaymentBatchPaid(batchId,
            new ConfirmLoanPaymentRequest("BANK-1", h.Today), CancellationToken.None));
        Assert.IsNotType<OkObjectResult>(await h.Maker.ApprovePaymentBatch(batchId, CancellationToken.None));
        Assert.IsType<OkObjectResult>(await h.Checker.ApprovePaymentBatch(batchId, CancellationToken.None));
        Assert.Empty(await h.Db.FinanceGlEntries.ToListAsync());

        Assert.IsType<OkObjectResult>(await h.Checker.ConfirmPaymentBatchPaid(batchId,
            new ConfirmLoanPaymentRequest("BANK-1", h.Today), CancellationToken.None));
        var active = await h.Db.EmployeeLoans.SingleAsync(x => x.Id == loan.Id);
        Assert.Equal("Active", active.Status);
        Assert.Equal(100m, active.OutstandingBalance);
        Assert.Equal(h.Today, active.DisbursementDate);
        var installments = await h.Db.LoanInstallments.OrderBy(x => x.InstallmentNumber).ToListAsync();
        Assert.Equal(3, installments.Count);
        Assert.Equal(100m, installments.Sum(x => x.AmountDue));
        Assert.All(installments, x => Assert.Equal(decimal.Round(x.AmountDue, 2), x.AmountDue));
        var gl = Assert.Single(await h.Db.FinanceGlEntries.ToListAsync());
        Assert.Equal("Disbursement", gl.EventType);
        Assert.Equal(100m, gl.Amount);
        Assert.Equal(h.Today, gl.EntryDate);

        await h.Checker.ConfirmPaymentBatchPaid(batchId, new ConfirmLoanPaymentRequest("BANK-1", h.Today), CancellationToken.None);
        Assert.Single(await h.Db.FinanceGlEntries.ToListAsync());
        Assert.Equal(3, await h.Db.LoanInstallments.CountAsync());
        Assert.IsNotType<OkObjectResult>(await h.Maker.CreatePaymentBatch(new CreateLoanPaymentBatchRequest(new[] { loan.Id }), CancellationToken.None));
    }

    [Fact]
    public async Task Receipts_AccumulatePartials_ReplayOnce_AndRefuseConflictingReferenceOrOverpayment()
    {
        await using var h = await Harness.Create(requiresApproval: false);
        var loan = await h.ActiveLoan();
        var payment = new RecordLoanRepaymentRequest(10m, h.Today, "RECEIPT-1");
        Assert.IsType<OkObjectResult>(await h.Checker.RecordRepayment(loan.Id, payment, CancellationToken.None));
        await h.Checker.RecordRepayment(loan.Id, payment, CancellationToken.None);
        Assert.Equal(10m, loan.TotalRepaid);
        Assert.Equal(90m, loan.OutstandingBalance);
        Assert.Single(await h.Db.Set<LoanRepayment>().ToListAsync());
        Assert.Single(await h.Db.FinanceGlEntries.Where(x => x.EventType == "Repayment").ToListAsync());
        Assert.Equal(10m, await h.Db.LoanInstallments.SumAsync(x => x.AmountPaid));

        Assert.IsNotType<OkObjectResult>(await h.Checker.RecordRepayment(loan.Id,
            new RecordLoanRepaymentRequest(11m, h.Today, "RECEIPT-1"), CancellationToken.None));
        Assert.IsNotType<OkObjectResult>(await h.Checker.RecordRepayment(loan.Id,
            new RecordLoanRepaymentRequest(91m, h.Today, "TOO-MUCH"), CancellationToken.None));
        Assert.IsType<OkObjectResult>(await h.Checker.RecordRepayment(loan.Id,
            new RecordLoanRepaymentRequest(15m, h.Today, "RECEIPT-2"), CancellationToken.None));
        Assert.Equal(25m, loan.TotalRepaid);
        Assert.Equal(25m, await h.Db.LoanInstallments.SumAsync(x => x.AmountPaid));
        Assert.Equal(75m, loan.OutstandingBalance);

        Assert.IsType<OkObjectResult>(await h.Checker.RecordRepayment(loan.Id,
            new RecordLoanRepaymentRequest(75m, h.Today, "RECEIPT-FINAL"), CancellationToken.None));
        Assert.Equal("Settled", loan.Status);
        Assert.Equal(0m, loan.OutstandingBalance);
        Assert.Equal(100m, loan.TotalRepaid);
        Assert.All(await h.Db.LoanInstallments.ToListAsync(), x => Assert.Equal(x.AmountDue, x.AmountPaid));
        Assert.All(await h.Db.LoanInstallments.ToListAsync(), x => Assert.Null(x.PayrollRunId));
    }

    [Fact]
    public async Task CancelledBatch_ReleasesReservation_AndCannotBePaid()
    {
        await using var h = await Harness.Create(requiresApproval: false);
        var loan = await h.Request();
        var batchId = await h.Batch(loan);
        Assert.IsType<OkObjectResult>(await h.Checker.ApprovePaymentBatch(batchId, CancellationToken.None));
        Assert.IsType<OkObjectResult>(await h.Maker.CancelPaymentBatch(batchId, CancellationToken.None));
        Assert.IsType<ConflictObjectResult>(await h.Checker.ConfirmPaymentBatchPaid(batchId,
            new ConfirmLoanPaymentRequest("CANCELLED-PAYOUT", h.Today), CancellationToken.None));
        Assert.IsType<OkObjectResult>(await h.Maker.CreatePaymentBatch(
            new CreateLoanPaymentBatchRequest(new[] { loan.Id }), CancellationToken.None));
        Assert.Equal(2, await h.Db.Set<LoanDisbursementBatch>().CountAsync());
        Assert.Equal(1, await h.Db.Set<LoanDisbursementLine>().CountAsync(x => !x.IsCancelled));
        Assert.Equal("Approved", loan.Status);
        Assert.Empty(await h.Db.FinanceGlEntries.ToListAsync());
    }

    [Fact]
    public async Task BankPayout_RequiresFrozenBankInstructions_AndOpenAccountingPeriod()
    {
        await using var h = await Harness.Create(requiresApproval: false);
        var loan = await h.Request();
        var batchId = await h.Batch(loan);
        Assert.IsType<OkObjectResult>(await h.Checker.ApprovePaymentBatch(batchId, CancellationToken.None));
        var line = await h.Db.Set<LoanDisbursementLine>().SingleAsync();
        line.Iban = "";
        await h.Db.SaveChangesAsync();
        Assert.IsNotType<OkObjectResult>(await h.Checker.ConfirmPaymentBatchPaid(batchId,
            new ConfirmLoanPaymentRequest("NO-BANK", h.Today), CancellationToken.None));
        line.Iban = "SA4420000001234567891234";
        h.Db.GlPeriodCloses.Add(new GlPeriodClose
        {
            TenantId = h.TenantId, CompanyId = loan.CompanyId, Period = h.Today.ToString("yyyy-MM"), Status = GlPeriodStatuses.Closed
        });
        await h.Db.SaveChangesAsync();
        Assert.IsType<UnprocessableEntityObjectResult>(await h.Checker.ConfirmPaymentBatchPaid(batchId,
            new ConfirmLoanPaymentRequest("CLOSED-PERIOD", h.Today), CancellationToken.None));
        Assert.Equal("Approved", loan.Status);
        Assert.Equal(0m, loan.OutstandingBalance);
        Assert.Empty(await h.Db.FinanceGlEntries.ToListAsync());
        Assert.Empty(await h.Db.LoanInstallments.ToListAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1.001)]
    public async Task Receipt_RefusesNonpositiveOrSubcentAmounts(decimal amount)
    {
        await using var h = await Harness.Create(requiresApproval: false);
        var loan = await h.ActiveLoan();
        Assert.IsNotType<OkObjectResult>(await h.Checker.RecordRepayment(loan.Id,
            new RecordLoanRepaymentRequest(amount, h.Today, "INVALID"), CancellationToken.None));
        Assert.Equal(100m, loan.OutstandingBalance);
        Assert.Empty(await h.Db.Set<LoanRepayment>().ToListAsync());
    }

    [Fact]
    public async Task Receipt_AndBatchRefusePendingOrRejectedLoan()
    {
        await using var h = await Harness.Create();
        var loan = await h.Request();
        Assert.IsNotType<OkObjectResult>(await h.Maker.CreatePaymentBatch(new CreateLoanPaymentBatchRequest(new[] { loan.Id }), CancellationToken.None));
        Assert.IsNotType<OkObjectResult>(await h.Checker.RecordRepayment(loan.Id,
            new RecordLoanRepaymentRequest(10m, h.Today, "PREMATURE"), CancellationToken.None));
        var step = await h.Db.LoanApprovals.SingleAsync();
        Assert.IsType<OkObjectResult>(await h.Checker.DecideApproval(loan.Id, step.Id,
            new ApprovalDecisionRequest("Rejected", "declined", null, null, null), CancellationToken.None));
        Assert.IsNotType<OkObjectResult>(await h.Maker.CreatePaymentBatch(new CreateLoanPaymentBatchRequest(new[] { loan.Id }), CancellationToken.None));
        Assert.Empty(await h.Db.FinanceGlEntries.ToListAsync());
    }

    [Fact]
    public async Task TenantAndEmployeeScope_PreventReceiptAndInstallmentAccess()
    {
        await using var h = await Harness.Create(requiresApproval: false);
        var loan = await h.ActiveLoan();
        var outsider = MakeController(h.Db, Guid.NewGuid(), Guid.NewGuid());
        Assert.IsType<NotFoundResult>(await outsider.RecordRepayment(loan.Id,
            new RecordLoanRepaymentRequest(10m, h.Today, "FOREIGN"), CancellationToken.None));
        var restricted = MakeController(h.Db, h.TenantId, Guid.NewGuid(), restricted: true);
        Assert.IsType<ForbidResult>(await restricted.RecordRepayment(loan.Id,
            new RecordLoanRepaymentRequest(10m, h.Today, "OUT-OF-SCOPE"), CancellationToken.None));
        Assert.IsType<ForbidResult>(await restricted.GetInstallments(loan.Id, CancellationToken.None));
        Assert.Equal(100m, loan.OutstandingBalance);
        Assert.Empty(await h.Db.Set<LoanRepayment>().ToListAsync());
    }

    [Fact]
    public async Task Approval_RejectsUnrepresentableRepaymentStartWithoutRecordingDecision()
    {
        await using var h = await Harness.Create();
        var loan = await h.Request();
        var step = await h.Db.LoanApprovals.SingleAsync();
        Assert.IsType<BadRequestObjectResult>(await h.Checker.DecideApproval(loan.Id, step.Id,
            new ApprovalDecisionRequest("Approved", null, null, null, new DateOnly(9999, 12, 31)), CancellationToken.None));
        Assert.Equal("Pending", step.Status);
        Assert.Equal("Pending", loan.Status);
        Assert.Empty(await h.Db.FinanceGlEntries.ToListAsync());
    }

    [Fact]
    public async Task CompanyFilteredDbContext_HidesForeignCompanyBatchesAndBlocksEveryPaymentMutation()
    {
        await using var h = await Harness.Create(requiresApproval: false);
        var loan = await h.Request();
        var batchId = await h.Batch(loan);
        Assert.IsType<OkObjectResult>(await h.Checker.ApprovePaymentBatch(batchId, CancellationToken.None));
        var otherCompany = new Company { TenantId = h.TenantId, LegalNameEn = "Other company", CountryCode = "SAU", DefaultCurrency = "SAR" };
        h.Db.Companies.Add(otherCompany);
        await h.Db.SaveChangesAsync();
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("tenant_id", h.TenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim(ClaimTypes.Role, "Finance"),
                new Claim("entity_access", JsonSerializer.Serialize(new { c = otherCompany.Id, r = "Finance" }))
            }, "test"))
        };
        await using var scopedDb = new ZayraDbContext(h.Options, new HttpContextAccessor { HttpContext = http });
        var scoped = new LoansController(scopedDb, new Scope(false))
        { ControllerContext = new ControllerContext { HttpContext = http } };
        Assert.Empty(await scopedDb.Set<LoanDisbursementBatch>().ToListAsync());
        Assert.IsType<NotFoundResult>(await scoped.GetPaymentBatch(batchId, CancellationToken.None));
        Assert.IsType<NotFoundResult>(await scoped.ExportPaymentBatch(batchId, CancellationToken.None));
        Assert.IsType<NotFoundResult>(await scoped.ApprovePaymentBatch(batchId, CancellationToken.None));
        Assert.IsType<NotFoundResult>(await scoped.CancelPaymentBatch(batchId, CancellationToken.None));
        Assert.IsType<NotFoundResult>(await scoped.ConfirmPaymentBatchPaid(batchId,
            new ConfirmLoanPaymentRequest("CROSS-COMPANY", h.Today), CancellationToken.None));
        Assert.IsType<NotFoundObjectResult>(await scoped.CreatePaymentBatch(
            new CreateLoanPaymentBatchRequest(new[] { loan.Id }), CancellationToken.None));
        Assert.IsType<NotFoundResult>(await scoped.RecordRepayment(loan.Id,
            new RecordLoanRepaymentRequest(10m, h.Today, "CROSS-COMPANY"), CancellationToken.None));
        Assert.Equal("Approved", (await h.Db.Set<LoanDisbursementBatch>().SingleAsync()).Status);
        Assert.Empty(await h.Db.FinanceGlEntries.ToListAsync());
    }

    private static LoansController MakeController(ZayraDbContext db, Guid tenant, Guid user, bool restricted = false)
        => new(db, new Scope(restricted))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("tenant_id", tenant.ToString()), new Claim(ClaimTypes.NameIdentifier, user.ToString()),
                        new Claim("permission", "loans.read"),
                        new Claim(ClaimTypes.Name, "Finance Reviewer"), new Claim(ClaimTypes.Role, "Finance"), new Claim(ClaimTypes.Role, "HR Manager")
                    }, "test"))
                }
            }
        };

    private sealed class Scope(bool restricted) : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
            => Task.FromResult(restricted
                ? new DataScope { Level = DataScopeLevel.Own, AllowedEmployeeIds = new HashSet<int>() }
                : new DataScope { Level = DataScopeLevel.Organization });
    }

    private sealed class Harness : IAsyncDisposable
    {
        public DbContextOptions<ZayraDbContext> Options { get; } = new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        public ZayraDbContext Db { get; }
        private Harness() => Db = new ZayraDbContext(Options);
        public Guid TenantId { get; } = Guid.NewGuid();
        private Guid MakerId { get; } = Guid.NewGuid();
        private Guid CheckerId { get; } = Guid.NewGuid();
        private Employee Employee { get; set; } = null!;
        private LoanType Type { get; set; } = null!;
        public DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);
        public LoansController Maker => MakeController(Db, TenantId, MakerId);
        public LoansController Checker => MakeController(Db, TenantId, CheckerId);

        public static async Task<Harness> Create(bool requiresApproval = true)
        {
            var h = new Harness();
            var company = new Company
            {
                TenantId = h.TenantId, LegalNameEn = "Loan Finance Co", DefaultCurrency = "SAR",
                CountryCode = "SAU", Jurisdiction = "KSA-mainland", IsActive = true
            };
            h.Employee = new Employee
            {
                TenantId = h.TenantId, CompanyId = company.Id, EmployeeCode = "LOAN-1",
                FullName = "Borrower", EnglishName = "Borrower", Status = EmployeeStatuses.Active,
                BankName = "Test Bank", BankIban = "SA4420000001234567891234", JoiningDate = DateTime.UtcNow.AddYears(-2)
            };
            h.Type = new LoanType
            {
                TenantId = h.TenantId, Code = "PERSONAL", NameEn = "Personal", MaxAmount = 10_000m,
                MaxInstallments = 12, RequiresApproval = requiresApproval, IsActive = true
            };
            h.Db.AddRange(company, h.Employee, h.Type);
            await h.Db.SaveChangesAsync();
            return h;
        }

        public async Task<EmployeeLoan> Request()
        {
            Assert.IsType<OkObjectResult>(await Maker.CreateLoan(new CreateLoanRequest(Employee.PublicId,
                Employee.FullName, Type.Id, 100m, 3, null, Employee.Id), CancellationToken.None));
            return await Db.EmployeeLoans.SingleAsync();
        }

        public async Task<Guid> Batch(EmployeeLoan loan)
        {
            if (loan.Status == "Pending")
            {
                var step = await Db.LoanApprovals.SingleAsync(x => x.LoanId == loan.Id);
                Assert.IsType<OkObjectResult>(await Checker.DecideApproval(loan.Id, step.Id,
                    new ApprovalDecisionRequest("Approved", "HR approval", null, null, null), CancellationToken.None));
            }
            var result = await Maker.CreatePaymentBatch(new CreateLoanPaymentBatchRequest(new[] { loan.Id }), CancellationToken.None);
            Assert.True(result is OkObjectResult or CreatedResult, result.ToString());
            return (await Db.Set<LoanDisbursementBatch>().SingleAsync()).Id;
        }

        public async Task<EmployeeLoan> ActiveLoan()
        {
            var loan = await Request();
            var batchId = await Batch(loan);
            Assert.IsType<OkObjectResult>(await Checker.ApprovePaymentBatch(batchId, CancellationToken.None));
            Assert.IsType<OkObjectResult>(await Checker.ConfirmPaymentBatchPaid(batchId,
                new ConfirmLoanPaymentRequest("BANK-PAYOUT", Today), CancellationToken.None));
            return loan;
        }
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
