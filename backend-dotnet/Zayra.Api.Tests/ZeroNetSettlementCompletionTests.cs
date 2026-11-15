using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// A leaver whose debts took all of the final pay has a zero net, so their settlement run has nothing for a bank batch to pay and
/// the settlement would wait in Disbursing for ever, with the leftover debt still on the ex-employee's advance. Finance completes
/// it with nothing to pay (<c>POST final-settlements/{id}/complete-without-payment</c>) through the same finalisation the bank
/// settlement uses: Paid with no batch, the leftover moved to 1420. Only for a zero net, only from Disbursing on a Locked run, only
/// by a payment settler who neither calculated nor submitted the settlement and is not its subject; audited and idempotent.
/// </summary>
public class ZeroNetSettlementCompletionTests
{
    [Fact]
    public async Task ZeroNetSettlement_WithNoPayingBatch_IsCompleted_TheLeftoverMovesTo1420_AndItIsAudited()
    {
        await using var h = await Fixture.Create();
        var (settlement, advance) = await h.SeedAsync(net: 0m);

        var ok = Assert.IsType<OkObjectResult>(await h.Payroll().CompleteFinalSettlementWithoutPayment(settlement.Id, new("all recovered"), default));

        var paid = await h.Db.EmployeeFinalSettlements.AsNoTracking().SingleAsync();
        Assert.Equal((FinalSettlementStatuses.Paid, (Guid?)null, 600m), (paid.Status, paid.PaymentBatchId, paid.ResidualDebtReclassed));
        Assert.NotNull(paid.PaidAtUtc);
        var moved = await h.Db.SalaryAdvances.AsNoTracking().SingleAsync(a => a.Id == advance.Id);
        Assert.Equal((0m, 0m, "Closed"), (moved.OutstandingBalance, moved.TotalRepaid, moved.Status));
        var reclass = Assert.Single(await h.Db.FinanceGlEntries.AsNoTracking().Where(g => g.EventType == GlEventTypes.SettlementResidualReclass).ToListAsync());
        Assert.Equal((600m, settlement.Id), (reclass.Amount, reclass.SourceEntityId));
        Assert.Equal(600m, Assert.Single(await h.Db.PayrollEmployeeReceivables.AsNoTracking().ToListAsync()).Amount);
        Assert.True((await h.Db.EmployeeOffboardings.AsNoTracking().SingleAsync()).FinalSettlementDone);
        Assert.Single(await h.Db.PayrollAuditLogs.AsNoTracking()
            .Where(a => a.Action == "payroll.final_settlement.completed_without_payment" && a.EntityId == settlement.Id.ToString()).ToListAsync());
        Assert.Contains("all recovered", JsonSerializer.Serialize(ok.Value));
    }

