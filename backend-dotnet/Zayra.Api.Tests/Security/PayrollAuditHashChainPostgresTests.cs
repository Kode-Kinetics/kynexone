using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// POD-A3 — real-Postgres tamper-evidence for <c>payroll_audit_logs</c>. The in-memory
/// <see cref="Zayra.Api.Tests.Security.PayrollAuditHashChainTests"/> prove the sealer, verifier, and
/// in-process ChangeTracker guard in isolation. This class proves the guarantees that ONLY real
/// Postgres can exercise — the ones an external (Big-4) auditor actually relies on:
///
///   (a) writing several payroll audit events through the real Npgsql save path (advisory lock +
///       execution-strategy transaction + <c>timestamptz</c> microsecond round-trip) produces a chain
///       the verifier accepts;
///   (b) an out-of-band, SET-BASED SQL UPDATE of a sealed row — the exact bypass the in-process guard
///       cannot see — is rejected by the DB-level append-only TRIGGER;
///   (c) a raw SQL DELETE is likewise rejected by the trigger;
///   (d) if that DB guard is circumvented by a privileged actor (superuser / trigger dropped /
///       session_replication_role), the cryptographic verifier STILL detects the tamper of the
///       persisted row (entry_hash_mismatch) — the defence-in-depth backstop;
///   (e) two tenants' chains are independent on real Postgres.
///
/// The shared <see cref="PostgresFixture"/> builds schema via <c>EnsureCreated</c> (not migrations), so
/// the trigger is not present by default. Tests (b)/(c) install the EXACT DDL from
/// <c>Migrations/20260803201058_AddPayrollAuditHashChain.cs</c> and drop it in a <c>finally</c>, keeping
/// the shared container clean for the other classes in the sequential Integration collection.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class PayrollAuditHashChainPostgresTests
{
    private readonly PostgresFixture _fx;
    public PayrollAuditHashChainPostgresTests(PostgresFixture fx) => _fx = fx;

    // ── Trigger DDL — copied VERBATIM from the AddPayrollAuditHashChain migration Up() ──────────
    private const string FunctionDdl = @"
CREATE OR REPLACE FUNCTION payroll_audit_logs_append_only() RETURNS trigger AS $$
BEGIN
    IF (TG_OP = 'DELETE') THEN
        RAISE EXCEPTION 'payroll_audit_logs is append-only: DELETE is not permitted (row id=%)', OLD.id
            USING ERRCODE = 'restrict_violation';
    END IF;
    IF (OLD.entry_hash IS DISTINCT FROM '') THEN
        RAISE EXCEPTION 'payroll_audit_logs is append-only: sealed rows are immutable (row id=%)', OLD.id
            USING ERRCODE = 'restrict_violation';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;";

    private const string TriggerDdl = @"
DROP TRIGGER IF EXISTS trg_payroll_audit_logs_append_only ON payroll_audit_logs;
CREATE TRIGGER trg_payroll_audit_logs_append_only
    BEFORE UPDATE OR DELETE ON payroll_audit_logs
    FOR EACH ROW EXECUTE FUNCTION payroll_audit_logs_append_only();";

    private const string DropTriggerSql =
        "DROP TRIGGER IF EXISTS trg_payroll_audit_logs_append_only ON payroll_audit_logs;";

    private static async Task InstallTriggerAsync(ZayraDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(FunctionDdl);
        await db.Database.ExecuteSqlRawAsync(TriggerDdl);
    }

    private static PayrollAuditLog Row(Guid tenantId, string action) => new()
    {
        TenantId = tenantId,
        Action = action,
        EntityName = "PayrollRun",
        EntityId = "run-1",
        UserId = Guid.NewGuid(),
        MetadataJson = "{\"k\":\"v\"}",
    };

    // ── (a) real Npgsql save path → valid, verifiable chain ─────────────────────────────────────

    [Fact]
    public async Task RealPostgres_MultipleEvents_ProduceValidChain()
    {
        await using var db = _fx.CreateDb();
        var t = await PostgresFixture.SeedMinimalTenant(db);

        // Two events in one save (fork hazard) + a third in a later save (tail-read hazard). This
        // drives the Npgsql-only sealer path: BeginTransaction → pg_advisory_xact_lock → seal → commit.
        db.PayrollAuditLogs.Add(Row(t, "payroll.run.processed"));
        db.PayrollAuditLogs.Add(Row(t, "payroll.run.approved"));
        await db.SaveChangesAsync();
        db.PayrollAuditLogs.Add(Row(t, "payroll.run.locked"));
        await db.SaveChangesAsync();

        var logs = await db.PayrollAuditLogs.AsNoTracking()
            .Where(x => x.TenantId == t).OrderBy(x => x.Seq).ThenBy(x => x.Id).ToListAsync();

        logs.Should().HaveCount(3);
        logs.Select(x => x.Seq).Should().Equal(1L, 2L, 3L); // Seq is a monotonic per-tenant ordinal
        logs[0].PreviousHash.Should().BeEmpty("first row of a tenant chain is genesis");
        logs[1].PreviousHash.Should().Be(logs[0].EntryHash);
        logs[2].PreviousHash.Should().Be(logs[1].EntryHash, "the later save must chain off the persisted tail");
        AuditService.VerifyPayrollChain(logs).IsValid
            .Should().BeTrue("the chain must verify after a real Npgsql timestamptz (microsecond) round-trip");
    }

    // ── (b) DB-level trigger rejects out-of-band, set-based UPDATE of a sealed row ───────────────

    [Fact]
    public async Task RealPostgres_Trigger_RejectsOutOfBandUpdate_OfSealedRow()
    {
        await using var db = _fx.CreateDb();
        var t = await PostgresFixture.SeedMinimalTenant(db);
        db.PayrollAuditLogs.Add(Row(t, "payroll.run.processed"));
        await db.SaveChangesAsync();
        var id = await db.PayrollAuditLogs.Where(x => x.TenantId == t).Select(x => x.Id).SingleAsync();

        await InstallTriggerAsync(db);
        try
        {
            // Set-based SQL that never enters the EF ChangeTracker — the exact bypass the in-process
            // append-only guard cannot see. The DB trigger is the backstop that stops it.
            var update = async () => await db.Database.ExecuteSqlRawAsync(
                "UPDATE payroll_audit_logs SET seq = seq + 1 WHERE id = {0}", id);
            await update.Should().ThrowAsync<PostgresException>()
                .WithMessage("*append-only*", "a sealed payroll audit row must be immutable at the database");
        }
        finally { await db.Database.ExecuteSqlRawAsync(DropTriggerSql); }
    }

    // ── (c) DB-level trigger rejects raw DELETE ─────────────────────────────────────────────────

    [Fact]
    public async Task RealPostgres_Trigger_RejectsRawDelete()
    {
        await using var db = _fx.CreateDb();
        var t = await PostgresFixture.SeedMinimalTenant(db);
        db.PayrollAuditLogs.Add(Row(t, "payroll.run.processed"));
        await db.SaveChangesAsync();
        var id = await db.PayrollAuditLogs.Where(x => x.TenantId == t).Select(x => x.Id).SingleAsync();

        await InstallTriggerAsync(db);
        try
        {
            var delete = async () => await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM payroll_audit_logs WHERE id = {0}", id);
            await delete.Should().ThrowAsync<PostgresException>()
                .WithMessage("*append-only*", "payroll audit rows can never be deleted");

            (await db.PayrollAuditLogs.AsNoTracking().CountAsync(x => x.Id == id))
                .Should().Be(1, "the blocked DELETE must leave the row intact");
        }
        finally { await db.Database.ExecuteSqlRawAsync(DropTriggerSql); }
    }

    // ── (d) verifier detects an out-of-band mutation of a PERSISTED row (DB guard bypassed) ──────

    [Fact]
    public async Task RealPostgres_Verifier_DetectsOutOfBandTamper_WhenDbGuardBypassed()
    {
        await using var db = _fx.CreateDb();
        var t = await PostgresFixture.SeedMinimalTenant(db);
        db.PayrollAuditLogs.Add(Row(t, "payroll.run.processed"));
        db.PayrollAuditLogs.Add(Row(t, "payroll.run.approved"));
        await db.SaveChangesAsync();

        var target = await db.PayrollAuditLogs.AsNoTracking()
            .Where(x => x.TenantId == t).OrderBy(x => x.Seq).FirstAsync();

        // No trigger installed here → this simulates a privileged actor who circumvented the DB guard
        // (superuser, trigger dropped, or session_replication_role=replica). Change a HASHED field of
        // the PERSISTED row WITHOUT recomputing entry_hash — a true out-of-band tamper.
        await db.Database.ExecuteSqlRawAsync(DropTriggerSql); // defensive: order-independent
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE payroll_audit_logs SET action = 'payroll.run.FORGED' WHERE id = {0}", target.Id);

        db.ChangeTracker.Clear();
        var logs = await db.PayrollAuditLogs.AsNoTracking()
            .Where(x => x.TenantId == t).OrderBy(x => x.Seq).ThenBy(x => x.Id).ToListAsync();

        var report = AuditService.VerifyPayrollChain(logs);
        report.IsValid.Should().BeFalse("an out-of-band mutation of a persisted row must be cryptographically detectable");
        // ONLY the tampered row fails — the untampered row still verifies, which also proves the
        // microsecond-aligned CreatedAtUtc round-trips through timestamptz (no false positives).
        report.Failures.Should().ContainSingle()
            .Which.Should().Match<AuditIntegrityFailure>(
                f => f.Reason == "entry_hash_mismatch" && f.AuditLogId == target.Id,
                "the verifier must pinpoint exactly the forged row");
    }

    // ── (e) two tenants have independent chains on real Postgres ─────────────────────────────────

    [Fact]
    public async Task RealPostgres_TwoTenants_HaveIndependentChains()
    {
        await using var db = _fx.CreateDb();
        var a = await PostgresFixture.SeedMinimalTenant(db);
        var b = await PostgresFixture.SeedMinimalTenant(db);

        // A multi-tenant save drives the sorted advisory-lock acquisition across both tenants.
        db.PayrollAuditLogs.Add(Row(a, "payroll.run.processed"));
        db.PayrollAuditLogs.Add(Row(a, "payroll.run.approved"));
        db.PayrollAuditLogs.Add(Row(b, "payroll.run.processed"));
        await db.SaveChangesAsync();

        var chainA = await db.PayrollAuditLogs.AsNoTracking().Where(x => x.TenantId == a).OrderBy(x => x.Seq).ToListAsync();
        var chainB = await db.PayrollAuditLogs.AsNoTracking().Where(x => x.TenantId == b).OrderBy(x => x.Seq).ToListAsync();

        chainA.Select(x => x.Seq).Should().Equal(1L, 2L); // tenant A owns an independent ordinal sequence
        chainA[0].PreviousHash.Should().BeEmpty();
        chainB.Single().Seq.Should().Be(1, "tenant B genesis is independent of tenant A");
        chainB.Single().PreviousHash.Should().BeEmpty();
        AuditService.VerifyPayrollChain(chainA).IsValid.Should().BeTrue();
        AuditService.VerifyPayrollChain(chainB).IsValid.Should().BeTrue();
    }
}
