using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Recruitment;

public enum OfferSendVerdict
{
    Sendable,
    InvalidState,
    ApprovalPending,
    ApprovalRejected,
}

/// <summary>
/// Rules shared by the two offer endpoints (<c>/api/recruitment/offers</c> and
/// <c>/api/recruitment/applications/offers</c>), so the application drawer and the Offers tab
/// cannot disagree about when an offer may go to the candidate.
/// </summary>
public static class OfferRules
{
    /// <summary>
    /// An offer with no approval steps can be sent from Draft. Once any approval step exists, the
    /// offer goes out only when every step is Approved (Status = Approved). A Draft offer that has
    /// approval rows is one a rejection sent back, and must not be sendable around that rejection.
    /// </summary>
    public static async Task<OfferSendVerdict> EvaluateSendAsync(ZayraDbContext db, OfferLetter offer, CancellationToken ct)
    {
        if (offer.Status is not ("Draft" or "Approved")) return OfferSendVerdict.InvalidState;

        var stepStatuses = await db.OfferApprovals.AsNoTracking()
            .Where(a => a.TenantId == offer.TenantId && a.OfferLetterId == offer.Id)
            .Select(a => a.Status)
            .ToListAsync(ct);
        if (stepStatuses.Count == 0) return OfferSendVerdict.Sendable;
        if (stepStatuses.Contains("Rejected")) return OfferSendVerdict.ApprovalRejected;
        if (offer.Status != "Approved" || stepStatuses.Any(s => s != "Approved")) return OfferSendVerdict.ApprovalPending;
        return OfferSendVerdict.Sendable;
    }

    public const string ApprovalPendingMessage =
        "This offer cannot be sent until every approval step is approved.";

    public const string ApprovalRejectedMessage =
        "An approver rejected this offer, so it cannot be sent. Generate a revised offer and submit that for approval.";

    /// <summary>A step was rejected on this offer: its terms cannot be edited, so it can never be
    /// re-approved. New approval steps would park it in PendingApproval for good.</summary>
    public static Task<bool> HasRejectedApprovalAsync(ZayraDbContext db, Guid tenantId, Guid offerId, CancellationToken ct) =>
        db.OfferApprovals.AsNoTracking()
            .AnyAsync(a => a.TenantId == tenantId && a.OfferLetterId == offerId && a.Status == "Rejected", ct);

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
}
