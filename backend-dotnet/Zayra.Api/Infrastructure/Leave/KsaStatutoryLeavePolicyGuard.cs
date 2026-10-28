using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Data;

namespace Zayra.Api.Infrastructure.Leave;

/// <summary>Which employees a leave policy reaches, as far as the Saudi floor is concerned.</summary>
public enum KsaPolicyReach
{
    /// <summary>No Saudi employee: the Saudi floor does not apply.</summary>
    None,
    /// <summary>Saudi employees only: the policy names SA, a Saudi company, or has no country in a
    /// tenant whose companies are all Saudi.</summary>
    SaudiOnly,
    /// <summary>A country-neutral, company-neutral policy in a tenant with Saudi AND non-Saudi
    /// companies: it would govern both, so the Saudi floor must not be forced onto it.</summary>
    Mixed,
}

/// <summary>
/// Who a leave policy reaches, as far as the Saudi floor is concerned (Arts. 113, 114, 151, 160). The floors
/// themselves are evaluated by <see cref="KsaLeaveFloorChecks"/> as acknowledgeable WARNINGS (owner decision
/// 2026-10-25): a policy that reaches Saudi employees may grant less than the statute only with an audited
/// acknowledgement.
///
/// <para>Shape follows the existing statutory floors: the figure is read from the platform
/// <c>statutory_rules</c> row (tenant null, so a tenant row cannot lower it) with a compiled
/// fallback, and a configuration below it raises a warning with the citation.</para>
///
/// <para>A policy that also reaches non-Saudi staff is never made to carry the Saudi figure: the
/// administrator is asked for a separate Saudi-scoped policy instead, which outranks the neutral
/// one for Saudi employees (<c>LeaveService.PolicySpecificity</c>).</para>
/// </summary>
public static class KsaStatutoryLeavePolicyGuard
{
    /// <summary>Which employees a policy with this country and company reaches.</summary>
    public static async Task<KsaPolicyReach> ReachAsync(
        ZayraDbContext db, Guid tenantId, string? countryCode, Guid? companyId, CancellationToken ct)
    {
        var cc = (countryCode ?? string.Empty).Trim();
        if (KsaStatutorySpecialLeave.IsKsa(cc)) return KsaPolicyReach.SaudiOnly;
        if (cc.Length > 0) return KsaPolicyReach.None;

        var codes = await CompanyCountriesAsync(db, tenantId, companyId, ct);
        if (!codes.Any(KsaStatutorySpecialLeave.IsKsa)) return KsaPolicyReach.None;
        return codes.All(KsaStatutorySpecialLeave.IsKsa) ? KsaPolicyReach.SaudiOnly : KsaPolicyReach.Mixed;
    }

    internal static async Task<bool> HasCompliantSaudiPolicyAsync(
        ZayraDbContext db, Guid tenantId, Guid leaveTypeId, Guid? excludePolicyId,
        KsaStatutoryLeaveKind kind, decimal floor, CancellationToken ct)
    {
        var saudiCompanies = (await ScopedBypass.TenantWide(db.Companies, tenantId, CompanyReadWhy)
                .AsNoTracking().Where(c => !c.IsDeleted).Select(c => new { c.Id, c.CountryCode }).ToListAsync(ct))
            .Where(c => KsaStatutorySpecialLeave.IsKsa(c.CountryCode)).Select(c => c.Id).ToHashSet();

        var candidates = await ScopedBypass.TenantWide(db.LeavePolicies, tenantId,
                "A neutral policy's lawfulness depends on whether a Saudi-scoped sibling exists anywhere in the "
                + "tenant, not only in the caller's company scope. Read-only; only the floor-relevant fields are used.")
            .AsNoTracking()
            .Where(p => p.LeaveTypeId == leaveTypeId && p.Status != "Archived"
                        && (excludePolicyId == null || p.Id != excludePolicyId))
            .ToListAsync(ct);

        return candidates
            // A locals-only or expats-only sibling covers only that group, so it cannot make a neutral policy lawful.
            .Where(p => p.NationalityScope == Zayra.Api.Models.LeaveNationalityScopes.Any)
            .Where(p => KsaStatutorySpecialLeave.IsKsa(p.CountryCode)
                        || (string.IsNullOrWhiteSpace(p.CountryCode) && p.CompanyId is { } cid && saudiCompanies.Contains(cid)))
            .Any(p => KsaStatutorySpecialLeave.PolicyViolations(kind, floor, p.AnnualEntitlementDays,
                p.MaximumDaysPerRequest, p.PayrollImpact, p.WeekendsIncluded && p.PublicHolidaysIncluded).Count == 0);
    }

    private static async Task<List<string>> CompanyCountriesAsync(
        ZayraDbContext db, Guid tenantId, Guid? companyId, CancellationToken ct)
    {
        var companies = ScopedBypass.TenantWide(db.Companies, tenantId, CompanyReadWhy)
            .AsNoTracking()
            .Where(c => !c.IsDeleted);
        if (companyId is { } id)
            companies = companies.Where(c => c.Id == id);
        return await companies.Select(c => c.CountryCode).ToListAsync(ct);
    }

    private const string CompanyReadWhy =
        "Deciding whether a leave policy reaches a Saudi employee is a property of the employer's legal "
        + "entities, not of the caller's company scope: an HR administrator scoped to one entity must not be "
        + "able to lower a statutory floor that applies to another. Only country codes are read; no company "
        + "data crosses the boundary.";

    private static bool IsArchived(string? status)
        => string.Equals(status?.Trim(), "Archived", StringComparison.OrdinalIgnoreCase);
}
