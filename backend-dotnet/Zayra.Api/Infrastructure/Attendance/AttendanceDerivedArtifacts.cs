using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Attendance;

/// <summary>
/// The ONE writer of the rows that a settled attendance day implies.
///
/// <para>One attendance day is read by three different surfaces from three different tables:</para>
/// <list type="bullet">
///   <item><c>attendance_daily_records</c> — the Attendance grid</item>
///   <item><c>attendance_records</c> — the legacy row the executive dashboard SUMS for its
///     overtime-hours tile and thresholds for its churn heuristic</item>
///   <item><c>attendance_payroll_impacts</c> — the ONLY table the payroll run reads for
///     attendance-driven money (overtime payable, late / early-exit / absence deductions)</item>
/// </list>
///
/// <para>The processor wrote all three; <c>MigrationImportController</c> wrote only the first. An
/// imported month therefore showed its overtime on the grid, read ZERO on the dashboard, and was
/// never paid — and its lateness and absences were never deducted. Making payroll read the other
/// copy instead would have spread the duplication further, so both doors now call this one writer
/// and produce the same state by construction.</para>
///
/// <para>Static and <c>ZayraDbContext</c>-only on purpose: the migration importer must be able to
/// call it without taking a dependency on <c>AttendanceService</c>, whose constructor pulls in
/// notifications and an HTTP client factory that an import has no use for.</para>
/// </summary>
public static class AttendanceDerivedArtifacts
{
    /// <summary>What one absent day costs when no jurisdiction rule reduces it.</summary>
    public const int OrdinaryAbsenceMinutes = 480;

    /// <summary>The attendance exception raised when a paid day's recomputed impacts differ from what payroll paid.</summary>
    public const string PaidAttendanceChanged = "PaidAttendanceChanged";

    /// <summary>
    /// Writes both derived representations for a settled daily record.
    /// </summary>
    /// <param name="employeeCompanyId">
    /// The owning employee's company, from the caller's already-resolved entity. Null is legitimate
    /// (an employee with no company, or a tenant with no company dimension yet) and passes through
    /// unchanged — this method must not invent a company it was not given.
    /// </param>
    /// <param name="absenceMinutes">What one absent day costs in minutes: 480 on an ordinary day, or
    /// the reduced KSA Art. 98 Ramadan baseline (360) on a Ramadan day.</param>
    public static async Task SyncAsync(
        ZayraDbContext db, Guid tenantId, Guid? employeeCompanyId,
        AttendanceDailyRecord daily, int absenceMinutes, CancellationToken ct)
    {
        await UpsertLegacyRecordAsync(db, tenantId, employeeCompanyId, daily, ct);
        await UpsertImpactsAsync(db, tenantId, daily, absenceMinutes, ct);
    }

    /// <summary>
    /// The legacy <c>attendance_records</c> row — what the executive dashboard sums.
    /// </summary>
    /// <remarks>
    /// WAVE 1 B1 (round 2) — <c>AttendanceRecord</c> is <c>ICompanyScopedOperational</c>, so a row
    /// whose <c>CompanyId</c> is null is invisible to every company-scoped user. The company is
    /// HANDED IN by the caller, taken from the <c>Employee</c> it already holds; there is
    /// deliberately no employee query in scope here to re-introduce that bug with.
    /// </remarks>
    public static async Task UpsertLegacyRecordAsync(
        ZayraDbContext db, Guid tenantId, Guid? employeeCompanyId,
        AttendanceDailyRecord daily, CancellationToken ct)
    {
        // Tenant-wide: (tenant, employee, work date) is UNIQUE on attendance_records, so the lookup must find the row
        // whatever the caller's company scope. The device webhook is anonymous, and an anonymous request has an EMPTY
        // company scope in every environment: the filtered read missed the row an earlier punch or run had created, the
        // insert below then broke the unique index (23505), and the ingest failed after its raw events were saved.
        var record = await ScopedBypass.NullableTenantWide(db.AttendanceRecords, tenantId, LegacyRecordJustification)
            .FirstOrDefaultAsync(x => x.EmployeeId == daily.EmployeeId && x.WorkDate == daily.WorkDate, ct);
        if (record is null)
        {
            record = new AttendanceRecord { TenantId = tenantId, EmployeeId = daily.EmployeeId, WorkDate = daily.WorkDate };
            db.AttendanceRecords.Add(record);
        }
        // ??= not =: an existing row's company is never reassigned here. EnforceCompanyScopeOnWritesAsync
        // throws company_reassignment_blocked on exactly that, and repairing a null is the only
        // transition this path is allowed to make.
        record.CompanyId ??= employeeCompanyId;
        record.TimeIn = daily.FirstInUtc is null ? null : TimeOnly.FromDateTime(daily.FirstInUtc.Value);
        record.TimeOut = daily.LastOutUtc is null ? null : TimeOnly.FromDateTime(daily.LastOutUtc.Value);
        record.OvertimeHours = Math.Round(daily.OvertimeMinutes / 60m, 2);
        record.Status = daily.Status;
        record.Notes = daily.MissingPunch ? "Missing punch" : "";
    }

