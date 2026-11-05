using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll.ManpowerRecon;

/// <summary>An HTTP-shaped outcome (status and body) so the service stays free of controller types.</summary>
public sealed record ReconActionResult(int Status, object? Body);

/// <summary>
/// Runs one change to a payroll run inside a transaction whose first read is the run row with FOR UPDATE, the same row lock
/// Lock, Void, Reopen and Approve take. EF InMemory has no transactions or row locks and runs the body directly, as does a
/// caller that already opened a transaction (which then owns the commit).
/// </summary>
internal static class ReconTransactions
{
    public static async Task<T> InRunRowLockAsync<T>(ZayraDbContext db, Func<Task<T>> body, CancellationToken ct)
    {
        if (!db.Database.IsRelational() || db.Database.CurrentTransaction is not null) return await body();
        var attempt = 0;
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            if (attempt++ > 0) db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var result = await body();
            await tx.CommitAsync(ct);
            return result;
        });
    }
}

/// <summary>
/// Records, withdraws and retires the manpower and value reconciliation sign-off. Every change shares one transaction with the
/// run's row lock, so a sign-off can never describe a run that was re-processed, reopened, sent back or voided a moment earlier,
/// and a run can never carry two Active sign-offs.
/// </summary>
public sealed class ReconSignoffLifecycleService
{
    public const int MaxExplanations = 1000;

    private readonly ZayraDbContext _db;
    private readonly ManpowerReconService _recon;

    public ReconSignoffLifecycleService(ZayraDbContext db, ManpowerReconService? recon = null)
    {
        _db = db;
        _recon = recon ?? new ManpowerReconService(db);
    }

    // ── Sign ─────────────────────────────────────────────────────────────────────────────────────

