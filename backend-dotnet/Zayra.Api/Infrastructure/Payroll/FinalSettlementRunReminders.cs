using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Operations;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// G-006 (owner decision): the Payroll Manager owns the off-cycle run that pays an approved final settlement. An approved
/// settlement used to be a dead end: nobody was told to create the run, and the date the law requires payment by (KSA Art. 88: one
/// week after the contract ends, two weeks where the worker ended it; <see cref="EmployeeFinalSettlement.SettlementDueDate"/>) raised
/// no alert. This is the single place that decides who is told, what they are told, and whether a run already exists.
/// None of it touches an amount or who approves.
/// </summary>
public static class FinalSettlementRunDuty
{
    public const string PayrollManagerRole = "Payroll Manager";
    public const string AssignedEvent = "FINAL_SETTLEMENT_RUN_ASSIGNED";
    public const string DueSoonEvent = "FINAL_SETTLEMENT_RUN_DUE_SOON";
    public const string DueTodayEvent = "FINAL_SETTLEMENT_RUN_DUE_TODAY";
    /// <summary>The reminder window before the due date, in days.</summary>
    public const int SoonDays = 2;

    /// <summary>
    /// The live off-cycle settlement run that holds this employee (an Include selection in a non-voided OffCycle run that settles
    /// final settlements, in the settlement's company), or null. A voided run does not count: nobody will pay from it.
    /// </summary>
    public static async Task<PayrollRun?> FindRunAsync(ZayraDbContext db, EmployeeFinalSettlement s, CancellationToken ct)
    {
        var runs = ScopedBypass.TenantWide(db.PayrollRuns, s.TenantId,
            "G-006: the off-cycle run holding a leaver is looked up tenant-wide by id for a settlement already authorised by company; the tenant is re-applied.");
        var selections = ScopedBypass.TenantWide(db.PayrollRunEmployeeSelections, s.TenantId,
            "G-006: the Include rows of a settlement run, read tenant-wide for the settlement's employee; the tenant is re-applied.");
        return await (from sel in selections.AsNoTracking()
                      join run in runs.AsNoTracking() on sel.PayrollRunId equals run.Id
                      where sel.EmployeeId == s.EmployeeId && sel.Mode == PayrollRunSelectionModes.Include
                            && run.SettlesFinalSettlements && run.RunType == PayrollRunTypes.OffCycle && run.Status != "Voided"
                            && run.CompanyId == s.CompanyId
                      orderby run.CreatedAtUtc descending
                      select run).FirstOrDefaultAsync(ct);
    }

    /// <summary>True when an approved settlement has neither a run it was disbursed through nor a live run holding the employee.</summary>
    public static async Task<bool> NeedsRunAsync(ZayraDbContext db, EmployeeFinalSettlement s, CancellationToken ct) =>
        s.Status == FinalSettlementStatuses.Approved && s.PayrollRunId is null && await FindRunAsync(db, s, ct) is null;

    /// <summary>The Payroll Managers whose company access covers the settlement's company (a group-scope one covers every company).</summary>
    public static Task<IReadOnlyList<Guid>> RecipientsAsync(ZayraDbContext db, EmployeeFinalSettlement s, CancellationToken ct) =>
        NotificationAudience.QueueHoldersAsync(db, s.TenantId, PayrollManagerRole, s.CompanyId, ct);

