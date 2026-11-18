using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Performance;

/// <summary>
/// THE rule for when an appraisal's rating is final enough to act on (pay, talent review): the review is
/// Published, Acknowledged or Closed AND no appeal against it is open.
///
/// <para>Not final: a review under appeal (<c>Appealed</c>, or any review with a Submitted / UnderReview appeal), a
/// review an upheld appeal or an HR correction sent back to <c>FinalApproval</c> for re-issue (it keeps its old
/// <c>PublishedAt</c>, so "has been published" is not the test), and anything before publication or excluded from
/// its cycle. A REJECTED appeal leaves the published result standing, so that review is final again.</para>
///
/// <para>Read by the merit worksheet (<c>MeritBasisLoader</c>) and the talent review (<see cref="PerformanceBands"/>),
/// so the two cannot disagree about which rating counts.</para>
/// </summary>
public static class FinalRatingRule
{
    /// <summary>The review statuses whose rating is final (subject to no open appeal).</summary>
    public static readonly string[] FinalStatuses = ["Published", "Acknowledged", "Closed"];

    /// <summary>Appeal statuses that are still open: the rating is in dispute.</summary>
    public static readonly string[] OpenAppealStatuses = ["Submitted", "UnderReview"];

    /// <summary>The tenant's reviews whose rating is final. Compose further filters on the result.</summary>
    public static IQueryable<AppraisalReview> FinalReviews(ZayraDbContext db, Guid tenantId) =>
        db.AppraisalReviews.AsNoTracking()
            .Where(r => r.TenantId == tenantId && FinalStatuses.Contains(r.Status)
                        && !db.AppraisalAppeals.Any(a => a.TenantId == tenantId && a.ReviewId == r.Id
                                                         && OpenAppealStatuses.Contains(a.Status)));
}
