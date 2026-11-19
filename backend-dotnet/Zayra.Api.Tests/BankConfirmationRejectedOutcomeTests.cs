using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// CTO gate 8.4.2: a bank REJECTION is "Not paid", never "Returned".
///
/// <para>Rejected (RJCT, REJECTED, DECLINED, FAILED ...) means the bank refused the instruction and the money never left.
/// Returned (RETURNED, RTRN, BOUNCED ...) means the money left and came back. Reading a rejection as a return is how
/// someone gets paid twice, and counting it as money awaiting a GL correction overstates what the ledger must undo.
/// So a rejection sets the EXISTING payment status Rejected, is stored as its own outcome, is kept out of
/// ReturnedAmountAwaitingGlCorrection and the returns and late-returns lists, and is listed on its own.</para>
///
/// <para>8.4.4: ACSC/ACCC stay Paid with the reason settled_by_bank; ACWC is held for a person.</para>
/// </summary>
public class BankConfirmationRejectedOutcomeTests
{
    private const string Iban = "SA0380000000608010167519";

    private static ZayraDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed record Seeded(Guid TenantId, Guid BatchId, Guid CompanyId, PayrollPaymentRecord Record);

    private static Seeded Seed(ZayraDbContext db, string status = "Pending")
    {
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var today = DateTime.UtcNow;
        var run = new PayrollRun
        {
            TenantId = tenantId, CompanyId = companyId, Year = today.Year, Month = today.Month, Status = "Locked",
        };
        db.PayrollRuns.Add(run);
        var batch = new PayrollPaymentBatch { TenantId = tenantId, PayrollRunId = run.Id };
        db.PayrollPaymentBatches.Add(batch);
        var record = new PayrollPaymentRecord
        {
            TenantId = tenantId, PaymentBatchId = batch.Id, EmployeeId = 1, Amount = 100m,
            Iban = Iban, Status = status, WpsReference = "W1",
        };
        db.PayrollPaymentRecords.Add(record);
        db.SaveChanges();
        return new Seeded(tenantId, batch.Id, companyId, record);
    }

    private static string Csv(params (string Status, string? Reason)[] rows) =>
        "wpsReference,amount,valueDate,status,reasonCode\n"
        + string.Concat(rows.Select(r => $"W1,100,2026-07-30,{r.Status},{r.Reason}\n"));

    private static string Ack(params (string Status, string? Reason)[] rows) =>
        $"ACK|B1|{rows.Length}\n"
        + string.Concat(rows.Select(r => $"REC|W1|{Iban}|100|{r.Status}|{r.Reason}||2026-07-30|BR1\n"))
        + $"EOF|{rows.Length}\n";

    private static BankConfirmationService Service(ZayraDbContext db) =>
        new(db, new IBankConfirmationParser[] { new GenericCsvBankConfirmationParser(), new WpsAckBankConfirmationParser() });

    private static async Task<BankConfirmationImportResult> Import(
        ZayraDbContext db, Seeded s, string parserKey, BankConfirmationImportOptions? options = null,
        params (string Status, string? Reason)[] rows)
    {
        var content = parserKey == "wps-ack" ? Ack(rows) : Csv(rows);
        var (result, refusal) = await Service(db).ImportAsync(s.TenantId, s.BatchId, "bank.txt", content, parserKey,
            options ?? new BankConfirmationImportOptions(), Guid.NewGuid(), "Payroll Officer", default);
        refusal.Should().BeNull();
        await db.SaveChangesAsync();
        return result!;
    }

    private static Task<BankConfirmationImportResult> Import(ZayraDbContext db, Seeded s, string parserKey, string status,
        string? reason = null) => Import(db, s, parserKey, null, (status, reason));

    private static string Json(object o) => JsonSerializer.Serialize(o);

    public static readonly TheoryData<string, string> ParserAndRejectionWord = new()
    {
        { "generic-csv", "RJCT" }, { "generic-csv", "REJECTED" }, { "generic-csv", "DECLINED" },
        { "generic-csv", "FAILED" }, { "generic-csv", "CANCELLED_BY_BANK" },
        { "wps-ack", "RJCT" }, { "wps-ack", "REJECT" }, { "wps-ack", "FAIL" }, { "wps-ack", "FAILURE" },
    };

    // ── A rejection: Rejected, not paid, not awaiting a GL correction ────────────────────────────