    public async Task<ReconActionResult> SignAsync(
        Guid tenantId, Guid runId, ReconCaller caller, ReconSignoffRequest req, string? ip, CancellationToken ct)
    {
        try
        {
            return await ReconTransactions.InRunRowLockAsync(_db, () => SignCoreAsync(tenantId, runId, caller, req, ip, ct), ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // The partial unique index is the backstop for "one Active sign-off per run".
            return new ReconActionResult(409, new { error = "already_signed_off", message = "This run already has an active reconciliation sign-off." });
        }
    }

    private async Task<ReconActionResult> SignCoreAsync(
        Guid tenantId, Guid runId, ReconCaller caller, ReconSignoffRequest req, string? ip, CancellationToken ct)
    {
        var run = await _db.PayrollRuns.TagWith(RowLockingInterceptor.ForUpdateTag)
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Id == runId, ct);
        if (run is null || !caller.EntityScope.CanAccessCompany(run.CompanyId)) return new ReconActionResult(404, null);

        if (!PayrollRunTypes.IsPeriodOwning(run.RunType))
            return new ReconActionResult(409, new { error = "recon_not_applicable", message = "Only a run that owns its period (Regular or Replacement) has a manpower and value reconciliation." });
        if (run.Status is not ("Processed" or "PendingFinanceReview"))
            return new ReconActionResult(400, new { error = "run_not_signable", message = "Only a Processed or PendingFinanceReview run can be signed off.", status = run.Status });
        if (await ProcessingIsLiveAsync(tenantId, run.Id, ct))
            return new ReconActionResult(409, new { error = "run_processing", message = "The run is being processed. Wait for it to finish, then review the new figures." });

        var partial = caller.DataScope.Level != Application.Common.DataScopeLevel.Organization && caller.DataScope.AllowedEmployeeIds is not null;
        if (partial)
            return new ReconActionResult(403, new { error = "signoff_requires_organization_scope", message = "A reconciliation can only be signed off by someone who can see the whole run, not a department or team." });
        if (caller.UserId is { } uid && (run.CreatedByUserId == uid || run.ProcessedByUserId == uid))
            return new ReconActionResult(403, new { error = "maker_checker_violation", message = "The user who created or processed this run cannot sign off its reconciliation. A different reviewer is required (maker-checker policy)." });

        if (await _db.PayrollRunReconSignoffs.AnyAsync(s => s.TenantId == tenantId && s.PayrollRunId == run.Id && s.Status == ReconSignoffStatuses.Active, ct))
            return new ReconActionResult(409, new { error = "already_signed_off", message = "This run already has an active reconciliation sign-off. Withdraw it first to sign again." });

        if (req.ExpectedProcessVersion != run.ProcessVersion)
            return new ReconActionResult(409, new
            {
                error = "signoff_stale", reason = "ProcessVersion", processVersion = run.ProcessVersion, expected = req.ExpectedProcessVersion,
                message = "The run was processed again since you opened the reconciliation. Review the new figures.",
            });

        var built = await _recon.BuildAsync(tenantId, run.Id, caller, req.PriorRunId, ct);
        if (built.Status == ReconBuildStatus.NotFound) return new ReconActionResult(404, null);
        if (built.Status == ReconBuildStatus.PriorRunInvalid)
            return new ReconActionResult(400, new { error = PriorRunSelector.PriorRunInvalid, message = "The chosen comparison run must be an earlier, non-voided Regular or Replacement run of the same company." });
        var build = built.Build!;

        if (!build.Dto.Integrity.Ok)
            return new ReconActionResult(422, new
            {
                error = "recon_integrity_failed", issues = build.Dto.Integrity.Issues,
                message = "The reconciliation does not tie to the payroll register. Resolve the listed issues before signing.",
            });
        if (!string.Equals(req.ExpectedSnapshotHash, build.SnapshotHash, StringComparison.OrdinalIgnoreCase))
            return new ReconActionResult(409, new
            {
                error = "signoff_stale", reason = "Snapshot", processVersion = run.ProcessVersion,
                message = "The figures changed since you opened the reconciliation. Review them again.",
            });
        if (req.AcknowledgedFlaggedEmployeeCount != build.FlaggedEmployeeIds.Count)
            return new ReconActionResult(409, new
            {
                error = "flagged_employees_not_acknowledged", flaggedEmployeeCount = build.FlaggedEmployeeIds.Count,
                acknowledged = req.AcknowledgedFlaggedEmployeeCount,
                message = $"{build.FlaggedEmployeeIds.Count} employee(s) moved more than the variance threshold. Review them and restate that number to acknowledge.",
            });

        var explanations = new List<ReconExplanationRequest>();
        var seen = new HashSet<(string, string)>();
        foreach (var e in req.Explanations ?? Array.Empty<ReconExplanationRequest>())
        {
            var scope = e.Scope?.Trim();
            var key = e.Key?.Trim();
            var text = e.Text?.Trim() ?? string.Empty;
            if (scope is not (ReconExplanationScopes.Item or ReconExplanationScopes.Employee) || string.IsNullOrEmpty(key) || key.Length > 120
                || text.Length > 1000 || explanations.Count >= MaxExplanations || !seen.Add((scope, key)))
                return new ReconActionResult(400, new { error = "invalid_explanation", message = "An explanation needs a scope of Item or Employee, a key, and at most 1000 characters, once per key." });
            explanations.Add(new ReconExplanationRequest(scope, key, text));
        }
        var missing = build.RequiredExplanations
            .Where(r => !explanations.Any(e => e.Scope == r.Scope && e.Key == r.Key
                                            && e.Text.Length >= PayrollValidationOverridePolicy.MinimumReasonLength))
            .ToList();
        if (missing.Count > 0)
            return new ReconActionResult(422, new
            {
                error = "variance_explanation_required", missing, missingKeys = missing.Select(m => m.Key).ToList(),
                message = $"{missing.Count} flagged item(s) or employee(s) need a written explanation of at least {PayrollValidationOverridePolicy.MinimumReasonLength} characters.",
            });
        var notes = req.Notes?.Trim();
        if (notes is { Length: > 1000 }) return new ReconActionResult(400, new { error = "notes_too_long", message = "Notes can be at most 1000 characters." });

        var signoff = new PayrollRunReconSignoff
        {
            TenantId = tenantId, CompanyId = run.CompanyId, PayrollRunId = run.Id, ProcessVersion = run.ProcessVersion,
            PriorRunId = build.Prior?.Id, PriorProcessVersion = build.PriorProcessVersion, SnapshotHash = build.SnapshotHash,
            SnapshotJson = ReconSnapshotHash.SummaryJson(build.Dto), SignedOffByUserId = caller.UserId,
            SignedOffByName = string.IsNullOrWhiteSpace(caller.UserName) ? "system" : caller.UserName,
            SignedOffAtUtc = DateTime.UtcNow, Status = ReconSignoffStatuses.Active, Notes = string.IsNullOrEmpty(notes) ? null : notes,
        };
        foreach (var e in explanations)
            signoff.Explanations.Add(new PayrollRunReconExplanation
            { TenantId = tenantId, SignoffId = signoff.Id, Scope = e.Scope, Key = e.Key, Text = e.Text, CreatedByUserId = caller.UserId });
        _db.PayrollRunReconSignoffs.Add(signoff);
        Audit(tenantId, caller.UserId, "payroll.recon.signed_off", run.Id, ip, new
        {
            signoffId = signoff.Id, processVersion = run.ProcessVersion, snapshotHash = signoff.SnapshotHash,
            priorRunId = signoff.PriorRunId, priorProcessVersion = signoff.PriorProcessVersion, explanations = explanations.Count,
            flaggedEmployees = build.FlaggedEmployeeIds.Count,
        });
        await _db.SaveChangesAsync(ct);
        return new ReconActionResult(201, ManpowerReconService.SignoffDto(signoff));
    }

