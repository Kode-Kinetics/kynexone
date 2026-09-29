namespace Zayra.Api.Infrastructure.Employees;

/// <summary>
/// Switches for applying approved employee changes on their effective date (section
/// <c>EmployeeEffectiveChanges</c>; every value is overridable by environment variable, e.g.
/// <c>EmployeeEffectiveChanges__Enabled=false</c>). Bound eagerly as a singleton POCO in <c>Program.cs</c>,
/// matching <see cref="Zayra.Api.Infrastructure.Retention.DataRetentionOptions"/>.
///
/// <para>ON BY DEFAULT, unlike the retention sweep. Not applying an approved change is the defect this
/// exists to fix, and the job cannot overwrite blindly: a change with no approval-time baseline (every
/// change approved before this shipped) or whose fields moved since approval is sent back to the Approval
/// Center instead of applied. <see cref="Enabled"/>=false is the kill switch; with it off, approved
/// future-dated changes wait exactly as they did before, and the /health "employeeChangesOverdue"
/// counter shows how many.</para>
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
}
