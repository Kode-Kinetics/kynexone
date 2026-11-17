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
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Which bank status words may move a salary to Paid or Returned.
///
/// <para>THE PRINCIPLE (CTO gate 8.2, owner-approved). A salary is shown Paid or Returned ONLY on an
/// unambiguous bank word; anything unclear is held for a person. Wrongly showing Paid means an unpaid salary
/// is believed paid; wrongly showing Returned means someone pays twice.</para>
///
/// <list type="bullet">
/// <item>Settlement still in process (ISO ACSP, ACWP and the other acceptance codes) is Pending: recorded as
/// evidence, changes no status.</item>
/// <item>"The bank accepted the file" words (SUCCESS, COMPLETED, EXECUTED, TRANSFERRED ...) never prove an
/// account was credited. They are Pending too, with the reason <c>accepted_credit_not_confirmed</c>, so the
/// operator sees why. A per-bank word list that may promote them to Paid for one bank is wave 2.</item>
/// <item>Ambiguous words (one or two letters, YES, SETTLE, CREDIT, COMPLETE, NOK, UNPAID, REVERSED) are not
/// recognised at all, which the import treats as an error that confirms nothing.</item>
/// </list>
///
/// <para>This changes how FUTURE files are read; stored confirmations keep the outcome written at import
/// time and are never re-read.</para>
/// </summary>
public class BankConfirmationOutcomeVocabularyTests
{
    private const string Paid = BankConfirmationOutcomes.Paid;
    private const string Returned = BankConfirmationOutcomes.Returned;
    private const string Pending = BankConfirmationOutcomes.Pending;
    private const string InProcess = "pending_informational";
    private const string AcceptedOnly = "accepted_credit_not_confirmed";

    /// <summary>Every word the vocabulary has ever known, plus the round-1 removals, with its outcome now.</summary>
    public static readonly TheoryData<string, string?, string?> EveryToken = new()
    {
        // Unambiguous credit: Paid.
        { "PAID", Paid, null }, { "ACSC", Paid, null }, { "ACCC", Paid, null },
        { "SETTLED", Paid, null }, { "CREDITED", Paid, null },

        // The bank accepted the file; no proof an account was credited.
        { "SUCCESS", Pending, AcceptedOnly }, { "SUCCESSFUL", Pending, AcceptedOnly },
        { "SUCCEEDED", Pending, AcceptedOnly }, { "COMPLETED", Pending, AcceptedOnly },
        { "EXECUTED", Pending, AcceptedOnly }, { "TRANSFERRED", Pending, AcceptedOnly },

        // Still in process.
        { "PDNG", Pending, InProcess }, { "PENDING", Pending, InProcess }, { "ACCP", Pending, InProcess },
        { "ACSP", Pending, InProcess }, { "ACWP", Pending, InProcess }, { "ACTC", Pending, InProcess },
        { "ACWC", Pending, InProcess }, { "RCVD", Pending, InProcess }, { "RECEIVED", Pending, InProcess },
        { "INPROGRESS", Pending, InProcess }, { "IN_PROGRESS", Pending, InProcess },
        { "PROCESSING", Pending, InProcess }, { "SUBMITTED", Pending, InProcess },
        { "QUEUED", Pending, InProcess }, { "AWAITING", Pending, InProcess },

        // Unambiguous failure: Returned.
        { "RJCT", Returned, null }, { "REJECT", Returned, null }, { "REJECTED", Returned, null },
        { "RETURN", Returned, null }, { "RETURNED", Returned, null }, { "RTRN", Returned, null },
        { "BOUNCE", Returned, null }, { "BOUNCED", Returned, null }, { "FAIL", Returned, null },
        { "FAILED", Returned, null }, { "FAILURE", Returned, null }, { "DECLINED", Returned, null },
        { "CANCELLED_BY_BANK", Returned, null },

        // Ambiguous: not recognised, held for a person.
        { "SETTLE", null, null }, { "CREDIT", null, null }, { "COMPLETE", null, null }, { "YES", null, null },
        { "R", null, null }, { "N", null, null }, { "NO", null, null }, { "NOK", null, null },
        { "UNPAID", null, null }, { "REVERSED", null, null },
        { "P", null, null }, { "Y", null, null }, { "OK", null, null },
    };

    // ── Vocabulary ───────────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(EveryToken))]
    public void EveryToken_ReadsAsItsNewOutcome(string raw, string? outcome, string? reason)
    {
        foreach (var form in new[] { raw, raw.ToLowerInvariant(), $" {raw} " })
        {
            BankOutcomeVocabulary.Canonicalise(form).Should().Be(outcome, $"'{form}'");
            var reading = BankOutcomeVocabulary.Read(form);
            reading.Outcome.Should().Be(outcome, $"'{form}'");
            reading.Reason.Should().Be(reason, $"'{form}'");
        }
    }

