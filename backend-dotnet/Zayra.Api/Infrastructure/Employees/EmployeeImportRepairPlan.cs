using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Employees;

/// <summary>
/// What an EXISTING employee's import row may still fill in. Shared by the import preview ("WillRepair") and
/// the commit (the repair path), so the dry run and the commit make the same decision per row.
///
/// <para>WHY THERE IS A REPAIR PATH. Before the import ran in one transaction, a failure after the first save
/// left the people committed without their payroll profiles or reporting lines, and re-running the same file
/// skipped every row as "already exists". A row whose EmployeeCode already exists is therefore matched to that
/// employee and may fill ONLY what is missing, and only what is NOT approval-gated: a blank payroll group,
/// salary-structure reference or currency (creating the profile if there is none), an unset manager/supervisor,
/// an unknown joining date. It never overwrites a value that is already there, so a retry of the same file is
/// safe: the second run finds nothing missing and reports the rows as skipped.</para>
///
/// <para>NEVER AN APPROVAL-GATED VALUE. Bank name, IBAN, account number, routing code, MOL ID, payment method,
/// social-insurance and GOSI references and salary change on an existing employee only through an approved
/// change request (maker-checker). The repair path used to "fill blanks" in exactly those columns, so a user
/// who may not even REQUEST a bank change (employees.write without employees.sensitive) could set an existing
/// employee's paying account — a terminated employee's included — by uploading a CSV. Those values are now
/// never written for an existing employee; <see cref="ApprovalGatedValuesNotApplied"/> names them so the
/// import says which ones it left alone. A NEW employee's bank details are initial data entry and follow the
/// create rule (POST /api/employees takes them with employees.write).</para>
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
    /// <summary>The payroll-profile CSV columns a repair row may fill when blank — none of them approval-gated.</summary>
    public static readonly IReadOnlyList<string> RepairablePayrollColumns = new[] { "PayrollGroup", "SalaryStructureCode", "Currency" };

    /// <summary>Payroll-profile CSV columns that change an existing employee only through approval.</summary>
    public static readonly IReadOnlyList<string> ApprovalGatedPayrollColumns = new[]
    {
        "IBAN", "BankName", "AccountNumber", "BankRoutingCode", "MolId", "PaymentMethod", "SocialInsuranceReference",
    };

    /// <summary>Salary cells: an existing employee's salary changes only through Payroll, never an import.</summary>
    private static readonly string[] SalaryColumns =
    {
        "BasicSalary", "HousingAllowance", "TransportAllowance", "FoodAllowance", "MobileAllowance", "OtherAllowance", "FixedDeduction",
    };

    /// <summary>
    /// Separated employees are never repaired: the import does not touch a Terminated / Offboarded / Archived /
    /// Exited / Inactive record at all. A final settlement pays whatever that record holds.
    /// </summary>
    public static bool IsSeparated(Employee emp) => SeparatedStatuses.Contains(emp.Status ?? string.Empty);

    private static readonly HashSet<string> SeparatedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        EmployeeStatuses.Terminated, EmployeeStatuses.Offboarded, EmployeeStatuses.Archived,
        EmployeeStatuses.Exited, EmployeeStatuses.Inactive,
    };

    /// <summary>True when this row can fill at least one NON-sensitive thing the existing employee is missing.</summary>
    /// <param name="rowJoiningDate">The row's joining date when the cell was supplied AND readable; else null.</param>
    public static bool NeedsRepair(Employee emp, IReadOnlyDictionary<string, string> row, ImportRepairLookups lookups, DateTime? rowJoiningDate)
    {
        if (IsSeparated(emp)) return false;
        string Cell(string column) => row.TryGetValue(column, out var v) ? v.Trim() : string.Empty;
        bool Missing(string? existing, string column) => string.IsNullOrWhiteSpace(existing) && Cell(column).Length > 0;

        lookups.ProfilesByEmployee.TryGetValue(emp.Id, out var profile);
        var payroll = profile is null
            ? RepairablePayrollColumns.Any(c => Cell(c).Length > 0)
            : Missing(profile.PayrollGroup, "PayrollGroup")
              || Missing(profile.SalaryStructureReference, "SalaryStructureCode")
              || Missing(profile.SalaryCurrency, "Currency");
        var joiningDate = emp.JoiningDate == default && rowJoiningDate is not null;
        var hierarchy =
            ((Cell("ManagerEmployeeCode").Length > 0 || Cell("ManagerEmail").Length > 0) && emp.ManagerEmployeeId is null)
            || ((Cell("SupervisorEmployeeCode").Length > 0 || Cell("SupervisorEmail").Length > 0) && emp.SupervisorEmployeeId is null);
        return payroll || joiningDate || hierarchy;
    }

    /// <summary>
    /// The approval-gated values this row carries for an EXISTING employee that differ from what the employee
    /// holds — each one left unapplied and reported, never written. Empty cells and equal values are not
    /// reported. Salary is reported only when the employee has no active salary structure (otherwise the file
    /// is merely restating, or disputing, a salary Payroll owns).
    /// </summary>
    public static IReadOnlyList<string> ApprovalGatedValuesNotApplied(Employee emp, IReadOnlyDictionary<string, string> row, ImportRepairLookups lookups)
    {
        string Cell(string column) => row.TryGetValue(column, out var v) ? v.Trim() : string.Empty;
        static string Norm(string? s, bool iban) => iban ? (s ?? string.Empty).Replace(" ", string.Empty).Trim() : (s ?? string.Empty).Trim();
        lookups.ProfilesByEmployee.TryGetValue(emp.Id, out var profile);
        string Stored(string column) => column switch
        {
            "IBAN" => string.IsNullOrWhiteSpace(profile?.Iban) ? emp.BankIban ?? string.Empty : profile!.Iban,
            "BankName" => string.IsNullOrWhiteSpace(profile?.BankName) ? emp.BankName ?? string.Empty : profile!.BankName,
            "AccountNumber" => profile?.AccountNumber ?? string.Empty,
            "BankRoutingCode" => profile?.BankRoutingCode ?? string.Empty,
            "MolId" => profile?.MolId ?? string.Empty,
            // A profile's payment method is never blank; with no profile, the default a new one would get.
            "PaymentMethod" => profile?.PaymentMethod ?? "BankTransfer",
            "SocialInsuranceReference" => profile?.SocialInsuranceReference ?? string.Empty,
            "GosiReference" => emp.GosiReference ?? string.Empty,
            _ => string.Empty,
        };

        var notApplied = new List<string>();
        foreach (var column in ApprovalGatedPayrollColumns.Append("GosiReference"))
        {
            var value = Cell(column);
            if (value.Length == 0) continue;
            var iban = column == "IBAN";
            if (!string.Equals(Norm(value, iban), Norm(Stored(column), iban), StringComparison.OrdinalIgnoreCase))
                notApplied.Add(column);
        }
        if (!lookups.ActiveSalaryEmployeeIds.Contains(emp.Id) && SalaryColumns.Any(c => Cell(c).Length > 0))
            notApplied.Add("Salary");
        return notApplied;
    }
}
