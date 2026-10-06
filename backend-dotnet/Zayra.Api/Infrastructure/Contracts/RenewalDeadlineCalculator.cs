using Zayra.Api.Application.Common;
using Zayra.Api.Application.Contracts;
using Zayra.Api.Application.Entitlements;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Contracts;

/// <summary>
/// Reads the renewal lead times from statutory_rules (tenant row overrides platform, <see cref="RenewalRuleSet"/>) and
/// calls the pure <see cref="RenewalDeadlineFormulas.Compute"/> (<see cref="IRenewalDeadlineCalculator"/>). Slice R4.
///
/// <para><b>One adjustment, on purpose.</b> A contract whose own notice period is longer than the renewal lead time
/// (say 150 days' notice against a 120-day lead) would open its case after its offer and notice dates had already
/// passed, and non-renewal would be lost before anyone saw the case. <see cref="RenewalDeadlines.OpensOn"/> is
/// therefore the EARLIER of the formula's open date and the offer due date. Every other date is the formula's.</para>
/// </summary>
public sealed class RenewalDeadlineCalculator : IRenewalDeadlineCalculator
{
    private readonly ZayraDbContext _db;
    private readonly ITenantClock _clock;

    public RenewalDeadlineCalculator(ZayraDbContext db, ITenantClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<RenewalDeadlines> ComputeAsync(Guid tenantId, EmployeeContract expiring, CancellationToken ct)
    {
        var rules = await RenewalRuleSet.LoadAsync(_db, tenantId, await _clock.TodayAsync(tenantId, ct), ct);
        return Compute(expiring, rules);
    }

    /// <summary>The pure part, exposed for tests and for callers that already hold the rule set.</summary>
    public static RenewalDeadlines Compute(EmployeeContract expiring, RenewalRuleSet rules)
    {
        if (expiring.EndDate is not { } end)
            throw new ArgumentException("An indefinite contract (no end date) has no renewal deadlines.", nameof(expiring));
        var f = RenewalDeadlineFormulas.Compute(end, expiring.NonRenewalNoticeDays, rules.Deadlines);
        var opensOn = f.OfferDueOn < f.OpensOn ? f.OfferDueOn : f.OpensOn;
        if (opensOn < expiring.StartDate) opensOn = expiring.StartDate;
        return new RenewalDeadlines(opensOn, f.NoticeDueOn, f.OfferDueOn, f.QiwaSubmitDueOn, f.QiwaGateDueOn,
            rules.Deadlines.QiwaResponseDays, rules.QiwaRuleId);
    }
}