    /// <summary>The impact status payroll stamps on what a run consumed (PayrollController; the void restores the prior one).</summary>
    public const string ProcessedStatus = "Processed";

    /// <summary>
    /// The payroll impacts for the day. Pending impacts are replaced wholesale rather than added to, so re-processing —
    /// and re-importing — is idempotent and never doubles what payroll owes.
    ///
    /// <para><b>A <c>Processed</c> impact is never deleted, replaced or duplicated.</b> It is what a payroll run consumed,
    /// and the run's witness row (<c>payroll_run_consumptions</c>) points at its id — the void/reopen restore finds it
    /// there. This used to replace every impact whatever its status, and since nothing writes
    /// <c>AttendanceLockPeriods</c> (so no period is ever locked), any HR process run over a paid period deleted the
    /// paid impacts and raised fresh pending ones that an off-cycle run would deduct again.</para>
    ///
    /// <para><b>A paid day</b> is one payroll covers (<see cref="IsCoveredByPayrollAsync"/>: a non-voided run at Processed
    /// or later that pays recurring salary and holds a non-voided slip for the employee in that month), or one with any
    /// <c>Processed</c> impact. Coverage is what decides it: a clean day, paid in full, has no impacts at all. On a paid
    /// day the processed impacts stay exactly as paid; no new deduction is raised for that day (an off-cycle run would
    /// take it); and when the recomputed deductions differ from what was paid, a <see cref="PaidAttendanceChanged"/>
    /// attendance exception lists the differences for HR to correct with a payroll adjustment. Overtime payable, which
    /// payroll never consumes (it is claimed through overtime detection), is still recomputed like any pending impact
    /// unless it was itself processed.</para>
    ///
    /// <para><b>A half-pair day</b> (<see cref="IsHalfPair"/>) raises no late-arrival or early-exit deduction: they are
    /// held until the pair is complete. On a paid day a held type is left out of the comparison with what payroll paid
    /// (it is undetermined, not different), so a held day raises no flag until its pair is completed.</para>
    ///
    /// <para>The exception's lifecycle (no schema change: its signature is a tag in <c>Details</c>): one open exception
    /// per day, re-worded as the difference changes; closed automatically when the day matches what was paid again;
    /// never re-raised for a difference HR already resolved (that would invite a second adjustment), but raised again
    /// when the difference changes; and, when the run that paid the day is voided or reopened, left open by the undo and
    /// closed by the next recompute of the day (the stale-attendance recompute the next Process asks for), once the day's
    /// deductions are pending again with what it now says.</para>
    /// </summary>
    /// <param name="absenceMinutes">What one absent day costs in minutes: 480 on an ordinary day, or the
    /// reduced KSA Art. 98 Ramadan baseline (360) on a Ramadan day. Deducting a full 480 for a 6-hour
    /// Ramadan day over-deducts by a third. See the call site for why the ordinary-day literal stays.</param>
    /// <param name="coverage">A range run's memo of employee-months already found paid (<see cref="PayrollCoverageMemo"/>).</param>
    public static async Task UpsertImpactsAsync(ZayraDbContext db, Guid tenantId, AttendanceDailyRecord daily, int absenceMinutes, CancellationToken ct,
        PayrollCoverageMemo? coverage = null)
    {
        var paid = await ReplacePendingAsync(db, tenantId, daily, ct);

        var recomputed = ImpliedImpacts(daily, absenceMinutes);
        var halfPair = IsHalfPair(daily);

        var dayIsPaid = paid.Count > 0 || (coverage is not null
            ? await coverage.IsCoveredAsync(db, tenantId, daily.EmployeeId, daily.WorkDate, ct)
            : await IsCoveredByPayrollAsync(db, tenantId, daily.EmployeeId, daily.WorkDate, ct));
        bool Frozen(string type) => paid.Any(p => p.ImpactType == type) || (dayIsPaid && type != AttendanceImpactTypes.OvertimePayable);
        foreach (var (type, minutes) in recomputed.Where(r => !Frozen(r.Type)))
            db.AttendancePayrollImpacts.Add(new AttendancePayrollImpact
            {
                TenantId = tenantId, EmployeeId = daily.EmployeeId, WorkDate = daily.WorkDate,
                ImpactType = type, Minutes = minutes, DailyRecordId = daily.Id,
            });
        var flags = await db.AttendanceExceptions.Where(x => x.TenantId == tenantId && x.EmployeeId == daily.EmployeeId
            && x.WorkDate == daily.WorkDate && x.ExceptionType == PaidAttendanceChanged).ToListAsync(ct);
        var open = flags.FirstOrDefault(x => !x.IsResolved);
        if (!dayIsPaid)
        {
            // The run that paid this day was voided or reopened, and the day has just been recomputed: its impacts are
            // pending again with what the day says now, so the difference the flag asked HR to adjust no longer exists.
            if (open is not null) AutoResolve(open, "the run that paid this day was voided or reopened; the day was recomputed and its deductions are pending again");
            return;
        }

        // What payroll paid vs what the day now says, for every type the paid day froze.
        // A held type is undetermined on a half-pair day, not different: it is compared again once the pair is complete.
        bool Held(string type) => halfPair && HeldOnHalfPairTypes.Contains(type);
        var differences = paid.Select(p => p.ImpactType).Concat(recomputed.Select(r => r.Type)).Distinct()
            .Where(type => Frozen(type) && !Held(type))
            .Select(type => (Type: type,
                Paid: paid.Where(p => p.ImpactType == type).Sum(p => p.Minutes),
                Now: recomputed.Where(r => r.Type == type).Sum(r => r.Minutes)))
            .Where(d => d.Paid != d.Now)
            .OrderBy(d => d.Type, StringComparer.Ordinal)
            .ToList();

        if (differences.Count == 0)
        {
            // An open flag about a held type stays open: the day does not match again, it is undetermined.
            var openSignature = open is null ? null : SignatureOf(open.Details);
            var aboutHeld = openSignature is not null && openSignature.Split(';').Any(d => Held(d.Split('=')[0]));
            if (open is not null && !aboutHeld) AutoResolve(open, "the day matches what payroll paid again");
            // Still open, but its "recomputed N min" is no longer true: with half a pair the deduction is undetermined.
            // The signature is kept, so the comparison resumes when the pair is completed.
            else if (open is not null)
                open.Details = $"{UndeterminedSentence} The paid impacts were left as paid and no new deduction was raised."
                    + $" {SignatureTag}{openSignature}]";
            return;
        }

        var signature = string.Join(";", differences.Select(d => $"{d.Type}={d.Paid}/{d.Now}"));
        var heldAndPaid = halfPair && paid.Any(p => HeldOnHalfPairTypes.Contains(p.ImpactType));
        var details = "Attendance for a paid day changed after payroll: "
            + string.Join("; ", differences.Select(d => $"{d.Type} paid {d.Paid} min, recomputed {d.Now} min"))
            + ". The paid impacts were left as paid and no new deduction was raised; correct the difference with a payroll adjustment."
            + (heldAndPaid ? " The day's late and early-exit deductions are undetermined until the punch is corrected." : string.Empty)
            + $" {SignatureTag}{signature}]";
        if (open is not null) { open.Details = details; return; }
        // HR resolved exactly this difference already (an automatic close does not count): do not ask for it twice.
        if (flags.Any(f => f.IsResolved && !f.Details.Contains(AutoResolvedTag, StringComparison.Ordinal) && SignatureOf(f.Details) == signature))
            return;
        db.AttendanceExceptions.Add(new AttendanceException
        {
            TenantId = tenantId, EmployeeId = daily.EmployeeId, DailyRecordId = daily.Id, WorkDate = daily.WorkDate,
            ExceptionType = PaidAttendanceChanged, Severity = "High", Details = details,
        });
    }

