using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;
using Zayra.Api.Tests.WpsReturnedFile;
using static Zayra.Api.Tests.WpsReturnedFile.WpsReturnedFileImportPathTests;

namespace Zayra.Api.Tests;

/// <summary>
/// A duplicate refusal is held for a person whatever way the bank writes it (final gate P1-3 and P2-5): the ISO code DUPL,
/// the ISO code AM05 ("Duplication"), or reason text that says duplicate, and on the returned-file reader a DUPL written in
/// the status field. Holding is the only effect: nothing is marked Paid, nothing reads "not paid, still owed". Synthetic data.
/// </summary>
public class BankDuplicateRefusalTests
{
    // ── the shared catalogue ─────────────────────────────────────────────────

    [Fact]
    public void Am05_IsInTheCatalogue_InTheDuplicateVoice_AndNeverSaysOwed()
    {
        var text = BankStatusReasonCatalogue.Describe("AM05");

        text.IsKnown.Should().BeTrue();
        text.Code.Should().Be("AM05");
        text.English.Should().Contain("duplicate").And.Contain("original payment").And.NotContainEquivalentOf("owed");
        text.Arabic.Should().Contain("مكرر").And.NotContain("مستحق");
    }

    [Theory]
    [InlineData("DUPL", null)]
    [InlineData("dupl", null)]
    [InlineData("AM05", null)]
    [InlineData(" am05 ", null)]
    [InlineData(null, "Duplicate payment")]
    [InlineData(null, "rejected: DUPLICATED instruction")]
    [InlineData("AC04", "duplicate of an earlier transfer")]
    [InlineData(null, "DUPLI\u200BCATE")]          // a zero-width character inside the word does not hide it
    public void ADuplicateRefusal_IsRecognised(string? code, string? text) =>
        BankStatusReasonCatalogue.IsDuplicate(code, text).Should().BeTrue();

    [Theory]
    [InlineData("AC04", "Account closed")]
    [InlineData(null, null)]
    [InlineData("AM04", "Insufficient funds")]
    public void OtherReasons_AreNotDuplicates(string? code, string? text) =>
        BankStatusReasonCatalogue.IsDuplicate(code, text).Should().BeFalse();

    // ── the generic import ───────────────────────────────────────────────────

    private static ZayraDbContext Db() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static (Guid TenantId, PayrollPaymentBatch Batch, PayrollPaymentRecord Record) SeedOne(ZayraDbContext db, string status)
    {
        var tenantId = Guid.NewGuid();
        var run = new PayrollRun { TenantId = tenantId, CompanyId = Guid.NewGuid(), Year = 2026, Month = 7, Status = "Locked" };
        var batch = new PayrollPaymentBatch { TenantId = tenantId, PayrollRunId = run.Id, BatchNumber = "PB-1", Currency = "SAR", TotalAmount = 100m };
        var record = new PayrollPaymentRecord
        {
            TenantId = tenantId, PaymentBatchId = batch.Id, EmployeeId = 1, Amount = 100m, Status = status,
            Iban = "SA0380000000608010167519", WpsReference = "W1",
        };
        db.AddRange(run, batch, record);
        db.SaveChanges();
        return (tenantId, batch, record);
    }

    private static async Task<BankConfirmationImportResult> ImportCsv(ZayraDbContext db, Guid tenantId, Guid batchId, string csv,
        BankConfirmationImportOptions? options = null)
    {
        var service = new BankConfirmationService(db, new IBankConfirmationParser[] { new GenericCsvBankConfirmationParser() });
        var (result, refusal) = await service.ImportAsync(tenantId, batchId, $"b-{Guid.NewGuid():N}.csv", csv, "generic-csv",
            options ?? new BankConfirmationImportOptions(), Guid.NewGuid(), "Payroll Officer", default);
        refusal.Should().BeNull();
        await db.SaveChangesAsync();
        return result!;
    }

