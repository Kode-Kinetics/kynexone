using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.FieldEncryption;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.FieldEncryption;

public class FieldEncryptionBackfillTests
{
    /// <summary>Rows written BEFORE encryption existed: plaintext only (mode Off, no interceptor).</summary>
    private static async Task SeedLegacyAsync(FeKit kit, Guid tenant, int employees)
    {
        await using var db = kit.Db(FieldEncryptionMode.Off, interceptors: false);
        for (var i = 0; i < employees; i++)
            db.Employees.Add(FeKit.NewEmployee(tenant, $"L{i}", iqama: $"21000000{i:D2}", iban: $"SA03800000006080101{i:D5}"));
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile { TenantId = tenant, EmployeeId = 1, Iban = "SA0380000000608010167519", AccountNumber = "6080101" });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Backfill_EncryptsEveryExistingRow_ThenVerifyProvesCiphertextMatchesPlaintext()
    {
        var kit = new FeKit(); var t = Guid.NewGuid();
        await SeedLegacyAsync(kit, t, 5);
        await using var db = kit.Db();
        var job = kit.Backfill(db);

        (await job.ProgressAsync(t, default)).Where(p => p.Entity == "Employee" && p.Field == "IqamaNumber").Single().Remaining.Should().Be(5);
        var done = await job.EncryptMissingAsync(t, 1000, default);
        done.Complete.Should().BeTrue();
        done.Encrypted.Should().Be(5 + 5 + 1 + 1);

        (await job.ProgressAsync(t, default)).Should().OnlyContain(p => p.Remaining == 0);
        var report = await job.VerifyAsync(t, 10_000, null, default);
        report.Complete.Should().BeTrue();
        report.MismatchTotal.Should().Be(0);
        report.Ok.Should().Be(12);
    }

    [Fact]
    public async Task Backfill_IsIdempotent_AndResumableInSlices()
    {
        var kit = new FeKit(); var t = Guid.NewGuid();
        await SeedLegacyAsync(kit, t, 7);
        var total = 0; var slices = 0;
        BackfillResult r;
        do
        {
            await using var db = kit.Db();   // a fresh context each slice == a restarted process
            r = await kit.Backfill(db).EncryptMissingAsync(t, 4, default);
            total += r.Encrypted; slices++;
        } while (!r.Complete && slices < 20);
        total.Should().Be(7 + 7 + 1 + 1);
        slices.Should().BeGreaterThan(1);

        await using var again = kit.Db();
        (await kit.Backfill(again).EncryptMissingAsync(t, 1000, default)).Encrypted.Should().Be(0, "a second run finds nothing to do");
    }

    [Fact]
    public async Task Backfill_TouchesOnlyTheNamedTenant()
    {
        var kit = new FeKit(); var a = Guid.NewGuid(); var b = Guid.NewGuid();
        await SeedLegacyAsync(kit, a, 2); await SeedLegacyAsync(kit, b, 2);
        await using var db = kit.Db();
        await kit.Backfill(db).EncryptMissingAsync(a, 1000, default);
        (await kit.Backfill(db).ProgressAsync(b, default)).Where(p => p.Entity == "Employee").Should().OnlyContain(p => p.Encrypted == 0);
        kit.Store.Snapshot().Select(k => k.TenantId).Should().BeEquivalentTo([a], "tenant b has no key until it is backfilled");
    }

    [Fact]
    public async Task Backfill_RefusesToRunBeforeDualWrite()
    {
        var kit = new FeKit(FieldEncryptionMode.Off); var t = Guid.NewGuid();
        await using var db = kit.Db();
        Func<Task> act = async () => await kit.Backfill(db).EncryptMissingAsync(t, 10, default);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*DualWrite*");
    }

