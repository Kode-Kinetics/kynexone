using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Employees;

/// <summary>The effective, post-union readiness policy for one (country, nationality) — the set of
/// requirements the evaluator checks, plus provenance and the retained disclaimer.</summary>
public sealed record ResolvedReadinessPolicy(
    string CountryCode,
    string Nationality,
    string Tier,
    IReadOnlyList<ReadinessRequirement> Items,
    IReadOnlyList<string> Sources,
    IReadOnlyList<string> FloorContradictions,
    string Disclaimer);

public interface IEmployeeReadinessPolicyResolver
{
    Task<ResolvedReadinessPolicy> ResolveAsync(
        Guid tenantId, Guid? companyId, string? countryCode, string? nationality, CancellationToken ct = default);
}

/// <summary>
/// Statutory-floor UNION resolver (§3.3) — modelled on StatutoryRateGuard's overlay, NOT the tax
/// replacement-resolver: EffectiveRequirements = CODE floor ∪ tenant-default profile ∪ company profile,
/// strictest-wins. Config may ADD a key or UPGRADE failClosed:false→true; it may NEVER remove a floor
/// key nor downgrade true→false (floor wins + a floor_contradiction is surfaced). The config LAYERS are
/// resolved with the exact CompanyTaxPolicyResolver composition (IgnoreQueryFilters + tenant +
/// (CompanyId==null || ==companyId) + newest EffectiveFrom + Active + date-covering) — IgnoreQueryFilters
/// is load-bearing so the system read sees the CompanyId==null tenant-default row.
/// </summary>
public sealed class EmployeeReadinessPolicyResolver : IEmployeeReadinessPolicyResolver
{
    private readonly ZayraDbContext _db;
    public EmployeeReadinessPolicyResolver(ZayraDbContext db) => _db = db;

