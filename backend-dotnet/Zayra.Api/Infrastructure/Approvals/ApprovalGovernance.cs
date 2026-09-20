using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Approvals;

/// <summary>
/// W2-E — per-tenant approval governance.
///
/// <para><b>Different person at each step.</b> Off by default, so a tenant that never touches it keeps
/// exactly the pre-W2-E behaviour. When it is on, a user who decided an earlier step of the current
/// submission of a request cannot decide a later step of it. "Decided" covers approve, reject and send
/// back alike: segregation of duties is about one person carrying a request through two checkpoints,
/// and any decision at the later checkpoint is that.</para>
///
/// <para><b>How <c>approvals.override</c> interacts.</b> Override does NOT bypass this rule. Override
/// exists to unblock routing — to let an administrator act on a step whose routed person is absent or
/// whose queue nobody holds. It is not a licence to be two approvers. The people most likely to hold
/// override are exactly the people most likely to also hold both a Manager and an HR role, so an override
/// that bypassed the rule would defeat it in the one case it is meant to catch. This mirrors the existing
/// maker-checker rule, which override also does not bypass (<c>CanDecideRequestAsync</c> checks the
/// requester before it looks at override). An administrator who has already decided an earlier step
/// and needs the request moved can reassign it to someone else; they cannot decide it themselves.</para>
///
/// <para>The check reads <see cref="ApprovalDecision"/> rows, which both the leave aggregate and the
/// generic engine write for every decision, so the rule holds whichever screen or endpoint is used.</para>
/// </summary>
public static class ApprovalGovernance
{
    public const string SettingsCategory = "Approvals";
    public const string DistinctApproverKey = "RequireDistinctApproverPerStep";

    public static async Task<bool> RequiresDistinctApproverAsync(ZayraDbContext db, Guid tenantId, CancellationToken ct)
    {
        var value = await db.SystemSettings.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.Category == SettingsCategory && s.SettingKey == DistinctApproverKey)
            .Select(s => s.SettingValue)
            .FirstOrDefaultAsync(ct);
        return bool.TryParse(value, out var on) && on;
    }

    public static async Task<ApprovalGovernanceSettingsDto> GetSettingsAsync(ZayraDbContext db, Guid tenantId, CancellationToken ct)
        => new(await RequiresDistinctApproverAsync(db, tenantId, ct));

    public static async Task<ApprovalGovernanceSettingsDto> SaveSettingsAsync(
        ZayraDbContext db, Guid tenantId, ApprovalGovernanceSettingsDto settings, Guid? userId, CancellationToken ct)
    {
        var row = await db.SystemSettings
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.Category == SettingsCategory && s.SettingKey == DistinctApproverKey, ct);
        if (row is null)
        {
            row = new SystemSetting
            {
                TenantId = tenantId,
                Category = SettingsCategory,
                SettingKey = DistinctApproverKey,
                DataType = "bool",
                Description = "Require a different person to decide each step of an approval chain.",
            };
            db.SystemSettings.Add(row);
        }
        row.SettingValue = settings.RequireDistinctApproverPerStep ? "true" : "false";
        row.UpdatedAtUtc = DateTime.UtcNow;
        row.UpdatedBy = userId;
        await db.SaveChangesAsync(ct);
        return settings;
    }

    /// <summary>
    /// The earliest earlier step of the request's current submission round that <paramref name="userId"/>
    /// decided, or null when they decided none (or the rule is off). Only the CURRENT round counts: a
    /// resubmission is a fresh pass through the chain.
    /// </summary>
    public static async Task<int?> EarlierStepDecidedByAsync(
        ZayraDbContext db, Guid tenantId, Guid approvalRequestId, int submissionRound, int currentStep, Guid? userId, CancellationToken ct)
    {
        if (userId is null) return null;
        if (!await RequiresDistinctApproverAsync(db, tenantId, ct)) return null;
        return await db.ApprovalDecisions.AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.ApprovalRequestId == approvalRequestId
                && d.SubmissionRound == submissionRound && d.StepOrder < currentStep && d.DecidedByUserId == userId)
            .OrderBy(d => d.StepOrder)
            .Select(d => (int?)d.StepOrder)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Throws <see cref="ApprovalDistinctApproverException"/> when the rule is on and the caller decided an earlier step.</summary>
    public static async Task EnsureDistinctApproverAsync(
        ZayraDbContext db, Guid tenantId, Guid approvalRequestId, int submissionRound, int currentStep, Guid? userId, CancellationToken ct)
    {
        var earlier = await EarlierStepDecidedByAsync(db, tenantId, approvalRequestId, submissionRound, currentStep, userId, ct);
        if (earlier is not null) throw new ApprovalDistinctApproverException(earlier.Value, currentStep);
    }
}
