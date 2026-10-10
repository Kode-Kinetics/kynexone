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
        // ONE resolution path with payroll: the company-aware reader. A company is passed only for the
        // keys payroll itself reads per company (StatutoryOverridePolicy.CompanyAppliedKeys), so the
        // value this returns is the value a payslip uses — never an override payroll would ignore.
        var company = StatutoryOverridePolicy.IsAppliedForCompany(ruleKey) ? companyId : null;
        return await _reader.GetDecimalAsync(countryCode, jurisdiction, ruleKey, onDate, tenantId, ct, company);
    }
}

public interface ICompanyRatePolicyResolver
{
    /// <summary>Resolves a non-statutory client rate: company row wins over tenant default; newest
    /// effective row that covers the date wins. Active rows AND Superseded rows count, each only
    /// inside its own effective window — a supersede end-dates the prior row the day before the new
    /// one starts, so a re-run or arrears for an earlier period still resolves the value it had.</summary>
    Task<CompanyRatePolicy?> ResolveAsync(
        Guid tenantId, Guid? companyId, string rateKey, DateOnly onDate, CancellationToken ct = default);
}

public sealed class CompanyRatePolicyResolver : ICompanyRatePolicyResolver
{
    /// <summary>A company rate replaced by a later version. Still resolvable inside its window.</summary>
    public const string SupersededStatus = "Superseded";

    private readonly ZayraDbContext _db;
    public CompanyRatePolicyResolver(ZayraDbContext db) => _db = db;

    public async Task<CompanyRatePolicy?> ResolveAsync(
        Guid tenantId, Guid? companyId, string rateKey, DateOnly onDate, CancellationToken ct = default)
    {
        return await _db.CompanyRatePolicies
            // IgnoreQueryFilters is intentional: system/config read — scope authorised above (or seeder), WHERE re-applies exact tenant+company scope; never reads another tenant.
            .IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.TenantId == tenantId && !p.IsDeleted
                     // Archived-and-not-deleted is how a supersede was recorded before "Superseded"
                     // existed; those rows carry the EffectiveTo the supersede gave them.
                     && (p.Status == CompanyPolicyStatuses.Active || p.Status == SupersededStatus
                         || p.Status == CompanyPolicyStatuses.Archived)
                     && p.RateKey == rateKey
                     && p.EffectiveFrom <= onDate
                     && (p.EffectiveTo == null || p.EffectiveTo >= onDate)
                     && (p.CompanyId == null || (companyId != null && p.CompanyId == companyId)))
            .OrderByDescending(p => p.CompanyId != null)
            .ThenByDescending(p => p.EffectiveFrom)
            .FirstOrDefaultAsync(ct);
    }
}
