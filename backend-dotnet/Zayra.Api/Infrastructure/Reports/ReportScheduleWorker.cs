using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Reports;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Operations;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Reports;

public static class ReportSchedulePolicy
{
    public static readonly IReadOnlySet<string> ReportKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "hr.headcount", "hr.new-joiners", "hr.exits", "hr.probation", "hr.status", "hr.nationality-mix",
        "attendance.daily", "attendance.monthly", "attendance.late-arrivals", "attendance.absences", "attendance.corrections",
        "leave.balance", "leave.usage", "leave.pending", "overtime.requests", "overtime.approved",
        "payroll.register", "payroll.summary", "payroll.slips", "recruitment.pipeline", "recruitment.time-to-hire",
        "compliance.visa-expiry", "compliance.passport-expiry", "compliance.contract-expiry", "compliance.document-compliance",
        "finance.loan-balance", "finance.advance-report", "finance.bonus-payout", "qiwa.readiness"
    };

    public static bool TryValidate(CreateScheduleRequest request, out string error)
    {
        if (!ReportKeys.Contains(request.ReportKey)) { error = "Unknown or unsupported report key."; return false; }
        if (string.IsNullOrWhiteSpace(request.ReportName)) { error = "Report name is required."; return false; }
        if (!new[] { "Daily", "Weekly", "Monthly", "Quarterly" }.Contains(request.Frequency, StringComparer.OrdinalIgnoreCase))
        { error = "Frequency must be Daily, Weekly, Monthly, or Quarterly."; return false; }
        // Email only, and it always was: the UI's SFTP and Portal options have been removed
        // rather than left to 400 after the user has filled the form in.
        if (!request.DeliveryMethod.Equals("Email", StringComparison.OrdinalIgnoreCase))
        { error = "Scheduled delivery supports Email only."; return false; }
        if (ReportExportFormats.NormalizeSchedulable(request.ExportFormat) is null)
        { error = $"Scheduled export format must be one of: {string.Join(", ", ReportExportFormats.Schedulable)}."; return false; }
        var recipients = ParseRecipients(request.Recipients);
        if (recipients.Count == 0) { error = "At least one valid email recipient is required."; return false; }
        error = string.Empty;
        return true;
    }

    public static IReadOnlyList<string> ParseRecipients(string? value) =>
        (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsValidEmail).Distinct(StringComparer.OrdinalIgnoreCase).Take(25).ToList();

    /// <summary>
    /// F09 — attempts per period before a schedule is dead-lettered: the first run plus two retries.
    /// After that it waits for its next regular period and the schedule shows it gave up.
    /// </summary>
    public const int MaxDeliveryAttempts = 3;

    /// <summary>Wait before retry N (1-based). Short: a relay blip should not cost a monthly pack.</summary>
    public static readonly TimeSpan[] RetryBackoff = [TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30)];

    /// <summary>Execution statuses beyond the historical Success/Failed. Strings, no migration.</summary>
    public const string StatusSuccess = "Success";
    public const string StatusFailed = "Failed";
    /// <summary>No SMTP relay for the workspace or the platform. Nothing was attempted.</summary>
    public const string StatusNotConfigured = "NotConfigured";
    /// <summary>Test delivery mode captured the report. Nobody received it.</summary>
    public const string StatusCaptured = "Captured";

    public const string NotConfiguredReason =
        "Email is not set up for this workspace (Settings → Email), and there is no platform relay, so the report was not sent.";

    public static DateTime NextRun(DateTime fromUtc, string frequency) => frequency.ToLowerInvariant() switch
    {
        "daily" => fromUtc.AddDays(1),
        "weekly" => fromUtc.AddDays(7),
        "monthly" => fromUtc.AddMonths(1),
        "quarterly" => fromUtc.AddMonths(3),
        _ => throw new InvalidOperationException("Unsupported report frequency.")
    };

    private static bool IsValidEmail(string value)
    {
        try { return new System.Net.Mail.MailAddress(value).Address.Equals(value, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
}

public sealed class ReportScheduleWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ReportScheduleWorker> _log;
    private readonly WorkerHeartbeatReporter? _heartbeat;

    public ReportScheduleWorker(IServiceScopeFactory scopeFactory, ILogger<ReportScheduleWorker> log,
        WorkerHeartbeatReporter? heartbeat = null)
    {
        _scopeFactory = scopeFactory;
        _log = log;
        _heartbeat = heartbeat;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_heartbeat is not null) await _heartbeat.StartedAsync(ProductionWorkerNames.Reports, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessOnceAsync(stoppingToken);
                if (_heartbeat is not null) await _heartbeat.SucceededAsync(ProductionWorkerNames.Reports, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Scheduled report worker iteration failed.");
                if (_heartbeat is not null)
                    try { await _heartbeat.FailedAsync(ProductionWorkerNames.Reports, ex, stoppingToken); }
                    catch (Exception heartbeatEx) { _log.LogWarning(heartbeatEx, "Could not persist report worker failure heartbeat."); }
            }
            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal async Task ProcessOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var dataScope = scope.ServiceProvider.GetRequiredService<IDataScopeService>();
        var email = scope.ServiceProvider.GetRequiredService<IEmailService>();
        var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();
        var now = DateTime.UtcNow;
        var due = await ScopedBypass.SystemWide(db.ReportSchedules, 20,
                "Scheduled report worker scans a bounded cross-tenant due queue.",
                x => x.IsActive && !x.IsDeleted && (x.NextRunAtUtc == null || x.NextRunAtUtc <= now),
                x => x.NextRunAtUtc ?? x.CreatedAtUtc)
            .AsNoTracking().ToListAsync(ct);

        foreach (var schedule in due)
        {
            if (!await TryClaimAsync(db, schedule, now, ct)) continue;
            var sw = Stopwatch.StartNew();
            var execution = new ReportExecutionLog
            {
                TenantId = schedule.TenantId,
                ScheduleId = schedule.Id,
                ReportKey = schedule.ReportKey,
                ReportName = schedule.ReportName,
                FiltersJson = schedule.FiltersJson,
                ExportFormat = schedule.ExportFormat,
                Status = "Failed",
                RunBy = schedule.CreatedBy,
                RunByName = "Scheduled report worker"
            };

            var deliveryStarted = false;
            try
            {
                var reportScope = await ResolveCurrentScopeAsync(db, schedule, ct);
                var filters = string.IsNullOrWhiteSpace(schedule.FiltersJson)
                    ? null
                    : JsonSerializer.Deserialize<ReportFilters>(schedule.FiltersJson);
                // The Nitaqat service is left to the controller's own default: this worker's
                // test harness builds a minimal service provider, and a GetRequiredService here
                // would make the worker unconstructable in it for no gain.
                var controller = new ReportsController(db, dataScope);
                var data = await controller.ExecuteReportDataAsync(
                    schedule.TenantId, new RunReportRequest(schedule.ReportKey, filters), reportScope, ct)
                    ?? throw new InvalidOperationException("The scheduled report key is no longer supported.");
                var json = JsonSerializer.SerializeToElement(data);
                var artifact = BuildArtifact(schedule, json);
                execution.RowCount = json.ValueKind == JsonValueKind.Array ? json.GetArrayLength() : 1;

                // Each recipient must still be somebody who could open this report by hand, over every
                // company it covers. Anyone who no longer is — left, demoted, narrowed, or never a user
                // of this organisation — is skipped, and the log says who and why.
                var audience = await ReportAudience.EvaluateRecipientsAsync(db, schedule.TenantId, schedule.ReportKey,
                    ReportSchedulePolicy.ParseRecipients(schedule.Recipients), reportScope.CompanyIds, ct);
                var refused = audience.Where(a => a.Refusal is not null).ToList();
                var allowed = audience.Where(a => a.Refusal is null).Select(a => a.Email).ToList();
                if (allowed.Count == 0)
                    throw new InvalidOperationException(
                        "No recipient may receive this report: " + ReportAudience.Describe(refused) + ".");

                // F09: each recipient's outcome is REPORTED, not assumed. "Success" means every
                // permitted recipient was accepted by a relay — nothing weaker.
                deliveryStarted = true;
                var outcome = await DeliverToRecipientsAsync(email, schedule, allowed, artifact, ct);
                execution.Status = outcome.Status;
                execution.ErrorMessage = refused.Count == 0
                    ? outcome.Message
                    : Truncate($"{outcome.Message ?? $"Accepted by the mail server for {allowed.Count} of {audience.Count} recipients."} "
                               + $"Not sent to: {ReportAudience.Describe(refused)}.");

                if (outcome.Status is ReportSchedulePolicy.StatusSuccess or ReportSchedulePolicy.StatusCaptured)
                {
                    await ClearFailureAsync(db, schedule, ct);
                }
                else
                {
                    // Not configured, or accepted for some recipients and refused for others. Neither
                    // is retried automatically: the first needs an admin, and the second would send a
                    // second copy to everyone who already has it. Both are surfaced on the schedule.
                    await SurfaceFailureAsync(db, notifications, schedule, outcome.Message ?? outcome.Status,
                        ownerProblem: false, retry: false, ct);
                }
            }
            catch (Exception ex)
            {
                execution.Status = ReportSchedulePolicy.StatusFailed;
                var reason = Truncate(ex.Message);
                execution.ErrorMessage = reason;
                // Type only: the message can name recipients (refusals, relay rejections). It is kept on the
                // execution row above, inside the tenant's own access controls.
                _log.LogError("Scheduled report {ScheduleId} failed for tenant {TenantId} ({ErrorType}).", schedule.Id, schedule.TenantId, ex.GetType().Name);
                // Only a relay failure is retried. Reaching here from the delivery step means nothing
                // went out for this run (see DeliverToRecipientsAsync), so a retry cannot duplicate.
                // Refusals before delivery — an owner who lost access, a retired report key, nobody
                // left who may receive it — are not transient, and waiting does not fix them.
                var ownerProblem = ex is UnauthorizedAccessException;
                await SurfaceFailureAsync(db, notifications, schedule, reason, ownerProblem,
                    retry: deliveryStarted && !ownerProblem, ct);
            }

            sw.Stop();
            execution.DurationMs = (int)Math.Min(int.MaxValue, sw.ElapsedMilliseconds);
            db.ReportExecutionLogs.Add(execution);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }
    }

    /// <summary>
    /// Sends the artifact to every recipient and reports what actually happened.
    ///
    /// <para>IDEMPOTENCY: if the relay fails before ANY recipient was accepted, the exception
    /// propagates and the whole run is retried — nobody has a copy, so nobody gets two. Once one
    /// recipient has been accepted, a later failure is recorded as a partial delivery and NOT
    /// retried, because a retry would send the report again to everyone who already has it.</para>
    /// </summary>
    private static async Task<(string Status, string? Message)> DeliverToRecipientsAsync(
        IEmailService email, ReportSchedule schedule, IReadOnlyList<string> recipients, EmailAttachment artifact,
        CancellationToken ct)
    {
        var accepted = 0;
        var captured = 0;
        var failures = new List<string>();

        foreach (var recipient in recipients)
        {
            EmailDeliveryResult result;
            try
            {
                result = await email.DeliverAsync(schedule.TenantId, recipient, recipient,
                    $"Scheduled report: {schedule.ReportName}",
                    $"<p>Your scheduled report <strong>{WebUtility.HtmlEncode(schedule.ReportName)}</strong> is attached.</p>",
                    [artifact], ct);
            }
            catch (EmailNotConfiguredException)
            {
                result = EmailDeliveryResult.NoRelay;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                if (accepted == 0 && captured == 0) throw;
                failures.Add($"{recipient}: {NotificationBodyPolicy.ScrubProviderError(ex.Message)}");
                continue;
            }

            switch (result.Status)
            {
                case EmailDeliveryStatus.NotConfigured when accepted == 0 && captured == 0:
                    return (ReportSchedulePolicy.StatusNotConfigured, ReportSchedulePolicy.NotConfiguredReason);
                case EmailDeliveryStatus.NotConfigured:
                    failures.Add($"{recipient}: email stopped being configured during the run");
                    break;
                case EmailDeliveryStatus.Captured:
                    captured++;
                    break;
                default:
                    accepted++;
                    break;
            }
        }

        if (failures.Count > 0)
            return (ReportSchedulePolicy.StatusFailed, Truncate(
                $"Accepted by the mail server for {accepted} of {recipients.Count} recipient(s); not sent to "
                + $"{string.Join("; ", failures)}. Not retried automatically, so nobody receives it twice."));

        if (captured > 0)
            return (ReportSchedulePolicy.StatusCaptured, accepted == 0
                ? "Test delivery mode captured this report. It was not sent to anyone."
                : $"Accepted by the mail server for {accepted} recipient(s); {captured} captured by test delivery rules and not sent.");

        return (ReportSchedulePolicy.StatusSuccess, null);
    }

    /// <summary>
    /// Records the failure on the schedule and tells a human. Wrapped because an alerting problem
    /// must never lose the execution log, which is the record of record.
    /// </summary>
    private async Task SurfaceFailureAsync(ZayraDbContext db, INotificationService notifications,
        ReportSchedule schedule, string reason, bool ownerProblem, bool retry, CancellationToken ct)
    {
        try { await RecordFailureAsync(db, notifications, schedule, reason, ownerProblem, retry, ct); }
        catch (Exception alertEx)
        {
            _log.LogWarning(alertEx, "Could not surface the failure of scheduled report {ScheduleId}.", schedule.Id);
        }
    }

    /// <summary>A delivered run clears the failure state, so the UI badge disappears by itself.</summary>
    private static async Task ClearFailureAsync(ZayraDbContext db, ReportSchedule schedule, CancellationToken ct)
    {
        if (schedule.ConsecutiveFailureCount == 0 && schedule.OwnerInvalidatedAtUtc is null) return;
        var tracked = await db.ReportSchedules
            .FirstOrDefaultAsync(x => x.Id == schedule.Id && x.TenantId == schedule.TenantId, ct);
        if (tracked is null) return;
        tracked.ConsecutiveFailureCount = 0;
        tracked.LastFailureReason = string.Empty;
        tracked.OwnerInvalidatedAtUtc = null;
    }

    /// <summary>
    /// Re-derives, on every run, what the schedule's OWNER may see today — not what they could see when
    /// they created it — and fails closed (an <see cref="UnauthorizedAccessException"/>, which marks the
    /// schedule as needing a new owner) the moment the report is no longer theirs to read.
    ///
    /// <para>This worker has no HTTP user, so the database's company filter is open for it: the scope
    /// returned here is the ONLY restriction the report runs under, and it must carry the companies as
    /// well as the employees.</para>
    /// </summary>
    private static async Task<ReportDataScope> ResolveCurrentScopeAsync(
        ZayraDbContext db, ReportSchedule schedule, CancellationToken ct)
    {
        if (schedule.CreatedBy is not Guid creatorId)
            throw new UnauthorizedAccessException("Schedule has no accountable creator.");
        var user = await ReportAudience.ActiveUsers(db, schedule.TenantId)
            .FirstOrDefaultAsync(x => x.Id == creatorId, ct)
            ?? throw new UnauthorizedAccessException("Schedule creator is inactive or missing.");
        var access = ReportAudience.AccessOf(user, await ReportAudience.ActiveCompanyIdsAsync(db, schedule.TenantId, ct));
        if (!access.Has("reports.schedule"))
            throw new UnauthorizedAccessException("Schedule creator no longer has reports.schedule permission.");

        // Not an owner problem: the report itself is gone, and no new owner would change that.
        if (!ReportAccessPolicy.IsKnown(schedule.ReportKey))
            throw new InvalidOperationException("The scheduled report key is no longer supported.");
        // The same data rule as the interactive endpoints. A demoted owner's schedule stops here.
        if (!ReportAccessPolicy.CanAccess(schedule.ReportKey, access.Has, access.InRole))
            throw new UnauthorizedAccessException(
                ReportAccessPolicy.ThirdPartyDenialMessage("The schedule's owner", schedule.ReportKey));
        // Interactively, a team-scoped user gets their team's rows. A delivery has no team to cut to and
        // was served organisation-wide, i.e. more than the owner could open by hand; refuse it instead.
        if (!ReportAccessPolicy.GrantsOrganisationScope(access.Has))
            throw new UnauthorizedAccessException(
                "The schedule's owner can only see their own team's records, but a scheduled report is " +
                "delivered organisation-wide, so it no longer runs.");
        if (access.SeesNothing)
            throw new UnauthorizedAccessException("Schedule creator has no active legal-entity scope.");
        if (ReportAccessPolicy.ScopeDenial(schedule.ReportKey, organisationLevel: true, access.GroupLevel) is { } scopeDenial)
            throw new UnauthorizedAccessException(scopeDenial);

        // Identity-document numbers are never emailed, whoever the owner is: an attachment leaves the
        // product, and recipients may not hold employees.sensitive even when the owner does.
        const bool canSeeSensitive = false;
        if (access.GroupLevel) return new ReportDataScope(null, null, canSeeSensitive);

        // Employee has a legacy nullable TenantId and cannot use ScopedBypass.TenantWide's
        // non-nullable type guard. System context already bypasses filters; the explicit
        // non-null tenant predicate below is the surviving tenant boundary.
        var companyIds = access.CompanyIds.ToList();
        var employeeIds = await db.Employees.AsNoTracking()
            .Where(x => x.TenantId == schedule.TenantId && !x.IsDeleted
                        && x.CompanyId != null && companyIds.Contains(x.CompanyId.Value))
            .Select(x => x.Id).ToListAsync(ct);
        return new ReportDataScope(employeeIds, companyIds, canSeeSensitive);
    }

    private static string Truncate(string message) => message.Length <= 1000 ? message : message[..1000];

    private static async Task<bool> TryClaimAsync(ZayraDbContext db, ReportSchedule item, DateTime now, CancellationToken ct)
    {
        var next = ReportSchedulePolicy.NextRun(now, item.Frequency);
        if (db.Database.IsRelational())
        {
            var observed = item.NextRunAtUtc;
            var query = ScopedBypass.TenantWide(db.ReportSchedules, item.TenantId,
                    "Scheduled report worker atomically claims one tenant-owned schedule.")
                .Where(x => x.Id == item.Id && x.IsActive && !x.IsDeleted);
            query = observed is null ? query.Where(x => x.NextRunAtUtc == null) : query.Where(x => x.NextRunAtUtc == observed);
            return await query.ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.LastRunAtUtc, now)
                .SetProperty(x => x.NextRunAtUtc, next)
                .SetProperty(x => x.UpdatedAtUtc, now), ct) == 1;
        }

        var tracked = await ScopedBypass.TenantWide(db.ReportSchedules, item.TenantId,
                "Scheduled report unit-test claim remains pinned to its owning tenant.").FirstOrDefaultAsync(
            x => x.Id == item.Id && x.IsActive && !x.IsDeleted && x.NextRunAtUtc == item.NextRunAtUtc, ct);
        if (tracked is null) return false;
        tracked.LastRunAtUtc = now;
        tracked.NextRunAtUtc = next;
        tracked.UpdatedAtUtc = now;
        await db.SaveChangesAsync(ct);
        db.Entry(tracked).State = EntityState.Detached;
        return true;
    }

    /// <summary>
    /// Builds the attachment. Rewritten: this method used to produce CSV bytes for every
    /// format, naming the file <c>.csv</c> and labelling it <c>application/vnd.ms-excel</c>
    /// when the user had chosen Excel — and delivering a <c>.csv</c> when they had chosen PDF.
    /// It now shares <see cref="ReportTabulator"/> and <see cref="ReportWorkbookWriter"/> with
    /// the synchronous export endpoint, so a scheduled Excel and a downloaded Excel are the
    /// same bytes, and "Excel" means a workbook.
    /// </summary>
    internal static EmailAttachment BuildArtifact(ReportSchedule schedule, JsonElement data)
    {
        var safeName = string.Concat(schedule.ReportName.Select(c => char.IsLetterOrDigit(c) ? c : '_')).Trim('_');
        if (safeName.Length == 0) safeName = "report";

        // Unknown values fall back to CSV rather than throwing: a schedule created before the
        // vocabulary was tightened must still deliver something a human can open.
        var format = ReportExportFormats.NormalizeSchedulable(schedule.ExportFormat) ?? ReportExportFormats.Csv;

        if (format == ReportExportFormats.Json)
            return new EmailAttachment($"{safeName}.json",
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true })),
                "application/json");

        var tables = ReportTabulator.Tabulate(data, ReportTabulator.Humanise(schedule.ReportName));
        var bytes = format == ReportExportFormats.Xlsx
            ? ReportWorkbookWriter.ToXlsx(tables)
            : ReportTabulator.ToCsv(tables);

        return new EmailAttachment(
            $"{safeName}.{ReportExportFormats.ExtensionFor(format)}",
            bytes,
            ReportExportFormats.ContentTypeFor(format));
    }

    /// <summary>
    /// Records a failed run against the schedule itself and, the first time a run fails,
    /// tells a human.
    ///
    /// <para>The specific case the review called out: <see cref="ResolveCurrentScopeAsync"/>
    /// throws when the creator is deactivated or has lost <c>reports.schedule</c>. That is not
    /// transient — the monthly pack will fail every month until somebody takes it over — and
    /// nothing surfaced it. The only trace was a <c>ReportExecutionLogs</c> row.</para>
    ///
    /// <para>Notification is sent once, on the transition into failure, not on every period:
    /// a monthly report that has been broken for a year should not produce twelve identical
    /// alerts, and an alert channel that repeats is an alert channel people filter.</para>
    ///
    /// <para>F09 RETRY AND DEAD LETTER. The claim has already moved NextRunAtUtc a whole period
    /// ahead, so a failed run used to wait a month for its next chance — one dropped connection cost
    /// the customer their monthly pack. A retryable failure now pulls the next run in by
    /// <see cref="ReportSchedulePolicy.RetryBackoff"/> until <see cref="ReportSchedulePolicy.MaxDeliveryAttempts"/>
    /// consecutive failures, then the schedule is dead-lettered: it says it gave up and returns to
    /// its regular cadence. A successful retry resumes the cadence from the retry time, so a period
    /// can shift by at most the sum of the backoffs (35 minutes).</para>
    /// </summary>
    private static async Task RecordFailureAsync(
        ZayraDbContext db, INotificationService notifications, ReportSchedule schedule,
        string reason, bool ownerProblem, bool retry, CancellationToken ct)
    {
        reason = Truncate(reason);
        var wasHealthy = schedule.ConsecutiveFailureCount == 0;

        var tracked = await db.ReportSchedules
            .FirstOrDefaultAsync(x => x.Id == schedule.Id && x.TenantId == schedule.TenantId, ct);
        if (tracked is null) return;

        tracked.ConsecutiveFailureCount++;
        tracked.LastFailureAtUtc = DateTime.UtcNow;
        tracked.LastFailureReason = reason;
        if (ownerProblem) tracked.OwnerInvalidatedAtUtc ??= DateTime.UtcNow;

        if (retry)
        {
            var attempts = tracked.ConsecutiveFailureCount;
            if (attempts < ReportSchedulePolicy.MaxDeliveryAttempts)
            {
                var wait = ReportSchedulePolicy.RetryBackoff[Math.Min(attempts - 1, ReportSchedulePolicy.RetryBackoff.Length - 1)];
                var retryAt = DateTime.UtcNow.Add(wait);
                if (tracked.NextRunAtUtc is null || retryAt < tracked.NextRunAtUtc) tracked.NextRunAtUtc = retryAt;
                tracked.LastFailureReason = Truncate(
                    $"{reason} Retrying at {retryAt:HH:mm} UTC (attempt {attempts + 1} of {ReportSchedulePolicy.MaxDeliveryAttempts}).");
            }
            else
            {
                tracked.LastFailureReason = Truncate(
                    $"Gave up after {attempts} attempts. Last error: {reason} The next attempt is the next scheduled run.");
            }
            reason = tracked.LastFailureReason;
        }

        if (!wasHealthy) return;

        // Who to tell. The creator is the wrong answer when the creator is the problem, so the
        // notification goes to everyone in the tenant who could actually fix it — the holders of
        // reports.schedule — and to the creator as well when they are still active.
        var candidates = await ScopedBypass.TenantWide(db.Users, schedule.TenantId,
                "A failing scheduled report must reach somebody who can repair it inside its own tenant.")
            .AsNoTracking()
            .Include(x => x.UserRoles).ThenInclude(x => x.Role).ThenInclude(x => x!.RolePermissions).ThenInclude(x => x.Permission)
            .Include(x => x.PermissionOverrides)
            .Where(x => x.IsActive && !x.IsDeleted)
            .ToListAsync(ct);

        var recipients = candidates
            .Where(u => AuthService.GetPermissions(u).Contains("reports.schedule", StringComparer.OrdinalIgnoreCase))
            .Select(u => u.Id)
            .Distinct()
            .Take(20)
            .ToList();

        var title = ownerProblem
            ? $"Scheduled report stopped: {schedule.ReportName}"
            : $"Scheduled report failed: {schedule.ReportName}";
        var message = ownerProblem
            ? $"\"{schedule.ReportName}\" can no longer run: {reason} It will keep failing until somebody with reports.schedule recreates it; an administrator can pause or delete this one."
            : $"\"{schedule.ReportName}\" did not deliver: {reason}";

        foreach (var userId in recipients)
            await notifications.NotifyAsync(schedule.TenantId, userId, title, message,
                nameof(ReportSchedule), schedule.Id.ToString(), ct);

        // Nobody in the tenant holds reports.schedule any more — which is itself the finding.
        // The row on the schedule is then the only surface, and the UI renders it.
        if (recipients.Count == 0)
            await notifications.NotifyAsync(schedule.TenantId, null, title,
                message + " No user in this tenant currently holds reports.schedule.",
                nameof(ReportSchedule), schedule.Id.ToString(), ct);
    }
}