    public async Task<ResolvedReadinessPolicy> ResolveAsync(
        Guid tenantId, Guid? companyId, string? countryCode, string? nationality, CancellationToken ct = default)
    {
        var iso2 = (CountryCodeStandard.NormalizeToIso2(countryCode) ?? countryCode ?? string.Empty).Trim().ToUpperInvariant();
        var nat = GccReadinessFloor.NormalizeNationality(nationality);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var sources = new List<string>();
        var contradictions = new List<string>();

        // ── Config layers (tenant default + company), exact tax-resolver composition ──
        // IgnoreQueryFilters is intentional: readiness resolution is a SYSTEM read (gate + nightly
        // sweep run with no user scope) that MUST see the CompanyId==null tenant-default row; the WHERE
        // below re-applies the exact tenant + company/default scope and never reads another tenant.
        var candidates = await _db.CompanyComplianceProfiles
            .IgnoreQueryFilters()
            .Where(p => p.TenantId == tenantId
                     && !p.IsDeleted
                     && p.Status == CompanyPolicyStatuses.Active
                     && p.EffectiveFrom <= today
                     && (p.EffectiveTo == null || p.EffectiveTo >= today)
                     && (p.CompanyId == null || (companyId != null && p.CompanyId == companyId)))
            .ToListAsync(ct);

        var tenantDefault = candidates.Where(p => p.CompanyId == null).OrderByDescending(p => p.EffectiveFrom).FirstOrDefault();
        var companyProfile = companyId != null
            ? candidates.Where(p => p.CompanyId == companyId).OrderByDescending(p => p.EffectiveFrom).FirstOrDefault()
            : null;

        // ── Optional additive GCC-setting layer (now company-scoped, D7) ──
        var gccSetting = await ResolveGccSettingAsync(tenantId, companyId, ct);
        var wpsEnabled = gccSetting?.WpsEnabled ?? false;

        // ── Start with the code floor (always present) ──
        var merged = new Dictionary<string, ReadinessRequirement>(StringComparer.OrdinalIgnoreCase);
        var floor = GccReadinessFloor.Resolve(iso2);
        if (floor.Count > 0) sources.Add("floor");
        foreach (var item in floor)
            Union(merged, ApplyConditions(item, nat, wpsEnabled), contradictions);

        // ── Layer tenant default, then company (config: add or upgrade only) ──
        if (tenantDefault is not null)
        {
            sources.Add("tenant");
            foreach (var item in ParseProfile(tenantDefault.RequiredFieldsJson, "tenant"))
                Union(merged, ApplyConditions(item, nat, wpsEnabled), contradictions);
        }
        if (companyProfile is not null)
        {
            sources.Add("company");
            foreach (var item in ParseProfile(companyProfile.RequiredFieldsJson, "company"))
                Union(merged, ApplyConditions(item, nat, wpsEnabled), contradictions);
        }

        // ── Additive GCC-setting toggles (never the floor guarantee) ──
        if (gccSetting is not null)
        {
            var added = false;
            void AddToggle(ReadinessRequirement r) { Union(merged, ApplyConditions(r, nat, wpsEnabled), contradictions); added = true; }
            if (gccSetting.IqamaRequired)
                AddToggle(new ReadinessRequirement("IqamaNumber", "identity", true, "activate", "gcc-setting", iso2, null, new AppliesWhen(NationalityNot: iso2)));
            if (gccSetting.EmiratesIdRequired)
                AddToggle(new ReadinessRequirement("EmiratesId", "identity", true, "activate", "gcc-setting", iso2));
            if (gccSetting.WpsEnabled)
                AddToggle(new ReadinessRequirement("BankIban", "payroll", true, "activate", "gcc-setting", iso2));
            if (gccSetting.VisaTrackingEnabled)
                AddToggle(new ReadinessRequirement("VisaExpiryDate", "identity", false, "pay", "gcc-setting", iso2));
            if (added) sources.Add("gcc-setting");
        }

        var items = merged.Values
            .OrderByDescending(i => i.FailClosed)
            .ThenBy(i => i.Gate == "activate" ? 0 : 1)
            .ThenBy(i => i.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ResolvedReadinessPolicy(
            iso2, nat, CountryTier.TierLabel(CountryTier.GetTier(iso2)),
            items, sources, contradictions, GccReadinessFloor.Disclaimer);
    }

    /// <summary>Apply nationality/WPS conditionality and the BankIban pay→activate upgrade. Returns
    /// null when the requirement does not apply to this employee.</summary>
    private static ReadinessRequirement? ApplyConditions(ReadinessRequirement r, string nationality, bool wpsEnabled)
    {
        var when = r.AppliesWhen;
        if (when is not null)
        {
            if (when.Nationality is { } natEq && !string.Equals(GccReadinessFloor.NormalizeNationality(natEq), nationality, StringComparison.OrdinalIgnoreCase))
                return null;
            if (when.NationalityNot is { } natNot && string.Equals(GccReadinessFloor.NormalizeNationality(natNot), nationality, StringComparison.OrdinalIgnoreCase))
                return null;
            if (when.WpsEnabled is { } needWps && needWps != wpsEnabled)
                return null;
        }
        // Floor IBAN: pay-gated by default, upgrades to activate-gated when WPS is enabled.
        if (wpsEnabled && string.Equals(r.Key, "BankIban", StringComparison.OrdinalIgnoreCase) && r.Gate == "pay")
            r = r with { Gate = "activate", FailClosed = true };
        return r with { AppliesWhen = null };
    }

    /// <summary>Strictest-wins merge. Config may add or upgrade; it may never downgrade a floor key —
    /// the floor value wins and a contradiction is recorded.</summary>
    private static void Union(Dictionary<string, ReadinessRequirement> merged, ReadinessRequirement? incomingOrNull, List<string> contradictions)
    {
        if (incomingOrNull is not ReadinessRequirement incoming) return;
        if (!merged.TryGetValue(incoming.Key, out var existing)) { merged[incoming.Key] = incoming; return; }

        // Floor contradiction: a non-floor layer trying to weaken a floor key.
        if (existing.Source == "floor" && incoming.Source != "floor" && existing.FailClosed && !incoming.FailClosed)
        {
            contradictions.Add($"{incoming.Key}:{incoming.Source}");
            // keep the floor value; may still tighten the gate below
        }

        var failClosed = existing.FailClosed || incoming.FailClosed;
        var gate = (existing.Gate == "activate" || incoming.Gate == "activate") ? "activate" : "pay";
        var requireVerified = (existing.RequireVerified ?? false) || (incoming.RequireVerified ?? false);
        // Source provenance: prefer floor as the authoritative source label when present.
        var source = existing.Source == "floor" || incoming.Source == "floor" ? "floor"
            : existing.Source;
        merged[incoming.Key] = existing with
        {
            FailClosed = failClosed,
            Gate = gate,
            RequireVerified = requireVerified ? true : existing.RequireVerified,
            Source = source,
        };
    }

    private async Task<GCCComplianceSetting?> ResolveGccSettingAsync(Guid tenantId, Guid? companyId, CancellationToken ct)
    {
        // IgnoreQueryFilters is intentional: same SYSTEM-read rationale as the profile query above —
        // the additive GCC-setting layer must see the tenant-default (CompanyId==null) row; explicit
        // tenant + company/default scope is re-applied in the WHERE.
        var rows = await _db.GCCComplianceSettings
            .IgnoreQueryFilters()
            .Where(g => g.TenantId == tenantId && (g.CompanyId == null || (companyId != null && g.CompanyId == companyId)))
            .ToListAsync(ct);
        return rows.FirstOrDefault(g => companyId != null && g.CompanyId == companyId)
            ?? rows.FirstOrDefault(g => g.CompanyId == null);
    }

    /// <summary>Parses a profile's RequiredFieldsJson into requirements. Backward compatible:
    /// "field" is an alias for "key"; a bare item still parses. Unknown keys are KEPT (the evaluator
    /// fails them CLOSED) — the write-side validator is what prevents them being saved.</summary>
    public static IReadOnlyList<ReadinessRequirement> ParseProfile(string? json, string source)
    {
        var results = new List<ReadinessRequirement>();
        if (string.IsNullOrWhiteSpace(json)) return results;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return results;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var key = GetStr(item, "key") ?? GetStr(item, "field");
                if (string.IsNullOrWhiteSpace(key)) continue;
                var category = GetStr(item, "category") ?? "identity";
                var failClosed = item.TryGetProperty("failClosed", out var fc) && fc.ValueKind == JsonValueKind.True;
                var gate = GetStr(item, "gate") ?? "activate";
                var requireVerified = item.TryGetProperty("requireVerified", out var rv) && rv.ValueKind == JsonValueKind.True;
                AppliesWhen? when = null;
                if (item.TryGetProperty("appliesWhen", out var aw) && aw.ValueKind == JsonValueKind.Object)
                {
                    when = new AppliesWhen(
                        GetStr(aw, "nationality"),
                        GetStr(aw, "nationalityNot"),
                        aw.TryGetProperty("wpsEnabled", out var we) && we.ValueKind == JsonValueKind.True ? true : (bool?)null);
                }
                results.Add(new ReadinessRequirement(key.Trim(), category, failClosed, gate, source,
                    RequireVerified: requireVerified ? true : null, AppliesWhen: when));
            }
        }
        catch { /* malformed profile JSON — validated on write; contribute nothing rather than crash */ }
        return results;
    }

    private static string? GetStr(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
