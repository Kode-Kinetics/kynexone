using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The Rejected import path on PostgreSQL (the in-memory provider proves the rules; this proves the real columns, the
/// settlement-time read on finance_gl_entries and the hold reason survive a round trip). A rejection imported after the
/// batch's settlement journal is Rejected, stored as Rejected, and reported as needing the GL correction; a later Paid word
/// for it is refused without a reasoned reversal.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class BankConfirmationRejectedPostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ARejectionAfterSettlement_IsStoredAsRejected_AndReportedForTheGlCorrection()
    {
        Guid tenant, batchId, recordId;
        await using (var seed = fixture.CreateDb())
        {
            tenant = await PostgresFixture.SeedMinimalTenant(seed);
            var company = new Company
            {
                TenantId = tenant, LegalNameEn = "Rejected Path Co", CountryCode = "SAU", Jurisdiction = "KSA-mainland",
                RegistrationNumber = $"RJ-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true,
                CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            };
            seed.Companies.Add(company);
            await seed.SaveChangesAsync();
            var run = new PayrollRun { TenantId = tenant, CompanyId = company.Id, Year = 2026, Month = 7, Status = "Locked" };
            var batch = new PayrollPaymentBatch
            {
                TenantId = tenant, PayrollRunId = run.Id, BatchNumber = $"PB-{Guid.NewGuid():N}"[..20], Currency = "SAR",
                TotalAmount = 300m, WpsStatus = WpsStatuses.Paid,
            };
            var paid = new PayrollPaymentRecord
            {
                TenantId = tenant, PaymentBatchId = batch.Id, EmployeeId = 1, Amount = 100m, Status = "Pending",
                Iban = "SA0380000000608010167519", WpsReference = "W1",
            };
            var rejected = new PayrollPaymentRecord
            {
                TenantId = tenant, PaymentBatchId = batch.Id, EmployeeId = 2, Amount = 200m, Status = "Pending",
                Iban = "SA4420000001234567891234", WpsReference = "W2",
            };
            seed.AddRange(run, batch, paid, rejected);
            seed.FinanceGlEntries.Add(new FinanceGlEntry
            {
                TenantId = tenant, CompanyId = company.Id, SourceModule = "Payroll", SourceEntityId = run.Id,
                SourceEntityRef = batch.BatchNumber, EventType = GlEventTypes.NetSettlement, DebitAccount = "2100 - Salaries Payable",
                CreditAccount = "1000 - Cash/Bank", Description = "Net pay settlement", Amount = 300m, Currency = "SAR",
                EntryDate = new DateOnly(2026, 7, 31), Period = "2026-07", CreatedAtUtc = DateTime.UtcNow.AddHours(-2),
            });
            await seed.SaveChangesAsync();
            batchId = batch.Id;
            recordId = rejected.Id;
        }

        await using (var db = fixture.CreateDb())
        {
            var service = new BankConfirmationService(db, new IBankConfirmationParser[] { new GenericCsvBankConfirmationParser() });
            var (result, refusal) = await service.ImportAsync(tenant, batchId, "bank.csv",
                "wpsReference,amount,valueDate,status,reasonCode\nW1,100,2026-07-30,ACSC,\nW2,200,2026-07-30,RJCT,AC04\n",
                "generic-csv", new BankConfirmationImportOptions(), Guid.NewGuid(), "Payroll Officer", default);
            refusal.Should().BeNull();
            await db.SaveChangesAsync();

            result!.RejectedApplied.Should().Be(1);
            result.SettledByBank.Should().Be(1);
            result.ReturnedAmountAwaitingGlCorrection.Should().Be(0m);
            result.RejectedAmountNotPaid.Should().Be(200m);
            result.RejectedAfterSettlementAwaitingGlCorrection.Should().Be(200m);
        }

        await using (var read = fixture.CreateDb())
        {
            (await read.PayrollPaymentRecords.AsNoTracking().SingleAsync(r => r.Id == recordId)).Status.Should().Be(PaymentRecordStatuses.Rejected);
            var stored = await read.BankPaymentConfirmations.AsNoTracking().SingleAsync(c => c.PaymentRecordId == recordId);
            stored.Outcome.Should().Be(BankConfirmationOutcomes.Rejected);
            stored.Applied.Should().BeTrue();
            stored.ReasonCode.Should().Be("AC04");

            var service = new BankConfirmationService(read, new IBankConfirmationParser[] { new GenericCsvBankConfirmationParser() });
            var (later, _) = await service.ImportAsync(tenant, batchId, "later.csv",
                "wpsReference,amount,valueDate,status\nW2,200,2026-08-02,PAID\n",
                "generic-csv", new BankConfirmationImportOptions(), Guid.NewGuid(), "Payroll Officer", default);
            await read.SaveChangesAsync();
            later!.Refused.Should().ContainSingle();
            later.RejectedAfterSettlementAwaitingGlCorrection.Should().Be(200m);
            (await read.BankPaymentConfirmations.AsNoTracking().Where(c => c.PaymentRecordId == recordId).CountAsync()).Should().Be(2);
            (await read.PayrollPaymentRecords.AsNoTracking().SingleAsync(r => r.Id == recordId)).Status.Should().Be(PaymentRecordStatuses.Rejected);
        }
    }
}