    [Fact]
    public void KnownTokens_AreExactlyTheRecognisedRowsOfTheTable()
    {
        var recognised = EveryToken.Select(r => (string)r[0]).Where(t => BankOutcomeVocabularyExpectation(t) is not null);
        BankOutcomeVocabulary.KnownTokens.Should().BeEquivalentTo(recognised,
            "a word cannot be added to or dropped from the vocabulary without a row in this table");
    }

    private static string? BankOutcomeVocabularyExpectation(string token) =>
        EveryToken.Where(r => (string)r[0] == token).Select(r => (string?)r[1]).Single();

    [Fact]
    public void NoToken_IsInTwoSets()
    {
        var all = BankOutcomeVocabulary.TokenSets.SelectMany(s => s.Value.Select(t => (Set: s.Key, Token: t.ToUpperInvariant())));
        all.GroupBy(x => x.Token).Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} in {string.Join(" + ", g.Select(x => x.Set))}")
            .Should().BeEmpty("a word that means two things means nothing");
    }

    [Fact]
    public void NoPaidOrReturnedToken_IsTwoCharactersOrFewer()
    {
        var sets = BankOutcomeVocabulary.TokenSets;
        sets.Should().ContainKeys(Paid, Returned);
        foreach (var key in new[] { Paid, Returned })
            sets[key].Where(t => t.Trim().Length <= 2).Should().BeEmpty(
                $"a one- or two-letter code is too ambiguous to move a salary to {key}");
    }

    // ── Parsers carry the outcome and its reason ─────────────────────────────────────────────────

    private static string Csv(string status) => $"wpsReference,amount,valueDate,status\nW1,100,2026-07-30,{status}\n";

    private static string Ack(string status) =>
        $"ACK|B1|1\nREC|W1|SA0380000000608010167519|100|{status}|||2026-07-30|BR1\nEOF|1\n";

    private static BankConfirmationParseResult ParseWith(string parserKey, string status) => parserKey == "wps-ack"
        ? new WpsAckBankConfirmationParser().Parse(Ack(status))
        : new GenericCsvBankConfirmationParser().Parse(Csv(status));

    [Theory]
    [InlineData("generic-csv")]
    [InlineData("wps-ack")]
    public void Parsers_CarryTheAcceptedOnlyReason(string parserKey)
    {
        var parse = ParseWith(parserKey, "SUCCESS");

        parse.Errors.Should().BeEmpty("an accepted-file word is understood, just not proof of credit");
        parse.Rows.Single().Outcome.Should().Be(Pending);
        parse.Rows.Single().OutcomeReason.Should().Be(AcceptedOnly);
    }

    [Theory]
    [InlineData("generic-csv", "N")]
    [InlineData("generic-csv", "YES")]
    [InlineData("wps-ack", "N")]
    [InlineData("wps-ack", "YES")]
    public void Parsers_ReportAmbiguousWordsAsUnknown(string parserKey, string raw)
    {
        var parse = ParseWith(parserKey, raw);

        parse.Rows.Single().Outcome.Should().BeNull();
        parse.Errors.Should().ContainSingle(e => e.Error == "unknown_outcome");
    }

    // ── Import service ───────────────────────────────────────────────────────────────────────────

    private static ZayraDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static (Guid TenantId, Guid BatchId, PayrollPaymentRecord Record) Seed(ZayraDbContext db)
    {
        var tenantId = Guid.NewGuid();
        var today = DateTime.UtcNow;
        var run = new PayrollRun
        {
            TenantId = tenantId, CompanyId = Guid.NewGuid(), Year = today.Year, Month = today.Month, Status = "Locked",
        };
        db.PayrollRuns.Add(run);
        var batch = new PayrollPaymentBatch { TenantId = tenantId, PayrollRunId = run.Id };
        db.PayrollPaymentBatches.Add(batch);
        var record = new PayrollPaymentRecord
        {
            TenantId = tenantId, PaymentBatchId = batch.Id, EmployeeId = 1, Amount = 100m,
            Iban = "SA0380000000608010167519", Status = "Pending", WpsReference = "W1",
        };
        db.PayrollPaymentRecords.Add(record);
        db.SaveChanges();
        return (tenantId, batch.Id, record);
    }

    private static async Task<BankConfirmationImportResult> Import(
        ZayraDbContext db, Guid tenantId, Guid batchId, string parserKey, string status)
    {
        var service = new BankConfirmationService(db,
            new IBankConfirmationParser[] { new GenericCsvBankConfirmationParser(), new WpsAckBankConfirmationParser() });
        var content = parserKey == "wps-ack" ? Ack(status) : Csv(status);
        var (result, refusal) = await service.ImportAsync(tenantId, batchId, "bank.txt", content, parserKey,
            new BankConfirmationImportOptions(), Guid.NewGuid(), "Payroll Officer", default);
        refusal.Should().BeNull();
        await db.SaveChangesAsync();
        return result!;
    }

    private static string Json(object o) => JsonSerializer.Serialize(o);

    [Theory]
    [InlineData("generic-csv", "N")]
    [InlineData("generic-csv", "YES")]
    [InlineData("wps-ack", "N")]
    [InlineData("wps-ack", "YES")]
    public async Task Import_AmbiguousWord_ChangesNothing_AndIsListedAsUnknownOutcome(string parserKey, string raw)
    {
        using var db = NewDb();
        var (tenantId, batchId, record) = Seed(db);

        var result = await Import(db, tenantId, batchId, parserKey, raw);

        result.PaidApplied.Should().Be(0);
        result.ReturnedApplied.Should().Be(0);
        result.PendingReported.Should().Be(0);
        Json(result.ParseErrors).Should().Contain("unknown_outcome");
        result.Unmatched.Should().ContainSingle();
        Json(result.Unmatched).Should().Contain("\"reason\":\"unknown_outcome\"");
        record.Status.Should().Be("Pending");
        db.BankPaymentConfirmations.Should().BeEmpty("an unrecognised word confirms nothing");
        result.Coverage.PaidCount.Should().Be(0);
        result.Coverage.ReturnedCount.Should().Be(0);
    }

    [Theory]
    [InlineData("generic-csv")]
    [InlineData("wps-ack")]
    public async Task Import_Success_IsInformational_AcceptedCreditNotConfirmed_StatusUnchanged(string parserKey)
    {
        using var db = NewDb();
        var (tenantId, batchId, record) = Seed(db);

        var result = await Import(db, tenantId, batchId, parserKey, "SUCCESS");

        result.ParseErrors.Should().BeEmpty("SUCCESS is understood, it is just not proof of credit");
        result.Unmatched.Should().BeEmpty();
        result.PaidApplied.Should().Be(0);
        result.PendingReported.Should().Be(1);
        result.AcceptedCreditNotConfirmed.Should().Be(1);
        record.Status.Should().Be("Pending", "a file the bank accepted is not money in the employee's account");
        result.Coverage.PaidCount.Should().Be(0);

        var evidence = db.BankPaymentConfirmations.Single();
        evidence.Outcome.Should().Be(Pending);
        evidence.Applied.Should().BeFalse();
        evidence.HoldReason.Should().Be(AcceptedOnly);
        evidence.RawOutcome.Should().Be("SUCCESS");
    }

    [Theory]
    [InlineData("generic-csv", "ACSP")]
    [InlineData("generic-csv", "ACWP")]
    [InlineData("wps-ack", "ACSP")]
    [InlineData("wps-ack", "ACWP")]
    public async Task Import_SettlementInProcess_IsInformational_PendingInformational(string parserKey, string raw)
    {
        using var db = NewDb();
        var (tenantId, batchId, record) = Seed(db);

        var result = await Import(db, tenantId, batchId, parserKey, raw);

        result.ParseErrors.Should().BeEmpty();
        result.PendingReported.Should().Be(1);
        result.AcceptedCreditNotConfirmed.Should().Be(0);
        record.Status.Should().Be("Pending");
        db.BankPaymentConfirmations.Single().HoldReason.Should().Be(InProcess);
    }

    [Theory]
    [InlineData("generic-csv")]
    [InlineData("wps-ack")]
    public async Task Import_Paid_StillMarksTheRecordPaid(string parserKey)
    {
        using var db = NewDb();
        var (tenantId, batchId, record) = Seed(db);

        var result = await Import(db, tenantId, batchId, parserKey, "PAID");

        result.PaidApplied.Should().Be(1);
        record.Status.Should().Be(PaymentRecordStatuses.Paid);
    }

    // ── Dashboard: an accepted-only file must not produce a pay date ────────────────────────────

    private static async Task<string?> DashboardPayDate(ZayraDbContext db, Guid tenantId)
    {
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Vocabulary", Slug = $"vocab-{tenantId:N}" });
        db.SaveChanges();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim("permission", "payroll.read"),
        }, "test"));
        var ctrl = new DashboardController(db,
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
            new VocabularyUnrestrictedScope())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } },
        };
        var full = Assert.IsType<DashboardFullDto>(Assert.IsType<OkObjectResult>(await ctrl.Full(6)).Value);
        return full.Overview.PayrollSummary!.PayDate;
    }

    [Theory]
    [InlineData("SUCCESS", null)]
    [InlineData("YES", null)]
    [InlineData("PAID", "2026-07-30")]
    public async Task Dashboard_PayDate_ComesOnlyFromAnUnambiguousPaidWord(string raw, string? expectedPayDate)
    {
        using var db = NewDb();
        var (tenantId, batchId, _) = Seed(db);

        await Import(db, tenantId, batchId, "generic-csv", raw);

        (await DashboardPayDate(db, tenantId)).Should().Be(expectedPayDate);
    }
}

file sealed class VocabularyUnrestrictedScope : IDataScopeService
{
    public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization, AllowedEmployeeIds = null });
}