    [Theory]
    [MemberData(nameof(ParserAndRejectionWord))]
    public async Task Rejection_SetsStatusRejected_StoredDistinctly_AndIsNotAReturn(string parserKey, string word)
    {
        using var db = NewDb();
        var s = Seed(db);

        var result = await Import(db, s, parserKey, word, "AC01");

        s.Record.Status.Should().Be(PaymentRecordStatuses.Rejected);
        result.RejectedApplied.Should().Be(1);
        result.ReturnedApplied.Should().Be(0);
        result.PaidApplied.Should().Be(0);

        var evidence = db.BankPaymentConfirmations.Single();
        evidence.Outcome.Should().Be(BankConfirmationOutcomes.Rejected);
        evidence.Applied.Should().BeTrue();
        evidence.RawOutcome.Should().Be(word);
        evidence.ReasonCode.Should().Be("AC01");

        result.Coverage.RejectedCount.Should().Be(1);
        result.Coverage.ReturnedCount.Should().Be(0);
        result.Coverage.PaidCount.Should().Be(0);
        result.Coverage.ReconciliationDelta.Should().Be(0m);
    }

    [Theory]
    [MemberData(nameof(ParserAndRejectionWord))]
    public async Task Rejection_IsKeptOutOfTheGlCorrection_AndTheReturnLists_AndListedOnItsOwn(string parserKey, string word)
    {
        using var db = NewDb();
        var s = Seed(db);

        var result = await Import(db, s, parserKey, word, "AC01");

        result.ReturnedAmountAwaitingGlCorrection.Should().Be(0m, "the money never left, so there is no outflow to correct");
        result.RejectedAmountNotPaid.Should().Be(100m);
        result.Returns.Should().BeEmpty();
        result.LateReturns.Should().BeEmpty();

        result.Rejections.Should().ContainSingle();
        var listed = Json(result.Rejections);
        listed.Should().Contain(s.Record.Id.ToString());
        listed.Should().Contain("\"reasonCode\":\"AC01\"");
        listed.Should().Contain(Json(BankOutcomeLabels.NotPaidRejectedByBank.English));
        listed.Should().Contain(Json(BankOutcomeLabels.NotPaidRejectedByBank.Arabic));
        listed.Should().Contain(Json(BankStatusReasonCatalogue.Describe("AC01").English));
        listed.Should().Contain(Json(BankStatusReasonCatalogue.Describe("AC01").Arabic));
    }

    [Theory]
    [InlineData("generic-csv", "RETURNED")]
    [InlineData("generic-csv", "RTRN")]
    [InlineData("generic-csv", "BOUNCED")]
    [InlineData("wps-ack", "RETURN")]
    [InlineData("wps-ack", "BOUNCE")]
    public async Task Return_StaysReturned_AndStillAwaitsTheGlCorrection(string parserKey, string word)
    {
        using var db = NewDb();
        var s = Seed(db);
        SettlementJournalFixture.Settle(db, s.BatchId);   // the correction applies to a settled batch (owner ruling 2026-11-17)

        var result = await Import(db, s, parserKey, word, "AC04");

        s.Record.Status.Should().Be(PaymentRecordStatuses.Returned);
        db.BankPaymentConfirmations.Single().Outcome.Should().Be(BankConfirmationOutcomes.Returned);
        result.ReturnedApplied.Should().Be(1);
        result.RejectedApplied.Should().Be(0);
        result.ReturnedAmountAwaitingGlCorrection.Should().Be(100m);
        result.RejectedAmountNotPaid.Should().Be(0m);
        result.Returns.Should().ContainSingle();
        Json(result.Returns).Should().Contain(Json(BankStatusReasonCatalogue.Describe("AC04").English));
        result.Rejections.Should().BeEmpty();
    }

    [Theory]
    [InlineData("generic-csv")]
    [InlineData("wps-ack")]
    public async Task ReimportOfTheSameFile_StillKeepsTheRejectionOutOfTheGlCorrection(string parserKey)
    {
        using var db = NewDb();
        var s = Seed(db);
        await Import(db, s, parserKey, "RJCT");

        var again = await Import(db, s, parserKey, "RJCT");

        again.Duplicate.Should().BeTrue();
        again.ReturnedAmountAwaitingGlCorrection.Should().Be(0m);
        again.RejectedAmountNotPaid.Should().Be(100m);
    }

