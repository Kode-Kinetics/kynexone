using Microsoft.Extensions.Configuration;

namespace Zayra.Api.Infrastructure.Payroll.SaudiBankExports;

/// <summary>Operator-owned release switch. Missing/malformed configuration is OFF. Both a global
/// switch and an exact tenant/company allow-list entry are required; no tenant-admin setting,
/// header, query parameter, wildcard or client flag can activate exports.</summary>
public static class SaudiBankExportActivation
{
    public const string Section = "SaudiBankExportActivation";
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
}

public sealed record SaudiBankExportAvailabilityDto(bool Enabled);
