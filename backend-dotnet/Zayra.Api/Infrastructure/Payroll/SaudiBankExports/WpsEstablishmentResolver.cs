using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;

namespace Zayra.Api.Infrastructure.Payroll.SaudiBankExports;

/// <summary>
/// The ONE place that decides which establishment id a company's wage file carries: the outbound file (PayrollController) and the
/// returned-file reader both call it, so they cannot disagree. The rules are in <see cref="WpsEstablishmentAuthority"/>. Every
/// query is tenant-pinned and the company's own values are read only for THIS company.
/// </summary>
public static class WpsEstablishmentResolver
{
    public static async Task<(string? Id, SaudiBankExportIssueDto? Conflict)> ResolveAsync(
        ZayraDbContext db, Guid tenantId, Guid? companyId, bool isKsa, CancellationToken ct)
    {
        var rows = await db.GCCComplianceSettings.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.IsActive && (x.CompanyId == null || x.CompanyId == companyId))
            .Select(x => new { x.CompanyId, x.WpsAgentId })
            .ToListAsync(ct);
        var ownAgentId = rows.FirstOrDefault(x => companyId != null && x.CompanyId == companyId && !string.IsNullOrWhiteSpace(x.WpsAgentId))?.WpsAgentId;
        var tenantAgentId = rows.FirstOrDefault(x => x.CompanyId == null && !string.IsNullOrWhiteSpace(x.WpsAgentId))?.WpsAgentId;
        // Decision 10: the company's own MOL establishment number is the authority when it is recorded.
        var companyValue = companyId is Guid id
            ? await db.Companies.AsNoTracking().Where(c => c.TenantId == tenantId && c.Id == id).Select(c => c.WpsEmployerId).FirstOrDefaultAsync(ct)
            : null;
        // A recorded company number is checked only against ids recorded for THIS company. The tenant-wide row is a
        // fallback for companies with no number of their own: in a group every legal entity has its own establishment,
        // so the workspace default must not refuse a sister company's file.
        var agentId = string.IsNullOrWhiteSpace(companyValue) ? ownAgentId ?? tenantAgentId : ownAgentId;
        string? mol = null;
        if (isKsa && companyId is Guid c)
            mol = (await new SaudiBankExportService(db).GetSettingsAsync(tenantId, c, ct)).MolEstablishmentId;
        return WpsEstablishmentAuthority.Resolve(companyValue, mol, agentId, isKsa && companyId is not null);
    }
}
