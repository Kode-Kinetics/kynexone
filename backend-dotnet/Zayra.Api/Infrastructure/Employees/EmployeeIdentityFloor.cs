namespace Zayra.Api.Infrastructure.Employees;

/// <summary>
/// The create-floor (§7.1): the ONLY bar to CREATE an employee is minimal legal identity — a
/// non-blank name (a unique EmployeeCode is generated when blank). No statutory / payroll / org
/// field is ever part of the create floor. Shared by import, the create form, and drafts so the three
/// paths agree. Creation is the easiest thing you can do; becoming Active is what requires effort.
/// </summary>
public static class EmployeeIdentityFloor
{
    public static bool IsCreatable(string? fullName) => !string.IsNullOrWhiteSpace(fullName);
}
