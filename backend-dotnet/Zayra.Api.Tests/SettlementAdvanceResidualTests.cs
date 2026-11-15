using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// When a leaver's final-settlement payment is settled, the advance debt the settlement could not recover moves to the 1420
/// Employee Receivable (<c>PayrollController.FinalizePaidSettlementsAsync</c>). That move lowers the advance's balance without
/// being a repayment, so the advance audit must count it, or the advance reads as unreconciled for ever; it is written to the
/// advance's own audit trail; and only advances owned by the paying legal entity are moved, as for loans.
/// </summary>
public class SettlementAdvanceResidualTests
{
    [Fact]
    public async Task AdvanceMovedTo1420OnSettlement_IsReconciledInTheAdvanceAudit_AndAudited()
    {
        await using var h = await Fixture.Create();
        var advance = h.Advance(h.Company.Id);
        h.Db.Add(advance);
        h.Db.Add(h.AdvanceDisbursementGl(h.Company.Id, advance.ApprovedAmount));
        var batch = await h.SeedSettlementBatchAsync();

        Assert.IsType<OkObjectResult>(await h.Payroll().SettlePaymentBatch(batch.Id, new(), default));

        var moved = await h.Db.SalaryAdvances.AsNoTracking().SingleAsync(a => a.Id == advance.Id);
        Assert.Equal((0m, 0m, "Closed"), (moved.OutstandingBalance, moved.TotalRepaid, moved.Status));
        var row = await h.AuditRowAsync(advance.AdvanceNumber);
        Assert.True(row.GetProperty("isReconciled").GetBoolean(), row.ToString());
        Assert.Equal(0m, row.GetProperty("balanceCheck").GetDecimal());
        var audit = await h.Db.AdvanceAuditLogs.AsNoTracking().Where(x => x.AdvanceId == advance.Id).ToListAsync();
        var entry = Assert.Single(audit);
        Assert.Contains("600", entry.NewValuesJson);
    }

    [Fact]
    public async Task SettlementOfOneCompany_LeavesAnAdvanceOwnedByAnotherCompanyAlone()
    {
        await using var h = await Fixture.Create();
        // The employee moved from the prior employer; the advance is the prior employer's. The paying company's 1410 carries
        // other advances, so a settlement that ignored ownership would have the budget to move this one.
        var advance = h.Advance(h.PriorCompany.Id);
        h.Db.Add(advance);
        h.Db.Add(h.AdvanceDisbursementGl(h.Company.Id, 5_000m));
        var batch = await h.SeedSettlementBatchAsync();

        Assert.IsType<OkObjectResult>(await h.Payroll().SettlePaymentBatch(batch.Id, new(), default));

        var untouched = await h.Db.SalaryAdvances.AsNoTracking().SingleAsync(a => a.Id == advance.Id);
        Assert.Equal((600m, 0m, "Active"), (untouched.OutstandingBalance, untouched.TotalRepaid, untouched.Status));
        var settlement = await h.Db.EmployeeFinalSettlements.AsNoTracking().SingleAsync();
        Assert.Equal((0m, 0m), (settlement.ResidualDebtReclassed, settlement.ResidualDebtUnbooked));
        Assert.Empty(await h.Db.PayrollEmployeeReceivables.ToListAsync());
    }

