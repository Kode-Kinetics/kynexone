using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;

namespace Zayra.Api.Infrastructure.Boot;

/// <summary>
/// READ-ONLY boot audit for the tenants and legal entities that have NO country.
///
/// <para><b>Why it does not fix anything.</b> Tenants created before the home jurisdiction was
/// required (testclaude, evostel) hold <c>Company.CountryCode = ""</c>. A country guessed from the
/// billing currency, the slug or the locale would seed the wrong labour law in silence — the failure
/// mode is worse than the gap. So this audit WRITES NOTHING. It makes the gap visible and names the
/// fix, and the tenant/platform administrator states the country.</para>
///
/// <para><b>Where an operator sees it.</b> Three surfaces, all from the one
/// <see cref="HomeJurisdiction"/> message: this startup warning (for the platform team), the
/// Setup → Companies banner (for the tenant admin), and the Add Employee modal, which used to say
/// only "Select the employing company and nationality to see the required identity documents" and
/// never mentioned the company's country at all.</para>
/// </summary>
public static class MissingCountryAudit
{
    /// <summary>One tenant or company with no usable country, plus the message that names the fix.</summary>
    public readonly record struct Finding(
        Guid TenantId, string TenantName, Guid? CompanyId, string Subject, string Message);

    public readonly record struct AuditSummary(
        int TenantsVisited, IReadOnlyList<Finding> Findings)
    {
        public int TenantsMissingCountry => Findings.Count(f => f.CompanyId is null);
        public int CompaniesMissingCountry => Findings.Count(f => f.CompanyId is not null);
    }

    public static async Task<AuditSummary> RunAsync(
        ZayraDbContext db, ILogger logger, CancellationToken ct = default)
    {
        var tenants = await db.Tenants.AsNoTracking()
            .Where(t => t.IsActive)
            .Select(t => new { t.Id, t.Name })
            .ToListAsync(ct);

        // A boot audit runs with NO request principal, so the ambient COMPANY filter resolves to an
        // empty scope and would hide every row this exists to find. ScopedBypass.TenantWide drops
        // exactly that one dimension and re-applies the tenant predicate itself, so the sweep stays
        // tenant-by-tenant and can never read across tenants. It only ever READS.
        const string why =
            "A startup audit has no HTTP principal, so the company read filter resolves to an EMPTY "
            + "company scope and would hide the very companies and settings rows whose missing country "
            + "this reports. The tenant filter is re-applied by the helper, one tenant at a time, and "
            + "nothing is written.";

        var findings = new List<Finding>();
        foreach (var tenant in tenants)
        {
            var home = await ScopedBypass.TenantWide(db.TenantLocalizationSettings, tenant.Id, why)
                .AsNoTracking()
                .Select(l => l.CountryCode)
                .FirstOrDefaultAsync(ct);

            if (HomeJurisdiction.IsMissing(home))
                findings.Add(new Finding(
                    tenant.Id, tenant.Name, null, tenant.Name, HomeJurisdiction.TenantMessage(tenant.Name)));

            var companies = await ScopedBypass.TenantWide(db.Companies, tenant.Id, why)
                .AsNoTracking()
                .Where(c => !c.IsDeleted)
                .Select(c => new { c.Id, c.LegalNameEn, c.TradeName, c.CountryCode })
                .ToListAsync(ct);

            foreach (var company in companies)
            {
                if (!HomeJurisdiction.IsMissing(company.CountryCode)) continue;
                var label = string.IsNullOrWhiteSpace(company.LegalNameEn)
                    ? (string.IsNullOrWhiteSpace(company.TradeName) ? "Unnamed company" : company.TradeName)
                    : company.LegalNameEn;
                findings.Add(new Finding(
                    tenant.Id, tenant.Name, company.Id, label, HomeJurisdiction.CompanyMessage(label)));
            }
        }

        foreach (var finding in findings)
            logger.LogWarning(
                "MissingCountryAudit: tenant {TenantId} ({TenantName}){CompanySuffix} — {Message}",
                finding.TenantId, finding.TenantName,
                finding.CompanyId is Guid cid ? $", company {cid}" : string.Empty,
                finding.Message);

        if (findings.Count == 0)
            logger.LogInformation(
                "MissingCountryAudit: {Tenants} tenant(s) checked, every tenant and company has a country.",
                tenants.Count);
        else
            logger.LogWarning(
                "MissingCountryAudit: {Tenants} tenant(s) checked — {TenantsMissing} tenant(s) and "
                + "{CompaniesMissing} company(ies) have NO country. Statutory rules, leave entitlements "
                + "and identity documents cannot be resolved for them, and employees cannot be added. "
                + "Nothing was guessed or written; set each one in {FixLocation}.",
                tenants.Count, findings.Count(f => f.CompanyId is null),
                findings.Count(f => f.CompanyId is not null), HomeJurisdiction.CompanyFixLocation);

        return new AuditSummary(tenants.Count, findings);
    }
}