    [Fact]
    public async Task Verify_ReportsEachKindOfDrift_WithoutLeakingValues()
    {
        var kit = new FeKit(); var t = Guid.NewGuid();
        await SeedLegacyAsync(kit, t, 4);
        await using var db = kit.Db();
        await kit.Backfill(db).EncryptMissingAsync(t, 1000, default);

        await using (var tamper = kit.Db(interceptors: false))
        {
            var emps = await tamper.Employees.OrderBy(e => e.EmployeeCode).ToListAsync();
            emps[0].IqamaNumber = "CHANGED-PLAINTEXT";                                         // plaintext moved, ciphertext did not
            tamper.Entry(emps[1]).Property("IqamaNumberEnc").CurrentValue = new byte[] { 1, 1, 9, 9 }; // garbage envelope
            tamper.Entry(emps[2]).Property("IqamaNumberBidx").CurrentValue = new byte[32];            // wrong index
            tamper.Entry(emps[3]).Property("IqamaNumberEnc").CurrentValue = null;                     // ciphertext lost
            await tamper.SaveChangesAsync();
        }

        var report = await kit.Backfill(db).VerifyAsync(t, 10_000, null, default);
        var iq = report.Mismatches.Where(m => m.Field == "IqamaNumber").Select(m => m.Kind).ToList();
        iq.Should().BeEquivalentTo([MismatchKind.PlaintextDiffers, MismatchKind.UndecryptableCiphertext, MismatchKind.BlindIndexDiffers, MismatchKind.MissingCiphertext]);
        System.Text.Json.JsonSerializer.Serialize(report).Should().NotContain("CHANGED-PLAINTEXT").And.NotContain("2100000000");
    }

    [Fact]
    public async Task Verify_IsResumable_AcrossSlices_AndFindsTheSameThingAsOneBigRun()
    {
        var kit = new FeKit(); var t = Guid.NewGuid();
        await SeedLegacyAsync(kit, t, 6);
        await using var db = kit.Db();
        await kit.Backfill(db).EncryptMissingAsync(t, 1000, default);
        IReadOnlyDictionary<string, int>? cursor = null; var checkedRows = 0; VerifyReport rep; var n = 0;
        do { rep = await kit.Backfill(db).VerifyAsync(t, 5, cursor, default); cursor = rep.Cursor; checkedRows += rep.RowsChecked; n++; } while (!rep.Complete && n < 50);
        rep.Complete.Should().BeTrue();
        checkedRows.Should().Be(6 + 6 + 1 + 1);
        rep.MismatchTotal.Should().Be(0);
    }

    [Fact]
    public async Task Rotation_ThenReencrypt_MovesEverythingToTheNewKey_AndVerifyStaysClean()
    {
        var kit = new FeKit(); var t = Guid.NewGuid();
        await SeedLegacyAsync(kit, t, 3);
        await using var db = kit.Db();
        var job = kit.Backfill(db);
        await job.EncryptMissingAsync(t, 1000, default);
        await kit.Keys.RotateDekAsync(t, default);

        var moved = await job.ReencryptToActiveAsync(t, 1000, default);
        moved.Encrypted.Should().Be(3 + 3 + 1 + 1);
        (await job.ReencryptToActiveAsync(t, 1000, default)).Encrypted.Should().Be(0);
        await using var read = kit.Db(interceptors: false);
        (await read.Employees.ToListAsync()).Should().OnlyContain(e => ((byte[])read.Entry(e).Property("IqamaNumberEnc").CurrentValue!)[1] == 2);
        (await job.VerifyAsync(t, 10_000, null, default)).MismatchTotal.Should().Be(0);
    }

    [Fact]
    public async Task EveryReader_StillSeesPlaintext_InDualWrite_SoDedupAndBankFilesKeepWorking()
    {
        var kit = new FeKit(); var t = Guid.NewGuid();
        await using (var w = kit.Db())
        {
            w.Employees.Add(FeKit.NewEmployee(t, "E1", iqama: "2123456789", iban: "SA0380000000608010167519"));
            await w.SaveChangesAsync();
        }
        await using var r = kit.Db();
        var det = new Zayra.Api.Infrastructure.Employees.EmployeeDuplicateDetector(r);
        var probe = (await r.Employees.AsNoTracking().SingleAsync());
        (await r.Employees.Where(e => e.TenantId == t && e.IqamaNumber == "2123456789").ToListAsync()).Should().ContainSingle();
        probe.BankIban.Should().Be("SA0380000000608010167519");
        _ = det;
    }
}
