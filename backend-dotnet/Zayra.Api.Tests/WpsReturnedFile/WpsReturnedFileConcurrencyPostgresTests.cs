using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Government;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.WpsReturnedFile;

/// <summary>
/// B5: imports racing. Each request reads (hash probe, FILE-REF memory) and only then saves, so the deterministic worst
/// interleaving is "both read, then both save"; a parallel race is run too. The returned-file claim (a unique ledger row written
/// before anything is applied) makes exactly one apply. Synthetic data only.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class WpsReturnedFileConcurrencyPostgresTests(PostgresFixture fixture)
{
    private static async Task<(Guid BatchId, IReadOnlyList<SynthRow> Rows)> SeedBatchAsync(ZayraDbContext db, Guid tenantId, Guid companyId, int month, int offset, string tag)
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
        db.PayrollPaymentRecords.AddRange(rows.Select((r, i) => new PayrollPaymentRecord
        {
            TenantId = tenantId, PaymentBatchId = batch.Id, EmployeeId = employees[i].Id, Amount = r.Net, Iban = r.Iban,
            Status = "Pending", WpsReference = $"WPS-{employees[i].EmployeeCode}-2026{month:D2}",
        }));
        await db.SaveChangesAsync();
        return (batch.Id, rows);
    }

    private BankConfirmationService Service(ZayraDbContext db) =>
        new(db, new IBankConfirmationParser[] { new WpsReturnedFileBankConfirmationParser(statusColumnVerified: true) },
            new ReturnedFileClaim(new GovernmentExchangeRecorder(fixture.CreateDb)));

    private async Task<(Guid TenantId, (Guid BatchId, IReadOnlyList<SynthRow> Rows) A, (Guid BatchId, IReadOnlyList<SynthRow> Rows) B)> SeedTwoBatchesAsync()
    {
        await using var db = fixture.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var company = new Company { TenantId = tenantId, LegalNameEn = "Synthetic Co", CountryCode = "SA", DefaultCurrency = "SAR", WpsEmployerId = "7-1234567" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var tag = Guid.NewGuid().ToString("N")[..6];
        return (tenantId, await SeedBatchAsync(db, tenantId, company.Id, 9, 0, tag), await SeedBatchAsync(db, tenantId, company.Id, 10, 10, tag));
    }

    private static string Paid(IReadOnlyList<SynthRow> rows) => WpsReturnedFileOracle.Build(rows.Select(r => r with { Status = "PAID" }).ToList());

    [Fact]
    public async Task Two_imports_of_one_file_reference_onto_two_batches_racing_exactly_one_applies()
    {
        Guid tenantId, companyId;
        (Guid BatchId, IReadOnlyList<SynthRow> Rows) a, b;
        await using (var db = fixture.CreateDb())
        {
            tenantId = await PostgresFixture.SeedMinimalTenant(db);
            var company = new Company { TenantId = tenantId, LegalNameEn = "Synthetic Co", CountryCode = "SA", DefaultCurrency = "SAR", WpsEmployerId = "7-1234567" };
            db.Companies.Add(company);
            await db.SaveChangesAsync();
            companyId = company.Id;
            var tag = Guid.NewGuid().ToString("N")[..6];
            a = await SeedBatchAsync(db, tenantId, companyId, 9, 0, tag);
            b = await SeedBatchAsync(db, tenantId, companyId, 10, 10, tag);
        }

        await using var first = fixture.CreateDb();
        await using var second = fixture.CreateDb();
        var fileA = WpsReturnedFileOracle.Build(a.Rows.Select(r => r with { Status = "PAID" }).ToList());
        var fileB = WpsReturnedFileOracle.Build(b.Rows.Select(r => r with { Status = "PAID" }).ToList());   // same FILE-REF

        // Both requests read before either saves.
        var sa = Service(first);
        var sb = Service(second);
        var ra = await sa.ImportAsync(tenantId, a.BatchId, "a.txt", fileA, "wps-returned-file", new(), Guid.NewGuid(), "x", default);
        var rb = await sb.ImportAsync(tenantId, b.BatchId, "b.txt", fileB, "wps-returned-file", new(), Guid.NewGuid(), "x", default);
        var saved = 0;
        foreach (var (service, r) in new[] { (sa, ra), (sb, rb) })
        {
            if (r.Result is null) continue;
            try { await service.SaveAsync(r.Result, default); saved++; }
            catch (DbUpdateException) { }
        }

        saved.Should().Be(1, "one bank file reference belongs to one batch: exactly one of two racing imports may apply");
        await using var check = fixture.CreateDb();
        var paid = await check.PayrollPaymentRecords.AsNoTracking()
            .Where(r => r.TenantId == tenantId && (r.PaymentBatchId == a.BatchId || r.PaymentBatchId == b.BatchId) && r.Status == "Paid")
            .Select(r => r.PaymentBatchId).Distinct().CountAsync();
        paid.Should().Be(1);
    }

    [Fact]
    public async Task Two_imports_of_the_identical_file_onto_one_batch_racing_exactly_one_applies()
    {
        var (tenantId, a, _) = await SeedTwoBatchesAsync();
        await using var first = fixture.CreateDb();
        await using var second = fixture.CreateDb();
        var sa = Service(first);
        var sb = Service(second);

        var r1 = await sa.ImportAsync(tenantId, a.BatchId, "a.txt", Paid(a.Rows), "wps-returned-file", new(), Guid.NewGuid(), "x", default);
        var r2 = await sb.ImportAsync(tenantId, a.BatchId, "a.txt", Paid(a.Rows), "wps-returned-file", new(), Guid.NewGuid(), "x", default);

        new[] { r1.Result, r2.Result }.Count(r => r is not null).Should().Be(1);
        (r1.Refusal ?? r2.Refusal)!.Status.Should().Be(409);
        await sa.SaveAsync(r1.Result!, default);
        await using var check = fixture.CreateDb();
        (await check.BankPaymentConfirmations.AsNoTracking().CountAsync(c => c.TenantId == tenantId && c.PaymentBatchId == a.BatchId))
            .Should().Be(3, "one file applied once: one confirmation per record");
    }

    [Fact]
    public async Task Eight_parallel_imports_of_one_file_reference_across_two_batches_apply_exactly_once()
    {
        var (tenantId, a, b) = await SeedTwoBatchesAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            await gate.Task;
            var target = i % 2 == 0 ? a : b;
            await using var db = fixture.CreateDb();
            var service = Service(db);
            var (result, _) = await service.ImportAsync(tenantId, target.BatchId, "f.txt", Paid(target.Rows), "wps-returned-file", new(), Guid.NewGuid(), "x", default);
            if (result is null || result.Duplicate) return 0;
            await service.SaveAsync(result, default);
            return 1;
        })).ToArray();
        gate.SetResult();
        var applied = (await Task.WhenAll(runs)).Sum();

        applied.Should().Be(1);
        await using var check = fixture.CreateDb();
        (await check.PayrollPaymentRecords.AsNoTracking()
            .Where(r => r.TenantId == tenantId && (r.PaymentBatchId == a.BatchId || r.PaymentBatchId == b.BatchId) && r.Status == "Paid")
            .Select(r => r.PaymentBatchId).Distinct().CountAsync()).Should().Be(1);
        (await check.GovernmentExchanges.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(x => x.TenantId == tenantId && x.Body == "bank_file" && x.Operation == ReturnedFileClaim.BindOperation)).Should().Be(1);
    }

    [Fact]
    public async Task A_claim_is_never_written_inside_a_callers_open_transaction()
    {
        var (tenantId, a, _) = await SeedTwoBatchesAsync();
        await using var db = fixture.CreateDb();
        // The production provider retries, so a user transaction runs inside its execution strategy (as the product's own do).
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            var act = () => Service(db).ImportAsync(tenantId, a.BatchId, "a.txt", Paid(a.Rows), "wps-returned-file", new(), Guid.NewGuid(), "x", default);
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned_file_claim_in_transaction*");
            await tx.RollbackAsync();
        });
        await using var check = fixture.CreateDb();
        (await check.GovernmentExchanges.IgnoreQueryFilters().AsNoTracking().CountAsync(x => x.TenantId == tenantId)).Should().Be(0);
    }
}
