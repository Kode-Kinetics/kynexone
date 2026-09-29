using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Recruitment;

public enum OfferSendVerdict
{
    Sendable,
    InvalidState,
    ApprovalRequired,
    ApprovalPending,
    ApprovalRejected,
    SenderIsApprover,
}

public enum OfferApproverVerdict
{
    Eligible,
    NoApproverNamed,
    AuthorUnknown,
    ApproverIsAuthor,
    NotAnEligibleUser,
}

/// <summary>
/// Rules shared by the two offer endpoints (<c>/api/recruitment/offers</c> and
/// <c>/api/recruitment/applications/offers</c>), so the application drawer and the Offers tab
/// cannot disagree about when an offer may go to the candidate, who may approve it, and when it
/// may be declined.
///
/// <para>Offer approval is a tenant policy that is ON unless an Admin switches it off
/// (<see cref="PolicyCategory"/>/<see cref="PolicyKey"/> = false). With it on, an offer needs at
/// least one approval step, and every step Approved, before it can be sent. With it off, an offer
/// that any approver on the same application has rejected still needs approval: otherwise
/// "generate a revised offer" would send the rejected terms around the rejection.</para>
///
/// <para>Maker-checker. <c>OfferLetter</c> has no creator or sender column (adding one needs a
/// migration), so both are read from the offer's own <see cref="RecruitmentAuditLog"/> rows
/// (<see cref="CreatedAction"/>, <see cref="SentAction"/>), which every creation and send path
/// writes. An approver is a named, active Admin or HR Manager who is not the offer's author; only
/// that named person may decide their step; and whoever approved a step may not also send it.</para>
/// </summary>
public static class OfferRules
{
    public const string PolicyCategory = "Recruitment";
    public const string PolicyKey = "OfferApprovalRequired";

    public const string CreatedAction = "Created";
    public const string SentAction = "Sent";
    public const string AcceptedAction = "Accepted";
    public const string ApprovalDecidedAction = "ApprovalDecided";

    /// <summary>The roles <c>OffersController.DecideApproval</c> admits. A step naming anyone else
    /// could never be decided.</summary>
    public static readonly string[] ApproverRoleNames = { "Admin", "HR Manager" };

    public const string ApprovalRequiredMessage =
        "This offer needs approval before it can be sent. Request approval from an HR Manager or Admin who did not write the offer.";

    public const string ApprovalPendingMessage =
        "This offer cannot be sent until every approval step is approved.";

    public const string ApprovalRejectedMessage =
        "An approver rejected this offer, so it cannot be sent. Generate a revised offer and submit that for approval.";

    public const string SenderIsApproverMessage =
        "You approved this offer, so someone else has to send it.";

    public const string DeclineStateMessage =
        "Only an offer that has been sent to the candidate can be declined. An accepted offer is reversed through onboarding, not by declining it.";

    // ── Policy ────────────────────────────────────────────────────────────────