    /// <summary>
    /// A leaver whose final pay was all taken by the debts it recovered has a zero net, so the bank batch leaves them out —
    /// there is nothing to send. Their settlement must still complete when the batch is settled, and the advance the final
    /// pay could not cover still moves to 1420; before, they were never seen, the settlement stayed Disbursing and the
    /// leftover stayed on the advance of an ex-employee.
    /// </summary>
    [Fact]
    public async Task ZeroNetLeaverLeftOutOfTheBankBatch_HasTheSettlementCompleted_AndTheLeftoverMovedTo1420()
    {
        await using var h = await Fixture.Create();
        var advance = h.Advance(h.Company.Id);
        var colleague = new Employee { TenantId = h.Tid, CompanyId = h.Company.Id, EmployeeCode = "STAYER", FullName = "Stayer", Nationality = "Indian", Status = "Active", JoiningDate = new(2024, 1, 1) };
        h.Db.AddRange(advance, colleague, h.AdvanceDisbursementGl(h.Company.Id, advance.ApprovedAmount),
            new EmployeePayrollProfile { TenantId = h.Tid, EmployeeId = h.Employee.Id, Iban = "AE070331234567890123456" });
        await h.Db.SaveChangesAsync();
        h.Db.Add(new EmployeePayrollProfile { TenantId = h.Tid, EmployeeId = colleague.Id, Iban = "AE070331234567890123457" });
        var run = new PayrollRun { TenantId = h.Tid, CompanyId = h.Company.Id, Year = 2026, Month = 1, RunType = PayrollRunTypes.OffCycle, Status = "Locked" };
        var offboarding = new EmployeeOffboarding { TenantId = h.Tid, EmployeeId = h.Employee.Id, Status = "InProgress", LastWorkingDay = new(2026, 1, 31) };
        var settlement = new EmployeeFinalSettlement
        {
            TenantId = h.Tid, CompanyId = h.Company.Id, EmployeeId = h.Employee.Id, EmployeeCode = h.Employee.EmployeeCode,
            OffboardingId = offboarding.Id, PayrollRunId = run.Id, Currency = "AED", Status = FinalSettlementStatuses.Disbursing,
        };
        h.Db.AddRange(run, offboarding, settlement,
            new PayrollSlip { TenantId = h.Tid, RunId = run.Id, EmployeeId = h.Employee.Id, EmployeeCode = h.Employee.EmployeeCode, EmployeeName = h.Employee.FullName, GrossSalary = 500, Deductions = 500, LoanDeductions = 500, NetSalary = 0 },
            new PayrollSlip { TenantId = h.Tid, RunId = run.Id, EmployeeId = colleague.Id, EmployeeCode = colleague.EmployeeCode, EmployeeName = colleague.FullName, GrossSalary = 1000, NetSalary = 1000 },
            new FinanceGlEntry { TenantId = h.Tid, CompanyId = h.Company.Id, SourceModule = "Payroll", SourceEntityId = run.Id,
                EventType = GlEventTypes.Accrual, CreditAccount = "2100 - Salaries Payable", Description = PayrollGlDescriptions.NetPayable, Amount = 1000, Currency = "AED" });
        await h.Db.SaveChangesAsync();
        Assert.IsType<CreatedResult>(await h.Payroll().CreatePaymentBatch(run.Id, new PayrollPaymentBatchRequest("WPS", "AED"), default));
        var batch = await h.Db.PayrollPaymentBatches.SingleAsync(b => b.PayrollRunId == run.Id);
        Assert.Equal(1000m, batch.TotalAmount);
        batch.WpsStatus = WpsStatuses.Accepted;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        Assert.IsType<OkObjectResult>(await h.Payroll().SettlePaymentBatch(batch.Id, new(), default));

        var paid = await h.Db.EmployeeFinalSettlements.AsNoTracking().SingleAsync();
        Assert.Equal(FinalSettlementStatuses.Paid, paid.Status);
        Assert.Equal(600m, paid.ResidualDebtReclassed);
        var moved = await h.Db.SalaryAdvances.AsNoTracking().SingleAsync(a => a.Id == advance.Id);
        Assert.Equal((0m, "Closed"), (moved.OutstandingBalance, moved.Status));
        Assert.Empty(await h.Db.PayrollPaymentRecords.AsNoTracking().Where(r => r.EmployeeId == h.Employee.Id).ToListAsync());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection = new("DataSource=:memory:");
        public ZayraDbContext Db { get; private set; } = null!;
        public Guid Tid { get; } = Guid.NewGuid();
        public Company Company { get; private set; } = null!;
        public Company PriorCompany { get; private set; } = null!;
        public Employee Employee { get; private set; } = null!;

        public static async Task<Fixture> Create()
        {
            var h = new Fixture();
            await h._connection.OpenAsync();
            h.Db = new(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(h._connection).Options);
            await h.Db.Database.EnsureCreatedAsync();
            h.Company = new() { TenantId = h.Tid, LegalNameEn = "Current employer", CountryCode = "ARE", Jurisdiction = "UAE-mainland", DefaultCurrency = "AED", RegistrationNumber = "CURRENT" };
            h.PriorCompany = new() { TenantId = h.Tid, LegalNameEn = "Prior employer", CountryCode = "ARE", DefaultCurrency = "AED", RegistrationNumber = "PRIOR" };
            h.Employee = new() { TenantId = h.Tid, CompanyId = h.Company.Id, EmployeeCode = "LEAVER", FullName = "Leaver", Nationality = "Indian", Status = "Active", JoiningDate = new(2024, 1, 1) };
            h.Db.AddRange(h.Company, h.PriorCompany, h.Employee);
            await h.Db.SaveChangesAsync();
            return h;
        }

        public SalaryAdvance Advance(Guid companyId) => new()
        {
            TenantId = Tid, CompanyId = companyId, EmployeeId = Employee.PublicId, EmployeeIntId = Employee.Id, EmployeeName = Employee.FullName,
            AdvanceNumber = $"ADV-{Guid.NewGuid():N}", RequestedAmount = 600m, ApprovedAmount = 600m, RepaymentType = "Installments",
            Installments = 2, InstallmentAmount = 300m, RepaymentStartDate = new(2026, 1, 1), OutstandingBalance = 600m, Status = "Active",
        };

        public FinanceGlEntry AdvanceDisbursementGl(Guid companyId, decimal amount) => new()
        {
            TenantId = Tid, CompanyId = companyId, SourceModule = "Advance", SourceEntityId = Guid.NewGuid(), EventType = "Disbursement",
            DebitAccount = "1410 - Employee Salary Advances", CreditAccount = "1000 - Cash/Bank", Amount = amount, Currency = "AED",
        };

        /// <summary>A Locked settlement run whose Accepted bank batch pays this employee's Disbursing final settlement.</summary>
        public async Task<PayrollPaymentBatch> SeedSettlementBatchAsync()
        {
            var run = new PayrollRun { TenantId = Tid, CompanyId = Company.Id, Year = 2026, Month = 1, RunType = PayrollRunTypes.OffCycle, Status = "Locked" };
            var batch = new PayrollPaymentBatch { TenantId = Tid, PayrollRunId = run.Id, BatchNumber = "LEAVER-SETTLEMENT", WpsStatus = WpsStatuses.Accepted, TotalAmount = 1000, Currency = "AED" };
            var offboarding = new EmployeeOffboarding { TenantId = Tid, EmployeeId = Employee.Id, Status = "InProgress", LastWorkingDay = new(2026, 1, 31) };
            var settlement = new EmployeeFinalSettlement
            {
                TenantId = Tid, CompanyId = Company.Id, EmployeeId = Employee.Id, EmployeeCode = Employee.EmployeeCode,
                OffboardingId = offboarding.Id, PayrollRunId = run.Id, Currency = "AED", Status = FinalSettlementStatuses.Disbursing,
            };
            Db.AddRange(run, batch, offboarding, settlement,
                new PayrollPaymentRecord { TenantId = Tid, PaymentBatchId = batch.Id, EmployeeId = Employee.Id, Amount = 1000, Status = "Pending" },
                new FinanceGlEntry { TenantId = Tid, CompanyId = Company.Id, SourceModule = "Payroll", SourceEntityId = run.Id,
                    EventType = GlEventTypes.Accrual, CreditAccount = "2100 - Salaries Payable", Description = PayrollGlDescriptions.NetPayable, Amount = 1000, Currency = "AED" });
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return batch;
        }

        public PayrollController Payroll()
        {
            var http = Context("Admin", "payroll.write", "payroll.export");
            return new(Db, new Scope(), new HttpContextAccessor { HttpContext = http }, new F2NullNotifications(), new KsaTestPackResolver(PayComponentNetPayDefectTests.KsaRules()),
                PayComponentNetPayDefectTests.KsaRules(), new F2NullLetters(), new NullDocumentStorage(), new Zayra.Api.Infrastructure.Documents.PdfRenderGate(4))
            { ControllerContext = new() { HttpContext = http } };
        }

        public async Task<JsonElement> AuditRowAsync(string advanceNumber)
        {
            var http = Context("Finance");
            var result = await new AdvancesController(Db, new Scope()) { ControllerContext = new() { HttpContext = http } }.AuditReport(default);
            var ok = Assert.IsType<OkObjectResult>(result);
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return doc.RootElement.GetProperty("reconciliation").EnumerateArray()
                .Single(r => r.GetProperty("advanceNumber").GetString() == advanceNumber).Clone();
        }

        private DefaultHttpContext Context(string role, params string[] permissions) => new()
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim("tenant_id", Tid.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, role) }
                    .Concat(permissions.Select(p => new Claim("permission", p))), "test")),
        };

        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await _connection.DisposeAsync(); }
    }

    private sealed class Scope : IDataScopeService
    { public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization }); }
}
