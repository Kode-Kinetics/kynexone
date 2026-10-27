using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>An HTTP-agnostic result: the status the caller should answer with and an optional body.</summary>
public sealed record RuleOutcome(int Status, object? Body);

public sealed record StoredChange(string Key, string? From, string? To);

public interface ICompanyPayrollRulesService
{
    Task<bool> CompanyExistsAsync(Guid tenantId, Guid companyId, CancellationToken ct);
    Task<Guid?> CompanyOfRequestAsync(Guid tenantId, Guid requestId, CancellationToken ct);
    Task<object> ViewAsync(Guid tenantId, Guid companyId, CancellationToken ct);
    Task<RuleOutcome> SaveAsync(Guid tenantId, Guid? userId, Guid companyId, List<PayrollRuleChange> requestChanges,
        bool acknowledge, string? note, DateOnly? effectiveFrom, List<string>? acknowledgedWarnings, CancellationToken ct);
    Task<RuleOutcome> DecideAsync(Guid tenantId, Guid? userId, Guid id, bool approve, string? note, CancellationToken ct);
}

/// <summary>
/// Data access and the maker-checker flow behind Setup -> Payroll rules (see <see cref="CompanyPayrollRules"/>).
/// Every write and its audit row share one transaction owned by the execution strategy. Scope checks and HTTP mapping
/// stay in the controller.
/// </summary>
public sealed class CompanyPayrollRulesService(ZayraDbContext _db, IAuditService _audit) : ICompanyPayrollRulesService
{
    private static RuleOutcome Fail(int status, object? body) => new(status, body);
    private static RuleOutcome Done(int status, object? body) => new(status, body);

    public Task<bool> CompanyExistsAsync(Guid tenantId, Guid companyId, CancellationToken ct)
        => _db.Companies.AsNoTracking().AnyAsync(c => c.TenantId == tenantId && c.Id == companyId, ct);

