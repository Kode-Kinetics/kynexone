using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Governance;

public interface ICompanyTaxPolicyResolver
{
    /// <summary>
    /// Resolves the effective tax policy for a legal entity on a given date.
    /// Precedence mirrors StatutoryRule: company-specific row (CompanyId = companyId)
    /// beats the tenant default row (CompanyId = null); among candidates the newest
    /// EffectiveFrom that covers the date wins. Only Active, non-deleted policies count.
    /// Returns null when nothing is configured — callers fall back to legacy behavior
    /// (tenant SystemSettings) until the Phase 3 payroll cutover.
    /// </summary>
    Task<CompanyTaxPolicy?> ResolveAsync(Guid tenantId, Guid? companyId, DateOnly onDate, CancellationToken ct = default);
}

public sealed class CompanyTaxPolicyResolver : ICompanyTaxPolicyResolver
{
    private readonly ZayraDbContext _db;
    public CompanyTaxPolicyResolver(ZayraDbContext db) => _db = db;

    public async Task<CompanyTaxPolicy?> ResolveAsync(Guid tenantId, Guid? companyId, DateOnly onDate, CancellationToken ct = default)
    {
        // IgnoreQueryFilters is intentional: payroll resolution is a SYSTEM read that must
        // see both the company row and the tenant-default row (CompanyId = null) regardless
        // of the caller's company claims; the WHERE below re-applies the exact tenant +
        // company/default scope and never reads another tenant's rows.
        return await _db.CompanyTaxPolicies
            .IgnoreQueryFilters()
            .Where(p => p.TenantId == tenantId
                     && !p.IsDeleted
                     && p.Status == CompanyPolicyStatuses.Active
                     && p.EffectiveFrom <= onDate
                     && (p.EffectiveTo == null || p.EffectiveTo >= onDate)
                     && (p.CompanyId == null || (companyId != null && p.CompanyId == companyId)))
            // Company-specific rows rank above tenant defaults; newest effective date wins.
            .OrderByDescending(p => p.CompanyId != null)
            .ThenByDescending(p => p.EffectiveFrom)
            .FirstOrDefaultAsync(ct);
    }
}
