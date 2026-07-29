using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

public interface IStatutoryRateResolver
{
    /// <summary>
    /// Resolves the effective statutory rate for a legal entity on a date. Precedence:
    /// company override (Active, date-covered CompanyStatutoryOverride) → tenant override
    /// (StatutoryRule TenantId=tenant) → seeded platform default (StatutoryRule TenantId=null).
    /// The seeded platform default remains the authoritative fallback (the compliance boundary):
    /// a company override can only be applied for its own entity and never redefines the default.
    /// </summary>
    Task<decimal?> ResolveDecimalAsync(
        Guid tenantId, Guid? companyId, string countryCode, string jurisdiction, string ruleKey,
        DateOnly onDate, CancellationToken ct = default);
}

public sealed class StatutoryRateResolver : IStatutoryRateResolver
{
    private readonly ZayraDbContext _db;
    private readonly IStatutoryRuleReader _reader;

    public StatutoryRateResolver(ZayraDbContext db, IStatutoryRuleReader reader)
    {
        _db = db;
        _reader = reader;
    }

    public async Task<decimal?> ResolveDecimalAsync(
        Guid tenantId, Guid? companyId, string countryCode, string jurisdiction, string ruleKey,
        DateOnly onDate, CancellationToken ct = default)
    {
        if (companyId is not null)
        {
            // IgnoreQueryFilters + explicit tenant/company scope (system read), mirroring
            // CompanyTaxPolicyResolver. Only Active, date-covered overrides for THIS company count.
            var overrideRow = await _db.CompanyStatutoryOverrides
                .IgnoreQueryFilters().AsNoTracking()
                .Where(o => o.TenantId == tenantId && !o.IsDeleted
                         && o.CompanyId == companyId
                         && o.Status == CompanyPolicyStatuses.Active
                         && o.CountryCode == countryCode && o.Jurisdiction == jurisdiction && o.RuleKey == ruleKey
                         && o.EffectiveFrom <= onDate
                         && (o.EffectiveTo == null || o.EffectiveTo >= onDate))
                .OrderByDescending(o => o.EffectiveFrom)
                .Select(o => o.OverrideValue)
                .FirstOrDefaultAsync(ct);
            if (overrideRow is not null && decimal.TryParse(overrideRow,
                System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v))
                return v;
        }

        // Fall back to the tenant override / platform default via the untouched reader.
        return await _reader.GetDecimalAsync(countryCode, jurisdiction, ruleKey, onDate, tenantId, ct);
    }
}

public interface ICompanyRatePolicyResolver
{
    /// <summary>Resolves a non-statutory client rate: company row wins over tenant default; newest
    /// effective row that covers the date wins. Only Active, non-deleted rows.</summary>
    Task<CompanyRatePolicy?> ResolveAsync(
        Guid tenantId, Guid? companyId, string rateKey, DateOnly onDate, CancellationToken ct = default);
}

public sealed class CompanyRatePolicyResolver : ICompanyRatePolicyResolver
{
    private readonly ZayraDbContext _db;
    public CompanyRatePolicyResolver(ZayraDbContext db) => _db = db;

    public async Task<CompanyRatePolicy?> ResolveAsync(
        Guid tenantId, Guid? companyId, string rateKey, DateOnly onDate, CancellationToken ct = default)
    {
        return await _db.CompanyRatePolicies
            // IgnoreQueryFilters is intentional: system/config read — scope authorised above (or seeder), WHERE re-applies exact tenant+company scope; never reads another tenant.
            .IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.TenantId == tenantId && !p.IsDeleted
                     && p.Status == CompanyPolicyStatuses.Active
                     && p.RateKey == rateKey
                     && p.EffectiveFrom <= onDate
                     && (p.EffectiveTo == null || p.EffectiveTo >= onDate)
                     && (p.CompanyId == null || (companyId != null && p.CompanyId == companyId)))
            .OrderByDescending(p => p.CompanyId != null)
            .ThenByDescending(p => p.EffectiveFrom)
            .FirstOrDefaultAsync(ct);
    }
}
