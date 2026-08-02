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
/// After such an apply, this copies the Employee bank scalars onto the EXISTING payroll-profile row so the
/// two homes agree. No row is created when none exists (the readiness snapshot's blank-as-unset fallback
/// already reads the Employee scalar in that case, and a WPS run requires a profile row regardless).
/// </summary>
public static class EmployeeBankProfileSync
{
    private static readonly string[] BankChangeKeys = { "bankIban", "bankName" };

    /// <summary>True when the applied change set touched a dual-home bank column.</summary>
    public static bool TouchesBankColumns(IEnumerable<string> changedKeys)
        => changedKeys.Any(k => BankChangeKeys.Contains(k, StringComparer.OrdinalIgnoreCase));

    /// <summary>Mirror the employee's bank scalars onto its existing payroll profile (if any). Does not
    /// SaveChanges — the caller persists as part of its own unit of work.</summary>
    public static async Task SyncAsync(ZayraDbContext db, Employee employee, IEnumerable<string> changedKeys, CancellationToken ct)
    {
        if (employee.TenantId is null || employee.Id == 0) return;
        if (!TouchesBankColumns(changedKeys)) return;

        var profile = await db.EmployeePayrollProfiles
            .FirstOrDefaultAsync(x => x.TenantId == employee.TenantId && x.EmployeeId == employee.Id && !x.IsDeleted, ct);
        if (profile is null) return;

        profile.BankName = employee.BankName;
        profile.Iban = employee.BankIban;
        profile.UpdatedAtUtc = DateTime.UtcNow;
    }
}
