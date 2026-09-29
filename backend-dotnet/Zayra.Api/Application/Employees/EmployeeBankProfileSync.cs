using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Application.Employees;

/// <summary>
/// Keeps the dual-home bank columns coherent (Δ13 / consultant P1-1). The authority for bank details is
/// the <see cref="EmployeePayrollProfile"/> — the payroll-run / WPS export reads <c>PP.Iban</c> /
/// <c>PP.BankName</c> directly, NOT the Employee scalar. But the readiness-checklist fast-fix and the
/// sensitive-change apply paths (EmployeesController.ApplyChanges, ApprovalWorkflowService) write only
/// <c>employee.BankIban</c> / <c>employee.BankName</c>. Without this sync the corrected IBAN would land on
/// the Employee scalar while <c>PP.Iban</c> stayed blank, so the employee could never actually be paid.
///
/// After such an apply, this copies the APPROVED bank scalar(s) onto the EXISTING payroll-profile row so
/// the two homes agree. No row is created when none exists (the readiness snapshot's blank-as-unset
/// fallback already reads the Employee scalar in that case, and a WPS run requires a profile row regardless).
///
/// FIELD-SCOPED. Only the key(s) in the applied change set are copied. This used to copy BOTH columns
/// whenever EITHER changed, so an IBAN-only approval overwrote the profile's BankName — the column the
/// WPS/SIF file pays from — with whatever the Employee display scalar happened to hold, which for an
/// imported employee is often blank or stale. An approval changes exactly what the approver saw.
/// </summary>
public static class EmployeeBankProfileSync
{
    private static readonly string[] BankChangeKeys = { "bankIban", "bankName" };

    /// <summary>True when the applied change set touched a dual-home bank column.</summary>
    public static bool TouchesBankColumns(IEnumerable<string> changedKeys)
        => changedKeys.Any(k => BankChangeKeys.Contains(k, StringComparer.OrdinalIgnoreCase));

    /// <summary>Mirror the APPROVED bank scalar(s) — and only those — onto the employee's existing payroll
    /// profile (if any). Does not SaveChanges — the caller persists as part of its own unit of work.</summary>
    public static async Task SyncAsync(ZayraDbContext db, Employee employee, IEnumerable<string> changedKeys, CancellationToken ct)
    {
        if (employee.TenantId is null || employee.Id == 0) return;
        var keys = changedKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!TouchesBankColumns(keys)) return;

        // The tracker first: the same approval may have just created the profile (EmployeeChangeApplier
        // .ApplyPayrollProfileAsync, e.g. a routing code approved with the IBAN), and a query cannot see an
        // unsaved row.
        var profile = db.EmployeePayrollProfiles.Local
                          .FirstOrDefault(x => x.TenantId == employee.TenantId && x.EmployeeId == employee.Id && !x.IsDeleted)
                      ?? await db.EmployeePayrollProfiles
                          .FirstOrDefaultAsync(x => x.TenantId == employee.TenantId && x.EmployeeId == employee.Id && !x.IsDeleted, ct);
        if (profile is null) return;

        if (keys.Contains("bankName")) profile.BankName = employee.BankName;
        if (keys.Contains("bankIban"))
        {
            var previousIban = profile.Iban;
            profile.Iban = employee.BankIban;
            ClearStaleRoutingAndAccount(db, employee, profile, previousIban, keys);
        }
        profile.UpdatedAtUtc = DateTime.UtcNow;
    }

    /// <summary>History event recorded when an approved IBAN moved the employee to another bank and the old bank's
    /// routing code was cleared. EmployeeReadinessEvaluator reads it to PAY-gate the employee until a routing code
    /// for the new bank is approved (the WPS/SIF line carries the routing code, read live from the profile).</summary>
    public const string RoutingCodeClearedEventType = "BankRoutingCodeCleared";

    /// <summary>History event recorded when only the stale account number was cleared (the export does not read it).</summary>
    public const string AccountNumberClearedEventType = "BankAccountNumberCleared";

    /// <summary>
    /// The routing code and account number belong to the account the OLD IBAN named. They used to survive an
    /// approved IBAN change untouched, so a move to another bank sent every later wage line with the new IBAN and
    /// the OLD bank's routing code. When the approved IBAN is at a different bank (the bank code inside the IBAN
    /// differs), a routing code not approved in the same change is cleared, and the employee is pay-gated until
    /// one is; the old account number is cleared with it. A new account at the same bank changes only the IBAN. There is no bank-code→routing
    /// table in the product, so the routing code is never derived — only cleared and asked for.
    /// </summary>
    private static void ClearStaleRoutingAndAccount(ZayraDbContext db, Employee employee, EmployeePayrollProfile profile,
        string? previousIban, IReadOnlySet<string> keys)
    {
        var before = Compact(previousIban);
        var after = Compact(employee.BankIban);
        if (before.Length == 0 || string.Equals(before, after, StringComparison.OrdinalIgnoreCase)) return;
        var movedBank = BankIdentifier(before) is { } oldBank && BankIdentifier(after) is { } newBank
                        && !string.Equals(oldBank, newBank, StringComparison.OrdinalIgnoreCase);
        // A new account at the SAME bank keeps the bank's routing code; only the approved IBAN changes.
        if (!movedBank) return;

        var cleared = new List<string>();
        if (!keys.Contains("bankRoutingCode") && !string.IsNullOrWhiteSpace(profile.BankRoutingCode))
        {
            profile.BankRoutingCode = string.Empty;
            cleared.Add("bankRoutingCode");
        }
        if (!keys.Contains("accountNumber") && !string.IsNullOrWhiteSpace(profile.AccountNumber))
        {
            profile.AccountNumber = string.Empty;
            cleared.Add("accountNumber");
        }
        if (cleared.Count == 0) return;

        db.EmployeeHistories.Add(new EmployeeHistory
        {
            TenantId = employee.TenantId!.Value,
            EmployeeId = employee.Id,
            EventType = cleared.Contains("bankRoutingCode") ? RoutingCodeClearedEventType : AccountNumberClearedEventType,
            FieldName = string.Join(',', cleared),
            EffectiveDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Reason = "The approved IBAN is at a different bank: the old bank's routing code and account number no longer apply. "
                     + "Submit the new bank's routing code for approval before the next wage file.",
            SnapshotJson = "{}",
        });
    }

    private static string Compact(string? value) => (value ?? string.Empty).Replace(" ", string.Empty).Trim();

    /// <summary>
    /// The bank named inside an IBAN: its country plus the bank code at the start of the BBAN, whose length is
    /// fixed by that country's IBAN format (SA 2, AE 3, OM 3, QA 4, KW 4, BH 4). Other countries use 4, which can
    /// only over-report a change (clearing a routing code that was still valid), never miss one. Null when the
    /// value is too short to be an IBAN.
    /// </summary>
    public static string? BankIdentifier(string? iban)
    {
        var s = Compact(iban).ToUpperInvariant();
        if (s.Length < 5) return null;
        var country = s[..2];
        var length = country switch { "SA" => 2, "AE" => 3, "OM" => 3, "QA" or "KW" or "BH" => 4, _ => 4 };
        return s.Length < 4 + length ? null : $"{country}:{s.Substring(4, length)}";
    }
}
