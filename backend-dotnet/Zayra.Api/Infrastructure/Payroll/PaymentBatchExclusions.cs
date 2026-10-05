using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>One payslip deliberately left out of a bank payment batch, and why.</summary>
public sealed record PaymentBatchExclusion(int EmployeeId, string EmployeeCode, decimal Amount, string ReasonCode, string Reason);

/// <summary>
/// Which locked payslips a BANK payment batch (and so its bank/WPS file) leaves out, rather than
/// blocking on: employees paid in cash or by cheque, and payslips with nothing to pay.
///
/// <para>The exclusions are decided ONCE, when the batch is created, and recorded in that batch's
/// sealed <c>payroll.payment_batch.created</c> audit entry (existing JSON, no new column). Everything
/// downstream — batch-total reconciliation, the "slip missing from batch" check, the bank file and the
/// API — reads that record back instead of re-deciding, so a later profile edit cannot silently move
/// money in or out of a frozen batch. Batch total = run net − Σ excluded amounts.</para>
/// </summary>
public static class PaymentBatchExclusions
{
    public const string CreatedAuditAction = "payroll.payment_batch.created";

    public const string ZeroNetCode = "zero_net";
    public const string PaidOutsideBankFileCode = "paid_outside_bank_file";

    private static readonly HashSet<string> NonBankMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cash", "Cheque", "Check",
    };

    /// <summary>True when the payroll profile's payment method is cash or cheque. Blank, BankTransfer
    /// and WPS (and any value we do not know) stay in the bank file, where missing bank details still
    /// block — an unknown method is never silently dropped from a wage file.</summary>
    public static bool IsPaidOutsideBankFile(string? paymentMethod) =>
        !string.IsNullOrWhiteSpace(paymentMethod) && NonBankMethods.Contains(paymentMethod.Trim());

    /// <summary>The exclusion for one payslip, or null when it belongs in the bank batch.</summary>
    public static PaymentBatchExclusion? Decide(PayrollSlip slip, EmployeePayrollProfile? profile)
    {
        if (slip.NetSalary <= 0m)
            return new(slip.EmployeeId, slip.EmployeeCode, slip.NetSalary, ZeroNetCode,
                "Net pay is zero, so there is nothing to send to the bank.");
        if (IsPaidOutsideBankFile(profile?.PaymentMethod))
            return new(slip.EmployeeId, slip.EmployeeCode, slip.NetSalary, PaidOutsideBankFileCode,
                $"Paid by {profile!.PaymentMethod.Trim().ToLowerInvariant()}, outside the bank file. Pay and record this salary separately.");
        return null;
    }

    /// <summary>The audit payload shape. Property names are fixed: <see cref="LoadAsync"/> reads them back.</summary>
    public static object ToAuditData(IReadOnlyList<PaymentBatchExclusion> exclusions) =>
        exclusions.Select(e => new
        {
            employeeId = e.EmployeeId, employeeCode = e.EmployeeCode, amount = e.Amount,
            reasonCode = e.ReasonCode, reason = e.Reason,
        }).ToList();

    /// <summary>
    /// The exclusions recorded when each batch was created. A batch created before exclusions existed
    /// has none recorded, which is exactly how it was built (every slip got a record).
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, IReadOnlyList<PaymentBatchExclusion>>> LoadAsync(
        ZayraDbContext db, Guid tenantId, IReadOnlyCollection<Guid> batchIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, IReadOnlyList<PaymentBatchExclusion>>();
        if (batchIds.Count == 0) return result;
        var keys = batchIds.Select(b => b.ToString()).ToList();
        var rows = await db.PayrollAuditLogs.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.Action == CreatedAuditAction && keys.Contains(a.EntityId))
            .Select(a => new { a.EntityId, a.MetadataJson })
            .ToListAsync(ct);
        foreach (var row in rows)
        {
            if (!Guid.TryParse(row.EntityId, out var batchId)) continue;
            result[batchId] = Parse(row.MetadataJson);
        }
        return result;
    }

    public static async Task<IReadOnlyList<PaymentBatchExclusion>> LoadAsync(
        ZayraDbContext db, Guid tenantId, Guid batchId, CancellationToken ct) =>
        (await LoadAsync(db, tenantId, new[] { batchId }, ct)).GetValueOrDefault(batchId)
        ?? Array.Empty<PaymentBatchExclusion>();

    private static IReadOnlyList<PaymentBatchExclusion> Parse(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return Array.Empty<PaymentBatchExclusion>();
        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("exclusions", out var list) || list.ValueKind != JsonValueKind.Array)
                return Array.Empty<PaymentBatchExclusion>();
            return list.EnumerateArray().Select(e => new PaymentBatchExclusion(
                e.GetProperty("employeeId").GetInt32(),
                e.GetProperty("employeeCode").GetString() ?? string.Empty,
                e.GetProperty("amount").GetDecimal(),
                e.GetProperty("reasonCode").GetString() ?? string.Empty,
                e.GetProperty("reason").GetString() ?? string.Empty)).ToList();
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return Array.Empty<PaymentBatchExclusion>();
        }
    }
}
