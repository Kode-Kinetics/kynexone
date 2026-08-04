using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// Core void logic extracted from PayrollController.VoidRun so it can be called
/// from both the tenant-scoped HTTP endpoint and the platform-level remediation sweep
/// without duplicating the GL contra-entry / audit-log / payslip-mark code.
///
/// This is the single canonical implementation of "void a payroll run" — any caller
/// that changes this changes it for everyone.
/// </summary>
public sealed class PayrollVoidService
{
    private readonly ZayraDbContext _db;
    public PayrollVoidService(ZayraDbContext db) => _db = db;

    /// <summary>
    /// Voids a single payroll run.
    ///
    /// Guarantees:
    ///   • The run must belong to <paramref name="tenantId"/> — cross-tenant access is prevented.
    ///   • Idempotent-safe: returns <see cref="VoidRunResult.AlreadyVoided"/> if already voided.
    ///   • GL contra-entries are written for Locked runs (originals preserved, IsReversed=true).
    ///   • Payslips are marked "Voided" (excluded from ESS / YTD / reporting).
    ///   • An audit log row is written with actor, reason, and timestamp.
    ///   • <see cref="SaveChangesAsync"/> is called inside this method — do NOT wrap in a
    ///     larger transaction unless you clear the tracker before calling.
    /// </summary>
    public async Task<VoidRunResult> VoidAsync(
        Guid runId, Guid tenantId,
        Guid? actorId, string actorName,
        string reason,
        CancellationToken ct = default)
    {
        var run = await _db.PayrollRuns.FirstOrDefaultAsync(
            r => r.TenantId == tenantId && r.Id == runId, ct);

        if (run is null) return VoidRunResult.NotFound;
        if (run.Status == "Voided") return VoidRunResult.AlreadyVoided;

        var today  = DateOnly.FromDateTime(DateTime.UtcNow);
        var period = $"{run.Year}-{run.Month:D2}";

        // GL contra-entries — only Locked runs have posted GL
        var glEntries = await _db.FinanceGlEntries
            .Where(g => g.TenantId == tenantId && g.SourceModule == "Payroll"
                     && g.SourceEntityId == runId && !g.IsReversed)
            .ToListAsync(ct);

        // POD-B1 (P0-3) — a void writes GL contras into the run period; refuse to silently rewrite a
        // CLOSED period's books. Force an audited reopen → void → re-close. Only blocks when there is
        // GL to reverse (a Processed run with no GL touches no books, so a closed period is irrelevant).
        if (glEntries.Count > 0 && await PeriodCloseGuard.IsClosedAsync(_db, tenantId, run.CompanyId, period, ct))
            return VoidRunResult.PeriodClosed(period);

        int glReversed = 0;
        if (glEntries.Count > 0)
        {
            var contras = glEntries.Select(orig => new FinanceGlEntry
            {
                TenantId          = tenantId,
                SourceModule      = "Payroll",
                SourceEntityId    = runId,
                SourceEntityRef   = period,
                EventType         = GlEventTypes.Void,
                DebitAccount      = orig.CreditAccount,
                CreditAccount     = orig.DebitAccount,
                Amount            = orig.Amount,
                Currency          = orig.Currency,
                EntryDate         = today,
                Period            = period,
                Description       = $"VOID — reversal of \"{orig.Description}\" — {reason}",
                PostedBy          = actorId,
                PostedByName      = actorName,
                IsReversed        = false,
                ReversalOfEntryId = orig.Id,
            }).ToList();

            foreach (var orig in glEntries) orig.IsReversed = true;
            _db.FinanceGlEntries.AddRange(contras);
            glReversed = contras.Count;
        }

        // Void payslips — excluded from ESS, YTD accumulation, and any report totals
        await _db.PayrollSlips
            .Where(s => s.TenantId == tenantId && s.RunId == runId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, "Voided"), ct);

        // POD-B1 (P1-5) — the GL contras above already reversed any net-pay settlement / statutory
        // remittance (they share SourceModule="Payroll"/SourceEntityId=runId). Keep the OPERATIONAL
        // state consistent with the returned-to-zero ledger: a settled batch (WpsStatus=Paid) reverts to
        // Accepted so status never reads "money left" while the ledger says it came back.
        int batchesReverted = await _db.PayrollPaymentBatches
            .Where(b => b.TenantId == tenantId && b.PayrollRunId == runId && b.WpsStatus == WpsStatuses.Paid)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.WpsStatus, WpsStatuses.Accepted)
                .SetProperty(p => p.WpsStatusChangedAtUtc, DateTime.UtcNow), ct);

        run.Status         = "Voided";
        run.VoidedAtUtc    = DateTime.UtcNow;
        run.VoidedByUserId = actorId;
        run.VoidedByName   = actorName;
        run.VoidReason     = reason;

        _db.PayrollAuditLogs.Add(new PayrollAuditLog
        {
            TenantId     = tenantId,
            Action       = "payroll.run.voided",
            EntityName   = "PayrollRun",
            EntityId     = runId.ToString(),
            UserId       = actorId,
            MetadataJson = JsonSerializer.Serialize(new
            {
                reason,
                glEntriesReversed = glReversed,
                batchesReverted,
                period,
                actorName,
                source = "PayrollVoidService",
            }),
        });

        await _db.SaveChangesAsync(ct);

        return VoidRunResult.Voided(period, glReversed);
    }
}

public sealed class VoidRunResult
{
    public enum Kind { Voided, AlreadyVoided, NotFound, PeriodClosed }

    public Kind   ResultKind    { get; private init; }
    public string Period        { get; private init; } = string.Empty;
    public int    GlReversed    { get; private init; }

    public bool IsVoided       => ResultKind == Kind.Voided;
    public bool IsAlreadyVoid  => ResultKind == Kind.AlreadyVoided;
    public bool IsNotFound     => ResultKind == Kind.NotFound;
    public bool IsPeriodClosed => ResultKind == Kind.PeriodClosed;

    public static VoidRunResult Voided(string period, int glReversed) => new()
        { ResultKind = Kind.Voided, Period = period, GlReversed = glReversed };
    public static VoidRunResult AlreadyVoided => new() { ResultKind = Kind.AlreadyVoided };
    public static VoidRunResult NotFound      => new() { ResultKind = Kind.NotFound };
    // POD-B1 (P0-3) — the run's GL period is closed; void must not silently mutate closed books.
    public static VoidRunResult PeriodClosed(string period) => new()
        { ResultKind = Kind.PeriodClosed, Period = period };
}