    [Theory]
    [InlineData("generic-csv", "PAID")]
    [InlineData("wps-ack", "PAID")]
    // Before F1 a RETURNED row and an RJCT row for one record were the same outcome and were collapsed and applied as
    // Returned. They now say two different things (money came back / money never left): a broken file.
    [InlineData("generic-csv", "RETURNED")]
    [InlineData("wps-ack", "BOUNCED")]
    public async Task ARejectedRowBesideAPaidOrReturnedRowForOneRecord_IsConflictingRows(string parserKey, string other)
    {
        using var db = NewDb();
        var s = Seed(db);

        var result = await Import(db, s, parserKey, null, (other, null), ("RJCT", "AC01"));

        result.ConflictingRows.Should().ContainSingle();
        Json(result.ConflictingRows).Should().Contain("conflicting_rows");
        s.Record.Status.Should().Be("Pending");
        db.BankPaymentConfirmations.Should().BeEmpty();
    }

    // Two rejected rows for one record, one with an ordinary reason and one DUPL, in either order: the evidence of a duplicate is
    // never discarded when rows collapse. The safe outcome is the duplicate hold, not Rejected with no hold.
    [Theory]
    [InlineData("generic-csv", "AC01", "DUPL")]
    [InlineData("generic-csv", "DUPL", "AC01")]
    [InlineData("wps-ack", "AC01", "DUPL")]
    [InlineData("wps-ack", "DUPL", "AC01")]
    public async Task TwoRejectedRows_OneOrdinaryAndOneDupl_AreHeldForTheDuplicate_InEitherOrder(string parserKey, string first, string second)
    {
        using var db = NewDb();
        var s = Seed(db);

        var result = await Import(db, s, parserKey, null, ("RJCT", first), ("RJCT", second));

        s.Record.Status.Should().Be("Pending", "a duplicate warning means the original payment may have succeeded");
        result.RejectedApplied.Should().Be(0);
        Json(result.HeldForPerson).Should().Contain(BankOutcomeHolds.DuplicateRejection);
        db.BankPaymentConfirmations.Should().ContainSingle()
            .Which.HoldReason.Should().Be(BankOutcomeHolds.DuplicateRejection);
    }

    [Theory]
    [InlineData("generic-csv")]
    [InlineData("wps-ack")]
    public async Task TwoRejectedRows_BothOrdinary_StillCollapseAndReject(string parserKey)
    {
        using var db = NewDb();
        var s = Seed(db);

        var result = await Import(db, s, parserKey, null, ("RJCT", "AC01"), ("RJCT", "AM04"));

        s.Record.Status.Should().Be(PaymentRecordStatuses.Rejected);
        result.CollapsedDuplicateRows.Should().Be(1);
        db.BankPaymentConfirmations.Should().ContainSingle().Which.HoldReason.Should().BeNull();
    }

    // ── Transitions ──────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("generic-csv")]
    [InlineData("wps-ack")]
    public async Task RejectedThenPaid_NeedsTheSameReversalAsReturned(string parserKey)
    {
        using var db = NewDb();
        var s = Seed(db, PaymentRecordStatuses.Rejected);

        // The caller may record payment status: the line is held with an instruction (without it the import is refused with a 403).
        var refused = await Import(db, s, parserKey, new BankConfirmationImportOptions(CallerMayRecordPaymentStatus: true), ("PAID", null));

        s.Record.Status.Should().Be(PaymentRecordStatuses.Rejected);
        refused.Refused.Should().ContainSingle();
        Json(refused.Refused).Should().Contain("return_reversal_not_permitted");

        using var db2 = NewDb();
        var s2 = Seed(db2, PaymentRecordStatuses.Rejected);
        var allowed = await Import(db2, s2, parserKey,
            new BankConfirmationImportOptions(AllowReturnReversal: true, ReturnReversalReason: "Bank re-ran the line", CallerMayRecordPaymentStatus: true), ("PAID", null));

        s2.Record.Status.Should().Be(PaymentRecordStatuses.Paid);
        allowed.PaidApplied.Should().Be(1);
    }

    [Theory]
    [InlineData("Paid", "RJCT", "paid_then_rejected_held")]
    [InlineData("Returned", "RJCT", "failed_outcome_changed_held")]
    [InlineData("Rejected", "RETURNED", "failed_outcome_changed_held")]
    public async Task AllowReturnReversal_OpensNeitherNewHold(string previous, string word, string hold)
    {
        using var db = NewDb();
        var s = Seed(db, previous);

        var result = await Import(db, s, "generic-csv",
            new BankConfirmationImportOptions(AllowReturnReversal: true, ReturnReversalReason: "Bank re-ran the line", CallerMayRecordPaymentStatus: true), (word, null));

        s.Record.Status.Should().Be(previous);
        db.BankPaymentConfirmations.Single().HoldReason.Should().Be(hold);
        result.Overrides.Should().BeEmpty();
    }

