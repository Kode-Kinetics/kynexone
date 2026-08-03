using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Boot;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// POD-A3 — payroll audit trail tamper-evidence. Proves the PayrollAuditLog chain gets the SAME
/// guarantees as the central AuditLog chain: hash-chained on write, append-only, and independently
/// verifiable, plus the payroll-specific hardening (monotonic Seq in the digest, strict
/// unsealed-row detection, and the boot backfill for legacy rows).
/// </summary>
public class PayrollAuditHashChainTests
{
    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    // SQLite in-memory: the backfill uses ExecuteUpdateAsync, unsupported by the InMemory provider.
    private static (ZayraDbContext db, SqliteConnection conn) CreateSqliteDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static PayrollAuditLog Row(Guid tenantId, string action, string entityId) => new()
    {
        TenantId = tenantId,
        Action = action,
        EntityName = "PayrollRun",
        EntityId = entityId,
        UserId = Guid.NewGuid(),
        MetadataJson = "{\"k\":\"v\"}",
    };

    // ── Sealer ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sealer_ChainsMultipleRowsInOneSave_AndVerifies()
    {
        // The F2 fork case: two audit rows Added before a single save must chain, not fork.
        await using var db = CreateDb();
        var t = Guid.NewGuid();
        db.PayrollAuditLogs.Add(Row(t, "payroll.run.processed", "1"));
        db.PayrollAuditLogs.Add(Row(t, "payroll.run.approved", "1"));
        await db.SaveChangesAsync();

        var logs = await db.PayrollAuditLogs.Where(x => x.TenantId == t).OrderBy(x => x.Seq).ToListAsync();
        logs.Should().HaveCount(2);
        logs[0].EntryHash.Should().NotBeNullOrWhiteSpace();
        logs[0].PreviousHash.Should().BeEmpty("first row of a tenant chain is genesis");
        logs[0].Seq.Should().Be(1);
        logs[1].Seq.Should().Be(2);
        logs[1].PreviousHash.Should().Be(logs[0].EntryHash);
        AuditService.VerifyPayrollChain(logs).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Sealer_ContinuesChainAcrossSeparateSaves()
    {
        await using var db = CreateDb();
        var t = Guid.NewGuid();
        db.PayrollAuditLogs.Add(Row(t, "payroll.run.processed", "1"));
        await db.SaveChangesAsync();
        db.PayrollAuditLogs.Add(Row(t, "payroll.run.locked", "1"));
        await db.SaveChangesAsync();

        var logs = await db.PayrollAuditLogs.Where(x => x.TenantId == t).OrderBy(x => x.Seq).ToListAsync();
        logs.Should().HaveCount(2);
        logs[1].PreviousHash.Should().Be(logs[0].EntryHash, "the second save must read the persisted tail");
        logs[1].Seq.Should().Be(2);
        AuditService.VerifyPayrollChain(logs).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Sealer_IsolatesChainsPerTenant()
    {
        await using var db = CreateDb();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        db.PayrollAuditLogs.Add(Row(a, "payroll.run.processed", "1"));
        db.PayrollAuditLogs.Add(Row(b, "payroll.run.processed", "1"));
        await db.SaveChangesAsync();

        var chainA = await db.PayrollAuditLogs.Where(x => x.TenantId == a).OrderBy(x => x.Seq).ToListAsync();
        var chainB = await db.PayrollAuditLogs.Where(x => x.TenantId == b).OrderBy(x => x.Seq).ToListAsync();
        chainA.Single().Seq.Should().Be(1);
        chainB.Single().Seq.Should().Be(1, "each tenant has an independent genesis");
        chainA.Single().PreviousHash.Should().BeEmpty();
        chainB.Single().PreviousHash.Should().BeEmpty();
    }

    // ── Verifier ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Verifier_DetectsTamperedMetadata()
    {
        await using var db = CreateDb();
        var t = Guid.NewGuid();
        db.PayrollAuditLogs.Add(Row(t, "payroll.run.processed", "1"));
        await db.SaveChangesAsync();
        var logs = await db.PayrollAuditLogs.Where(x => x.TenantId == t).OrderBy(x => x.Seq).ToListAsync();

        logs[0].MetadataJson = "{\"k\":\"FORGED\"}";
        var report = AuditService.VerifyPayrollChain(logs);
        report.IsValid.Should().BeFalse();
        report.Failures.Should().ContainSingle(f => f.Reason == "entry_hash_mismatch");
    }

    [Fact]
    public async Task Verifier_DetectsDeletedMiddleRow_AsPreviousHashBreak()
    {
        await using var db = CreateDb();
        var t = Guid.NewGuid();
        db.PayrollAuditLogs.Add(Row(t, "a", "1"));
        db.PayrollAuditLogs.Add(Row(t, "b", "1"));
        db.PayrollAuditLogs.Add(Row(t, "c", "1"));
        await db.SaveChangesAsync();
        var logs = await db.PayrollAuditLogs.Where(x => x.TenantId == t).OrderBy(x => x.Seq).ToListAsync();

        // Excise the middle row: the row after the gap keeps a valid EntryHash but its PreviousHash
        // now points to a row that is no longer its predecessor.
        var withGap = new List<PayrollAuditLog> { logs[0], logs[2] };
        var report = AuditService.VerifyPayrollChain(withGap);
        report.IsValid.Should().BeFalse();
        report.Failures.Should().Contain(f => f.Reason == "previous_hash_mismatch");
    }

    [Fact]
    public async Task Verifier_TreatsUnsealedRowAsHardFailure()
    {
        // Strict-by-design: a blanked EntryHash is a failure, not a lenient "legacy" pass — this is
        // what closes the erase-to-legacy bypass.
        await using var db = CreateDb();
        var t = Guid.NewGuid();
        db.PayrollAuditLogs.Add(Row(t, "payroll.run.processed", "1"));
        await db.SaveChangesAsync();
        var logs = await db.PayrollAuditLogs.Where(x => x.TenantId == t).OrderBy(x => x.Seq).ToListAsync();

        logs[0].EntryHash = string.Empty;
        var report = AuditService.VerifyPayrollChain(logs);
        report.IsValid.Should().BeFalse();
        report.Failures.Should().ContainSingle(f => f.Reason == "unsealed_row");
    }

    [Fact]
    public void ComputePayrollHash_IsDeterministic_AndBindsSeq()
    {
        var t = Guid.NewGuid();
        var row = new PayrollAuditLog
        {
            TenantId = t, Action = "payroll.run.processed", EntityName = "PayrollRun", EntityId = "1",
            MetadataJson = "{}", Seq = 5, PreviousHash = "abc",
            CreatedAtUtc = AuditService.NormalizeUtcMicroseconds(DateTime.UtcNow),
        };
        var h1 = AuditService.ComputePayrollHash(row);
        AuditService.ComputePayrollHash(row).Should().Be(h1, "same inputs => same digest");

        row.Seq = 6;
        AuditService.ComputePayrollHash(row).Should().NotBe(h1, "Seq is bound into the digest, so reordering is detectable");
    }

    // ── Append-only guard ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task PayrollAuditLog_IsAppendOnly_OnUpdateAndDelete()
    {
        await using var db = CreateDb();
        var t = Guid.NewGuid();
        db.PayrollAuditLogs.Add(Row(t, "payroll.run.processed", "1"));
        await db.SaveChangesAsync();
        var log = await db.PayrollAuditLogs.SingleAsync(x => x.TenantId == t);

        log.MetadataJson = "{\"forged\":true}";
        var modify = () => db.SaveChangesAsync();
        await modify.Should().ThrowAsync<InvalidOperationException>().WithMessage("*audit_log_append_only_violation*");

        db.Entry(log).State = EntityState.Unchanged;
        db.PayrollAuditLogs.Remove(log);
        var delete = () => db.SaveChangesAsync();
        await delete.Should().ThrowAsync<InvalidOperationException>().WithMessage("*audit_log_append_only_violation*");
    }

    [Fact]
    public async Task SyncSave_SealsPayrollAudit_Too()
    {
        // The synchronous save path also seals (the sealer normalizes CreatedAtUtc itself, so the
        // hash is path-independent). This keeps existing sync seeders working AND immutable.
        await using var db = CreateDb();
        var t = Guid.NewGuid();
        db.PayrollAuditLogs.Add(Row(t, "payroll.run.processed", "1"));
        db.SaveChanges();

        var log = await db.PayrollAuditLogs.AsNoTracking().SingleAsync(x => x.TenantId == t);
        log.EntryHash.Should().NotBeNullOrWhiteSpace();
        log.Seq.Should().Be(1);
        AuditService.VerifyPayrollChain(new[] { log }).IsValid.Should().BeTrue();
    }

    // ── Backfill (relational / SQLite — ExecuteUpdate) ────────────────────────────────

    [Fact]
    public async Task Backfill_SealsLegacyRows_AndIsIdempotent()
    {
        var (db, conn) = CreateSqliteDb();
        try
        {
            var t = Guid.NewGuid();
            db.PayrollAuditLogs.Add(Row(t, "payroll.run.processed", "1"));
            db.PayrollAuditLogs.Add(Row(t, "payroll.run.approved", "1"));
            db.PayrollAuditLogs.Add(Row(t, "payroll.run.locked", "1"));
            await db.SaveChangesAsync();

            // Simulate legacy rows written before the feature: strip the chain fields.
            await db.PayrollAuditLogs.Where(x => x.TenantId == t)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.EntryHash, string.Empty)
                    .SetProperty(x => x.PreviousHash, string.Empty)
                    .SetProperty(x => x.Seq, 0L));
            db.ChangeTracker.Clear();

            var sealedCount = await PayrollAuditChainBackfill.RunAsync(db, NullLogger.Instance);
            sealedCount.Should().Be(3);

            var logs = await db.PayrollAuditLogs.AsNoTracking().Where(x => x.TenantId == t)
                .OrderBy(x => x.Seq).ToListAsync();
            logs.Select(x => x.Seq).Should().Equal(1, 2, 3);
            AuditService.VerifyPayrollChain(logs).IsValid.Should().BeTrue("backfill establishes a verifiable baseline");

            // Second run touches nothing.
            var again = await PayrollAuditChainBackfill.RunAsync(db, NullLogger.Instance);
            again.Should().Be(0, "backfill only ever writes rows whose entry_hash is empty");
        }
        finally
        {
            await db.DisposeAsync();
            conn.Dispose();
        }
    }