    [Theory]
    [InlineData("Pending", "REJECTED", "AM05", "")]
    [InlineData("Pending", "RJCT", "", "Duplicate payment")]
    [InlineData("Paid", "RETURNED", "AM05", "")]
    [InlineData("Pending", "PAID", "", "duplicate instruction")]
    public async Task AnAm05OrDuplicateTextLine_IsHeldForAPerson_AndChangesNothing(string previous, string word, string code, string text)
    {
        using var db = Db();
        var (tenantId, batch, record) = SeedOne(db, previous);

        var result = await ImportCsv(db, tenantId, batch.Id,
            $"wpsReference,amount,status,reasonCode,reason\nW1,100,{word},{code},{text}\n");

        record.Status.Should().Be(previous, "a duplicate refusal never changes the status by itself");
        result.RejectedApplied.Should().Be(0);
        result.PaidApplied.Should().Be(0);
        result.RejectedAmountNotPaid.Should().Be(0m);
        JsonSerializer.Serialize(result.HeldForPerson).Should().Contain(BankOutcomeHolds.DuplicateRejection);
        db.BankPaymentConfirmations.Single().HoldReason.Should().Be(BankOutcomeHolds.DuplicateRejection);
    }

    [Fact]
    public async Task AnAm05Hold_ResolvesThroughTheDuplicateExit_Only()
    {
        using var db = Db();
        var (tenantId, batch, record) = SeedOne(db, "Pending");
        await ImportCsv(db, tenantId, batch.Id, "wpsReference,amount,status,reasonCode\nW1,100,RJCT,AM05\n");

        var options = new BankConfirmationImportOptions(Exits: new[]
        {
            new BankLineExit(BankLineExitKinds.ConfirmDuplicateNotPaid, record.Id, null, "Statement shows no earlier credit", "STMT-1"),
        });
        var result = await ImportCsv(db, tenantId, batch.Id, "wpsReference,amount,status,reasonCode,valueDate\nW1,100,RJCT,AM05,2026-08-01\n", options);

        record.Status.Should().Be(PaymentRecordStatuses.Rejected);
        result.HeldForPerson.Should().BeEmpty();
    }

    // ── the returned-file reader: DUPL in the status field ───────────────────

    [Fact]
    public async Task ADuplInTheStatusField_IsAHeldLine_WithABlockerAndTheDuplicateExit_NeverPaid()
    {
        using var db = NewDb();
        var s = Seed(db, 2);
        var rows = s.Rows.Select(r => r with { Status = "PAID" }).ToList();
        rows[0] = rows[0] with { Status = "DUPL" };

        var (result, refusal) = await Import(db, s, WpsReturnedFileOracle.Build(rows));
        refusal.Should().BeNull();
        await db.SaveChangesAsync();

        Reload(db, s.Records[0]).Status.Should().Be("Pending", "held: nothing is applied and nothing is marked Paid");
        result!.PaidApplied.Should().Be(1);
        var held = db.BankPaymentConfirmations.Single(c => c.PaymentRecordId == s.Records[0].Id);
        held.Applied.Should().BeFalse();
        held.HoldReason.Should().Be(BankOutcomeHolds.DuplicateRejection);
        JsonSerializer.Serialize(result.HeldForPerson).Should().Contain(BankOutcomeHolds.DuplicateRejection);
        JsonSerializer.Serialize(result.ParseErrors).Should().Contain("held_for_person");

        // The duplicate exit resolves it, through a later file of the bank that carries the line again.
        var later = WpsReturnedFileOracle.Build(rows, new WpsReturnedFileOracle.Header(FileRef: "WPS2026090077"));
        var options = new BankConfirmationImportOptions(Exits: new[]
        {
            new BankLineExit(BankLineExitKinds.ConfirmOriginalPaymentReceived, s.Records[0].Id, null, "Earlier credit on the statement", "STMT-77"),
        });
        var (resolved, refused) = await Import(db, s, later, options);
        refused.Should().BeNull();
        await db.SaveChangesAsync();
        Reload(db, s.Records[0]).Status.Should().Be("Paid");
        resolved!.HeldForPerson.Should().BeEmpty();
    }
}