    /// <summary>The notice text (English / Arabic in one line, as the other bell notices a role queue reads). Pure.</summary>
    public static (string Title, string Message) Compose(string eventCode, EmployeeFinalSettlement s)
    {
        var due = s.SettlementDueDate.ToString("yyyy-MM-dd");
        var net = $"{s.NetPayable:N2} {s.Currency}";
        return eventCode switch
        {
            DueTodayEvent => (
                $"Final settlement run for {s.EmployeeName} is due today — الدورة خارج الدورة لـ {s.EmployeeName} مستحقة اليوم",
                $"The final settlement of {s.EmployeeName} ({net}) must be paid today ({due}) and no off-cycle run holds it yet. Create it now. / "
                + $"يجب صرف التسوية النهائية لـ {s.EmployeeName} ({net}) اليوم ({due}) ولا توجد دورة خارج الدورة تشملها بعد. أنشئها الآن."),
            DueSoonEvent => (
                $"Final settlement run for {s.EmployeeName}: due by {due} — الدورة خارج الدورة لـ {s.EmployeeName} تستحق بحلول {due}",
                $"The final settlement of {s.EmployeeName} ({net}) must be paid by {due} and no off-cycle run holds it yet. Create the off-cycle run. / "
                + $"يجب صرف التسوية النهائية لـ {s.EmployeeName} ({net}) بحلول {due} ولا توجد دورة خارج الدورة تشملها بعد. أنشئ الدورة."),
            _ => (
                $"Create the off-cycle run for {s.EmployeeName} — due by {due} — أنشئ الدورة خارج الدورة لـ {s.EmployeeName} بحلول {due}",
                $"The final settlement of {s.EmployeeName} ({net}) is approved. Create the off-cycle run that pays it, due by {due} (the legal payment date). "
                + $"Open Payroll, Final Settlement. / اعتُمدت التسوية النهائية لـ {s.EmployeeName} ({net}). أنشئ دورة الرواتب خارج الدورة التي تصرفها، وتستحق بحلول {due} (الموعد النظامي للصرف). افتح الرواتب ثم التسوية النهائية."),
        };
    }

    /// <summary>Tells every Payroll Manager in the settlement's scope. Returns how many were addressed; nobody named means nothing is sent.</summary>
    public static async Task<int> NotifyAsync(ZayraDbContext db, INotificationService notifications, EmployeeFinalSettlement s, string eventCode, CancellationToken ct)
    {
        var recipients = await RecipientsAsync(db, s, ct);
        if (recipients.Count == 0) return 0;
        var (title, message) = Compose(eventCode, s);
        foreach (var userId in recipients)
            await notifications.EnqueueAsync(new NotificationRequest
            {
                TenantId = s.TenantId, UserId = userId, EventCode = eventCode, EntityName = nameof(EmployeeFinalSettlement),
                EntityId = s.Id.ToString(), Title = title, Message = message,
            }, ct);
        return recipients.Count;
    }
}

public sealed class FinalSettlementRunReminderOptions
{
    public const string Section = "FinalSettlementRunReminders";
    public bool Enabled { get; set; } = true;
    /// <summary>
    /// How often the sweep runs. Six hours guarantees at least one run every calendar day even across restarts; the notification
    /// pipeline's business-identity dedupe key (event, entity, recipient, content) makes the repeats harmless, so each reminder is
    /// delivered once however many times the sweep looks.
    /// </summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(6);
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMinutes(4);
    public int MaxTenantsPerRun { get; set; } = 500;
}