    [Fact]
    public async Task Backfill_ContinuesFromPartiallySealedChain()
    {
        var (db, conn) = CreateSqliteDb();
        try
        {
            var t = Guid.NewGuid();
            db.PayrollAuditLogs.Add(Row(t, "a", "1"));
            db.PayrollAuditLogs.Add(Row(t, "b", "1"));
            db.PayrollAuditLogs.Add(Row(t, "c", "1"));
            await db.SaveChangesAsync();

            // Unseal only the LAST row (simulate an interrupted prior backfill).
            var last = await db.PayrollAuditLogs.Where(x => x.TenantId == t).OrderByDescending(x => x.Seq).FirstAsync();
            await db.PayrollAuditLogs.Where(x => x.Id == last.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.EntryHash, string.Empty));
            db.ChangeTracker.Clear();

            var sealedCount = await PayrollAuditChainBackfill.RunAsync(db, NullLogger.Instance);
            sealedCount.Should().Be(1, "only the unsealed row is rewritten");

            var logs = await db.PayrollAuditLogs.AsNoTracking().Where(x => x.TenantId == t)
                .OrderBy(x => x.Seq).ToListAsync();
            AuditService.VerifyPayrollChain(logs).IsValid.Should().BeTrue();
        }
        finally
        {
            await db.DisposeAsync();
            conn.Dispose();
        }
    }
}
