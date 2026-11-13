namespace Zayra.Api.Infrastructure.Leave;

/// <summary>
/// Switches for the scheduled monthly leave accrual and year rollover (section <c>LeaveAccrual</c>; every value is overridable
/// by environment variable, e.g. <c>LeaveAccrual__Enabled=true</c>). OFF by default (CTO decision 2026-11-12): turning it on posts
/// accruals, front-loads yearly policies and rolls years over for every tenant, so a deploy must never do that silently. Switch it on
/// deliberately, after the tenants' balances have been reviewed. The engine itself is guarded (every month is a ledger entry that can
/// only be posted once, and a balance holding a figure the engine did not post is left alone), so switching it off again only stops new
/// postings.
/// </summary>
public sealed class LeaveAccrualOptions
{
    public const string SectionName = "LeaveAccrual";

    /// <summary>Whether <see cref="LeaveAccrualScheduler"/> enqueues any work at all.</summary>
    public bool Enabled { get; set; }

    /// <summary>How often the scheduler looks for tenants whose month has turned. The enqueue is idempotent (one job per tenant per tenant-local month).</summary>
    public TimeSpan ScheduleInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Wait after start-up before the first look.</summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMinutes(3);
}
