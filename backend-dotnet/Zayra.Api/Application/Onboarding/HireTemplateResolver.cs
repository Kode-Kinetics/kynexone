using Zayra.Api.Models;

namespace Zayra.Api.Application.Onboarding;

public sealed record HireTemplateResolution(IReadOnlyList<HireTemplate> Candidates, Guid? SuggestedId);

/// <summary>
/// Which hire template fits a hire. A template matches when each key it names (designation, grade, branch) equals the
/// hire's; a key it leaves empty matches anything. Specificity is designation 4 &gt; grade 2 &gt; branch 1, summed over the
/// keys it names. The best match is suggested; a tie suggests nothing and HR picks (never a guess).
/// </summary>
public static class HireTemplateResolver
{
    public const int DesignationWeight = 4, GradeWeight = 2, BranchWeight = 1;

    public static HireTemplateResolution Resolve(IEnumerable<HireTemplate> templates, Guid? designationId, Guid? gradeId, Guid? branchId)
    {
        var scored = new List<(HireTemplate Template, int Score)>();
        foreach (var t in templates.Where(t => t.IsActive && !t.IsDeleted))
        {
            var score = 0;
            if (t.MatchDesignationId is Guid d) { if (d != designationId) continue; score += DesignationWeight; }
            if (t.MatchGradeId is Guid g) { if (g != gradeId) continue; score += GradeWeight; }
            if (t.MatchBranchId is Guid b) { if (b != branchId) continue; score += BranchWeight; }
            if (score > 0) scored.Add((t, score));
        }
        var ordered = scored.OrderByDescending(x => x.Score).ThenBy(x => x.Template.Name, StringComparer.Ordinal).ToList();
        Guid? suggested = null;
        if (ordered.Count > 0 && (ordered.Count == 1 || ordered[0].Score > ordered[1].Score)) suggested = ordered[0].Template.Id;
        return new HireTemplateResolution(ordered.Select(x => x.Template).ToList(), suggested);
    }
}
