using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Employees;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Employees;

namespace Zayra.Api.Infrastructure.Recruitment;

/// <summary>
/// The makers of a hire, across both modules: the employee module's own view (the draft's creator and
/// everyone who changed the draft, <see cref="DraftHireMakers"/>) plus, for an accepted offer's draft,
/// everyone who made the hire on the recruitment side: whoever sent the offer and whoever accepted it
/// (<see cref="OfferRules.HireMakersForDraftAsync"/>). None of them may approve or reject the draft.
///
/// <para>This is what Program.cs registers. Without it the offer's sender, who neither created nor
/// edited the draft, could activate the hire they had offered.</para>
/// </summary>
public sealed class OfferDraftHireMakers : IDraftHireMakers
{
    private readonly ZayraDbContext _db;
    private readonly DraftHireMakers _draftMakers;

    public OfferDraftHireMakers(ZayraDbContext db)
    {
        _db = db;
        _draftMakers = new DraftHireMakers(db);
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlySet<Guid>>> MakersAsync(
        Guid tenantId, IReadOnlyCollection<Guid> draftIds, CancellationToken ct)
    {
        var fromDraft = await _draftMakers.MakersAsync(tenantId, draftIds, ct);
        var result = fromDraft.ToDictionary(kv => kv.Key, kv => new HashSet<Guid>(kv.Value));
        if (result.Count == 0) return new Dictionary<Guid, IReadOnlySet<Guid>>();

        // Only drafts that an accepted offer created have recruitment-side makers. Which drafts those are
        // is read tenant-wide. OfferRules then reads the application, offer and offer audit rows through
        // the caller's own filters; a caller who cannot see the application cannot see its draft either
        // (EmployeesController.VisibleDrafts), so for anyone able to act on the draft the set is complete.
        var ids = result.Keys.ToList();
        var offerDrafts = await ScopedBypass.TenantWide(_db.JobApplications, tenantId,
                "Which of these drafts an accepted offer created, whatever company the caller is scoped to.")
            .AsNoTracking()
            .Where(a => a.OnboardingDraftId != null && ids.Contains(a.OnboardingDraftId.Value))
            .Select(a => a.OnboardingDraftId!.Value)
            .Distinct()
            .ToListAsync(ct);
        foreach (var draftId in offerDrafts)
            result[draftId].UnionWith(await OfferRules.HireMakersForDraftAsync(_db, tenantId, draftId, ct));

        return result.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<Guid>)kv.Value);
    }
}
