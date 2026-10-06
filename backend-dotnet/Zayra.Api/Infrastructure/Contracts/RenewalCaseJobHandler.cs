using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Contracts;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Infrastructure.Modules;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Contracts;

/// <summary>Payload of one tenant's daily run. <paramref name="Today"/> is the tenant-local date fixed at enqueue, so a
/// retry hours later decides against the same day as the first attempt.</summary>
public sealed record RenewalCasePayload(DateOnly Today);

/// <summary>
/// The daily per-tenant renewal job (<c>contracts.renewal-cases</c>), enqueued once per tenant-local day by
/// <see cref="RenewalCaseScheduler"/>. Each step is a checkpointed item of the F3 queue (fenced lease, one transaction
/// with the checkpoint), so a crash or retry resumes without repeating finished work:
/// <list type="number">
/// <item><c>census</c> — the chain census (<see cref="ContractChainCensus"/>).</item>
/// <item><c>reconcile:{case}</c> — a case whose expiring contract was terminated, superseded or deleted is cancelled (an EXPIRED one is left for R6's holdover)
///   (T21) with an audit row; one whose end date changed is re-baselined with an audit row.</item>
/// <item><c>open:{contract}</c> — T1 for every fixed-term term whose open date has arrived (UNIQUE makes it
///   exactly once; <see cref="RenewalCaseOpener"/>).</item>
/// <item><c>remind:{key}</c> — the deadline reminders and next-day escalations (<see cref="RenewalReminderService"/>).</item>
/// </list>
/// Terms that ended with no outcome are counted in the result (<c>expiredNoOutcome</c>) and shown on the dashboard;
/// continuing them by law (holdover, T22) is R6's step. Tenant-scoped throughout: every query is pinned to the job's
/// tenant, and a tenant without the release_a flag is skipped. Slice R4.
/// </summary>
public sealed class RenewalCaseJobHandler : IBackgroundJobHandler
{
    public const string JobType = "contracts.renewal-cases";
    public const string SystemActor = "kynexone:renewal-job";

    public static readonly BackgroundJobTypeDescriptor Descriptor = new(
        JobType,
        typeof(RenewalCaseJobHandler),
        ViewPermissions: ["contracts.renewal.read"],
        CancelPermissions: ["contracts.renewal.manage"],
        // Forever: the key is the tenant-local date, so there is exactly one run per tenant per day even when the
        // hourly scheduler ticks again after it finished. Tomorrow is a new key.
        KeyRetention: BackgroundJobKeyRetention.Forever,
        MaxAttempts: 5);

    /// <summary>One run per tenant per tenant-local day.</summary>
    public static string IdempotencyKey(DateOnly tenantLocalDate) => $"{JobType}:{tenantLocalDate:yyyy-MM-dd}";

    private static readonly HashSet<string> EndedStatuses = new(StringComparer.Ordinal) { "Terminated", "Superseded" };

    private readonly ContractChainCensus _census;
    private readonly RenewalCaseOpener _opener;
    private readonly RenewalReminderService _reminders;
    private readonly ITenantModuleService _modules;
    private readonly ILogger<RenewalCaseJobHandler> _log;

    public RenewalCaseJobHandler(ContractChainCensus census, RenewalCaseOpener opener, RenewalReminderService reminders,
        ITenantModuleService modules, ILogger<RenewalCaseJobHandler> log)
    {
        _census = census;
        _opener = opener;
        _reminders = reminders;
        _modules = modules;
        _log = log;
    }