    [Theory]
    [InlineData("generic-csv")]
    [InlineData("wps-ack")]
    public async Task PaidThenRejected_IsHeldForAPerson_NeverSilentlyUnpaid(string parserKey)
    {
        using var db = NewDb();
        var s = Seed(db, PaymentRecordStatuses.Paid);

        var result = await Import(db, s, parserKey, "RJCT", "AC01");

        s.Record.Status.Should().Be(PaymentRecordStatuses.Paid, "the bank already said Paid; a later refusal is a contradiction a person resolves");
        result.RejectedApplied.Should().Be(0);
        result.Refused.Should().ContainSingle();
        Json(result.Refused).Should().Contain("paid_then_rejected_held");
        var evidence = db.BankPaymentConfirmations.Single();
        evidence.Applied.Should().BeFalse();
        evidence.HoldReason.Should().Be("paid_then_rejected_held");
        evidence.Outcome.Should().Be(BankConfirmationOutcomes.Rejected);
        result.LateReturns.Should().BeEmpty("a refusal after Paid is not money that came back");
        result.ReturnedAmountAwaitingGlCorrection.Should().Be(0m);
        result.HeldForPerson.Should().ContainSingle();
        Json(result.HeldForPerson).Should().Contain("paid_then_rejected_held");
    }

    [Theory]
    [InlineData("generic-csv", "Returned", "RJCT")]
    [InlineData("generic-csv", "Rejected", "RETURNED")]
    [InlineData("wps-ack", "Returned", "FAILED")]
    [InlineData("wps-ack", "Rejected", "BOUNCED")]
    public async Task ReturnedAndRejected_NeverSwapSilently(string parserKey, string previous, string word)
    {
        using var db = NewDb();
        var s = Seed(db, previous);

        var result = await Import(db, s, parserKey, word);

        s.Record.Status.Should().Be(previous, "moving between Returned and Rejected changes what the GL must correct");
        result.Refused.Should().ContainSingle();
        Json(result.Refused).Should().Contain("failed_outcome_changed_held");
        db.BankPaymentConfirmations.Single().Applied.Should().BeFalse();
    }

    // ── 8.4.4: settled by bank; accepted with change ─────────────────────────────────────────────

    [Theory]
    [InlineData("generic-csv", "ACSC")]
    [InlineData("generic-csv", "ACCC")]
    [InlineData("wps-ack", "ACSC")]
    [InlineData("wps-ack", "ACCC")]
    public async Task IsoSettlement_StaysPaid_WithTheReasonSettledByBank(string parserKey, string word)
    {
        var parse = parserKey == "wps-ack"
            ? new WpsAckBankConfirmationParser().Parse(Ack((word, null)))
            : new GenericCsvBankConfirmationParser().Parse(Csv((word, null)));
        parse.Rows.Single().Outcome.Should().Be(BankConfirmationOutcomes.Paid);
        parse.Rows.Single().OutcomeReason.Should().Be(BankOutcomeReasons.SettledByBank);

        using var db = NewDb();
        var s = Seed(db);
        var result = await Import(db, s, parserKey, word);

        s.Record.Status.Should().Be(PaymentRecordStatuses.Paid);
        result.PaidApplied.Should().Be(1);
        result.SettledByBank.Should().Be(1);
    }

    [Theory]
    [InlineData("generic-csv")]
    [InlineData("wps-ack")]
    public async Task PlainPaid_IsNotCountedAsSettledByBank(string parserKey)
    {
        using var db = NewDb();
        var s = Seed(db);

        var result = await Import(db, s, parserKey, "PAID");

        result.PaidApplied.Should().Be(1);
        result.SettledByBank.Should().Be(0);
    }

    [Theory]
    [InlineData("generic-csv")]
    [InlineData("wps-ack")]
    public async Task AcceptedWithChange_IsHeldForAPerson_AndChangesNothing(string parserKey)
    {
        using var db = NewDb();
        var s = Seed(db);

        var result = await Import(db, s, parserKey, "ACWC");

        s.Record.Status.Should().Be("Pending");
        result.PendingReported.Should().Be(0, "ACWC is no longer read as settlement in process");
        result.Unmatched.Should().ContainSingle();
        Json(result.Unmatched).Should().Contain("\"reason\":\"unknown_outcome\"");
        Json(result.ParseErrors).Should().Contain("unknown_outcome").And.Contain("accepted it with a change");
        db.BankPaymentConfirmations.Should().BeEmpty();
    }

