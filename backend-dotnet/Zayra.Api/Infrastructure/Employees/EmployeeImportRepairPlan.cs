using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Employees;

/// <summary>
/// What an EXISTING employee's import row may still fill in. Shared by the import preview ("WillRepair") and
/// the commit (the repair path), so the dry run and the commit make the same decision per row.
///
/// <para>WHY THERE IS A REPAIR PATH. Before the import ran in one transaction, a failure after the first save
/// left the people committed without their payroll profiles, salary structures or reporting lines. Re-running
/// the same file then skipped every row as "already exists", so the missing payroll data could never be
/// imported again. A row whose EmployeeCode already exists is now matched to that employee and may fill ONLY
/// what is missing — a blank profile column, an absent profile, an absent active salary structure, an unset
/// manager/supervisor, an unknown joining date. It never overwrites a value that is already there, so a
/// retry of the same file is safe: the second run finds nothing missing and reports the rows as skipped.</para>
/// </summary>
public sealed class ImportRepairLookups
{
    public required Dictionary<int, EmployeePayrollProfile> ProfilesByEmployee { get; init; }
    public required HashSet<int> ActiveSalaryEmployeeIds { get; init; }

    /// <param name="track">True on the commit path (profiles are filled in place); false for the preview.</param>
    public static async Task<ImportRepairLookups> LoadAsync(ZayraDbContext db, Guid tenantId, bool track, CancellationToken ct)
    {
        var profiles = db.EmployeePayrollProfiles.Where(p => p.TenantId == tenantId && !p.IsDeleted);
        if (!track) profiles = profiles.AsNoTracking();
        return new ImportRepairLookups
        {
            ProfilesByEmployee = (await profiles.ToListAsync(ct))
                .GroupBy(p => p.EmployeeId)
                .ToDictionary(g => g.Key, g => g.First()),
            ActiveSalaryEmployeeIds = (await db.EmployeeSalaryStructures.AsNoTracking()
                .Where(s => s.TenantId == tenantId && s.IsActive)
                .Select(s => s.EmployeeId)
                .ToListAsync(ct)).ToHashSet(),
        };
    }
}

public static class EmployeeImportRepairPlan
{
    /// <summary>The payroll-profile CSV columns, in the order the import reads them.</summary>
    public static readonly IReadOnlyList<string> PayrollProfileColumns = new[]
    {
        "IBAN", "BankName", "AccountNumber", "BankRoutingCode", "MolId", "PayrollGroup", "PaymentMethod",
        "SocialInsuranceReference", "SalaryStructureCode", "Currency",
    };

    /// <summary>True when this row can fill at least one thing the existing employee is missing.</summary>
    /// <param name="salaryAssignable">The row's salary parses, has a positive basic and a gross above zero.</param>
    /// <param name="rowJoiningDate">The row's joining date when the cell was supplied AND readable; else null.</param>
    public static bool NeedsRepair(Employee emp, IReadOnlyDictionary<string, string> row, ImportRepairLookups lookups,
        bool salaryAssignable, DateTime? rowJoiningDate)
    {
        string Cell(string column) => row.TryGetValue(column, out var v) ? v.Trim() : string.Empty;
        bool Missing(string? existing, string column) => string.IsNullOrWhiteSpace(existing) && Cell(column).Length > 0;

        lookups.ProfilesByEmployee.TryGetValue(emp.Id, out var profile);
        var payroll = profile is null
            ? PayrollProfileColumns.Any(c => Cell(c).Length > 0)
            : Missing(profile.Iban, "IBAN") || Missing(profile.BankName, "BankName")
              || Missing(profile.AccountNumber, "AccountNumber") || Missing(profile.BankRoutingCode, "BankRoutingCode")
              || Missing(profile.MolId, "MolId") || Missing(profile.PayrollGroup, "PayrollGroup")
              || Missing(profile.SocialInsuranceReference, "SocialInsuranceReference")
              || Missing(profile.SalaryStructureReference, "SalaryStructureCode");
        var joiningDate = emp.JoiningDate == default && rowJoiningDate is not null;
        // A salary structure takes its effective date from the joining date, so it can only be recovered once
        // the joining date is known (already, or from this same row).
        var salary = salaryAssignable && !lookups.ActiveSalaryEmployeeIds.Contains(emp.Id)
                     && (emp.JoiningDate != default || rowJoiningDate is not null);
        var hierarchy =
            ((Cell("ManagerEmployeeCode").Length > 0 || Cell("ManagerEmail").Length > 0) && emp.ManagerEmployeeId is null)
            || ((Cell("SupervisorEmployeeCode").Length > 0 || Cell("SupervisorEmail").Length > 0) && emp.SupervisorEmployeeId is null);
        var bankMirror = profile is not null
            && ((string.IsNullOrWhiteSpace(emp.BankIban) && !string.IsNullOrWhiteSpace(profile.Iban))
                || (string.IsNullOrWhiteSpace(emp.BankName) && !string.IsNullOrWhiteSpace(profile.BankName)));
        return payroll || joiningDate || salary || hierarchy || bankMirror;
    }
}
