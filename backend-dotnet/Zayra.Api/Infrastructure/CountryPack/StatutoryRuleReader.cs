using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.CountryPack;

public sealed class StatutoryRuleReader : IStatutoryRuleReader
{
    private readonly ZayraDbContext _db;

    // Per-request memo (the reader is AddScoped → one instance per request = one payroll run).
    // A payroll run resolves the same statutory rule with identical arguments 5-6× per employee;
    // without this, a 500-employee KSA run fires ~2,500-3,000 identical uncached FirstOrDefault
    // queries and times out. Keyed on the full argument tuple so distinct rules never collide;
    // caching the null-miss too preserves the exact fallback-default behaviour. Values are
    // byte-identical to the un-memoized query — only repeat lookups are served from the dict.
    private readonly Dictionary<(string cc, string jur, string ruleKey, DateOnly eff, Guid? tenantId), string?> _memo = new();
    // Company overrides memoised separately and keyed WITH the company, so one entity's override can
    // never be served to another entity (or to a company-less read) from the cache.
    private readonly Dictionary<(string cc, string jur, string ruleKey, DateOnly eff, Guid tenantId, Guid companyId), string?> _companyMemo = new();

    public StatutoryRuleReader(ZayraDbContext db) => _db = db;

    public Task<decimal?> GetDecimalAsync(
        string countryCode, string jurisdiction, string ruleKey,
        DateOnly effectiveDate, Guid? tenantId = null, CancellationToken ct = default)
        => GetDecimalAsync(countryCode, jurisdiction, ruleKey, effectiveDate, tenantId, ct, companyId: null);

    public Task<string?> GetStringAsync(
        string countryCode, string jurisdiction, string ruleKey,
        DateOnly effectiveDate, Guid? tenantId = null, CancellationToken ct = default)
        => GetStringAsync(countryCode, jurisdiction, ruleKey, effectiveDate, tenantId, ct, companyId: null);

