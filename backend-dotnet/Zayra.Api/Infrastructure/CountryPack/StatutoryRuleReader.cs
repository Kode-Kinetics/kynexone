using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Data;

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

    public StatutoryRuleReader(ZayraDbContext db) => _db = db;

    public async Task<decimal?> GetDecimalAsync(
        string countryCode, string jurisdiction, string ruleKey,
        DateOnly effectiveDate, Guid? tenantId = null, CancellationToken ct = default)
    {
        var raw = await FetchAsync(countryCode, jurisdiction, ruleKey, effectiveDate, tenantId, ct);
        if (raw is null) return null;
        return decimal.TryParse(raw, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    public async Task<string?> GetStringAsync(
        string countryCode, string jurisdiction, string ruleKey,
        DateOnly effectiveDate, Guid? tenantId = null, CancellationToken ct = default)
        => await FetchAsync(countryCode, jurisdiction, ruleKey, effectiveDate, tenantId, ct);

    private async Task<string?> FetchAsync(
        string countryCode, string jurisdiction, string ruleKey,
        DateOnly effectiveDate, Guid? tenantId, CancellationToken ct)
    {
        var memoKey = (countryCode, jurisdiction, ruleKey, effectiveDate, tenantId);
        if (_memo.TryGetValue(memoKey, out var cached)) return cached;

        var cutoff = effectiveDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        // IgnoreQueryFilters is intentional: StatutoryRule rows span two scopes — platform defaults
        // (TenantId = null, set by StatutoryRuleSeeder) and tenant overrides (TenantId = tenantId).
        // The EF global query filter excludes TenantId=null rows entirely, so bypassing it is
        // required to reach the platform defaults. The WHERE clause below re-applies the correct
        // per-call scope restriction (tenantId == null ? platform-only : tenant-or-platform fallback).
        // SYSTEM CONTEXT: tenant scope intentionally bypassed — reads platform-default rates AND
        // the calling tenant's overrides; never reads another tenant's rows (see WHERE predicate).
        var row = await _db.StatutoryRules
            .IgnoreQueryFilters()
            .Where(r => r.CountryCode == countryCode
                     && r.Jurisdiction == jurisdiction
                     && r.RuleKey == ruleKey
                     && r.EffectiveFrom <= cutoff
                     && (r.EffectiveTo == null || r.EffectiveTo > cutoff)
                     && (tenantId == null
                         ? r.TenantId == null
                         : r.TenantId == tenantId || r.TenantId == null))
            // Tenant-specific rows rank above platform defaults; newest effective date wins.
            .OrderByDescending(r => r.TenantId != null)
            .ThenByDescending(r => r.EffectiveFrom)
            .Select(r => r.RuleValue)
            .FirstOrDefaultAsync(ct);

        _memo[memoKey] = row;
        return row;
    }
}
