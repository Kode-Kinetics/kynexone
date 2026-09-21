namespace Zayra.Api.Infrastructure.Retention;

/// <summary>
/// D3 — the switches that decide whether the retention sweep can destroy anything.
///
/// <para>EVERY DEFAULT HERE IS THE SAFE ONE, deliberately. The sweep does not run at all unless
/// <see cref="ScheduleEnabled"/> is turned on, and even then it cannot change a single subject row
/// unless <see cref="ApplyDeletions"/> is ALSO turned on. Erasing a whole tenant needs a third switch,
/// <see cref="AllowTenantErasure"/>. A mechanism nobody has turned on is recoverable; a row deleted in
/// error is not, so the asymmetry is priced into the defaults rather than into a runbook.</para>
///
/// <para>Bound eagerly as a singleton POCO in <c>Program.cs</c>, matching
/// <see cref="Zayra.Api.Infrastructure.Jobs.BackgroundJobOptions"/> — section <c>DataRetention</c>, so
/// every value is overridable by environment variable (<c>DataRetention__ApplyDeletions=true</c>).</para>
/// </summary>
public sealed class DataRetentionOptions
{
    public const string SectionName = "DataRetention";

    /// <summary>
    /// Whether <see cref="DataRetentionScheduler"/> enqueues the daily sweep at all. OFF by default: on
    /// a fresh deployment nobody has yet read a dry-run report, so there is nothing to act on and no
    /// reason to be writing assessment rows. Turning ONLY this on gives a daily dry run and nothing else.
    /// </summary>
    public bool ScheduleEnabled { get; set; }

    /// <summary>
    /// THE MASTER SAFETY SWITCH. False (the default) means every sweep is a dry run: rules still evaluate
    /// and still write their findings to <c>retention_purge_audits</c>, but no subject row is anonymised
    /// or deleted. Nothing else in this class can override it.
    /// </summary>
    public bool ApplyDeletions { get; set; }

    /// <summary>
    /// A SECOND switch for the one disposition that is irreversible at scale: erasing every row of a
    /// soft-deleted tenant. Requires <see cref="ApplyDeletions"/> as well. Off, the tenant rule reports
    /// eligibility and retains.
    /// </summary>
    public bool AllowTenantErasure { get; set; }

    /// <summary>
    /// How long a soft-deleted tenant stays recoverable. 90 days is not invented here — it is what the
    /// published privacy policy already promises ("Deleted accounts — anonymised within 90 days of
    /// account closure"), so this default makes the product match the commitment rather than a guess.
    /// </summary>
    public int SoftDeletedTenantRetentionDays { get; set; } = 90;

    /// <summary>
    /// The statutory floor under a payroll record, in years, measured from the record's own date and NOT
    /// from the employee's departure. An employee's erasure clock can expire while their last payslip is
    /// still inside this window; when it does the employee rule retains and records why. 7 years is the
    /// figure the product's own privacy policy publishes for payroll. <b>This is a legal parameter with an
    /// engineering default — see the lawyer questions in scratchpad/data-retention.md.</b>
    /// </summary>
    public int StatutoryPayrollRetentionYears { get; set; } = 7;

    /// <summary>
    /// Grace after a refresh token's own expiry before the row may be removed. The token is already
    /// unusable at expiry; the grace keeps it available a while longer for replay forensics.
    /// </summary>
    public int RefreshTokenGraceDays { get; set; } = 30;

    /// <summary>
    /// Candidates one rule may consider in one sweep. Bounds the work and, because rules order their
    /// scans oldest-first, a backlog larger than this drains over successive days instead of starving.
    /// Capped at 1000 by <see cref="Zayra.Api.Infrastructure.Data.ScopedBypass"/>'s own bound.
    /// </summary>
    public int MaxCandidatesPerRule { get; set; } = 200;

    /// <summary>How often the scheduler looks for tenants that have not had today's sweep.</summary>
    public TimeSpan ScheduleInterval { get; set; } = TimeSpan.FromHours(6);
}