    public async Task<decimal?> GetDecimalAsync(
        string countryCode, string jurisdiction, string ruleKey,
        DateOnly effectiveDate, Guid? tenantId, CancellationToken ct, Guid? companyId)
    {
        var raw = await ResolveAsync(countryCode, jurisdiction, ruleKey, effectiveDate, tenantId, companyId, ct);
        if (raw is null) return null;
        return decimal.TryParse(raw, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    public Task<string?> GetStringAsync(
        string countryCode, string jurisdiction, string ruleKey,
        DateOnly effectiveDate, Guid? tenantId, CancellationToken ct, Guid? companyId)
        => ResolveAsync(countryCode, jurisdiction, ruleKey, effectiveDate, tenantId, companyId, ct);

    private async Task<string?> ResolveAsync(
        string countryCode, string jurisdiction, string ruleKey,
        DateOnly effectiveDate, Guid? tenantId, Guid? companyId, CancellationToken ct)
    {
        // Callers pass a company's own country ("SA", no jurisdiction); rules are keyed "SAU"/"KSA-mainland".
        (countryCode, jurisdiction) = StatutoryPackKey.Normalize(countryCode, jurisdiction);
        if (companyId is { } cid && tenantId is { } tid
            && await CompanyOverrideAsync(countryCode, jurisdiction, ruleKey, effectiveDate, tid, cid, ct) is { } overrideValue)
            return overrideValue;
        return await FetchAsync(countryCode, jurisdiction, ruleKey, effectiveDate, tenantId, ct);
    }

    /// <summary>
    /// The Active, date-covered override THIS legal entity holds for the rule, or null. An override
    /// that weakens a statutory floor against the platform value on that date is ignored — such a row
    /// can only predate the write-time floor check, and paying it would underpay an employee.
    /// </summary>
    private async Task<string?> CompanyOverrideAsync(
        string countryCode, string jurisdiction, string ruleKey,
        DateOnly effectiveDate, Guid tenantId, Guid companyId, CancellationToken ct)
    {
        var key = (countryCode, jurisdiction, ruleKey, effectiveDate, tenantId, companyId);
        if (_companyMemo.TryGetValue(key, out var cached)) return cached;

        var value = await ScopedBypass.TenantWide(_db.CompanyStatutoryOverrides, tenantId,
                "Payroll statutory read for one legal entity: the run's company is pinned in the predicate below.")
            .AsNoTracking()
            .Where(o => o.CompanyId == companyId && !o.IsDeleted
                     && o.Status == CompanyPolicyStatuses.Active
                     && o.DataType != StatutoryOverridePolicy.RetireRequest
                     && o.CountryCode == countryCode && o.Jurisdiction == jurisdiction && o.RuleKey == ruleKey
                     && o.EffectiveFrom <= effectiveDate
                     && (o.EffectiveTo == null || o.EffectiveTo >= effectiveDate))
            .OrderByDescending(o => o.EffectiveFrom)
            .Select(o => o.OverrideValue)
            .FirstOrDefaultAsync(ct);

        if (value is not null)
        {
            var platformRaw = await FetchAsync(countryCode, jurisdiction, ruleKey, effectiveDate, null, ct);
            decimal? platform = decimal.TryParse(platformRaw, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : null;
            if (StatutoryOverridePolicy.ViolatesDirection(ruleKey, value, platform)) value = null;
        }

        _companyMemo[key] = value;
        return value;
    }

    /// <inheritdoc />
    public async Task PrimeAsync(string countryCode, string jurisdiction, DateOnly effectiveDate, Guid? tenantId, CancellationToken ct = default)
    {
        (countryCode, jurisdiction) = StatutoryPackKey.Normalize(countryCode, jurisdiction);
        var cutoff = effectiveDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        // Same predicate and ranking as FetchAsync, for every key of the pack at once.
        var rows = await Covering(countryCode, jurisdiction, cutoff, tenantId)
            .Select(r => new { r.RuleKey, IsTenant = r.TenantId != null, r.EffectiveFrom, r.RuleValue })
            .ToListAsync(ct);
        foreach (var g in rows.GroupBy(r => r.RuleKey))
        {
            var memoKey = (countryCode, jurisdiction, g.Key, effectiveDate, tenantId);
            if (_memo.ContainsKey(memoKey)) continue;
            _memo[memoKey] = g.OrderByDescending(r => r.IsTenant).ThenByDescending(r => r.EffectiveFrom).First().RuleValue;
        }
    }

    private async Task<string?> FetchAsync(
        string countryCode, string jurisdiction, string ruleKey,
        DateOnly effectiveDate, Guid? tenantId, CancellationToken ct)
    {
        var memoKey = (countryCode, jurisdiction, ruleKey, effectiveDate, tenantId);
        if (_memo.TryGetValue(memoKey, out var cached)) return cached;

        var cutoff = effectiveDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var row = await Covering(countryCode, jurisdiction, cutoff, tenantId)
            .Where(r => r.RuleKey == ruleKey)
            // Tenant-specific rows rank above platform defaults; newest effective date wins.
            .OrderByDescending(r => r.TenantId != null)
            .ThenByDescending(r => r.EffectiveFrom)
            .Select(r => r.RuleValue)
            .FirstOrDefaultAsync(ct);

        _memo[memoKey] = row;
        return row;
    }

    /// <summary>The pack's rules in force on <paramref name="cutoff"/>, platform rows plus (when a tenant is
    /// given) that tenant's own. Shared by the per-key read and <see cref="PrimeAsync"/> so both rank the
    /// same rows.</summary>
    private IQueryable<StatutoryRule> Covering(string countryCode, string jurisdiction, DateTime cutoff, Guid? tenantId)
        // IgnoreQueryFilters is intentional: StatutoryRule rows span two scopes — platform defaults
        // (TenantId = null, set by StatutoryRuleSeeder) and tenant overrides (TenantId = tenantId).
        // The EF global query filter excludes TenantId=null rows entirely, so bypassing it is
        // required to reach the platform defaults. The WHERE clause below re-applies the correct
        // per-call scope restriction (tenantId == null ? platform-only : tenant-or-platform fallback).
        // SYSTEM CONTEXT: tenant scope intentionally bypassed — reads platform-default rates AND
        // the calling tenant's overrides; never reads another tenant's rows (see WHERE predicate).
        => _db.StatutoryRules
            .IgnoreQueryFilters()
            .Where(r => r.CountryCode == countryCode
                     && r.Jurisdiction == jurisdiction
                     && r.EffectiveFrom <= cutoff
                     && (r.EffectiveTo == null || r.EffectiveTo > cutoff)
                     && (tenantId == null
                         ? r.TenantId == null
                         : r.TenantId == tenantId || r.TenantId == null));
}
