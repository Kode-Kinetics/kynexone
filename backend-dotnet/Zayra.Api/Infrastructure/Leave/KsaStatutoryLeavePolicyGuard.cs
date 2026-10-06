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
/// The write-path floor for KSA statutory special leave (Arts. 113, 114, 151, 160). A leave policy
/// that reaches Saudi employees may grant MORE than the statute for maternity, marriage, bereavement,
/// birth, Hajj or iddah leave — never less, never unpaid, and for maternity and iddah never counted
/// in working days.
///
/// <para>Shape follows the existing statutory floors: the figure is read from the platform
/// <c>statutory_rules</c> row (tenant null, so a tenant row cannot lower it) with a compiled
/// fallback, and a configuration below it is refused with the citation rather than stored.</para>
///
/// <para>A policy that also reaches non-Saudi staff is never made to carry the Saudi figure: the
/// administrator is asked for a separate Saudi-scoped policy instead, which outranks the neutral
/// one for Saudi employees (<c>LeaveService.PolicySpecificity</c>).</para>
/// </summary>
public static class KsaStatutoryLeavePolicyGuard
{
    /// <summary>The violations of the statutory floor, in plain words, or an empty list when the
    /// policy is lawful, is not for a statutory leave type, or does not reach a Saudi employee.</summary>
    public static async Task<IReadOnlyList<string>> CheckAsync(
        ZayraDbContext db, Guid tenantId,
        Guid? policyId, Guid? leaveTypeId, string? typeCode, string? typeNameEn, string? typeCategory,
        string? policyCountryCode, Guid? policyCompanyId, string? policyStatus,
        decimal entitlementDays, decimal maxPerRequest, string? payrollImpact,
        bool weekendsIncluded, bool publicHolidaysIncluded,
        CancellationToken ct)
    {
        // An archived policy governs nothing, so there is nothing to protect.
        if (IsArchived(policyStatus)) return Array.Empty<string>();

        var kind = KsaStatutorySpecialLeave.Classify(typeCode, typeNameEn, typeCategory);
        if (kind is null) return Array.Empty<string>();
        var reach = await ReachAsync(db, tenantId, policyCountryCode, policyCompanyId, ct);
        if (reach == KsaPolicyReach.None) return Array.Empty<string>();

        var floor = await KsaStatutorySpecialLeave.ResolveFloorAsync(
            new StatutoryRuleReader(db), kind.Value, DateOnly.FromDateTime(DateTime.UtcNow), ct);
        if (floor is null) return Array.Empty<string>();

        var violations = KsaStatutorySpecialLeave.PolicyViolations(
            kind.Value, floor.Value, entitlementDays, maxPerRequest, payrollImpact,
            weekendsIncluded && publicHolidaysIncluded);
        if (violations.Count == 0 || reach == KsaPolicyReach.SaudiOnly) return violations;

        // Mixed: the figure may be right for the other countries. It is lawful as long as Saudi
        // employees resolve a compliant Saudi-scoped policy of their own, which outranks this one.
        if (leaveTypeId is { } typeId
            && await HasCompliantSaudiPolicyAsync(db, tenantId, typeId, policyId, kind.Value, floor.Value, ct))
            return Array.Empty<string>();

        var what = KsaStatutorySpecialLeave.Describe(kind.Value);
        return new[]
        {
            "This policy has no country or company, and you have employees in Saudi Arabia and elsewhere, so it would also govern "
            + $"Saudi employees, for whom: {string.Join(" ", violations)} Do not change this policy for the other countries — "
            + $"create a separate Saudi policy (country SA) for {what.ToLowerInvariant()} at {floor.Value:0.##} days"
            + (KsaStatutorySpecialLeave.IsCalendarSpan(kind.Value) ? " counted on the calendar" : string.Empty)
            + ", then save this one.",
        };
    }

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

    private static async Task<bool> HasCompliantSaudiPolicyAsync(
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
