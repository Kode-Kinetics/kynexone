using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Data;

namespace Zayra.Api.Infrastructure.Leave;

/// <summary>
/// The write-path floor for KSA statutory special leave (Arts. 113, 114, 151, 160). A leave policy
/// that reaches Saudi employees may grant MORE than the statute for maternity, marriage, bereavement,
/// birth, Hajj or iddah leave — never less, and never unpaid.
///
/// <para>Shape follows the existing statutory floors: the figure is read from the platform
/// <c>statutory_rules</c> row (tenant null, so a tenant row cannot lower it) with a compiled
/// fallback, and a configuration below it is refused with the citation rather than stored.
/// Annual leave (Art. 109) is floored on the accrual read path instead, because its figure moves
/// with each employee's tenure; these do not.</para>
/// </summary>
public static class KsaStatutoryLeavePolicyGuard
{
    /// <summary>The violations of the statutory floor, in plain words, or an empty list when the
    /// policy is lawful, is not for a statutory leave type, or does not reach a Saudi employee.</summary>
    public static async Task<IReadOnlyList<string>> CheckAsync(
        ZayraDbContext db, Guid tenantId,
        string? typeCode, string? typeNameEn, string? typeCategory,
        string? policyCountryCode, Guid? policyCompanyId, string? policyStatus,
        decimal entitlementDays, decimal maxPerRequest, string? payrollImpact,
        CancellationToken ct)
    {
        // An archived policy governs nothing, so there is nothing to protect.
        if (string.Equals(policyStatus?.Trim(), "Archived", StringComparison.OrdinalIgnoreCase))
            return Array.Empty<string>();

        var kind = KsaStatutorySpecialLeave.Classify(typeCode, typeNameEn, typeCategory);
        if (kind is null) return Array.Empty<string>();
        if (!await ReachesKsaAsync(db, tenantId, policyCountryCode, policyCompanyId, ct))
            return Array.Empty<string>();

        var floor = await KsaStatutorySpecialLeave.ResolveFloorAsync(
            new StatutoryRuleReader(db), kind.Value, DateOnly.FromDateTime(DateTime.UtcNow), ct);
        if (floor is null) return Array.Empty<string>();

        return KsaStatutorySpecialLeave.PolicyViolations(kind.Value, floor.Value, entitlementDays, maxPerRequest, payrollImpact);
    }

    /// <summary>
    /// Whether a policy applies to Saudi employees: it names Saudi Arabia, or it names no country
    /// (so applies to every employee) and either belongs to a Saudi company or, tenant-wide, the
    /// tenant has one.
    /// </summary>
    public static async Task<bool> ReachesKsaAsync(
        ZayraDbContext db, Guid tenantId, string? countryCode, Guid? companyId, CancellationToken ct)
    {
        var cc = (countryCode ?? string.Empty).Trim();
        if (KsaStatutorySpecialLeave.IsKsa(cc)) return true;
        if (cc.Length > 0) return false;

        var companies = ScopedBypass.TenantWide(db.Companies, tenantId,
                "Deciding whether a leave policy reaches a Saudi employee is a property of the employer's "
                + "legal entities, not of the caller's company scope: an HR administrator scoped to one entity "
                + "must not be able to lower a statutory floor that applies to another. Only country codes are "
                + "read; no company data crosses the boundary.")
            .AsNoTracking()
            .Where(c => !c.IsDeleted);
        if (companyId is { } id)
            companies = companies.Where(c => c.Id == id);

        var codes = await companies.Select(c => c.CountryCode).ToListAsync(ct);
        return codes.Any(KsaStatutorySpecialLeave.IsKsa);
    }
}
