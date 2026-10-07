using Zayra.Api.Application.Common;
using Zayra.Api.Application.Contracts;
using Zayra.Api.Application.Entitlements;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Contracts;

/// <summary>
/// Reads the renewal lead times from statutory_rules (tenant row overrides platform, <see cref="RenewalRuleSet"/>) and
/// calls the pure <see cref="RenewalDeadlineFormulas.Compute"/> (<see cref="IRenewalDeadlineCalculator"/>): the case
/// opens at end − max(lead, notice + offer lead + margin), never before the term starts. Slice R4.
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

    /// <summary>The lead the case opens at for a contract with the default notice: what the dashboard's window defaults to.</summary>
    public static int DefaultOpenLeadDays(RenewalRuleSet rules) =>
        Math.Max(rules.Deadlines.RenewalLeadDays, rules.Deadlines.DefaultNonRenewalNoticeDays + rules.Deadlines.OfferLeadDays + rules.Deadlines.OpenMarginDays);

    /// <summary>The pure part, exposed for tests and for callers that already hold the rule set.</summary>
    public static RenewalDeadlines Compute(EmployeeContract expiring, RenewalRuleSet rules)
    {
        if (expiring.EndDate is not { } end)
            throw new ArgumentException("An indefinite contract (no end date) has no renewal deadlines.", nameof(expiring));
        var f = RenewalDeadlineFormulas.Compute(expiring.StartDate, end, expiring.NonRenewalNoticeDays, rules.Deadlines);
        return new RenewalDeadlines(f.OpensOn, f.NoticeDueOn, f.OfferDueOn, f.QiwaSubmitDueOn, f.QiwaGateDueOn,
            rules.Deadlines.QiwaResponseDays, rules.QiwaRuleId);
    }
}
