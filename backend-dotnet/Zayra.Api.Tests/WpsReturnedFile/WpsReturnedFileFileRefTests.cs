using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Government;
using Zayra.Api.Models;
using static Zayra.Api.Tests.WpsReturnedFile.WpsReturnedFileImportPathTests;

namespace Zayra.Api.Tests.WpsReturnedFile;

/// <summary>
/// The FILE-REF memory: a bank file reference binds to ONE payment batch of the company. The bank's later status file for the
/// SAME batch (Pending first, Paid later) carries the same reference and is accepted; the same reference on another batch is
/// refused; the identical file again changes nothing. Synthetic data only.
/// </summary>
public class WpsReturnedFileFileRefTests
{
    [Fact]
    public async Task The_banks_later_status_file_for_the_same_batch_with_the_same_file_reference_is_applied()
    {
        using var db = NewDb();
        var s = Seed(db, 3);
        var pending = WpsReturnedFileOracle.Build(s.Rows.Select(r => r with { Status = "PENDING" }).ToList());
        var (first, none) = await Import(db, s, pending);
        none.Should().BeNull();
        first!.PendingReported.Should().Be(3);
        db.SaveChanges();

        // Same FILE-REF (the oracle default), different content: the bank now reports the lines paid.
        var paid = WpsReturnedFileOracle.Build(s.Rows.Select(r => r with { Status = "PAID" }).ToList());
        var (second, refusal) = await Import(db, s, paid);
        db.SaveChanges();

        refusal.Should().BeNull("the reference binds to this batch, so the bank's follow-up for it is accepted");
        second!.PaidApplied.Should().Be(3);
        s.Records.Should().OnlyContain(r => Reload(db, r).Status == "Paid");
    }

    [Fact]
    public async Task The_identical_file_again_changes_nothing()
    {
        using var db = NewDb();
        var s = Seed(db, 3);
        var file = WpsReturnedFileOracle.Build(s.Rows.Select(r => r with { Status = "PENDING" }).ToList());
        await Import(db, s, file);
        db.SaveChanges();

        var (again, _) = await Import(db, s, file);
        again!.Duplicate.Should().BeTrue();
        again.PendingReported.Should().Be(0);
    }
}

