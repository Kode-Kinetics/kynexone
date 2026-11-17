using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Finance;

/// <summary>
/// Loads, for a payment batch, what a contextual parser needs: the company's single MOL establishment ID (the company
/// value is the authority; <see cref="WpsEstablishmentAuthority"/>, never the file) and each payment record's wage-file
/// government ID (<see cref="WageFileGovernmentId"/>, the same resolution the bank file and WPS register use).
/// READ-ONLY, tenant-scoped on every query; the caller has already authorised the batch's company.
/// </summary>
public static class BankConfirmationBatchContextLoader
{
    public static async Task<BankConfirmationBatchContext> LoadAsync(
        ZayraDbContext db, Guid tenantId, PayrollPaymentBatch batch, PayrollRun? run, CancellationToken ct)
    {
        string? establishment = null;
        if (run?.CompanyId is Guid companyId)
        {
            var company = await db.Companies.AsNoTracking()
                .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == companyId && !c.IsDeleted, ct);
            if (company is not null)
            {
                var settings = await new SaudiBankExportService(db).GetSettingsAsync(tenantId, companyId, ct);
                // A conflict between recorded establishment IDs leaves the establishment unknown: the file is then refused.
                establishment = WpsEstablishmentAuthority.Resolve(company.WpsEmployerId, settings.MolEstablishmentId, null, isKsa: true).Id;
            }
        }

        var records = await db.PayrollPaymentRecords.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.PaymentBatchId == batch.Id).ToListAsync(ct);
        var ids = records.Select(r => r.EmployeeId).Distinct().ToList();
        var employees = await db.Employees.AsNoTracking() // ambient tenant, company and soft-delete filters stay on
            .Where(e => e.TenantId == tenantId && ids.Contains(e.Id)).ToListAsync(ct);
        var borderIds = run is null
            ? new Dictionary<int, BorderNumberRule.WageFileBorderId>()
            : await BorderNumberRule.ResolveWageFileIdsAsync(db, tenantId, employees, BorderNumberRule.PeriodEnd(run.Year, run.Month), ct);
        var byId = employees.ToDictionary(e => e.Id);

        var expected = records.Select(r =>
        {
            byId.TryGetValue(r.EmployeeId, out var emp);
            var molId = emp is null ? null : WageFileGovernmentId.Resolve(emp, borderIds).Value;
            return new WpsExpectedLine(r.EmployeeId, emp?.EmployeeCode ?? string.Empty, molId, r.Iban, r.Amount,
                Cancelled: PaymentRecordStatuses.IsCancelled(r.Status));
        }).ToList();

        return new BankConfirmationBatchContext(establishment, expected);
    }
}
