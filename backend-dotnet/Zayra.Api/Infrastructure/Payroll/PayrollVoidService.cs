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

        int glReversed = 0;
        if (glEntries.Count > 0)
        {
            var contras = glEntries.Select(orig => new FinanceGlEntry
            {
                TenantId          = tenantId,
                SourceModule      = "Payroll",
                SourceEntityId    = runId,
                SourceEntityRef   = period,
                EventType         = "PayrollVoid",
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
    public enum Kind { Voided, AlreadyVoided, NotFound }

    public Kind   ResultKind    { get; private init; }
    public string Period        { get; private init; } = string.Empty;
    public int    GlReversed    { get; private init; }

    public bool IsVoided      => ResultKind == Kind.Voided;
    public bool IsAlreadyVoid => ResultKind == Kind.AlreadyVoided;
    public bool IsNotFound    => ResultKind == Kind.NotFound;

    public static VoidRunResult Voided(string period, int glReversed) => new()
        { ResultKind = Kind.Voided, Period = period, GlReversed = glReversed };
    public static VoidRunResult AlreadyVoided => new() { ResultKind = Kind.AlreadyVoided };
    public static VoidRunResult NotFound      => new() { ResultKind = Kind.NotFound };
}
