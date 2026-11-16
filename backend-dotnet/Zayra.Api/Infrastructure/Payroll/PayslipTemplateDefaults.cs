using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// What a NEW Saudi company's payslip looks like before anyone designs one: a bilingual (English and Arabic) default template,
/// because a Saudi workforce reads both. The payslip PDF takes its language from the tenant's default template
/// (<see cref="PayslipBrandingConfig.Locale"/>); with no template it is English only.
///
/// <para>Insert-if-absent and never an update: a tenant that already has a default template keeps it exactly as it is, so no
/// existing company's payslip changes. It runs only where a Saudi company or tenant is CREATED (tenant provisioning and company
/// creation), never as a boot backfill. The template is an ordinary editable row, switchable to English in the template designer.</para>
/// </summary>
public static class PayslipTemplateDefaults
{
    public const string BilingualLocale = "bilingual";
    public const string DefaultTemplateName = "Standard payslip (English and Arabic)";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private const string ReadJustification =
        "Whether the tenant already has a default payslip template, so a new Saudi company never adds a second one. Seeding and company " +
        "creation may run with no tenant principal, where the tenant filter would hide the tenant's own rows; the tenant is re-applied.";

    public static bool IsSaudi(string? countryCode) =>
        !string.IsNullOrWhiteSpace(countryCode)
        && (string.Equals(countryCode.Trim(), "SA", StringComparison.OrdinalIgnoreCase)
            || string.Equals(countryCode.Trim(), "SAU", StringComparison.OrdinalIgnoreCase));

    private const string NewTenantJustification =
        "Whether the tenant already runs a company or has payroll history, so the bilingual payslip default is only ever added for a tenant that is new. " +
        "Company creation may run with no tenant principal, where the tenant filter would hide the tenant's own rows; the tenant is re-applied.";

    /// <summary>The company-creation form of <see cref="EnsureSaudiBilingualDefaultAsync"/>: adds the bilingual default only when the
    /// tenant is still new, meaning it has no company yet (the one being created is not saved at this point) and no payroll run. A tenant
    /// that already operates keeps the payslip it has, whatever company it adds. Tenant provisioning calls
    /// <see cref="EnsureSaudiBilingualDefaultAsync"/> directly.</summary>
    public static async Task<bool> EnsureSaudiBilingualDefaultForNewCompanyAsync(ZayraDbContext db, Guid tenantId, string? countryCode, CancellationToken ct)
    {
        if (tenantId == Guid.Empty || !IsSaudi(countryCode)) return false;
        // Companies already saved or pending in this unit of work, other than the one being created, make the tenant an existing one.
        var pendingCompanies = db.Companies.Local.Count(c => c.TenantId == tenantId && !c.IsDeleted);
        if (pendingCompanies > 1) return false;
        if (await ScopedBypass.TenantWide(db.Companies, tenantId, NewTenantJustification).AnyAsync(ct)) return false;
        if (await ScopedBypass.TenantWide(db.PayrollRuns, tenantId, NewTenantJustification).AnyAsync(ct)) return false;
        return await EnsureSaudiBilingualDefaultAsync(db, tenantId, countryCode, ct);
    }

    /// <summary>Adds the bilingual default template to the unit of work when the country is Saudi and the tenant has no default
    /// template. The caller saves. Returns whether a template was added.</summary>
    public static async Task<bool> EnsureSaudiBilingualDefaultAsync(ZayraDbContext db, Guid tenantId, string? countryCode, CancellationToken ct)
    {
        if (tenantId == Guid.Empty || !IsSaudi(countryCode)) return false;
        if (db.PayslipTemplates.Local.Any(t => t.TenantId == tenantId && t.IsDefault && !t.IsDeleted)) return false;
        var exists = await ScopedBypass.TenantWide(db.PayslipTemplates, tenantId, ReadJustification)
            .AnyAsync(t => t.IsDefault && !t.IsDeleted, ct);
        if (exists) return false;

        db.PayslipTemplates.Add(new PayslipTemplate
        {
            TenantId = tenantId,
            Name = DefaultTemplateName,
            IsDefault = true,
            Version = 1,
            Status = "active",
            BrandingJson = JsonSerializer.Serialize(new PayslipBrandingConfig(Locale: BilingualLocale), Web),
            LayoutJson = JsonSerializer.Serialize(new PayslipLayoutConfig(Locale: BilingualLocale), Web),
        });
        return true;
    }
}