    /// <summary>
    /// The impacts a settled daily record implies: what <see cref="UpsertImpactsAsync"/> writes for an unpaid day, and
    /// what the payroll stale-attendance check (<see cref="AttendanceImpactReconciliation"/>) compares pending impacts
    /// with. Late and early-exit deductions are held on a half-pair day (<see cref="IsHalfPair"/>).
    /// </summary>
    public static List<(string Type, int Minutes)> ImpliedImpacts(AttendanceDailyRecord daily, int absenceMinutes)
    {
        var implied = new List<(string Type, int Minutes)>();
        var halfPair = IsHalfPair(daily);
        if (daily.LateMinutes > 0 && !halfPair) implied.Add((AttendanceImpactTypes.LateDeduction, daily.LateMinutes));
        if (daily.EarlyExitMinutes > 0 && !halfPair) implied.Add((AttendanceImpactTypes.EarlyExitDeduction, daily.EarlyExitMinutes));
        if (daily.Status == "Absent") implied.Add((AttendanceImpactTypes.AbsenceDeduction, absenceMinutes > 0 ? absenceMinutes : OrdinaryAbsenceMinutes));
        if (daily.OvertimeMinutes > 0) implied.Add((AttendanceImpactTypes.OvertimePayable, daily.OvertimeMinutes));
        return implied;
    }