    [Fact]
    public async Task CompletingAgain_ChangesNothing()
    {
        await using var h = await Fixture.Create();
        var (settlement, _) = await h.SeedAsync(net: 0m);
        Assert.IsType<OkObjectResult>(await h.Payroll().CompleteFinalSettlementWithoutPayment(settlement.Id, new(), default));
        h.Db.ChangeTracker.Clear();

        var again = Assert.IsType<OkObjectResult>(await h.Payroll().CompleteFinalSettlementWithoutPayment(settlement.Id, new(), default));

        Assert.Contains("\"alreadyCompleted\":true", JsonSerializer.Serialize(again.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Single(await h.Db.FinanceGlEntries.AsNoTracking().Where(g => g.EventType == GlEventTypes.SettlementResidualReclass).ToListAsync());
        Assert.Single(await h.Db.PayrollEmployeeReceivables.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ASettlementWithMoneyToPay_IsRefused_InBothLanguages_AndLeftAlone()
    {
        await using var h = await Fixture.Create();
        var (settlement, advance) = await h.SeedAsync(net: 250m);

        var refused = Assert.IsType<UnprocessableEntityObjectResult>(await h.Payroll().CompleteFinalSettlementWithoutPayment(settlement.Id, new(), default));

        Assert.Equal("settlement_net_not_zero", Code(refused));
        Assert.False(string.IsNullOrWhiteSpace(Field(refused, "messageAr")));
        await h.AssertUntouchedAsync(advance.Id);
    }

    [Fact]
    public async Task TheUserWhoCalculatedOrSubmittedTheSettlement_MayNotCompleteIt()
    {
        await using var h = await Fixture.Create();
        var (settlement, advance) = await h.SeedAsync(net: 0m, createdBy: h.ActorId);

        var refused = Assert.IsType<ConflictObjectResult>(await h.Payroll().CompleteFinalSettlementWithoutPayment(settlement.Id, new(), default));

        Assert.Equal("segregation_of_duties", Code(refused));
        Assert.False(string.IsNullOrWhiteSpace(Field(refused, "messageAr")));
        await h.AssertUntouchedAsync(advance.Id);
    }

    [Fact]
    public async Task TheLeaverThemself_MayNotCompleteTheirOwnSettlement()
    {
        await using var h = await Fixture.Create();
        h.Employee.UserAccountId = h.ActorId;
        await h.Db.SaveChangesAsync();
        var (settlement, advance) = await h.SeedAsync(net: 0m);

        var refused = Assert.IsType<ObjectResult>(await h.Payroll().CompleteFinalSettlementWithoutPayment(settlement.Id, new(), default));

        Assert.Equal((403, "subject_is_recorder"), (refused.StatusCode, Code(refused)));
        await h.AssertUntouchedAsync(advance.Id);
    }

    [Fact]
    public async Task AUserWhoMayNotSettlePayments_IsForbidden()
    {
        await using var h = await Fixture.Create();
        var (settlement, advance) = await h.SeedAsync(net: 0m);

        Assert.IsType<ForbidResult>(await h.Payroll("payroll.read", "payroll.write", "payroll.approve").CompleteFinalSettlementWithoutPayment(settlement.Id, new(), default));

        await h.AssertUntouchedAsync(advance.Id);
    }

    [Fact]
    public async Task ASettlementNotAwaitingPayment_OrOnARunNotLocked_IsRefused()
    {
        await using var h = await Fixture.Create();
        var (settlement, advance) = await h.SeedAsync(net: 0m, status: FinalSettlementStatuses.Approved);
        var notDisbursing = Assert.IsType<ConflictObjectResult>(await h.Payroll().CompleteFinalSettlementWithoutPayment(settlement.Id, new(), default));
        Assert.Equal("settlement_not_awaiting_payment", Code(notDisbursing));
        await h.AssertUntouchedAsync(advance.Id);

        await using var h2 = await Fixture.Create();
        var (settlement2, advance2) = await h2.SeedAsync(net: 0m, runStatus: "Approved");
        var notLocked = Assert.IsType<ConflictObjectResult>(await h2.Payroll().CompleteFinalSettlementWithoutPayment(settlement2.Id, new(), default));
        Assert.Equal("run_not_locked", Code(notLocked));
        await h2.AssertUntouchedAsync(advance2.Id);
    }

    [Fact]
    public async Task TheLocksAreTakenInTheSharedOrder_BatchPaymentThenTheAdvance()
    {
        await using var h = await Fixture.Create();
        var (settlement, advance) = await h.SeedAsync(net: 0m);
        var batch = new PayrollPaymentBatch { TenantId = h.Tid, PayrollRunId = settlement.PayrollRunId!.Value, BatchNumber = "EMPTY", WpsStatus = WpsStatuses.Accepted, TotalAmount = 0, Currency = "AED" };
        h.Db.Add(batch);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var recorded = new List<(string Scope, Guid TenantId, Guid AggregateId)>();
        FinanceDecisionSerializer.Recorder.Value = recorded;
        try { Assert.IsType<OkObjectResult>(await h.Payroll().CompleteFinalSettlementWithoutPayment(settlement.Id, new(), default)); }
        finally { FinanceDecisionSerializer.Recorder.Value = null; }

        var batchLock = recorded.FindIndex(r => r.Scope == "payroll.batch-payment" && r.AggregateId == batch.Id);
        var advanceLock = recorded.FindIndex(r => r.Scope == FinanceDecisionSerializer.ScopeAdvance && r.AggregateId == advance.Id);
        Assert.True(batchLock >= 0 && advanceLock > batchLock, string.Join(" | ", recorded.Select(r => $"{r.Scope}:{r.AggregateId}")));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(250, false)]
    public async Task TheRegister_FlagsOnlyAZeroNetSettlementAwaitingPayment(int net, bool completable)
    {
        await using var h = await Fixture.Create();
        await h.SeedAsync(net: net);

        var ok = Assert.IsType<OkObjectResult>(await h.Payroll().ListFinalSettlements(null, null, default));

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        var row = Assert.Single(doc.RootElement.GetProperty("rows").EnumerateArray().ToList());
        Assert.Equal(completable, row.GetProperty("completableWithoutPayment").GetBoolean());
    }

    private static string? Code(ObjectResult r) => Field(r, "error");

    private static string? Field(ObjectResult r, string name)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(r.Value));
        return doc.RootElement.TryGetProperty(name, out var v) ? v.GetString() : null;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection = new("DataSource=:memory:");
        public ZayraDbContext Db { get; private set; } = null!;
        public Guid Tid { get; } = Guid.NewGuid();
        public Guid ActorId { get; } = Guid.NewGuid();
        public Company Company { get; private set; } = null!;
        public Employee Employee { get; private set; } = null!;

        public static async Task<Fixture> Create()
        {
            var h = new Fixture();
            await h._connection.OpenAsync();
            h.Db = new(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(h._connection).Options);
            await h.Db.Database.EnsureCreatedAsync();
            h.Company = new() { TenantId = h.Tid, LegalNameEn = "Employer", CountryCode = "ARE", Jurisdiction = "UAE-mainland", DefaultCurrency = "AED", RegistrationNumber = "EMPLOYER" };
            h.Employee = new() { TenantId = h.Tid, CompanyId = h.Company.Id, EmployeeCode = "LEAVER", FullName = "Leaver", Nationality = "Indian", Status = "Offboarded", JoiningDate = new(2024, 1, 1) };
            h.Db.AddRange(h.Company, h.Employee);
            await h.Db.SaveChangesAsync();
            return h;
        }

        /// <summary>A Locked settlement run whose only payslip (the leaver's) nets to <paramref name="net"/>, the leaver's
        /// Disbursing settlement, and a 600 advance (with the 1410 debit its payout booked) the final pay did not cover.</summary>
        public async Task<(EmployeeFinalSettlement Settlement, SalaryAdvance Advance)> SeedAsync(
            decimal net, Guid? createdBy = null, string status = FinalSettlementStatuses.Disbursing, string runStatus = "Locked")
        {
            var advance = new SalaryAdvance
            {
                TenantId = Tid, CompanyId = Company.Id, EmployeeId = Employee.PublicId, EmployeeIntId = Employee.Id, EmployeeName = Employee.FullName,
                AdvanceNumber = $"ADV-{Guid.NewGuid():N}", RequestedAmount = 600m, ApprovedAmount = 600m, RepaymentType = "Installments",
                Installments = 2, InstallmentAmount = 300m, RepaymentStartDate = new(2026, 1, 1), OutstandingBalance = 600m, Status = "Active",
            };
            var run = new PayrollRun { TenantId = Tid, CompanyId = Company.Id, Year = 2026, Month = 1, RunType = PayrollRunTypes.OffCycle, Status = runStatus };
            var offboarding = new EmployeeOffboarding { TenantId = Tid, EmployeeId = Employee.Id, Status = "InProgress", LastWorkingDay = new(2026, 1, 31) };
            var settlement = new EmployeeFinalSettlement
            {
                TenantId = Tid, CompanyId = Company.Id, EmployeeId = Employee.Id, EmployeeCode = Employee.EmployeeCode,
                OffboardingId = offboarding.Id, PayrollRunId = run.Id, Currency = "AED", Status = status,
                CreatedByUserId = createdBy ?? Guid.NewGuid(), SubmittedByUserId = Guid.NewGuid(), ApprovedByUserId = Guid.NewGuid(),
            };
            Db.AddRange(advance, run, offboarding, settlement,
                new FinanceGlEntry { TenantId = Tid, CompanyId = Company.Id, SourceModule = "Advance", SourceEntityId = advance.Id, EventType = "Disbursement",
                    DebitAccount = "1410 - Employee Salary Advances", CreditAccount = "1000 - Cash/Bank", Amount = 600m, Currency = "AED" },
                new PayrollSlip { TenantId = Tid, RunId = run.Id, EmployeeId = Employee.Id, EmployeeCode = Employee.EmployeeCode, EmployeeName = Employee.FullName,
                    GrossSalary = 500m, Deductions = 500m - net, NetSalary = net });
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return (settlement, advance);
        }

        public async Task AssertUntouchedAsync(Guid advanceId)
        {
            var settlement = await Db.EmployeeFinalSettlements.AsNoTracking().SingleAsync();
            Assert.NotEqual(FinalSettlementStatuses.Paid, settlement.Status);
            var advance = await Db.SalaryAdvances.AsNoTracking().SingleAsync(a => a.Id == advanceId);
            Assert.Equal((600m, "Active"), (advance.OutstandingBalance, advance.Status));
            Assert.Empty(await Db.FinanceGlEntries.AsNoTracking().Where(g => g.EventType == GlEventTypes.SettlementResidualReclass).ToListAsync());
        }

        public PayrollController Payroll(params string[] permissions)
        {
            if (permissions.Length == 0) permissions = new[] { "payroll.read", "payroll.payment.settle" };
            var http = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim("tenant_id", Tid.ToString()), new Claim(ClaimTypes.NameIdentifier, ActorId.ToString()), new Claim(ClaimTypes.Name, "Finance Approver"), new Claim(ClaimTypes.Role, "Finance Approver") }
                        .Concat(permissions.Select(p => new Claim("permission", p))), "test")),
            };
            return new(Db, new Scope(), new HttpContextAccessor { HttpContext = http }, new F2NullNotifications(), new KsaTestPackResolver(PayComponentNetPayDefectTests.KsaRules()),
                PayComponentNetPayDefectTests.KsaRules(), new F2NullLetters(), new NullDocumentStorage(), new Zayra.Api.Infrastructure.Documents.PdfRenderGate(4))
            { ControllerContext = new() { HttpContext = http } };
        }

        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await _connection.DisposeAsync(); }
    }

    private sealed class Scope : IDataScopeService
    { public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization }); }
}