/// <summary>
/// The reminder for the run nobody has created: two days before the settlement's legal due date, and on it, while no off-cycle run
/// holds the employee. Reads only; sends addressed notices (never a broadcast); a settlement already tied to a run, not approved,
/// or past its date is left alone (the list's overdue badge covers a missed date).
/// </summary>
public sealed class FinalSettlementRunReminderService(ZayraDbContext db, ITenantClock tenantClock, INotificationService notifications,
    ILogger<FinalSettlementRunReminderService> log)
{
    public async Task<IReadOnlyList<Guid>> TenantsAsync(int max, CancellationToken ct) =>
        await ScopedBypass.SystemWideDistinct(db.EmployeeFinalSettlements, Math.Clamp(max, 1, 1000),
            "G-006 reminder sweep: which tenants hold an approved final settlement with no run yet; background sweep with no request principal, bounded.",
            s => s.Status == FinalSettlementStatuses.Approved && s.PayrollRunId == null,
            s => s.TenantId).ToListAsync(ct);

    /// <summary>Returns how many Payroll Managers were addressed across the tenant's settlements.</summary>
    public async Task<int> RunTenantAsync(Guid tenantId, CancellationToken ct)
    {
        var today = await tenantClock.TodayAsync(tenantId, ct);
        var soonEnd = today.AddDays(FinalSettlementRunDuty.SoonDays);
        var waiting = await ScopedBypass.TenantWide(db.EmployeeFinalSettlements, tenantId,
                "G-006 reminder sweep: approved settlements of one tenant waiting for a run; background sweep, the tenant is re-applied.")
            .AsNoTracking()
            .Where(s => s.Status == FinalSettlementStatuses.Approved && s.PayrollRunId == null
                        && s.SettlementDueDate >= today && s.SettlementDueDate <= soonEnd)
            .OrderBy(s => s.SettlementDueDate).Take(500).ToListAsync(ct);
        var addressed = 0;
        foreach (var settlement in waiting)
        {
            try
            {
                if (await FinalSettlementRunDuty.FindRunAsync(db, settlement, ct) is not null) continue;
                var stage = settlement.SettlementDueDate == today ? FinalSettlementRunDuty.DueTodayEvent : FinalSettlementRunDuty.DueSoonEvent;
                addressed += await FinalSettlementRunDuty.NotifyAsync(db, notifications, settlement, stage, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning("Final settlement run reminder: settlement {SettlementId} could not be checked ({Error}).", settlement.Id, ex.GetType().Name);
            }
        }
        return addressed;
    }
}

/// <summary>
/// Runs <see cref="FinalSettlementRunReminderService"/> for every tenant with an approved settlement waiting for a run, on one
/// instance at a time (<see cref="SingletonWorkerLease"/>), with a heartbeat under <see cref="WorkerName"/>. Not in
/// ProductionWorkerNames.All: a missed reminder run must never fail readiness.
/// </summary>
public sealed class FinalSettlementRunReminderWorker(IServiceScopeFactory scopes, ILogger<FinalSettlementRunReminderWorker> log,
    IOptions<FinalSettlementRunReminderOptions> options, WorkerHeartbeatReporter? heartbeat = null) : BackgroundService
{
    public const string WorkerName = "final-settlement-run-reminders";
    private readonly SweepOutcomeReporter _outcomes = new(WorkerName, log, heartbeat);

    public SweepOutcome LastSweepOutcome { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        if (heartbeat is not null) await heartbeat.StartedAsync(WorkerName, stoppingToken);
        try { await Task.Delay(options.Value.InitialDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DrainOnceAsync(stoppingToken);
                await _outcomes.ReportAsync(LastSweepOutcome, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                log.LogError(ex, "Final settlement run reminder iteration failed.");
                if (heartbeat is not null)
                    try { await heartbeat.FailedAsync(WorkerName, ex, stoppingToken); }
                    catch (Exception heartbeatEx) { log.LogWarning(heartbeatEx, "Could not persist the final settlement run reminder failure heartbeat."); }
            }

            try { await Task.Delay(options.Value.Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One run over every tenant with a waiting settlement. Exposed for tests and operator one-shots.</summary>
    public async Task<IReadOnlyDictionary<Guid, int>> DrainOnceAsync(CancellationToken ct)
    {
        LastSweepOutcome = SweepOutcome.Completed;
        await using var leaseScope = scopes.CreateAsyncScope();
        var leaseDb = leaseScope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        await using var lease = await SingletonWorkerLease.TryAcquireAsync(leaseDb, WorkerName, ct);
        if (lease is null)
        {
            LastSweepOutcome = SweepOutcome.Skipped;
            return new Dictionary<Guid, int>();
        }

        var results = new Dictionary<Guid, int>();
        IReadOnlyList<Guid> tenants;
        await using (var listScope = scopes.CreateAsyncScope())
            tenants = await listScope.ServiceProvider.GetRequiredService<FinalSettlementRunReminderService>().TenantsAsync(options.Value.MaxTenantsPerRun, ct);

        foreach (var tenantId in tenants)
        {
            if (ct.IsCancellationRequested || lease.IsLost) break;
            await using var scope = scopes.CreateAsyncScope();
            try { results[tenantId] = await scope.ServiceProvider.GetRequiredService<FinalSettlementRunReminderService>().RunTenantAsync(tenantId, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Final settlement run reminder for tenant {TenantId} failed; continuing with the next tenant.", tenantId);
            }
        }
        if (lease.IsLost || !await lease.IsStillHeldAsync(ct)) LastSweepOutcome = SweepOutcome.LeaseLost;
        return results;
    }
}

public static class FinalSettlementRunReminderServiceCollectionExtensions
{
    public static IServiceCollection AddFinalSettlementRunReminders(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<FinalSettlementRunReminderOptions>(configuration.GetSection(FinalSettlementRunReminderOptions.Section));
        services.AddScoped<FinalSettlementRunReminderService>();
        services.AddHostedService<FinalSettlementRunReminderWorker>();
        return services;
    }
}
