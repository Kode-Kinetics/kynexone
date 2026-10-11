using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// Applies an APPROVED tenant-level statutory change to <c>statutory_rules</c>.
///
/// <para>Tenant-level statutory values used to be written by one person and applied by payroll the
/// same moment. They are now requested as a <see cref="CompanyStatutoryOverride"/> row with
/// <c>CompanyId = null</c> and <c>Status = PendingApproval</c> (the same maker-checker queue as a
/// company override, no new table), and written here only once a second person approves.</para>
///
/// <para>Date conventions differ between the two stores and are converted here only:
/// <c>statutory_rules.EffectiveTo</c> is EXCLUSIVE (a UTC midnight); a request's
/// <c>EffectiveTo</c> is INCLUSIVE (a DateOnly), like every company override.</para>
/// </summary>
public static class TenantStatutoryChange
{
    public static DateTime ToUtc(DateOnly d) => d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    /// <summary>The exclusive rule end for an inclusive request end.</summary>
    public static DateTime? ExclusiveEnd(DateOnly? inclusiveEnd) => inclusiveEnd is { } d ? ToUtc(d.AddDays(1)) : null;

    /// <summary>The inclusive request end for an exclusive rule end.</summary>
    public static DateOnly? InclusiveEnd(DateTime? exclusiveEnd) =>
        exclusiveEnd is { } d ? DateOnly.FromDateTime(d).AddDays(-1) : null;

    private static IQueryable<StatutoryRule> TenantRows(ZayraDbContext db, Guid tenantId) =>
        ScopedBypass.NullableTenantWide(db.StatutoryRules, tenantId,
            "Tenant-level statutory versions of the approving caller's own tenant; platform rows are never written here.");

    public static Task<bool> OverlapsAsync(ZayraDbContext db, Guid tenantId, string country, string jurisdiction, string key,
        DateTime from, DateTime? to, Guid? excluding, CancellationToken ct)
        => TenantRows(db, tenantId).AsNoTracking().AnyAsync(r =>
               r.CountryCode == country && r.Jurisdiction == jurisdiction && r.RuleKey == key
            && (excluding == null || r.Id != excluding)
            && (to == null || r.EffectiveFrom < to) && (r.EffectiveTo == null || r.EffectiveTo > from), ct);

    /// <summary>
    /// Writes a new tenant version from the request's start date, closing the open-ended version that
    /// started before it (a supersede). Returns a refusal message, or null when applied.
    /// </summary>
    public static async Task<string?> ApplySetAsync(ZayraDbContext db, CompanyStatutoryOverride request, CancellationToken ct)
    {
        var from = ToUtc(request.EffectiveFrom);
        var to = ExclusiveEnd(request.EffectiveTo);
        if (from < DateTime.UtcNow.Date.AddDays(1))
            return $"This change was due to start on {request.EffectiveFrom:yyyy-MM-dd}, which has passed. Approving it now would rewrite pay already worked out; ask for a new change with a future start date.";

        var open = await TenantRows(db, request.TenantId)
            .Where(r => r.CountryCode == request.CountryCode && r.Jurisdiction == request.Jurisdiction
                     && r.RuleKey == request.RuleKey && r.EffectiveTo == null && r.EffectiveFrom < from)
            .OrderByDescending(r => r.EffectiveFrom)
            .FirstOrDefaultAsync(ct);
        if (await OverlapsAsync(db, request.TenantId, request.CountryCode, request.Jurisdiction, request.RuleKey, from, to, open?.Id, ct))
            return "Another tenant value already covers this period. Reload the rule history and request the change again.";

        if (open is not null) open.EffectiveTo = from;
        db.StatutoryRules.Add(new StatutoryRule
        {
            TenantId = request.TenantId, CountryCode = request.CountryCode, Jurisdiction = request.Jurisdiction,
            RuleKey = request.RuleKey, RuleValue = request.OverrideValue,
            DataType = string.IsNullOrWhiteSpace(request.DataType) ? "decimal" : request.DataType,
            Description = request.Reason, EffectiveFrom = from, EffectiveTo = to,
            CreatedBy = request.CreatedBy, CreatedAtUtc = DateTime.UtcNow,
        });
        return null;
    }

    /// <summary>Ends the tenant version live on the retirement date (never earlier than tomorrow UTC).</summary>
    public static async Task<string?> ApplyRetireAsync(ZayraDbContext db, CompanyStatutoryOverride request, CancellationToken ct)
    {
        var requested = ToUtc(request.EffectiveFrom);
        var tomorrow = DateTime.UtcNow.Date.AddDays(1);
        var retireOn = requested > tomorrow ? requested : tomorrow;
        var rule = await TenantRows(db, request.TenantId)
            .Where(r => r.CountryCode == request.CountryCode && r.Jurisdiction == request.Jurisdiction
                     && r.RuleKey == request.RuleKey && r.EffectiveFrom < retireOn
                     && (r.EffectiveTo == null || r.EffectiveTo > retireOn))
            .OrderByDescending(r => r.EffectiveFrom)
            .FirstOrDefaultAsync(ct);
        if (rule is null) return "There is no tenant value in effect to retire any more. Decline this request instead.";
        rule.EffectiveTo = retireOn;
        return null;
    }
}
