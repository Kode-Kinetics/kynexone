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
    ///   • Bonuses the run consumed are re-opened (P1-4) — see the body.
    ///   • An audit log row is written with actor, reason, and timestamp.
    ///   • ATOMIC (POD-B1b-FIX, re-audit #3): the whole void — the four ExecuteUpdateAsync statements
    ///     AND the tracked GL contras / run status / audit row — commits or rolls back as ONE unit.
    ///
    /// <para>WHY ATOMICITY IS LOAD-BEARING NOW. <c>ExecuteUpdateAsync</c> commits immediately on its own;
    /// the tracked writes only land at the final SaveChanges. Before the P1-4 bonus re-open that split was
    /// merely untidy, but re-opening bonuses makes it a DOUBLE-PAY vector: if the final SaveChanges throws
    /// (payroll audit hash-chain / append-only guard, concurrency, constraint) the bonuses are already
    /// back to Approved / PayrollRunId = null and the batch unlocked, while the run is still Locked with
    /// its BonusPayrollClearing lines LIVE — so the next run pays the same employee again and, because
    /// LoadPositionsAsync still sees the un-reversed clearing, books that second payment straight to
    /// EARN:BONUS, expensing it twice.</para>
    ///
    /// <para>Program.cs:195 enables EnableRetryOnFailure, which forbids a bare BeginTransaction, so the
    /// unit runs inside <c>CreateExecutionStrategy().ExecuteAsync</c> exactly like
    /// PayrollController.ProcessRun:780 and EstablishmentGuardService:187. The delegate is safe to re-run
    /// from scratch: the tracker is cleared on retry and every entity it mutates is (re)loaded inside.
    /// An AMBIENT transaction (a caller that already opened one) is joined rather than nested, so the
    /// caller keeps ownership of the commit.</para>
    /// </summary>
    public async Task<VoidRunResult> VoidAsync(
        Guid runId, Guid tenantId,
        Guid? actorId, string actorName,
        string reason,
        CancellationToken ct = default)
    {
        if (_db.Database.CurrentTransaction is not null)
            return await VoidCoreAsync(runId, tenantId, actorId, actorName, reason, ct);

        var attempt = 0;
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            // Retry-safety: discard the failed attempt's tracked state so the rebuild starts clean.
            // Not on the first attempt — that would detach entities the caller is still holding.
            if (attempt++ > 0) _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var result = await VoidCoreAsync(runId, tenantId, actorId, actorName, reason, ct);
            // NotFound / AlreadyVoided / PeriodClosed write nothing; letting the tx dispose without a
            // commit rolls back the no-op and keeps "refused ⇒ untouched" literally true.
            if (result.IsVoided) await tx.CommitAsync(ct);
            return result;
        });
    }

    private async Task<VoidRunResult> VoidCoreAsync(
        Guid runId, Guid tenantId,
        Guid? actorId, string actorName,
        string reason,
        CancellationToken ct)
    {
        var run = await _db.PayrollRuns.FirstOrDefaultAsync(
            r => r.TenantId == tenantId && r.Id == runId, ct);

        if (run is null) return VoidRunResult.NotFound;
        if (run.Status == "Voided") return VoidRunResult.AlreadyVoided;

        var today  = DateOnly.FromDateTime(DateTime.UtcNow);
        // POD-B2 (M7) — the contra must land in the SAME period the accrual did. For a non-Regular run
        // that booked into a later open month (GlPostingPeriod), reversing into its pay period instead
        // would leave both periods permanently unbalanced. Regular runs have a null GlPostingPeriod, so
        // this is byte-identical to the previous $"{run.Year}-{run.Month:D2}" for every existing run.
        var period = Controllers.PayrollController.GlAccrualPeriod(run);

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
                // POD-B1b — carry the original line's legal-entity dimension onto its contra so a
                // per-company trial balance nets to zero on a void exactly as the group one does.
                CompanyId         = orig.CompanyId,
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

        // ── POD-B1b-FIX (P1-4) — re-open the BONUSES this run consumed ────────────────────────────────
        // The GL contras above reverse the run's BonusPayrollClearing lines and flag the originals
        // IsReversed=true, which makes BonusGlLedger.LoadPositionsAsync (BonusGlLedger.cs:91-95) report the
        // Bonus Payable as OUTSTANDING again — correctly, because the bonus was not paid. But the
        // operational state was left as "PaidInPayroll" + IsLockedByPayroll, and from there the re-opened
        // payable could NEVER be cleared again: Process only picks up Status=="Approved" &&
        // PayrollRunId==null (PayrollController.cs:672-674) and MarkBatchPaid 409s on IsLockedByPayroll
        // (BonusesController.cs:631). The employee's bonus was silently lost and 2300 carried the credit
        // forever. Restoring the operational state is what makes the contra actionable.
        //
        // IgnoreQueryFilters is intentional: a void is a SYSTEM correction over the whole run and must
        // restore EVERY company's bonuses (the platform remediation sweep runs with no company claims at
        // all); the TenantId + PayrollRunId predicate keeps it tenant- and run-contained.
        var runBonusBatchIds = await _db.EmployeeBonuses.IgnoreQueryFilters()
            .Where(b => b.TenantId == tenantId && b.PayrollRunId == runId && !b.IsDeleted
                     && b.Status == "PaidInPayroll")
            .Select(b => b.BonusBatchId)
            .Distinct()
            .ToListAsync(ct);

        // A batch carrying a LIVE BonusPayment entry was paid in cash by MarkBatchPaid, which also stamps
        // PayrollRunId when the caller passes one (BonusesController.cs:652). Re-opening those rows would
        // let the next run pay a bonus whose cash has already left the bank, so they are deliberately left
        // alone — a void must never be able to create a double payment.
        // IgnoreQueryFilters is intentional: FinanceGlEntry.CompanyId is a plain reporting dimension, and
        // this double-pay probe must see a cash payment posted by ANY company in the batch; the TenantId +
        // batch-id predicate re-applies exact tenant scope and never reads another tenant.
        var cashPaidBatchIds = runBonusBatchIds.Count == 0
            ? new List<Guid>()
            : await _db.FinanceGlEntries.IgnoreQueryFilters()
                .Where(x => x.TenantId == tenantId && x.SourceModule == BonusGlLedger.SourceModule
                         && x.EventType == GlEventTypes.BonusPayment && !x.IsReversed
                         && runBonusBatchIds.Contains(x.SourceEntityId))
                .Select(x => x.SourceEntityId)
                .Distinct()
                .ToListAsync(ct);

        // ── POD-B1b-FIX (re-audit #2) — never resurrect a CANCELLED batch's bonuses ───────────────────
        // The P1-4 re-open keys on the BONUS, and PayrollController.cs:670-677 selects pending bonuses on
        // the bonus status alone. A batch that was only PARTIALLY consumed stays Status="Approved" /
        // IsLockedByPayroll=false (PayrollController.cs:1161-1169 locks it only when nothing approved is
        // left), so it could be cancelled while this run held its consumed slice; re-opening those
        // children to "Approved" under a Cancelled parent would have the NEXT run pay a batch finance had
        // explicitly cancelled — and whose accrual had already been contra'd. RejectBatch now refuses to
        // cancel while any child is consumed (BonusesController RejectBatch), which stops this arising;
        // this branch is the belt-and-braces for batches cancelled by the PRE-FIX code on live tenants.
        // Those children are restored to "Cancelled" instead: honest (they were not paid), and no run and
        // no MarkBatchPaid can pick them up.
        // IgnoreQueryFilters is intentional: a void is a SYSTEM correction; the TenantId + batch-id
        // predicate re-applies exact tenant scope and never reads another tenant.
        var candidateBatchIds = runBonusBatchIds.Except(cashPaidBatchIds).ToList();
        var cancelledBatchIds = candidateBatchIds.Count == 0
            ? new List<Guid>()
            : await _db.BonusBatches.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.TenantId == tenantId && candidateBatchIds.Contains(x.Id) && x.Status == "Cancelled")
                .Select(x => x.Id)
                .ToListAsync(ct);

        int bonusesCancelled = 0;
        // IgnoreQueryFilters is intentional: same system-correction rationale as the re-open below — a
        // void must restore EVERY company's consumed bonuses (the platform remediation sweep runs with no
        // company claims at all); the TenantId + PayrollRunId predicate keeps this tenant- and
        // run-contained and never reads another tenant.
        if (cancelledBatchIds.Count > 0)
            bonusesCancelled = await _db.EmployeeBonuses.IgnoreQueryFilters()
                .Where(b => b.TenantId == tenantId && b.PayrollRunId == runId && !b.IsDeleted
                         && b.Status == "PaidInPayroll" && cancelledBatchIds.Contains(b.BonusBatchId))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.Status, "Cancelled")
                    .SetProperty(b => b.PayrollRunId, (Guid?)null), ct);

        var reopenBatchIds = candidateBatchIds.Except(cancelledBatchIds).ToList();
        int bonusesReopened = 0, bonusBatchesUnlocked = 0;
        if (reopenBatchIds.Count > 0)
        {
            // IgnoreQueryFilters is intentional: same system-correction rationale as the probe above — the
            // void must restore every company's consumed bonuses, including when the platform remediation
            // sweep runs with no company claims; TenantId + PayrollRunId keeps it tenant- and run-contained.
            bonusesReopened = await _db.EmployeeBonuses.IgnoreQueryFilters()
                .Where(b => b.TenantId == tenantId && b.PayrollRunId == runId && !b.IsDeleted
                         && b.Status == "PaidInPayroll" && reopenBatchIds.Contains(b.BonusBatchId))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.Status, "Approved")
                    .SetProperty(b => b.PayrollRunId, (Guid?)null), ct);

            // Unlock the batch so the re-opened bonuses can be re-consumed by a corrected run or paid
            // manually. Safe on a partially-consumed batch: MarkBatchPaid only touches Status=="Approved"
            // rows and the payable cursor caps every debit at what is still outstanding.
            // IgnoreQueryFilters is intentional: the batch lock is a BATCH-wide flag, so unlocking it is a
            // system correction that must not depend on the actor's company scope (P1-6's mirror image);
            // the TenantId + batch-id predicate re-applies exact tenant scope.
            bonusBatchesUnlocked = await _db.BonusBatches.IgnoreQueryFilters()
                .Where(x => x.TenantId == tenantId && reopenBatchIds.Contains(x.Id) && !x.IsDeleted
                         && (x.IsLockedByPayroll || x.Status == "Paid"))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, "Approved")
                    .SetProperty(x => x.IsLockedByPayroll, false), ct);
        }

        // POD-B1 (P1-5) — the GL contras above already reversed any net-pay settlement / statutory
        // remittance (they share SourceModule="Payroll"/SourceEntityId=runId). Keep the OPERATIONAL
        // state consistent with the returned-to-zero ledger: a settled batch (WpsStatus=Paid) reverts to
        // Accepted so status never reads "money left" while the ledger says it came back.
        int batchesReverted = await _db.PayrollPaymentBatches
            .Where(b => b.TenantId == tenantId && b.PayrollRunId == runId && b.WpsStatus == WpsStatuses.Paid)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.WpsStatus, WpsStatuses.Accepted)
                .SetProperty(p => p.WpsStatusChangedAtUtc, DateTime.UtcNow), ct);

        // POD-B2 — runs that AMEND this one (Correction/Supplementary with ParentRunId == this run).
        // Voiding the parent leaves them dangling. REPORTED, not blocked and not cascaded: cascade
        // recovery of a correction chain is POD-B3, and half-implementing it here would be worse than
        // surfacing the fact and letting the operator act.
        var childRunIds = await _db.PayrollRuns.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.ParentRunId == runId && r.Status != "Voided")
            .Select(r => r.Id)
            .ToListAsync(ct);

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
                // POD-B1b-FIX (P1-4) — operational bonus state restored alongside the GL contra.
                bonusesReopened,
                bonusBatchesUnlocked,
                // POD-B1b-FIX (re-audit #2) — consumed bonuses under an already-CANCELLED batch are
                // restored to Cancelled, never to Approved, so a void can never resurrect them.
                bonusesCancelled,
                period,
                payPeriod = $"{run.Year}-{run.Month:D2}",
                // POD-B2 — runs that AMEND this one. Reported, never blocked: cascade recovery of a
                // correction chain is POD-B3 and must not be half-implemented here.
                runType = run.RunType,
                childRunIds,
                actorName,
                source = "PayrollVoidService",
            }),
        });

        await _db.SaveChangesAsync(ct);

        return VoidRunResult.Voided(period, glReversed, childRunIds);
    }
}

