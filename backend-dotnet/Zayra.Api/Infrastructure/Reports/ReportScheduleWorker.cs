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
        if (!request.DeliveryMethod.Equals("Email", StringComparison.OrdinalIgnoreCase))
        { error = "Scheduled delivery currently supports Email only."; return false; }
        if (!new[] { "JSON", "CSV", "Excel" }.Contains(request.ExportFormat, StringComparer.OrdinalIgnoreCase))
        { error = "Scheduled export format must be JSON, CSV, or Excel."; return false; }
        var recipients = ParseRecipients(request.Recipients);
        if (recipients.Count == 0) { error = "At least one valid email recipient is required."; return false; }
        error = string.Empty;
        return true;
    }

    public static IReadOnlyList<string> ParseRecipients(string? value) =>
        (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsValidEmail).Distinct(StringComparer.OrdinalIgnoreCase).Take(25).ToList();

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

            try
            {
                var employeeIds = await ResolveCurrentScopeAsync(db, schedule, ct);
                var filters = string.IsNullOrWhiteSpace(schedule.FiltersJson)
                    ? null
                    : JsonSerializer.Deserialize<ReportFilters>(schedule.FiltersJson);
                var controller = new ReportsController(db, dataScope);
                var data = await controller.ExecuteReportDataAsync(
                    schedule.TenantId, new RunReportRequest(schedule.ReportKey, filters), employeeIds, ct)
                    ?? throw new InvalidOperationException("The scheduled report key is no longer supported.");
                var json = JsonSerializer.SerializeToElement(data);
                var artifact = BuildArtifact(schedule, json);

                if (!await email.IsConfiguredAsync(schedule.TenantId, ct))
                    throw new InvalidOperationException("Tenant SMTP is not configured; scheduled report delivery failed closed.");
                foreach (var recipient in ReportSchedulePolicy.ParseRecipients(schedule.Recipients))
                {
                    await email.SendAsync(schedule.TenantId, recipient, recipient,
                        $"Scheduled report: {schedule.ReportName}",
                        $"<p>Your scheduled report <strong>{WebUtility.HtmlEncode(schedule.ReportName)}</strong> is attached.</p>",
                        new[] { artifact }, ct);
                }

                execution.Status = "Success";
                execution.RowCount = json.ValueKind == JsonValueKind.Array ? json.GetArrayLength() : 1;
            }
            catch (Exception ex)
            {
                execution.ErrorMessage = ex.Message.Length <= 1000 ? ex.Message : ex.Message[..1000];
                _log.LogError(ex, "Scheduled report {ScheduleId} failed for tenant {TenantId}.", schedule.Id, schedule.TenantId);
            }

            sw.Stop();
            execution.DurationMs = (int)Math.Min(int.MaxValue, sw.ElapsedMilliseconds);
            db.ReportExecutionLogs.Add(execution);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }
    }

    private static async Task<IReadOnlyCollection<int>?> ResolveCurrentScopeAsync(
        ZayraDbContext db, ReportSchedule schedule, CancellationToken ct)
    {
        if (schedule.CreatedBy is not Guid creatorId)
            throw new UnauthorizedAccessException("Schedule has no accountable creator.");
        var user = await ScopedBypass.TenantWide(db.Users, schedule.TenantId,
                "Scheduled report revalidates its creator inside the owning tenant.").AsNoTracking()
            .Include(x => x.UserRoles).ThenInclude(x => x.Role).ThenInclude(x => x!.RolePermissions).ThenInclude(x => x.Permission)
            .Include(x => x.PermissionOverrides)
            .Include(x => x.EmployeeUserAccounts)
            .Include(x => x.EntityAccesses)
            .FirstOrDefaultAsync(x => x.Id == creatorId && x.TenantId == schedule.TenantId && x.IsActive && !x.IsDeleted, ct)
            ?? throw new UnauthorizedAccessException("Schedule creator is inactive or missing.");
        if (!AuthService.GetPermissions(user).Contains("reports.schedule", StringComparer.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Schedule creator no longer has reports.schedule permission.");

        var activeCompanyIds = await ScopedBypass.TenantWide(db.Companies, schedule.TenantId,
                "Scheduled report resolves active legal entities inside its tenant.").AsNoTracking()
            .Where(x => x.IsActive && !x.IsDeleted)
            .Select(x => x.Id).ToListAsync(ct);
        var grants = user.EntityAccesses.Where(x => x.IsActive)
            .Select(x => new EntityAccessGrant(x.CompanyId, x.Role, x.GrantMode)).ToList();
        var descriptor = EntityScopeClaims.Resolve(user.IsGroupScope, grants, activeCompanyIds);
        if (descriptor.Mode == EntityScopeModes.Group) return null;
        if (descriptor.Mode != EntityScopeModes.Companies || descriptor.CompanyIds.Count == 0)
            throw new UnauthorizedAccessException("Schedule creator has no active legal-entity scope.");
        // Employee has a legacy nullable TenantId and cannot use ScopedBypass.TenantWide's
        // non-nullable type guard. System context already bypasses filters; the explicit
        // non-null tenant predicate below is the surviving tenant boundary.
        return await db.Employees.AsNoTracking()
            .Where(x => x.TenantId == schedule.TenantId && !x.IsDeleted
                        && x.CompanyId != null && descriptor.CompanyIds.Contains(x.CompanyId.Value))
            .Select(x => x.Id).ToListAsync(ct);
    }

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

    private static EmailAttachment BuildArtifact(ReportSchedule schedule, JsonElement data)
    {
        var safeName = string.Concat(schedule.ReportName.Select(c => char.IsLetterOrDigit(c) ? c : '_')).Trim('_');
        if (safeName.Length == 0) safeName = "report";
        if (schedule.ExportFormat.Equals("JSON", StringComparison.OrdinalIgnoreCase))
            return new EmailAttachment($"{safeName}.json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true })), "application/json");

        var (headers, rows) = Flatten(data);
        var csv = new StringBuilder();
        csv.AppendLine(string.Join(',', headers.Select(Csv)));
        foreach (var row in rows) csv.AppendLine(string.Join(',', headers.Select(h => Csv(row.GetValueOrDefault(h, string.Empty)))));
        var bytes = new UTF8Encoding(true).GetBytes(csv.ToString());
        return schedule.ExportFormat.Equals("Excel", StringComparison.OrdinalIgnoreCase)
            ? new EmailAttachment($"{safeName}.csv", bytes, "application/vnd.ms-excel")
            : new EmailAttachment($"{safeName}.csv", bytes, "text/csv");
    }

    private static (List<string> Headers, List<Dictionary<string, string>> Rows) Flatten(JsonElement data)
    {
        var items = data.ValueKind == JsonValueKind.Array ? data.EnumerateArray().ToList() : new List<JsonElement> { data };
        var headers = items.Where(x => x.ValueKind == JsonValueKind.Object)
            .SelectMany(x => x.EnumerateObject().Select(p => p.Name)).Distinct().ToList();
        var rows = items.Select(x => x.ValueKind == JsonValueKind.Object
                ? x.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ToString())
                : new Dictionary<string, string> { ["Value"] = x.ToString() })
            .ToList();
        if (headers.Count == 0 && rows.Count > 0) headers.Add("Value");
        return (headers, rows);
    }

    private static string Csv(string value)
    {
        var safe = value.Length > 0 && "=+-@".Contains(value[0]) ? "'" + value : value;
        return $"\"{safe.Replace("\"", "\"\"")}\"";
    }
}