    /// <summary>
    /// A day's first-in and last-out from its punches (in time order): the first In (or direction-less) punch, and the
    /// last Out — or, for a device that sends no direction, the last of two or more punches. The one pairing rule, used
    /// by the day processor and by the stale-attendance check.
    /// </summary>
    public static (DateTime? FirstInUtc, DateTime? LastOutUtc) PairOf(IReadOnlyList<(DateTime PunchUtc, string Direction)> ordered)
    {
        DateTime? firstIn = null, lastOut = null;
        foreach (var (at, direction) in ordered)
        {
            if (firstIn is null && direction is "In" or "Unknown") firstIn = at;
            if (direction == "Out") lastOut = at;
        }
        lastOut ??= ordered.Count > 1 ? ordered[^1].PunchUtc : null;
        return (firstIn, lastOut);
    }

    /// <summary>The deductions held on a half-pair day until the pair is complete.</summary>
    public static readonly string[] HeldOnHalfPairTypes = [AttendanceImpactTypes.LateDeduction, AttendanceImpactTypes.EarlyExitDeduction];

    /// <summary>
    /// A missing-punch day the employee did punch on: In-only, Out-only, or an Out at or before the In. Its late-arrival
    /// and early-exit deductions are HELD, not raised: with half a pair the other end of the day is unknown (an In-only
    /// day's "early exit" is a guess, an Out-only day's lateness is a guess, a reversed pair proves neither), and pay is
    /// never deducted on a guessed value. The computed minutes stay on the daily record for information, the High
    /// <c>MissingPunch</c> exception names them for HR, and completing the pair (the Out arriving, or an approved
    /// correction) reprocesses the day and raises them then, once, like any pending impact. A day with no punch at all is
    /// not half a pair (it is Absent, or leave / holiday / rest) and holds nothing. Overtime needs no hold: a half-pair
    /// day has no worked span, so the processor computes no overtime for it.
    /// </summary>
    public static bool IsHalfPair(AttendanceDailyRecord daily) => IsHalfPairCompiled(daily);

    /// <summary><see cref="IsHalfPair"/> as a query expression, so a database query applies the identical rule.</summary>
    public static readonly System.Linq.Expressions.Expression<Func<AttendanceDailyRecord, bool>> IsHalfPairExpression =
        d => d.MissingPunch && (d.FirstInUtc != null || d.LastOutUtc != null);

