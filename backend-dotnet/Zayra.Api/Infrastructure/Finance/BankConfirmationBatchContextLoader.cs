using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Finance;

/// <summary>
/// Loads, for a payment batch, what a contextual parser needs: the company's single MOL establishment ID (resolved by the same
/// <see cref="WpsEstablishmentResolver"/> the outbound file uses, never read from the file), each payment record's wage-file
/// government ID (<see cref="WageFileGovernmentId"/>), the batch's creation date and total, and the FILE-REF keys already
/// imported. READ-ONLY, tenant-scoped on every query; the caller has already authorised the batch's company.
/// </summary>
public static class BankConfirmationBatchContextLoader
{
    /// <summary>The audit action the import writes; the FILE-REF key of an accepted import rides in its metadata.</summary>
    public const string ImportAuditAction = "payroll.bank_confirmation.imported";

    private static readonly Regex FileRefKeyPattern = new("\"fileRefKey\":\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static async Task<BankConfirmationBatchContext> LoadAsync(
        ZayraDbContext db, Guid tenantId, PayrollPaymentBatch batch, PayrollRun? run, CancellationToken ct)
    {
        string? establishment = null;
        if (run?.CompanyId is Guid companyId)
            establishment = (await WpsEstablishmentResolver.ResolveAsync(db, tenantId, companyId, isKsa: true, ct)).Id;

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

        var imported = new HashSet<string>(StringComparer.Ordinal);
        var metadata = await db.PayrollAuditLogs.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.Action == ImportAuditAction && a.MetadataJson.Contains("fileRefKey"))
            .Select(a => a.MetadataJson).ToListAsync(ct);
        foreach (var json in metadata)
            foreach (Match m in FileRefKeyPattern.Matches(json))
                imported.Add(System.Text.Json.JsonSerializer.Deserialize<string>("\"" + m.Groups[1].Value + "\"") ?? string.Empty);

        return new BankConfirmationBatchContext(
            establishment, expected,
            BatchCreatedOn: DateOnly.FromDateTime(batch.CreatedAtUtc),
            BatchTotal: expected.Where(l => !l.Cancelled).Sum(l => l.Amount),
            FileRefScope: run?.CompanyId?.ToString("N"),
            ImportedFileRefKeys: imported);
    }
}