    /// <summary>The tenant's offer-approval policy. Absent means ON: approval is not opt-in.</summary>
    public static async Task<bool> IsApprovalRequiredByPolicyAsync(ZayraDbContext db, Guid tenantId, CancellationToken ct)
    {
        var value = await db.SystemSettings.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.Category == PolicyCategory && s.SettingKey == PolicyKey)
            .Select(s => s.SettingValue)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(value)) return true;
        return value.Trim().ToLowerInvariant() is not ("false" or "0" or "off" or "no" or "disabled");
    }

    /// <summary>Whether this offer must pass approval: the policy, or a rejection of any offer
    /// on the same application. A revision of a rejected offer carries the rejection's requirement.</summary>
    public static async Task<bool> IsApprovalRequiredAsync(ZayraDbContext db, OfferLetter offer, CancellationToken ct)
    {
        if (await IsApprovalRequiredByPolicyAsync(db, offer.TenantId, ct)) return true;
        return await db.OfferApprovals.AsNoTracking()
            .AnyAsync(a => a.TenantId == offer.TenantId && a.ApplicationId == offer.ApplicationId && a.Status == "Rejected", ct);
    }

    // ── Send ──────────────────────────────────────────────────────────────────

    public static async Task<OfferSendVerdict> EvaluateSendAsync(
        ZayraDbContext db, OfferLetter offer, Guid? senderUserId, CancellationToken ct)
    {
        if (offer.Status is not ("Draft" or "Approved")) return OfferSendVerdict.InvalidState;

        var steps = await db.OfferApprovals.AsNoTracking()
            .Where(a => a.TenantId == offer.TenantId && a.OfferLetterId == offer.Id)
            .Select(a => new { a.Status, a.ApproverUserId })
            .ToListAsync(ct);
        if (steps.Any(s => s.Status == "Rejected")) return OfferSendVerdict.ApprovalRejected;
        if (steps.Count == 0)
            return await IsApprovalRequiredAsync(db, offer, ct) ? OfferSendVerdict.ApprovalRequired : OfferSendVerdict.Sendable;
        if (offer.Status != "Approved" || steps.Any(s => s.Status != "Approved")) return OfferSendVerdict.ApprovalPending;
        if (senderUserId is { } sender && steps.Any(s => s.ApproverUserId == sender)) return OfferSendVerdict.SenderIsApprover;
        return OfferSendVerdict.Sendable;
    }

    public static (string Error, string Message) SendRefusal(OfferSendVerdict verdict) => verdict switch
    {
        OfferSendVerdict.ApprovalRequired => ("offer_approval_required", ApprovalRequiredMessage),
        OfferSendVerdict.ApprovalPending => ("offer_approval_incomplete", ApprovalPendingMessage),
        OfferSendVerdict.ApprovalRejected => ("offer_approval_rejected", ApprovalRejectedMessage),
        OfferSendVerdict.SenderIsApprover => ("offer_maker_checker", SenderIsApproverMessage),
        _ => throw new InvalidOperationException($"No refusal for send verdict '{verdict}'."),
    };

    // ── Decline ───────────────────────────────────────────────────────────────

    /// <summary>Only a Sent offer can be declined. Declining an Accepted offer left the application
    /// Hired and its employee draft live, as if the candidate had joined.</summary>
    public static bool CanDecline(OfferLetter offer) => offer.Status == "Sent";

    // ── Approvers ─────────────────────────────────────────────────────────────

    /// <summary>A step was rejected on this offer: its terms cannot be edited, so it can never be
    /// re-approved. New approval steps would park it in PendingApproval for good.</summary>
    public static Task<bool> HasRejectedApprovalAsync(ZayraDbContext db, Guid tenantId, Guid offerId, CancellationToken ct) =>
        db.OfferApprovals.AsNoTracking()
            .AnyAsync(a => a.TenantId == tenantId && a.OfferLetterId == offerId && a.Status == "Rejected", ct);

    /// <summary>Who wrote the offer, from its <see cref="CreatedAction"/> audit row. Null when the
    /// offer predates that record or was written by the system.</summary>
    public static Task<Guid?> AuthorAsync(ZayraDbContext db, Guid tenantId, Guid offerId, CancellationToken ct)
    {
        var entityId = offerId.ToString();
        return db.RecruitmentAuditLogs.AsNoTracking()
            .Where(l => l.TenantId == tenantId && l.EntityType == "Offer" && l.EntityId == entityId && l.Action == CreatedAction)
            .OrderBy(l => l.CreatedAtUtc)
            .Select(l => l.PerformedByUserId)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Active users of the tenant who hold a role that can decide an offer approval.</summary>
    public static IQueryable<Domain.Entities.User> EligibleApprovers(ZayraDbContext db, Guid tenantId)
    {
        var normalized = ApproverRoleNames.Select(r => r.ToUpperInvariant()).ToArray();
        return db.Users.AsNoTracking()
            .Where(u => u.TenantId == tenantId && u.IsActive && !u.IsDeleted && u.Status == "Active"
                && u.UserRoles.Any(ur => ur.Role != null && ur.Role.IsActive && !ur.Role.IsDeleted
                    && normalized.Contains(ur.Role.NormalizedName)));
    }

    public static async Task<OfferApproverVerdict> EvaluateApproverAsync(
        ZayraDbContext db, Guid tenantId, Guid offerId, Guid? approverUserId, CancellationToken ct)
    {
        if (approverUserId is not { } approver || approver == Guid.Empty) return OfferApproverVerdict.NoApproverNamed;
        var author = await AuthorAsync(db, tenantId, offerId, ct);
        if (author is null) return OfferApproverVerdict.AuthorUnknown;
        if (author == approver) return OfferApproverVerdict.ApproverIsAuthor;
        if (!await EligibleApprovers(db, tenantId).AnyAsync(u => u.Id == approver, ct)) return OfferApproverVerdict.NotAnEligibleUser;
        return OfferApproverVerdict.Eligible;
    }

    public static (string Error, string Message) ApproverRefusal(OfferApproverVerdict verdict) => verdict switch
    {
        OfferApproverVerdict.NoApproverNamed => ("offer_approver_required",
            "Name the person who approves this step. Only that person can decide it."),
        OfferApproverVerdict.AuthorUnknown => ("offer_author_unknown",
            "This offer was created before its author was recorded, so an independent approver cannot be checked. Generate a new offer and request approval on that."),
        OfferApproverVerdict.ApproverIsAuthor => ("offer_maker_checker",
            "The person who wrote the offer cannot approve it. Choose another HR Manager or Admin."),
        OfferApproverVerdict.NotAnEligibleUser => ("offer_approver_ineligible",
            "The approver must be an active HR Manager or Admin in this organisation."),
        _ => throw new InvalidOperationException($"No refusal for approver verdict '{verdict}'."),
    };

    // ── Hire makers ───────────────────────────────────────────────────────────

    /// <summary>
    /// For an accepted offer's employee draft: everyone who made the hire happen on the recruitment
    /// side — whoever accepted the offer (the draft's creator) and whoever sent it to the candidate.
    /// Draft activation must be done by someone else. Empty for a draft that did not come from an
    /// offer.
    /// </summary>
    public static async Task<IReadOnlySet<Guid>> HireMakersForDraftAsync(ZayraDbContext db, Guid tenantId, Guid draftId, CancellationToken ct)
    {
        var applicationId = await db.JobApplications.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.OnboardingDraftId == draftId)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync(ct);
        if (applicationId is null) return new HashSet<Guid>();

        var offerIds = await db.OfferLetters.AsNoTracking()
            .Where(o => o.TenantId == tenantId && o.ApplicationId == applicationId && o.Status == "Accepted")
            .Select(o => o.Id.ToString())
            .ToListAsync(ct);
        var makers = await db.RecruitmentAuditLogs.AsNoTracking()
            .Where(l => l.TenantId == tenantId && l.EntityType == "Offer" && offerIds.Contains(l.EntityId)
                && (l.Action == SentAction || l.Action == AcceptedAction) && l.PerformedByUserId != null)
            .Select(l => l.PerformedByUserId!.Value)
            .Distinct()
            .ToListAsync(ct);
        var creator = await db.EmployeeDrafts.AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.Id == draftId)
            .Select(d => d.CreatedByUserId)
            .FirstOrDefaultAsync(ct);
        var set = new HashSet<Guid>(makers);
        if (creator is { } c && c != Guid.Empty) set.Add(c);
        set.Remove(Guid.Empty);
        return set;
    }

    // ── Currency ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The currency an offer is denominated in: its legal entity's default currency, falling back to
    /// the tenant's. Never a hard-coded code — a group can employ in more than one currency.
    /// </summary>
    public static async Task<string> ResolveCurrencyAsync(ZayraDbContext db, Guid tenantId, Guid? companyId, CancellationToken ct)
    {
        if (companyId is Guid id)
        {
            var companyCurrency = await db.Companies.AsNoTracking()
                .Where(c => c.TenantId == tenantId && c.Id == id)
                .Select(c => c.DefaultCurrency)
                .FirstOrDefaultAsync(ct);
            if (!string.IsNullOrWhiteSpace(companyCurrency)) return companyCurrency.Trim().ToUpperInvariant();
        }
        return (await db.ResolveTenantCurrencyAsync(tenantId, ct)).Trim().ToUpperInvariant();
    }

    /// <summary>The audit row every creation, send, decision and acceptance path writes against the
    /// offer. The maker-checker rules above read them back.</summary>
    public static RecruitmentAuditLog AuditRow(Guid tenantId, Guid offerId, string action, Guid? userId, string userName, object? newValues = null) => new()
    {
        TenantId = tenantId,
        EntityType = "Offer",
        EntityId = offerId.ToString(),
        Action = action,
        PerformedByUserId = userId,
        PerformedByName = userName,
        NewValuesJson = newValues is null ? "{}" : System.Text.Json.JsonSerializer.Serialize(newValues),
    };
}
