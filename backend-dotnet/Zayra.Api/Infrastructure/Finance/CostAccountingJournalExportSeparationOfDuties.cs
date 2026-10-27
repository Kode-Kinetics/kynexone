using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;

namespace Zayra.Api.Infrastructure.Finance;

/// <summary>
/// AM-22: the production <see cref="IJournalExportSeparationOfDuties"/>, over WP-B's <see cref="CostAccountingSod"/>.
/// <para>A person is barred when they changed the company's costing set-up since its previous lock (ERP API keys cannot create
/// exports, so only a signed-in person is ever checked). The rule applies only while the company's
/// costing switch is on (AM-26): <see cref="CostAccountingSod.HasConflictAsync"/> answers false for a company that is off or
/// unknown. When the export names no company (a group export) every company of the tenant is checked, because the file would
/// carry them all; a run-only request resolves its company and month from the run. When the period cannot be determined the
/// check is made over the company's whole history, the strictest reading, so an unreadable scope never relaxes the rule.</para>
/// </summary>
public sealed class CostAccountingJournalExportSeparationOfDuties(ZayraDbContext db) : IJournalExportSeparationOfDuties
{
    public async Task<bool> IsBarredAsync(JournalExportSodRequest request, CancellationToken ct)
    {
        var companyIds = new List<Guid>();
        var period = request.Period;

        if (request.PayrollRunId is { } runId)
        {
            var run = await ScopedBypass.TenantWide(db.PayrollRuns, request.TenantId, "SoD resolves the run an export is for, regardless of the caller's company claims.")
                .AsNoTracking().Where(r => r.Id == runId).Select(r => new { r.CompanyId, r.Year, r.Month }).FirstOrDefaultAsync(ct);
            if (run is not null)
            {
                if (run.CompanyId is { } rc) companyIds.Add(rc);
                // The run decides the period the file is built for; a client value never narrows the review window.
                period = $"{run.Year:0000}-{run.Month:00}";
            }
        }
        if (request.CompanyId is { } c) companyIds.Add(c);
        if (companyIds.Count == 0)
            companyIds = await ScopedBypass.TenantWide(db.Companies, request.TenantId, "A tenant-wide export is checked against every company of the tenant.")
                .AsNoTracking().Select(x => x.Id).ToListAsync(ct);

        foreach (var company in companyIds.Distinct())
            if (await CostAccountingSod.HasConflictAsync(db, request.TenantId, company, period ?? string.Empty, request.ActorUserId, ct))
                return true;
        return false;
    }
}