public sealed class VoidRunResult
{
    public enum Kind { Voided, AlreadyVoided, NotFound, PeriodClosed }

    public Kind   ResultKind    { get; private init; }
    public string Period        { get; private init; } = string.Empty;
    public int    GlReversed    { get; private init; }
    /// <summary>POD-B2 — ids of runs whose ParentRunId is this run (corrections/supplementaries amending
    /// it). Surfaced so the operator sees the chain they have just orphaned. Voiding is NOT blocked and
    /// children are NOT cascaded: recovery of a correction chain is POD-B3.</summary>
    public IReadOnlyList<Guid> ChildRunIds { get; private init; } = Array.Empty<Guid>();

    public bool IsVoided       => ResultKind == Kind.Voided;
    public bool IsAlreadyVoid  => ResultKind == Kind.AlreadyVoided;
    public bool IsNotFound     => ResultKind == Kind.NotFound;
    public bool IsPeriodClosed => ResultKind == Kind.PeriodClosed;

    public static VoidRunResult Voided(string period, int glReversed, IReadOnlyList<Guid>? childRunIds = null) => new()
        { ResultKind = Kind.Voided, Period = period, GlReversed = glReversed, ChildRunIds = childRunIds ?? Array.Empty<Guid>() };
    public static VoidRunResult AlreadyVoided => new() { ResultKind = Kind.AlreadyVoided };
    public static VoidRunResult NotFound      => new() { ResultKind = Kind.NotFound };
    // POD-B1 (P0-3) — the run's GL period is closed; void must not silently mutate closed books.
    public static VoidRunResult PeriodClosed(string period) => new()
        { ResultKind = Kind.PeriodClosed, Period = period };
}