    /// <summary>
    /// The days whose late and early-exit minutes count: every day but a half-pair one, whose minutes are held
    /// (<see cref="IsHalfPair"/>). The ONE filter every lateness total applies — the payroll summary, reports, analytics
    /// and the AI advisory context — so they reconcile with what payroll deducts. A live "late today" counter is the one
    /// exception, deliberately: until the clock-out arrives every late arrival today is an In-only day, so filtering it
    /// would show nobody late until the end of the day; it counts the late clock-in, which is a fact, not a deduction.
    /// </summary>
    public static readonly System.Linq.Expressions.Expression<Func<AttendanceDailyRecord, bool>> LatenessCountsExpression =
        d => !(d.MissingPunch && (d.FirstInUtc != null || d.LastOutUtc != null));

    private static readonly Func<AttendanceDailyRecord, bool> IsHalfPairCompiled = IsHalfPairExpression.Compile();

    /// <summary>The missing-punch exception's text: what is missing and, when there are any, the minutes held.</summary>
    public static string DescribeMissingPunch(AttendanceDailyRecord daily)
    {
        var held = new List<string>();
        if (daily.LateMinutes > 0) held.Add($"{daily.LateMinutes} late minutes");
        if (daily.EarlyExitMinutes > 0) held.Add($"{daily.EarlyExitMinutes} early-exit minutes");
        return held.Count == 0
            ? "Missing in or out punch."
            : $"Missing in or out punch. Held, not deducted, until the punch is corrected: {string.Join(" and ", held)}.";
    }

    /// <summary>
    /// Removes the day's pending impacts and returns the ones payroll has consumed (<c>Processed</c>).
    ///
    /// <para><b>The status predicate is ON THE DELETE</b>, in one statement, as <c>LeaveService</c> does for leave
    /// deductions. Payroll takes no attendance day lock: its mark is a compare-and-swap on <c>status</c>. A read, then a
    /// delete by id, let this ordering through — the read saw the impact pending, payroll marked it Processed and
    /// committed, and the delete (it had waited on payroll's row lock under READ COMMITTED) then removed the consumed
    /// row; the day looked unpaid and its deduction was raised again as pending, to be taken twice. With the predicate
    /// on the statement, Postgres re-checks the row after the wait and leaves a consumed impact alone; the read AFTER the
    /// delete then sees it, and the day is treated as paid. No row lock is taken for this (a <c>FOR UPDATE</c> here
    /// would order against the void's locks).</para>
    /// </summary>
    private static async Task<List<AttendancePayrollImpact>> ReplacePendingAsync(ZayraDbContext db, Guid tenantId,
        AttendanceDailyRecord daily, CancellationToken ct)
    {
        var day = db.AttendancePayrollImpacts
            .Where(x => x.TenantId == tenantId && x.EmployeeId == daily.EmployeeId && x.WorkDate == daily.WorkDate);
        if (!db.Database.IsRelational())
        {
            // The in-memory test provider has no set-based delete and no concurrency to guard against.
            var existing = await day.ToListAsync(ct);
            db.AttendancePayrollImpacts.RemoveRange(existing.Where(x => x.Status != ProcessedStatus));
            return existing.Where(x => x.Status == ProcessedStatus).ToList();
        }
        await day.Where(x => x.Status != ProcessedStatus).ExecuteDeleteAsync(ct);
        // Tracked copies of rows that statement removed would otherwise be written back (or deleted again) on save.
        foreach (var entry in db.ChangeTracker.Entries<AttendancePayrollImpact>().ToList())
            if (entry.Entity.TenantId == tenantId && entry.Entity.EmployeeId == daily.EmployeeId && entry.Entity.WorkDate == daily.WorkDate
                && entry.State != EntityState.Added)
                entry.State = EntityState.Detached;
        return await day.AsNoTracking().Where(x => x.Status == ProcessedStatus).ToListAsync(ct);
    }

    private const string UndeterminedSentence =
        "Attendance for a paid day changed after payroll, and the day now has a missing punch: its late and early-exit " +
        "deductions are undetermined until the punch is corrected.";

