namespace Zayra.Api.Infrastructure.Employees;

/// <summary>
/// Switches for applying approved employee changes on their effective date (section
/// <c>EmployeeEffectiveChanges</c>; every value is overridable by environment variable, e.g.
/// <c>EmployeeEffectiveChanges__Enabled=false</c>). Bound eagerly as a singleton POCO in <c>Program.cs</c>,
/// matching <see cref="Zayra.Api.Infrastructure.Retention.DataRetentionOptions"/>.
///
/// <para>ON BY DEFAULT, unlike the retention sweep. Not applying an approved change is the defect this
/// exists to fix, and the job cannot overwrite blindly: a change whose fields moved since approval, or
/// with no approval-time baseline (every change approved before this shipped), is sent back to the
/// Approval Center instead of applied — or expired, when unverifiable and older than
/// <see cref="UnverifiableMaxAgeDays"/>. <see cref="Enabled"/>=false is the kill switch; with it off,
/// approved future-dated changes wait exactly as they did before, and the /health "employeeChangesOverdue"
/// counter shows how many. RECOMMENDED FIRST DEPLOY: ship with it off, run the read-only count in the PR,
/// then turn it on.</para>
/// </summary>
public sealed class EffectiveChangeOptions
{
    public const string SectionName = "EmployeeEffectiveChanges";

    /// <summary>Whether <see cref="EffectiveChangeScheduler"/> enqueues any work at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How often the scheduler looks for changes whose date has arrived. An hour means a change takes
    /// effect within an hour of local midnight. The enqueue is idempotent, so a shorter interval only
    /// costs one small query per tick.
    /// </summary>
    public TimeSpan ScheduleInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Wait after start-up before the first look (let the host and migrations settle).</summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>Most changes one tenant's job takes per run; the rest are picked up by the next run.</summary>
    public int MaxChangesPerRun { get; set; } = 500;

    /// <summary>
    /// A change whose approval-time values cannot be checked (approved before they were recorded, or the
    /// record is unreadable) is EXPIRED — audited, notified, closed, never applied — when it was approved, or
    /// was due, more than this many days ago. Younger ones go back to the Approval Center. Stops the first run
    /// after deploy from offering months-old IBANs for a reflex re-approval.
    /// </summary>
    public int UnverifiableMaxAgeDays { get; set; } = 30;
}