/// <summary>The import path on a real PostgreSQL database: the FILE-REF memory reads the json audit column without a text search in SQL.</summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class WpsReturnedFileImportPostgresTests(PostgresFixture fixture)
{
    private const string Est = "7-1234567";

    private sealed record PgSeeded(Guid TenantId, Guid CompanyId, Guid BatchId, IReadOnlyList<SynthRow> Rows, IReadOnlyList<Guid> RecordIds);

    private static async Task<PgSeeded> SeedBatchAsync(ZayraDbContext db, Guid tenantId, Guid companyId, int month, int offset, string tag)
    {
        var run = new PayrollRun { TenantId = tenantId, CompanyId = companyId, Year = 2026, Month = month, Status = "Locked" };
        db.PayrollRuns.Add(run);
        var batch = new PayrollPaymentBatch
        {
            TenantId = tenantId, PayrollRunId = run.Id, Currency = "SAR", BatchNumber = $"B-{tag}-{month}",
            CreatedAtUtc = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc),
        };
        db.PayrollPaymentBatches.Add(batch);
        var rows = WpsReturnedFileOracle.Rows(3 + offset).Skip(offset).ToList();
        var employees = rows.Select((r, i) => new Employee
        {
            TenantId = tenantId, CompanyId = companyId, EmployeeCode = $"E{offset + i}-{tag}", FullName = $"Employee {offset + i}",
            EnglishName = $"Employee {offset + i}", Status = "Active", JoiningDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            IdNumber = r.MolId, Nationality = r.MolId[0] == '1' ? "Saudi" : "Egyptian",
        }).ToList();
        db.Employees.AddRange(employees);
        await db.SaveChangesAsync();
        var records = rows.Select((r, i) => new PayrollPaymentRecord
        {
            TenantId = tenantId, PaymentBatchId = batch.Id, EmployeeId = employees[i].Id, Amount = r.Net, Iban = r.Iban,
            Status = "Pending", WpsReference = $"WPS-{employees[i].EmployeeCode}-2026{month:D2}",
        }).ToList();
        db.PayrollPaymentRecords.AddRange(records);
        await db.SaveChangesAsync();
        return new PgSeeded(tenantId, companyId, batch.Id, rows, records.Select(r => r.Id).ToList());
    }

    private static async Task<Guid> SeedCompanyAsync(ZayraDbContext db, Guid tenantId)
    {
        var company = new Company { TenantId = tenantId, LegalNameEn = "Synthetic Co", CountryCode = "SA", DefaultCurrency = "SAR", WpsEmployerId = Est };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        return company.Id;
    }

    private BankConfirmationService Service(ZayraDbContext db) =>
        new(db, new IBankConfirmationParser[] { new WpsReturnedFileBankConfirmationParser(statusColumnVerified: true) },
            new ReturnedFileClaim(new GovernmentExchangeRecorder(fixture.CreateDb)));

    private async Task<(BankConfirmationImportResult? Result, JournalExportRefusal? Refusal)> ImportAndSave(PgSeeded s, string content)
    {
        await using var db = fixture.CreateDb();
        var service = Service(db);
        var outcome = await service.ImportAsync(s.TenantId, s.BatchId, "returned.txt", content, "wps-returned-file",
            new BankConfirmationImportOptions(), Guid.NewGuid(), "Payroll Officer", default);
        if (outcome.Result is { Duplicate: false }) await service.SaveAsync(outcome.Result, default);
        return outcome;
    }

    [Fact]
    public async Task On_postgres_a_file_reference_binds_to_its_batch_follow_ups_apply_and_another_batch_is_refused()
    {
        Guid tenantId;
        PgSeeded a, b;
        await using (var db = fixture.CreateDb())
        {
            tenantId = await PostgresFixture.SeedMinimalTenant(db);
            var companyId = await SeedCompanyAsync(db, tenantId);
            var tag = Guid.NewGuid().ToString("N")[..6];
            a = await SeedBatchAsync(db, tenantId, companyId, 9, 0, tag);
            b = await SeedBatchAsync(db, tenantId, companyId, 10, 10, tag);
        }

        // 1. The bank's first file for batch A: still pending. It runs on Postgres and remembers the reference.
        var (first, firstRefusal) = await ImportAndSave(a, WpsReturnedFileOracle.Build(a.Rows.Select(r => r with { Status = "PENDING" }).ToList()));
        firstRefusal.Should().BeNull();
        first!.PendingReported.Should().Be(3);

        // 2. Its later status file: same reference, same batch, now paid.
        var (second, secondRefusal) = await ImportAndSave(a, WpsReturnedFileOracle.Build(a.Rows.Select(r => r with { Status = "PAID" }).ToList()));
        secondRefusal.Should().BeNull();
        second!.PaidApplied.Should().Be(3);

        // 3. The same reference on another batch of the company: refused, nothing applied.
        var (third, thirdRefusal) = await ImportAndSave(b, WpsReturnedFileOracle.Build(b.Rows.Select(r => r with { Status = "PAID" }).ToList()));
        third.Should().BeNull();
        JsonSerializer.Serialize(thirdRefusal!.Payload).Should().Contain("file_ref_already_imported");

        await using var check = fixture.CreateDb();
        (await check.PayrollPaymentRecords.AsNoTracking().Where(r => r.TenantId == tenantId && r.PaymentBatchId == a.BatchId).ToListAsync())
            .Should().OnlyContain(r => r.Status == "Paid");
        (await check.PayrollPaymentRecords.AsNoTracking().Where(r => r.TenantId == tenantId && r.PaymentBatchId == b.BatchId).ToListAsync())
            .Should().OnlyContain(r => r.Status == "Pending");
    }
}
