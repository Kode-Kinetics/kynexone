using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Entitlements;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Entitlements;

// Release A slice R2 owns this file. Registered (descriptor + scoped handler) by ReleaseAServiceCollectionExtensions.

/// <summary>Payload of <see cref="PackageFreezeJobHandler"/>: one company's employees.</summary>
public sealed record PackageFreezeJobPayload(Guid CompanyId);

/// <summary>
/// Proposed packages for employees already on a running term when Release A is switched on for a company (CTO decision,
/// review round 1). The job WRITES NOTHING to <c>employee_entitlements</c>: one checkpointed item per running term stores
/// the package freezing would write (<see cref="FreezeProposal"/>) as the item's result. The job's id is the batch. HR then
/// confirms each proposal against the signed contract — a different person from whoever asked for the job, citing the
/// contract document — and only then are rows written (Migrated, Verified); or rejects it. Resumable and idempotent: a
/// resumed attempt skips checkpointed terms, and a term that already has a package proposes nothing.
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

    /// <summary>One live proposal run per company: a second request returns the running job.</summary>
    public static string IdempotencyKey(Guid companyId) => $"company:{companyId:N}";

    /// <summary>The checkpoint key of one term's proposal.</summary>
    public static string ItemKey(Guid contractId) => $"contract:{contractId:N}";

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
        var terms = await RunningTermsAsync(db, tenantId, payload.CompanyId, today, context.AbortToken);
        await context.SetTotalAsync(terms.Count, $"{terms.Count} contract terms to check");

        var writer = new EntitlementWriter(db, _clock, _resolver);
        int proposed = 0, nothing = 0;
        foreach (var contractId in terms)
        {
            FreezeProposal? proposal = null;
            await context.RunItemAsync(ItemKey(contractId), async ct =>
            {
                proposal = null;
                try { proposal = await writer.ProposeAsync(tenantId, contractId, ct); }
                catch (EntitlementWriteRefusedException) { proposal = null; }
            }, () => proposal is { Rows.Count: > 0 } ? proposal : null);
            if (proposal is { Rows.Count: > 0 }) proposed++; else nothing++;
        }
        context.SetResult(new { terms = terms.Count, proposed, nothingToPropose = nothing });
    }

    private static Task<List<Guid>> RunningTermsAsync(ZayraDbContext db, Guid tenantId, Guid companyId, DateOnly today, CancellationToken ct) =>
        ScopedBypass.TenantWide(db.EmployeeContracts, tenantId,
                "The proposal run covers every running term of the requested company; the request was scope-checked.")
            .AsNoTracking()
            .Where(x => x.CompanyId == companyId && !x.IsDeleted && x.Status == "Active"
                && x.StartDate <= today && (x.EndDate == null || x.EndDate >= today))
            .OrderBy(x => x.StartDate).Select(x => x.Id)
            .ToListAsync(ct);

    /// <summary>A stored proposal (the item's result), or NULL when the item proposed nothing.</summary>
    public static FreezeProposal? ReadProposal(string? resultJson) =>
        string.IsNullOrWhiteSpace(resultJson) || resultJson == "null" ? null : JsonSerializer.Deserialize<FreezeProposal>(resultJson);
}
