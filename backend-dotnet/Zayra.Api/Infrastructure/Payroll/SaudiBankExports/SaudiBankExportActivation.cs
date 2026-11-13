using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll.SaudiBankExports;

/// <summary>
/// Decides whether the Saudi bank-instruction export is open for a legal entity.
///
/// <para><b>Saudi companies are ON by default.</b> A company whose country is SA/SAU in the caller's own tenant has the export
/// with nothing configured: Saudi clients need the file, and Setup already collects its settings. Three things can close it, and
/// each is operator- or platform-owned (no tenant-admin setting, header, query parameter, wildcard or client flag can open or
/// close anything):</para>
/// <list type="number">
///   <item>The platform team's per-tenant switch <see cref="FeatureKeys.SaudiBankExport"/>: an explicit row with IsEnabled = false
///   (the tenant's Features tab). It is final — it also overrides the allow-list below. An absent row, or IsEnabled = true, changes
///   nothing.</item>
///   <item>The operator's global kill switch: <c>SaudiBankExportActivation:Enabled</c> set to anything other than <c>true</c>
///   (unset or blank leaves the default alone). It closes every tenant.</item>
///   <item>The company is not Saudi (and is not on the allow-list).</item>
/// </list>
/// <para>The operator allow-list (<c>Enabled=true</c> plus an exact tenant/company pair under <c>Companies</c>) still opens a
/// company that is not Saudi; it is the pre-existing mechanism, kept so nothing already switched on goes dark.</para>
/// </summary>
public static class SaudiBankExportActivation
{
    public const string Section = "SaudiBankExportActivation";

    // ── Operator allow-list (synchronous, configuration only) ─────────────────────────────────

    public static IReadOnlyList<Guid> EnabledCompanies(IConfiguration? configuration, Guid tenantId)
    {
        if (tenantId == Guid.Empty || configuration is null
            || !bool.TryParse(configuration[$"{Section}:Enabled"], out var enabled) || !enabled)
            return Array.Empty<Guid>();
        return configuration.GetSection($"{Section}:Companies").GetChildren()
            .Where(x => Guid.TryParse(x["TenantId"], out var t) && t == tenantId)
            .Select(x => Guid.TryParse(x["CompanyId"], out var c) ? c : Guid.Empty)
            .Where(c => c != Guid.Empty).Distinct().ToArray();
    }

    public static bool IsEnabled(IConfiguration? configuration, Guid tenantId, Guid companyId) =>
        companyId != Guid.Empty && EnabledCompanies(configuration, tenantId).Contains(companyId);

    /// <summary>True when the operator has explicitly set the global switch to anything but <c>true</c>. Unset or blank is not a kill.</summary>
    public static bool IsKilledByOperator(IConfiguration? configuration)
    {
        var value = configuration?[$"{Section}:Enabled"];
        return !string.IsNullOrWhiteSpace(value) && !(bool.TryParse(value, out var enabled) && enabled);
    }

    // ── The decision (default ON for a Saudi company) ─────────────────────────────────────────

    public static async Task<bool> IsEnabledAsync(
        ZayraDbContext db, IConfiguration? configuration, Guid tenantId, Guid companyId, CancellationToken ct)
    {
        if (tenantId == Guid.Empty || companyId == Guid.Empty || IsKilledByOperator(configuration)) return false;
        if (await IsSwitchedOffByPlatformAsync(db, tenantId, ct)) return false;
        if (IsEnabled(configuration, tenantId, companyId)) return true;
        return await LiveSaudiCompanies(db, tenantId).AnyAsync(c => c.Id == companyId, ct);
    }

    /// <summary>Every company of the tenant that has the export open: its Saudi companies plus the operator allow-list.</summary>
    public static async Task<IReadOnlyList<Guid>> EnabledCompanyIdsAsync(
        ZayraDbContext db, IConfiguration? configuration, Guid tenantId, CancellationToken ct)
    {
        if (tenantId == Guid.Empty || IsKilledByOperator(configuration) || await IsSwitchedOffByPlatformAsync(db, tenantId, ct))
            return Array.Empty<Guid>();
        var saudi = await LiveSaudiCompanies(db, tenantId).Select(c => c.Id).ToListAsync(ct);
        return saudi.Concat(EnabledCompanies(configuration, tenantId)).Distinct().ToArray();
    }

    // A flag row exists only when the platform wrote one; IsEnabled = false is the kill switch, anything else is "default".
    private static Task<bool> IsSwitchedOffByPlatformAsync(ZayraDbContext db, Guid tenantId, CancellationToken ct) =>
        db.TenantFeatureFlags.AsNoTracking()
            .AnyAsync(f => f.TenantId == tenantId && f.FeatureKey == FeatureKeys.SaudiBankExport && !f.IsEnabled, ct);

    // A live Saudi company of the tenant (SA or SAU, any case: the two codes every other KSA check accepts). Inline so EF translates it.
    private static IQueryable<Company> LiveSaudiCompanies(ZayraDbContext db, Guid tenantId) =>
        db.Companies.AsNoTracking().Where(c => c.TenantId == tenantId && !c.IsDeleted
            && c.CountryCode != null && (c.CountryCode.ToUpper() == "SA" || c.CountryCode.ToUpper() == "SAU"));
}

public sealed record SaudiBankExportAvailabilityDto(bool Enabled);
