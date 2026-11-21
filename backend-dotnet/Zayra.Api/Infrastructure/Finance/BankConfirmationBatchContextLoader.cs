using System.Text.Json;
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
/// imported for OTHER payment batches. READ-ONLY, tenant-scoped on every query; the caller has already authorised the batch's company.
///
/// <para>FILE-REF memory (interim). The key of an accepted import rides in the import's audit metadata, whose entity is the
/// batch. <c>metadata_json</c> is a PostgreSQL <c>json</c> column, which has no LIKE: the SQL filters only on tenant, action
/// and entity, and the key is read from the JSON in memory. A key remembered for THIS batch is not a refusal (the bank's later
/// status file for the same batch carries the same reference); a key remembered for another batch is. Two concurrent imports
/// are NOT serialised here: that needs a unique store (see the hand-back).</para>
/// </summary>
public static class BankConfirmationBatchContextLoader
{
    /// <summary>The audit action the import writes; the FILE-REF key of an accepted import rides in its metadata.</summary>
    public const string ImportAuditAction = "payroll.bank_confirmation.imported";

    /// <summary>The audit entity the import writes for a payment batch; its EntityId is the batch id.</summary>
    public const string ImportAuditEntity = "PayrollPaymentBatch";

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

        // No text search in SQL: json has no LIKE on PostgreSQL. Tenant, action and entity in SQL; the key from the JSON in memory.
        var thisBatch = batch.Id.ToString();
        var priorImports = await db.PayrollAuditLogs.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.Action == ImportAuditAction && a.EntityName == ImportAuditEntity)
            .Select(a => new { a.EntityId, a.MetadataJson }).ToListAsync(ct);
        var imported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prior in priorImports)
            if (!string.Equals(prior.EntityId, thisBatch, StringComparison.OrdinalIgnoreCase) && FileRefKey(prior.MetadataJson) is { } key)
                imported.Add(key);

        return new BankConfirmationBatchContext(
            establishment, expected,
            BatchCreatedOn: DateOnly.FromDateTime(batch.CreatedAtUtc),
            BatchTotal: expected.Where(l => !l.Cancelled).Sum(l => l.Amount),
            FileRefScope: run?.CompanyId?.ToString("N"),
            ImportedFileRefKeys: imported);
    }

    /// <summary>The remembered FILE-REF key in an import's audit metadata, or null when it has none or is not readable JSON.</summary>
    private static string? FileRefKey(string? metadataJson)
    {
        if (string.IsNullOrEmpty(metadataJson) || !metadataJson.Contains("fileRefKey", StringComparison.Ordinal)) return null;
        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("fileRefKey", out var key) && key.ValueKind == JsonValueKind.String
                ? key.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