    private const string SignatureTag = "[sig:";
    private const string AutoResolvedTag = "[auto-resolved:";

    /// <summary>The difference signature stored at the end of a <see cref="PaidAttendanceChanged"/> exception's details.</summary>
    private static string? SignatureOf(string details)
    {
        var at = details.LastIndexOf(SignatureTag, StringComparison.Ordinal);
        if (at < 0) return null;
        var end = details.IndexOf(']', at);
        return end < 0 ? null : details[(at + SignatureTag.Length)..end];
    }

    /// <summary>The exception's description for people: its details without the stored signature or close note.</summary>
    public static string DescribePaidAttendanceChange(string details)
    {
        var at = details.IndexOf(" " + SignatureTag, StringComparison.Ordinal);
        return at < 0 ? details : details[..at];
    }

    /// <summary>Closes an open attendance exception (a <see cref="PaidAttendanceChanged"/> flag, or a missing punch the day's
    /// punches now complete) the system no longer needs HR to act on.</summary>
    public static void AutoResolve(AttendanceException exception, string reason)
    {
        exception.IsResolved = true;
        exception.Details = $"{exception.Details} {AutoResolvedTag} {reason}]";
    }

    /// <summary>Payroll run statuses at which the run has paid, or committed to pay, its period (Draft and Voided have not).</summary>
    public static readonly string[] PaidRunStatuses = ["Processed", "PendingFinanceReview", "Approved", "Locked", "Paid"];

    private const string LegacyRecordJustification =
        "The legacy attendance row of the employee-day being settled is unique per (tenant, employee, work date) and must " +
        "be found to be updated whatever the caller's company scope; the employee is already authorised by the calling " +
        "path and nothing is returned to the caller.";

    private const string PayrollCoverageJustification =
        "Whether payroll already paid an employee's attendance day decides whether recomputing it may raise a new " +
        "deduction. The answer must not depend on the caller's company scope (the anonymous device webhook has none); " +
        "the employee is already authorised by the calling path and only a yes/no leaves this method.";

    /// <summary>
    /// A range run's memo of payroll coverage, so a month of days asks "has payroll paid this employee's month?" once
    /// instead of once per day. Only a YES is remembered. A month found paid stays paid for the rest of the run (it can
    /// only stop being paid through a void or reopen, and treating it as paid a little longer is the safe side: no
    /// deduction is raised, a difference is flagged). A NO is asked again for every day, because a payroll run that
    /// commits mid-range must protect the days processed after it — remembering a NO would let the range raise new
    /// deductions on days that run has just paid.
    /// </summary>
    public sealed class PayrollCoverageMemo
    {
        private readonly HashSet<(int EmployeeId, int Year, int Month)> _paid = new();

        public async Task<bool> IsCoveredAsync(ZayraDbContext db, Guid tenantId, int employeeId, DateOnly workDate, CancellationToken ct)
        {
            var key = (employeeId, workDate.Year, workDate.Month);
            if (_paid.Contains(key)) return true;
            var covered = await IsCoveredByPayrollAsync(db, tenantId, employeeId, workDate, ct);
            if (covered) _paid.Add(key);
            return covered;
        }
    }

    /// <summary>
    /// True when payroll has paid <paramref name="workDate"/> for the employee: a non-voided slip in a run for that
    /// month that pays recurring salary (only those consume attendance impacts) and is at Processed or later.
    /// </summary>
    public static async Task<bool> IsCoveredByPayrollAsync(ZayraDbContext db, Guid tenantId, int employeeId, DateOnly workDate, CancellationToken ct)
    {
        var runs = ScopedBypass.TenantWide(db.PayrollRuns, tenantId, PayrollCoverageJustification)
            .Where(r => r.Year == workDate.Year && r.Month == workDate.Month && r.IncludesRecurringPay && PaidRunStatuses.Contains(r.Status));
        return await ScopedBypass.TenantWide(db.PayrollSlips, tenantId, PayrollCoverageJustification)
            .Where(s => s.EmployeeId == employeeId && s.Status != "Voided")
            .AnyAsync(s => runs.Any(r => r.Id == s.RunId), ct);
    }
}