    public Task<Guid?> CompanyOfRequestAsync(Guid tenantId, Guid requestId, CancellationToken ct)
        => _db.PayrollRuleChangeRequests.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.Id == requestId).Select(r => (Guid?)r.CompanyId).FirstOrDefaultAsync(ct);

    public async Task<object> ViewAsync(Guid tenantId, Guid companyId, CancellationToken ct)
    {
        var rules = await CompanyPayrollRules.ListAsync(_db, tenantId, companyId, ct);
        var effective = await CompanyPayrollRules.ResolveAsync(_db, tenantId, companyId, ct);
        var pending = await _db.PayrollRuleChangeRequests.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.CompanyId == companyId && r.Status == PayrollRuleChangeRequest.Pending)
            .OrderBy(r => r.RequestedAtUtc).ToListAsync(ct);
        // The leave payout on exit pays only a leave type coded ANNUAL or categorised Annual: tell HR when there is none.
        var annualLeaveTypeFound = (await _db.LeaveTypes.AsNoTracking().Where(t => t.TenantId == tenantId).ToListAsync(ct))
            .Any(Zayra.Api.Infrastructure.Payroll.LeaveEncashmentCalculator.IsAnnualType);
        return new
        {
            companyId, rules, effective, annualLeaveTypeFound,
            pending = pending.Select(r => new
            {
                r.Id, r.RequestedBy, r.RequestedAtUtc, r.Reason, r.EffectiveFrom,
                changes = JsonSerializer.Deserialize<List<StoredChange>>(r.ChangesJson) ?? [],
                warnings = JsonSerializer.Deserialize<List<string>>(r.WarningCodesJson) ?? [],
            }).ToList(),
        };
    }

    public async Task<RuleOutcome> SaveAsync(Guid tenantId, Guid? userId, Guid companyId, List<PayrollRuleChange> requestChanges,
        bool acknowledge, string? note, DateOnly? effectiveFrom, List<string>? acknowledgedWarnings, CancellationToken ct)
    {
        return await InTransactionAsync(async () =>
        {
            var nowUtc = DateTime.UtcNow;
            var today = CompanyPayrollRules.Today(nowUtc);
            var startDay = effectiveFrom ?? today;
            if (startDay < today)
                return Fail(400, new { code = "ERR_PAYROLL_RULE_DATE_PAST", message = "A rule cannot start on a day that has already passed: choose today or a later date." });

            // "From" is what is in force on the day the change starts, so a change queued behind an earlier dated one diffs against it.
            var before = (await CompanyPayrollRules.ListAsync(_db, tenantId, companyId, ct, startDay)).ToDictionary(r => r.Key, r => r.Value);
            // Only the keys that actually change are saved, acknowledged and audited.
            var changes = requestChanges.Where(c => before.GetValueOrDefault(c.Key)?.Trim() != c.Value?.Trim()).ToList();
            if (changes.Count == 0) return Done(200, await ViewAsync(tenantId, companyId, ct));

            var (errors, warnings) = CompanyPayrollRules.Validate(changes);
            if (errors.Count > 0)
                return Fail(400, new { code = "ERR_PAYROLL_RULE_INVALID", message = string.Join(" ", errors), errors });

            var needsApproval = warnings.Count > 0
                || changes.Any(c => CompanyPayrollRules.LowersPay(c.Key, before.GetValueOrDefault(c.Key), c.Value));
            var warningCodes = CompanyPayrollRules.WarningCodesFor(changes);

            if (!needsApproval)
            {
                var saved = await TrySaveAsync(() => CompanyPayrollRules.SaveAsync(_db, tenantId, companyId, userId, changes,
                    false, null, nowUtc, ct, startDay));
                if (saved.Failure is not null) return saved.Failure;
                // The rules were saved by the helper above; the audit row commits in this same outer transaction
                // (InTransactionAsync), so the change cannot exist without it.
                StageAudit("payroll.rules.changed", companyId, tenantId, userId, new
                {
                    companyId, effectiveFrom = startDay, approval = "not needed (cannot lower pay)",
                    changes = changes.Select(c => new StoredChange(c.Key, before.GetValueOrDefault(c.Key), c.Value)),
                });
                await _db.SaveChangesAsync(ct);
                return Done(200, await ViewAsync(tenantId, companyId, ct));
            }

            // Pay-lowering or below-statutory: the maker states it, a different approver decides it.
            if (warnings.Count > 0 && !acknowledge)
                return Fail(409, new
                {
                    code = "ERR_PAYROLL_RULE_ACK_REQUIRED",
                    message = "One or more values are below the statutory minimum. Confirm them to send for approval.",
                    warnings, warningCodes,
                });
            if ((note?.Trim().Length ?? 0) < 5)
                return Fail(400, new
                {
                    code = "ERR_PAYROLL_RULE_REASON_REQUIRED",
                    message = "A reason of at least 5 characters is required for a change that lowers pay or goes below the statutory minimum.",
                });
            if (warnings.Count > 0 && !warningCodes.SequenceEqual((acknowledgedWarnings ?? []).OrderBy(x => x, StringComparer.Ordinal)))
                return Fail(400, new
                {
                    code = "ERR_PAYROLL_RULE_ACK_MISMATCH",
                    message = "The acknowledgement does not match the warnings raised by these values. Review them and confirm again.",
                    warnings, warningCodes,
                });
            if (!await CompanyPayrollRules.HasEligibleApproverAsync(_db, tenantId, companyId, userId, ct))
                return Fail(409, new
                {
                    code = "ERR_PAYROLL_RULE_NO_APPROVER",
                    message = "This change lowers pay and needs a second person to approve it, but no one else can. Add another user with the payroll approval permission (for example a Finance Approver), then try again.",
                    messageAr = "هذا التغيير يخفض الأجور ويحتاج إلى شخص آخر للموافقة عليه، لكن لا يوجد من يستطيع ذلك. أضف مستخدمًا آخر لديه صلاحية اعتماد الرواتب (مثل المعتمد المالي) ثم حاول مرة أخرى.",
                });

            var pending = new PayrollRuleChangeRequest
            {
                TenantId = tenantId, CompanyId = companyId, Reason = note!.Trim(), EffectiveFrom = startDay,
                ChangesJson = JsonSerializer.Serialize(changes.Select(c => new StoredChange(c.Key, before.GetValueOrDefault(c.Key), c.Value))),
                WarningCodesJson = JsonSerializer.Serialize(warningCodes),
                RequestedBy = userId, RequestedAtUtc = nowUtc,
            };
            _db.PayrollRuleChangeRequests.Add(pending);
            StageAudit("payroll.rules.change_requested", companyId, tenantId, userId, new
            {
                requestId = pending.Id, companyId, effectiveFrom = startDay, warnedKeys = warningCodes, reason = pending.Reason,
                changes = changes.Select(c => new StoredChange(c.Key, before.GetValueOrDefault(c.Key), c.Value)),
            });
            await _db.SaveChangesAsync(ct);
            return Done(202, new { status = "PendingApproval", requestId = pending.Id, view = await ViewAsync(tenantId, companyId, ct) });
        }, ct);
    }

    public async Task<RuleOutcome> DecideAsync(Guid tenantId, Guid? userId, Guid id, bool approve, string? note, CancellationToken ct)
    {
        return await InTransactionAsync(async () =>
        {
            var request = await _db.PayrollRuleChangeRequests.FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Id == id, ct);
            if (request is null) return Fail(404, null);
            if (request.Status != PayrollRuleChangeRequest.Pending)
                return Fail(409, new { code = "ERR_PAYROLL_RULE_NOT_PENDING", message = $"This change was already {request.Status.ToLowerInvariant()}." });
            if (userId is null || request.RequestedBy == userId)
                return Fail(403, new { code = "ERR_PAYROLL_RULE_SELF_APPROVAL", message = "You made this change, so someone else has to decide it." });

            var nowUtc = DateTime.UtcNow;
            var stored = JsonSerializer.Deserialize<List<StoredChange>>(request.ChangesJson) ?? [];
            if (approve)
            {
                var changes = stored.Select(c => new PayrollRuleChange(c.Key, c.To)).ToList();
                var (errors, warnings) = CompanyPayrollRules.Validate(changes);
                if (errors.Count > 0)
                    return Fail(400, new { code = "ERR_PAYROLL_RULE_INVALID", message = string.Join(" ", errors), errors });
                var effectiveFrom = request.EffectiveFrom > CompanyPayrollRules.Today(nowUtc) ? request.EffectiveFrom : CompanyPayrollRules.Today(nowUtc);
                try
                {
                    // The acknowledgement on the rows is the MAKER's; the approver is recorded on the request and in the audit.
                    await CompanyPayrollRules.ApplyAsync(_db, tenantId, request.CompanyId, request.RequestedBy, changes,
                        warnings.Select(w => w.Key).ToHashSet(), request.Reason, nowUtc, effectiveFrom, ct);
                }
                catch (DbUpdateException)
                {
                    return Fail(409, new { code = "ERR_PAYROLL_RULE_CONFLICT", message = "Another person saved these rules at the same time. Reload and try again." });
                }
                request.EffectiveFrom = effectiveFrom;
            }
            request.Status = approve ? PayrollRuleChangeRequest.Approved : PayrollRuleChangeRequest.Rejected;
            request.DecidedBy = userId;
            request.DecidedAtUtc = nowUtc;
            request.DecisionNote = note?.Trim();
            StageAudit(approve ? "payroll.rules.change_approved" : "payroll.rules.change_rejected", request.CompanyId, tenantId, userId, new
            {
                requestId = request.Id, request.CompanyId, requestedBy = request.RequestedBy, decidedBy = userId,
                effectiveFrom = request.EffectiveFrom, decisionNote = request.DecisionNote, changes = stored,
            });
            if (approve)
                StageAudit("payroll.rules.changed", request.CompanyId, tenantId, userId, new
                {
                    request.CompanyId, effectiveFrom = request.EffectiveFrom, requestId = request.Id, madeBy = request.RequestedBy,
                    approvedBy = userId, warnedKeys = JsonSerializer.Deserialize<List<string>>(request.WarningCodesJson), acknowledgedNote = request.Reason,
                    changes = stored,
                });
            await _db.SaveChangesAsync(ct);
            return Done(200, await ViewAsync(tenantId, request.CompanyId, ct));
        }, ct);
    }

    private static async Task<(RuleOutcome? Failure, PayrollRuleSaveResult? Result)> TrySaveAsync(Func<Task<PayrollRuleSaveResult>> save)
    {
        try
        {
            var result = await save();
            if (result.Errors.Count > 0)
                return (Fail(400, new { code = "ERR_PAYROLL_RULE_INVALID", message = string.Join(" ", result.Errors), errors = result.Errors }), null);
            return (null, result);
        }
        catch (DbUpdateException)
        {
            // Two first-saves of the same rule raced on the unique index: tell the caller to reload, not a 500.
            return (Fail(409, new { code = "ERR_PAYROLL_RULE_CONFLICT", message = "Another person saved these rules at the same time. Reload and try again." }), null);
        }
    }

    /// <summary>
    /// Runs the write AND its audit row as one transaction. The execution strategy owns the transaction: the Npgsql retrying
    /// strategy refuses a user-started transaction outside it, and a retry replays the whole unit from a clean tracker.
    /// A non-2xx result rolls everything back.
    /// </summary>
    private async Task<RuleOutcome> InTransactionAsync(Func<Task<RuleOutcome>> work, CancellationToken ct)
    {
        if (!_db.Database.IsRelational()) return await work();
        RuleOutcome result = null!;
        await _db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var transaction = await _db.Database.BeginTransactionAsync(ct);
            result = await work();
            if (result.Status is >= 200 and < 300) await transaction.CommitAsync(ct);
            else { await transaction.RollbackAsync(ct); _db.ChangeTracker.Clear(); }
        });
        return result;
    }

    private void StageAudit(string action, Guid companyId, Guid tenantId, Guid? userId, object payload)
        => _audit.Stage(action, "CompanyPayrollRules", companyId.ToString(),
            new RequestContext(null, null, userId, tenantId), JsonSerializer.Serialize(payload));
}
