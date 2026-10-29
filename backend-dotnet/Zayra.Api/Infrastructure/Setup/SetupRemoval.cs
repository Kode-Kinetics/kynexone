using Microsoft.AspNetCore.Mvc;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Application.Organization;

namespace Zayra.Api.Controllers;

/// <summary>
/// The one response contract for Setup's Remove / Restore on rows that are not organisation rows (leave, loan, bonus,
/// payslip, notification and letter templates, scorecards, checklists, compliance profiles, country rules, shifts).
/// It is the same body <see cref="OrgWriteRules"/> returns for organisation rows -
/// <c>{ outcome: deleted | turnedOff | restored, message, usage: { total, summary, items[] } }</c> - so the shared
/// UI (<c>useOrgRowRemoval</c>, <c>OrgRowActions</c>) renders every Setup list the same way.
/// A refusal is a 409 with a stable <c>code</c> (IN_USE / SYSTEM / LAST / STATUTORY) and a plain-language message.
/// </summary>
internal static class SetupRemoval
{
    /// <summary>A usage entry. <paramref name="kind"/> is the structured key the UI translates; <paramref name="label"/>
    /// is the English fallback ("leave requests").</summary>
    public static OrgRowUsageItem Used(string kind, string label, int count) => new(kind, label, count);

    /// <summary>Never used: the row was deleted.</summary>
    public static IActionResult Deleted(ControllerBase c) =>
        c.Ok(OrgWriteRules.Body(new OrgRowRemovalResult(OrgRowOutcomes.Deleted, OrgRowUsage.None, "Removed.")));

    /// <summary>Used or has history: the row was turned off and can be restored. Items with a zero count are dropped.</summary>
    public static IActionResult TurnedOff(ControllerBase c, IEnumerable<OrgRowUsageItem> items)
    {
        var usage = new OrgRowUsage(items.Where(i => i.Count > 0).OrderByDescending(i => i.Count).ToList());
        var message = usage.InUse
            ? $"Turned off. {usage.Summary}. It stays in history and can be restored anytime."
            : "Turned off. It stays in history and can be restored anytime.";
        return c.Ok(OrgWriteRules.Body(new OrgRowRemovalResult(OrgRowOutcomes.TurnedOff, usage, message)));
    }

    public static IActionResult Restored(ControllerBase c) => c.Ok(new { outcome = "restored", message = "Turned back on." });

    /// <summary>A blocked action, with the reason the UI shows in plain text.</summary>
    public static IActionResult Blocked(ControllerBase c, string code, string message) => c.Conflict(new { code, message });

    /// <summary>One thing that can still point at the row. <paramref name="Wide"/> counts across every company of the tenant (the
    /// decision: a sister company's use must keep the row from being erased); <paramref name="Visible"/> counts through the caller's
    /// own company scope (what the response may show).</summary>
    public sealed record Probe(string Kind, string Label, Func<CancellationToken, Task<int>> Wide, Func<CancellationToken, Task<int>> Visible);

    /// <summary>A probe for rows of <typeparamref name="T"/> matching <paramref name="where"/>: counted across the tenant for the
    /// decision, and through the caller's own company scope for what is shown.</summary>
    public static Probe Of<T>(ZayraDbContext db, Guid tenantId, string kind, string label, Expression<Func<T, bool>> where) where T : class =>
        new(kind, label,
            ct => ScopedBypass.TenantWideByConvention(db.Set<T>(), tenantId, "Removing a Setup row must see every reference to it in the tenant, whatever company the caller's switcher shows.").AsNoTracking().CountAsync(where, ct),
            ct => db.Set<T>().AsNoTracking().CountAsync(where, ct));

    /// <param name="Shown">Counts the caller may see.</param>
    /// <param name="WideTotal">Every reference in the tenant; decides delete vs turn off.</param>
    /// <param name="Elsewhere">Some references are in a company the caller cannot see.</param>
    public sealed record Measured(IReadOnlyList<OrgRowUsageItem> Shown, int WideTotal, bool Elsewhere)
    {
        public bool InUse => WideTotal > 0;
    }

    /// <summary>Counts every probe tenant-wide for the decision, but shows a company-scoped caller only the numbers inside their own
    /// companies, plus a flag that the row is also used elsewhere. A group-level caller sees everything.</summary>
    public static async Task<Measured> MeasureAsync(ControllerBase c, IEnumerable<Probe> probes, CancellationToken ct)
    {
        var group = c.HasGroupEntityScope();
        var shown = new List<OrgRowUsageItem>();
        int wide = 0, visible = 0;
        foreach (var p in probes)
        {
            var w = await p.Wide(ct);
            wide += w;
            var v = group ? w : Math.Min(w, await p.Visible(ct));
            visible += v;
            if (v > 0) shown.Add(new OrgRowUsageItem(p.Kind, p.Label, v));
        }
        return new Measured(shown, wide, wide > visible);
    }

    /// <summary>The turned-off response for a measured row: the caller's own counts, and a plain sentence when other companies also use it.</summary>
    public static IActionResult TurnedOff(ControllerBase c, Measured m)
    {
        var usage = new OrgRowUsage(m.Shown.OrderByDescending(i => i.Count).ToList());
        var message = usage.InUse ? $"Turned off. {usage.Summary}. It stays in history and can be restored anytime."
            : "Turned off. It stays in history and can be restored anytime.";
        if (m.Elsewhere) message += " Other companies in your group also use it, so it is kept.";
        return c.Ok(new
        {
            outcome = OrgRowOutcomes.TurnedOff, message,
            usage = new { total = usage.Total, summary = usage.Summary, items = usage.Items.Select(i => new { kind = i.Kind, label = i.Label, count = i.Count }) },
            alsoUsedElsewhere = m.Elsewhere,
        });
    }
}
