using Zayra.Api.Application.Leave;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Leave;

/// <summary>Payload of one tenant's monthly run. <paramref name="TenantLocalDate"/> is the tenant's own calendar day fixed at ENQUEUE, so a retry decides against the same month.</summary>
public sealed record LeaveAccrualPayload(DateOnly TenantLocalDate);

/// <summary>
/// The monthly per-tenant leave job (<c>leave.accrual</c>), enqueued once per tenant-local month by <see cref="LeaveAccrualScheduler"/>:
/// accrues every completed month of the year not yet accrued (so a month missed while the process was down is posted once, when it
/// is back), front-loads yearly policies for joiners, and — from January — carries the previous year's unused annual leave into the
/// new year and closes the old row. All of it is <see cref="ILeaveService.RunScheduledAccrualAsync"/>, which owns its own
/// transactions and is idempotent by ledger entry; the handler adds nothing but the queue's lease, retry and visibility.
/// </summary>
public sealed class LeaveAccrualJobHandler : IBackgroundJobHandler
{
    public const string JobType = "leave.accrual";

    public static readonly BackgroundJobTypeDescriptor Descriptor = new(
        JobType,
        typeof(LeaveAccrualJobHandler),
        ViewPermissions: ["leave.policy_manage"],
        CancelPermissions: ["leave.policy_manage"],
        // Forever: the key is the tenant-local MONTH, so there is exactly one run per tenant per month even when the hourly scheduler
        // ticks again after it finished. Next month is a new key.
        KeyRetention: BackgroundJobKeyRetention.Forever,
        MaxAttempts: 3);

    /// <summary>One run per tenant per tenant-local month.</summary>
    public static string IdempotencyKey(DateOnly tenantLocalDate) => $"{JobType}:{tenantLocalDate:yyyy-MM}";

    private readonly ILogger<LeaveAccrualJobHandler> _log;

    public LeaveAccrualJobHandler(ILogger<LeaveAccrualJobHandler> log) => _log = log;

    public async Task ExecuteAsync(JobExecutionContext ctx)
    {
        var today = ctx.GetPayload<LeaveAccrualPayload>().TenantLocalDate;
        var leave = ctx.Services.GetRequiredService<ILeaveService>();
        var result = await leave.RunScheduledAccrualAsync(ctx.TenantId, today, ctx.AbortToken);
        _log.LogInformation(
            "Leave accrual for tenant {TenantId} on {LocalDate:yyyy-MM-dd}: {Accruals} accrual and {Allocations} allocation entries, {Closed} balance row(s) rolled over ({Days} day(s)).",
            ctx.TenantId, today, result.AccrualEntries, result.AllocationEntries, result.Rollover.RowsClosed, result.Rollover.DaysCarried);
        ctx.SetResult(new
        {
            tenantLocalDate = today.ToString("yyyy-MM-dd"),
            accrualYear = result.AccrualYear,
            accrualEntries = result.AccrualEntries,
            allocationEntries = result.AllocationEntries,
            rolledOverRows = result.Rollover.RowsClosed,
            daysCarriedForward = result.Rollover.DaysCarried,
        });
    }
}