    // ── The dashboard and the batch history ──────────────────────────────────────────────────────

    private static async Task<string?> DashboardPayDate(ZayraDbContext db, Guid tenantId)
    {
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Outcomes", Slug = $"outcomes-{tenantId:N}" });
        db.SaveChanges();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim("permission", "payroll.read"),
        }, "test"));
        var ctrl = new DashboardController(db,
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
            new OutcomesUnrestrictedScope())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } },
        };
        var full = Assert.IsType<DashboardFullDto>(Assert.IsType<OkObjectResult>(await ctrl.Full(6)).Value);
        return full.Overview.PayrollSummary!.PayDate;
    }

    [Theory]
    [InlineData("RJCT")]
    [InlineData("FAILED")]
    [InlineData("DECLINED")]
    public async Task Dashboard_PaidCountAndPayDate_ComeFromThePaidLineOnly_NeverFromARejection(string word)
    {
        using var db = NewDb();
        var s = Seed(db);
        var second = new PayrollPaymentRecord
        {
            TenantId = s.TenantId, PaymentBatchId = s.BatchId, EmployeeId = 2, Amount = 100m, Iban = Iban, Status = "Pending", WpsReference = "W2",
        };
        db.PayrollPaymentRecords.Add(second);
        db.SaveChanges();
        var content = "wpsReference,amount,valueDate,status\nW1,100,2026-07-30,PAID\n" + $"W2,100,2026-08-02,{word}\n";
        var (result, refusal) = await Service(db).ImportAsync(s.TenantId, s.BatchId, "bank.csv", content, "generic-csv",
            new BankConfirmationImportOptions(), Guid.NewGuid(), "Payroll Officer", default);
        refusal.Should().BeNull();
        await db.SaveChangesAsync();

        result!.Coverage.PaidCount.Should().Be(1);
        result.Coverage.RejectedCount.Should().Be(1);
        (await DashboardPayDate(db, s.TenantId)).Should().Be("2026-07-30", "the rejected line's later date is not a pay date");
    }

    private static BankConfirmationsController Controller(ZayraDbContext db, Seeded s)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new List<Claim>
        {
            new("tenant_id", s.TenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "Payroll Officer"),
            new("permission", "payroll.export"),
            new("is_group_scope", "true"),
        }, "Test"));
        return new BankConfirmationsController(db, Service(db))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } },
        };
    }

    [Fact]
    public async Task BatchHistory_KeepsTheRejectionOutOfTheGlCorrection_AndShowsItSeparately()
    {
        using var db = NewDb();
        var s = Seed(db);
        await Import(db, s, "generic-csv", "RJCT", "AM04");

        var ok = Assert.IsType<OkObjectResult>(await Controller(db, s).History(s.BatchId, default));
        var body = JsonDocument.Parse(Json(ok.Value!)).RootElement;

        body.GetProperty("returnedAmountAwaitingGlCorrection").GetDecimal().Should().Be(0m);
        body.GetProperty("rejectedAmountNotPaid").GetDecimal().Should().Be(100m);
        body.GetProperty("coverage").GetProperty("RejectedCount").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task ImportEndpoint_ReportsTheRejectionApart_FromTheReturnsAndTheGlCorrection()
    {
        using var db = NewDb();
        var s = Seed(db);
        var ctrl = Controller(db, s);
        ctrl.ControllerContext.HttpContext.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Csv(("RJCT", "AC01"))));
        ctrl.ControllerContext.HttpContext.Request.ContentType = "text/csv";

        var ok = Assert.IsType<OkObjectResult>(await ctrl.Import(s.BatchId, ct: default));
        var body = JsonDocument.Parse(Json(ok.Value!)).RootElement;

        body.GetProperty("applied").GetProperty("rejected").GetInt32().Should().Be(1);
        body.GetProperty("applied").GetProperty("returned").GetInt32().Should().Be(0);
        body.GetProperty("rejections").GetArrayLength().Should().Be(1);
        body.GetProperty("returns").GetArrayLength().Should().Be(0);
        body.GetProperty("returnedAmountAwaitingGlCorrection").GetProperty("amount").GetDecimal().Should().Be(0m);
        body.GetProperty("rejectedAmountNotPaid").GetProperty("amount").GetDecimal().Should().Be(100m);
        s.Record.Status.Should().Be(PaymentRecordStatuses.Rejected);
    }
}

file sealed class OutcomesUnrestrictedScope : IDataScopeService
{
    public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization, AllowedEmployeeIds = null });
}
