using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Entitlements;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Entitlements;

// Release A slice R2 owns this file. Registered (descriptor + scoped handler) by ReleaseAServiceCollectionExtensions.

/// <summary>Payload of <see cref="PackageFreezeJobHandler"/>: one company's employees.</summary>
public sealed record PackageFreezeJobPayload(Guid CompanyId);

/// <summary>
/// Bulk freeze for employees already on a running term when Release A is switched on for a company. One checkpointed
/// item per contract term (resumable, idempotent: the writer reports an already-frozen term and writes nothing). Rows are
/// <c>Migrated</c> and <c>Unverified</c> until HR confirms them on the package panel; the confirm list is the set of
/// Unverified rows, keyed by the job (its id is the batch).
/// </summary>
public sealed class PackageFreezeJobHandler : IBackgroundJobHandler
{
    public const string JobType = "entitlements.package-freeze";

    public static readonly BackgroundJobTypeDescriptor Descriptor = new(
        JobType,
        typeof(PackageFreezeJobHandler),
        ViewPermissions: ["entitlements.manage"],
        CancelPermissions: ["entitlements.manage"],
        KeyRetention: BackgroundJobKeyRetention.WhileActive,
        MaxAttempts: 5);

    /// <summary>One live bulk freeze per company: a second request returns the running job.</summary>
    public static string IdempotencyKey(Guid companyId) => $"company:{companyId:N}";

    private readonly ITenantClock _clock;
    private readonly IEntitlementResolver _resolver;

    public PackageFreezeJobHandler(ITenantClock clock, IEntitlementResolver resolver)
    {
        _clock = clock;
        _resolver = resolver;
    }

    public async Task ExecuteAsync(JobExecutionContext context)
    {
        var payload = context.GetPayload<PackageFreezeJobPayload>();
        var db = context.Db;
        var tenantId = context.TenantId;
        var today = await _clock.TodayAsync(tenantId, context.AbortToken);
        var terms = await ScopedBypass.TenantWide(db.EmployeeContracts, tenantId,
                "The bulk freeze covers every running term of the requested company; the request was scope-checked.")
            .AsNoTracking()
            .Where(x => x.CompanyId == payload.CompanyId && !x.IsDeleted && x.Status == "Active"
                && x.StartDate <= today && (x.EndDate == null || x.EndDate >= today))
            .OrderBy(x => x.StartDate).Select(x => x.Id)
            .ToListAsync(context.AbortToken);
        await context.SetTotalAsync(terms.Count, $"{terms.Count} contract terms to check");

        var writer = new EntitlementWriter(db, _clock, _resolver);
        int frozen = 0, already = 0, skipped = 0;
        foreach (var contractId in terms)
        {
            FreezeResult? outcome = null;
            await context.RunItemAsync($"contract:{contractId:N}", async ct =>
            {
                outcome = null;
                try { outcome = await writer.FreezeExistingAsync(tenantId, contractId, ct); }
                catch (EntitlementWriteRefusedException) { outcome = new FreezeResult(false, false, 0); }
            }, () => outcome);
            if (outcome is null) continue;
            if (outcome.Frozen) frozen++;
            else if (outcome.AlreadyFrozen) already++;
            else skipped++;
        }
        context.SetResult(new { terms = terms.Count, frozen, alreadyFrozen = already, nothingToFreeze = skipped });
    }
}