    // ── Withdraw ─────────────────────────────────────────────────────────────────────────────────

    public Task<ReconActionResult> WithdrawAsync(
        Guid tenantId, Guid runId, ReconCaller caller, ReconWithdrawRequest req, string? ip, CancellationToken ct) =>
        ReconTransactions.InRunRowLockAsync(_db, async () =>
        {
            var run = await _db.PayrollRuns.TagWith(RowLockingInterceptor.ForUpdateTag)
                .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Id == runId, ct);
            if (run is null || !caller.EntityScope.CanAccessCompany(run.CompanyId)) return new ReconActionResult(404, null);
            if (run.Status is not ("Processed" or "PendingFinanceReview"))
                return new ReconActionResult(400, new { error = "run_not_signable", message = "A sign-off can only be withdrawn while the run is waiting for approval.", status = run.Status });
            var reason = req.Reason?.Trim() ?? string.Empty;
            if (reason.Length < PayrollValidationOverridePolicy.MinimumReasonLength)
                return new ReconActionResult(400, new
                {
                    error = "reason_required",
                    message = $"A reason of at least {PayrollValidationOverridePolicy.MinimumReasonLength} characters is required to withdraw a sign-off.",
                });
            var active = await _db.PayrollRunReconSignoffs
                .Where(s => s.TenantId == tenantId && s.PayrollRunId == runId && s.Status == ReconSignoffStatuses.Active).ToListAsync(ct);
            if (active.Count == 0) return new ReconActionResult(404, new { error = "no_active_signoff", message = "This run has no active reconciliation sign-off." });
            foreach (var s in active) Retire(s, ReconSupersededReasons.Withdrawn);
            Audit(tenantId, caller.UserId, "payroll.recon.withdrawn", run.Id, ip, new { signoffId = active[0].Id, reason });
            await _db.SaveChangesAsync(ct);
            return new ReconActionResult(200, ManpowerReconService.SignoffDto(active[0]));
        }, ct);

    // ── Export audit ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Records that the reconciliation was exported, in the same save as nothing else: an export changes no figure.</summary>
    public async Task RecordExportAsync(
        Guid tenantId, Guid runId, Guid? userId, string? ip, string format, string label, bool partial, int processVersion, CancellationToken ct)
    {
        Audit(tenantId, userId, "payroll.recon.exported", runId, ip, new { format, label, partial, processVersion });
        await _db.SaveChangesAsync(ct);
    }

    // ── Retire ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Supersedes the run's Active sign-off, if any, and queues its audit row. Does not save: it runs inside the caller's
    /// transaction (Process, Reopen, Send back, Void, Approve) so the sign-off retires with the change that made it stale, or not at
    /// all. Writes nothing when nothing is Active.
    /// </summary>
    public static async Task SupersedeActiveAsync(
        ZayraDbContext db, Guid tenantId, Guid runId, string reason, Guid? actorUserId, CancellationToken ct)
    {
        var active = await db.PayrollRunReconSignoffs
            .Where(s => s.TenantId == tenantId && s.PayrollRunId == runId && s.Status == ReconSignoffStatuses.Active).ToListAsync(ct);
        foreach (var s in active)
        {
            Retire(s, reason);
            db.PayrollAuditLogs.Add(new PayrollAuditLog
            {
                TenantId = tenantId, Action = "payroll.recon.superseded", EntityName = "PayrollRun", EntityId = runId.ToString(), UserId = actorUserId,
                MetadataJson = JsonSerializer.Serialize(new { userId = actorUserId?.ToString(), data = new { signoffId = s.Id, reason, processVersion = s.ProcessVersion } }),
            });
        }
    }

    private static void Retire(PayrollRunReconSignoff s, string reason)
    {
        s.Status = ReconSignoffStatuses.Superseded;
        s.SupersededAtUtc = DateTime.UtcNow;
        s.SupersededReason = reason;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    private Task<bool> ProcessingIsLiveAsync(Guid tenantId, Guid runId, CancellationToken ct)
    {
        var key = PayrollRunProcessJobHandler.IdempotencyKey(runId);
        return _db.BackgroundJobs.AsNoTracking().AnyAsync(j => j.TenantId == tenantId && j.JobType == PayrollRunProcessJobHandler.JobType
            && j.IdempotencyKey == key && (j.Status == BackgroundJobStatuses.Queued || j.Status == BackgroundJobStatuses.Running), ct);
    }

    private void Audit(Guid tenantId, Guid? userId, string action, Guid runId, string? ip, object data) =>
        _db.PayrollAuditLogs.Add(new PayrollAuditLog
        {
            TenantId = tenantId, Action = action, EntityName = "PayrollRun", EntityId = runId.ToString(), UserId = userId,
            MetadataJson = JsonSerializer.Serialize(new { ip = ip ?? "unknown", userId = userId?.ToString(), data }),
        });

    internal static bool IsUniqueViolation(DbUpdateException ex)
    {
        for (var e = (Exception?)ex; e is not null; e = e.InnerException)
        {
            if (e is Npgsql.PostgresException { SqlState: "23505" }) return true;
            if (e.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}

/// <summary>
/// The Approve-time check: a period-owning run needs an Active sign-off for its CURRENT process version (and an unchanged
/// comparison run), unless the company turned the requirement off, in which case the approval is let through and recorded as
/// approved without one. Beyond the version stamps, the bridge is rebuilt for the run (as a caller who sees the whole run, as
/// the sign-off required) and its fingerprint compared with the one that was signed, so a change to anything the bridge reads
/// (a final settlement, an offboarding, a hold-out selection, an employee's status or company, the thresholds) since the review
/// is caught even though the run was not processed again.
/// </summary>
public static class ReconApprovalGate
{
    public const string Missing = "Missing";
    public const string Stale = "Stale";
    public const string PriorChanged = "PriorChanged";
    public const string Changed = "Changed";

    public sealed record Result(bool Blocked, bool Waived, string? Reason, int ProcessVersion, int? SignedVersion);

    public static async Task<Result> CheckAsync(ZayraDbContext db, Guid tenantId, PayrollRun run, CancellationToken ct)
    {
        if (!PayrollRunTypes.IsPeriodOwning(run.RunType)) return new Result(false, false, null, run.ProcessVersion, null);

        var rules = await CompanyPayrollRules.ResolveReconAsync(db, tenantId, run.CompanyId, ct);
        var active = await db.PayrollRunReconSignoffs.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.PayrollRunId == run.Id && s.Status == ReconSignoffStatuses.Active)
            .OrderByDescending(s => s.SignedOffAtUtc).FirstOrDefaultAsync(ct);

        string? reason = null;
        if (active is null) reason = Missing;
        else if (active.ProcessVersion != run.ProcessVersion) reason = Stale;
        else if (active.PriorRunId is { } priorId)
        {
            var prior = await db.PayrollRuns.AsNoTracking().FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Id == priorId, ct);
            if (prior is null || prior.Status == "Voided" || prior.ProcessVersion != active.PriorProcessVersion) reason = PriorChanged;
        }
        if (reason is null && active is not null)
        {
            var wholeRun = new ReconCaller(null, "approval check", false, Application.Common.EntityScopeContext.GroupLevel,
                new Application.Common.DataScope { Level = Application.Common.DataScopeLevel.Organization, AllowedEmployeeIds = null });
            var built = await new ManpowerReconService(db).BuildAsync(tenantId, run.Id, wholeRun, active.PriorRunId, ct);
            if (built.Status != ReconBuildStatus.Ok
                || !string.Equals(built.Build!.SnapshotHash, active.SnapshotHash, StringComparison.OrdinalIgnoreCase))
                reason = Changed;
        }

        if (reason is null) return new Result(false, false, null, run.ProcessVersion, active?.ProcessVersion);
        return rules.SignoffRequired
            ? new Result(true, false, reason, run.ProcessVersion, active?.ProcessVersion)
            : new Result(false, true, reason, run.ProcessVersion, active?.ProcessVersion);
    }
}
