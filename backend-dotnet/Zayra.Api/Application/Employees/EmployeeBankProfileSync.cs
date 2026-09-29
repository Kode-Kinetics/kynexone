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

        var profile = await db.EmployeePayrollProfiles
            .FirstOrDefaultAsync(x => x.TenantId == employee.TenantId && x.EmployeeId == employee.Id && !x.IsDeleted, ct);
        if (profile is null) return;

        if (keys.Contains("bankName")) profile.BankName = employee.BankName;
        if (keys.Contains("bankIban")) profile.Iban = employee.BankIban;
        profile.UpdatedAtUtc = DateTime.UtcNow;
    }
}
