using Zayra.Api.Application.Common;
using Zayra.Api.Application.Contracts;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Contracts;

/// <summary>Where a term stands against Article 55, as the dashboard badge and the chain drawer's meter show it.</summary>
/// <param name="RenewalsUsed">Renewals before the expiring term (its renewal_number), or NULL when the chain is unconfirmed.</param>
/// <param name="YearsServed">Chain start to the end of the expiring term, 1 dp.</param>
/// <param name="YearsIfRenewed">Chain start to the end of the renewed term, 1 dp (what the conservative reading tests).</param>
public sealed record Art55Meter(
    string? NationalityClass,
    int? RenewalsUsed,
    int MaxRenewals,
    decimal? YearsServed,
    decimal? YearsIfRenewed,
    int MaxYears,
    bool ThresholdReached,
    string Reading);

/// <summary>
/// Reads the Art. 55 rules for a tenant and calls <see cref="Art55.DeriveAllowedActions"/> — it never re-implements
/// the rule. Adds exactly one thing on top: a chain that is not confirmed gets NO actions whatever the nationality
/// (plan §1.3 T1: an unconfirmed chain opens NeedsConfirmation and nothing is assumed). Art. 55 already does this for
/// a Saudi worker; for a non-Saudi worker the chain does not change the law, but the case still waits for it.
/// Slice R4.
/// </summary>
public sealed class AllowedActionsDeriver
{
    private readonly ZayraDbContext _db;
    private readonly ITenantClock _clock;

    public AllowedActionsDeriver(ZayraDbContext db, ITenantClock clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>The allowed actions for <paramref name="expiring"/> today, with its notice date.</summary>
    public async Task<AllowedActionsResult> DeriveAsync(Guid tenantId, EmployeeContract expiring, DateOnly noticeDueOn, CancellationToken ct)
    {
        var today = await _clock.TodayAsync(tenantId, ct);
        var rules = await RenewalRuleSet.LoadAsync(_db, tenantId, today, ct);
        return Derive(expiring, noticeDueOn, today, rules);
    }

    /// <summary>The chain fields Art. 55 counts from are on file.</summary>
    public static bool IsChainConfirmed(EmployeeContract c) => c.RenewalNumber is not null && c.ChainStartedOn is not null;

    /// <summary>The pure derivation (tests, and callers that already hold the rule set and today).</summary>
    public static AllowedActionsResult Derive(EmployeeContract expiring, DateOnly noticeDueOn, DateOnly today, RenewalRuleSet rules)
    {
        if (expiring.EndDate is not { } end)
            throw new ArgumentException("An indefinite contract (no end date) is not renewed.", nameof(expiring));
        var result = Art55.DeriveAllowedActions(new Art55Input(
            expiring.WorkerNationalityClass,
            expiring.RenewalNumber,
            expiring.ChainStartedOn,
            end,
            TermMonths(expiring.StartDate, end),
            noticeDueOn,
            today,
            rules.Art55Reading,
            rules.Art55MaxConsecutiveRenewals,
            rules.Art55MaxTotalYears));
        if (IsChainConfirmed(expiring) || result.Actions.Count == 0) return result;
        var blocks = result.BlockCodes.Append(ReleaseABlockReasons.RenewalChainUnconfirmed).Distinct().ToList();
        return result with { Actions = [], ThresholdReached = false, BlockCodes = blocks };
    }

    /// <summary>
    /// The length of a "similar term" (Art. 37) in whole months: the exact month count when the term is
    /// start + n months − 1 day (the normal shape), otherwise the nearest whole month (at least one).
    /// </summary>
    public static int TermMonths(DateOnly start, DateOnly endInclusive)
    {
        var days = endInclusive.DayNumber - start.DayNumber + 1;
        var estimate = Math.Max(1, (int)Math.Round(days / 30.436875m, MidpointRounding.AwayFromZero));
        for (var m = Math.Max(1, estimate - 1); m <= estimate + 1; m++)
            if (ContractTermMath.EndOf(start, m) == endInclusive) return m;
        return estimate;
    }

    /// <summary>The Art. 55 meter for a term (badge and chain drawer).</summary>
    public static Art55Meter Meter(EmployeeContract expiring, AllowedActionsResult derived, RenewalRuleSet rules)
    {
        decimal? served = expiring.ChainStartedOn is { } chainStart && expiring.EndDate is { } end
            ? Years(chainStart, end)
            : null;
        return new Art55Meter(expiring.WorkerNationalityClass, expiring.RenewalNumber, rules.Art55MaxConsecutiveRenewals,
            served, derived.YearsIfRenewed, rules.Art55MaxTotalYears, derived.ThresholdReached, rules.Art55Reading.ToString());
    }

    /// <summary>Inclusive years between two dates, 1 dp — the same arithmetic as <see cref="Art55"/>'s meter.</summary>
    public static decimal Years(DateOnly fromInclusive, DateOnly toInclusive) =>
        Math.Round((toInclusive.DayNumber - fromInclusive.DayNumber + 1) / 365.25m, 1, MidpointRounding.AwayFromZero);
}