    public async Task ExecuteAsync(JobExecutionContext context)
    {
        var tenantId = context.TenantId;
        var today = context.GetPayload<RenewalCasePayload>().Today;
        var ct = context.AbortToken;
        var db = context.Db;

        if (!(await _modules.GetStateAsync(tenantId, ct)).IsEnabled(FeatureKeys.ReleaseA))
        {
            context.SetResult(new { skipped = "release_a is not enabled for this tenant" });
            return;
        }

        ChainCensusResult? census = null;
        await context.RunItemAsync("census", async itemCt => census = await _census.RunAsync(tenantId, null, itemCt),
            countsTowardProgress: false);

        // ── Reconcile open cases with their contract ─────────────────────────────────────────────
        var open = await db.ContractRenewalCases.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.ClosedAt == null)
            .Select(c => new { c.Id, c.EmployeeId, c.ExpiringContractId, c.ExpiringEndDate })
            .ToListAsync(ct);
        var openContractIds = open.Select(c => c.ExpiringContractId).ToList();
        var contracts = await ScopedBypass.TenantWide(db.EmployeeContracts, tenantId,
                "Renewal job reconciles open cases with their contract, deleted rows included; tenant from the job.")
            .AsNoTracking()
            .Where(c => openContractIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Status, c.EndDate, c.IsDeleted })
            .ToDictionaryAsync(c => c.Id, ct);
        int cancelled = 0, rebaselined = 0;
        foreach (var c in open)
        {
            if (!contracts.TryGetValue(c.ExpiringContractId, out var contract)) continue;
            if (contract.IsDeleted || EndedStatuses.Contains(contract.Status))
            {
                if (await context.RunItemAsync($"reconcile:{c.Id:N}:ended",
                        itemCt => CancelForEndedContractAsync(context, c.Id, c.EmployeeId, itemCt)))
                    cancelled++;
            }
            else if (contract.EndDate is { } end && end != c.ExpiringEndDate)
            {
                if (await context.RunItemAsync($"reconcile:{c.Id:N}:end:{end:yyyyMMdd}",
                        itemCt => RebaselineAsync(context, c.Id, c.EmployeeId, today, itemCt)))
                    rebaselined++;
            }
        }

        // ── T1: open the cases whose date has arrived ───────────────────────────────────────────
        var rules = await RenewalRuleSet.LoadAsync(db, tenantId, today, ct);
        var candidates = await _opener.CandidatesAsync(tenantId, null, today.AddDays(rules.Deadlines.RenewalLeadDays + 366), tracked: false, ct);
        var withCase = (await db.ContractRenewalCases.AsNoTracking()
                .Where(c => c.TenantId == tenantId).Select(c => c.ExpiringContractId).ToListAsync(ct)).ToHashSet();
        int opened = 0, skipped = 0;
        foreach (var candidate in candidates.Where(c => !withCase.Contains(c.Contract.Id)))
        {
            var plan = RenewalCaseOpener.Plan(candidate, rules, today);
            if (plan.SkipReason == RenewalOpenSkipReasons.NotDue) continue;
            if (!plan.CanOpen) { skipped++; continue; }
            RenewalOpenOutcome? outcome = null;
            await context.RunItemAsync($"open:{candidate.Contract.Id:N}", async itemCt =>
            {
                await FinanceDecisionSerializer.AcquireAsync(db, FinanceDecisionSerializer.ScopeEmployeePackage, tenantId,
                    candidate.Contract.EmployeeId, itemCt);
                outcome = await _opener.StageOpenAsync(tenantId, candidate.Contract.Id, today, null, SystemActor, itemCt);
            });
            if (outcome?.Result == RenewalOpenOutcome.Opened) opened++;
        }

        // ── Reminders and escalations ───────────────────────────────────────────────────────────
        int remindersEnqueued = 0, remindersWithoutRecipient = 0;
        foreach (var reminder in await _reminders.PlanAsync(tenantId, today, ct))
        {
            RenewalReminderResult? sent = null;
            await context.RunItemAsync($"remind:{reminder.Key}", async itemCt => sent = await _reminders.SendAsync(tenantId, reminder, itemCt));
            if (sent is null) continue;
            remindersEnqueued += sent.Enqueued;
            if (sent.NoRecipient)
            {
                remindersWithoutRecipient++;
                _log.LogWarning("Renewal reminder {Key} for tenant {TenantId} has no HR recipient with access to the company.", reminder.Key, tenantId);
            }
        }

        var expiredNoOutcome = await db.ContractRenewalCases.AsNoTracking()
            .CountAsync(c => c.TenantId == tenantId && c.ClosedAt == null && c.ExpiringEndDate < today, ct);
        context.SetResult(new
        {
            today,
            census,
            cancelled,
            rebaselined,
            opened,
            notOpenedNeedsData = skipped,
            remindersEnqueued,
            remindersWithoutRecipient,
            expiredNoOutcome,
            rulesFellBack = rules.FellBack,
        });
    }

    /// <summary>T21: the expiring term was terminated, superseded or deleted — the review has nothing left to decide.</summary>
    private static async Task CancelForEndedContractAsync(JobExecutionContext context, Guid caseId, Guid employeeId, CancellationToken ct)
    {
        var db = context.Db;
        await FinanceDecisionSerializer.AcquireAsync(db, FinanceDecisionSerializer.ScopeEmployeePackage, context.TenantId, employeeId, ct);
        var c = await db.ContractRenewalCases.FirstOrDefaultAsync(x => x.TenantId == context.TenantId && x.Id == caseId, ct);
        if (c is null || RenewalStates.IsTerminal(c.State)) return;
        var from = c.State;
        var transition = RenewalCaseTransitions.Cancel(c, DateTime.UtcNow);
        db.ComplianceAuditLogs.Add(RenewalCaseOpener.Audit(context.TenantId, c, "Cancelled", null, SystemActor,
            new { transition = transition.Id, from, reason = "ContractEnded" }));
    }

    private static async Task RebaselineAsync(JobExecutionContext context, Guid caseId, Guid employeeId, DateOnly today, CancellationToken ct)
    {
        var db = context.Db;
        await FinanceDecisionSerializer.AcquireAsync(db, FinanceDecisionSerializer.ScopeEmployeePackage, context.TenantId, employeeId, ct);
        var c = await db.ContractRenewalCases.FirstOrDefaultAsync(x => x.TenantId == context.TenantId && x.Id == caseId, ct);
        if (c is null) return;
        var contract = await db.EmployeeContracts.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == context.TenantId && x.Id == c.ExpiringContractId, ct);
        if (contract?.EndDate is null || contract.EndDate == c.ExpiringEndDate) return;
        var rules = await RenewalRuleSet.LoadAsync(db, context.TenantId, today, ct);
        RenewalCaseOpener.Rebaseline(db, c, contract, rules, today, "ContractEndDateChanged", null, SystemActor);
    }
}
