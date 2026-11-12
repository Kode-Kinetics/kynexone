using Zayra.Api.Application.Employees.Onboarding;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Employees;

/// <summary>
/// The fields the Add-Employee form derives for a hire (nationality code and mapping state, Saudi / NonSaudi, primary ID type, contract start), applied to
/// a hire that arrives as a draft with only free text (an accepted offer copies the candidate's nationality as typed). It reuses the form's own helpers
/// (<see cref="EmployeeManagementService.DeriveWorkerClass"/>, <see cref="EmployeeManagementService.PrimaryIdType"/>, the nationality catalogue the CSV import
/// uses), so the two paths cannot disagree. Never guessed: text the catalogue does not resolve is flagged Unresolved and leaves the class blank.
/// </summary>
internal static class DraftHireDerivation
{
    /// <summary>Resolves <c>employee.Nationality</c> (free text) through the nationality catalogue, then derives the Saudi / NonSaudi class.
    /// A code already on the employee is kept.</summary>
    public static void ApplyNationality(Employee employee)
    {
        var text = employee.Nationality?.Trim() ?? string.Empty;
        if (employee.NationalityCode is null && text.Length > 0)
        {
            if (NationalityCatalog.TryResolve(text) is { } hit)
            {
                employee.NationalityCode = hit.Iso2;
                employee.NationalityMappingState = NationalityMappingStates.Confirmed;
            }
            else employee.NationalityMappingState = NationalityMappingStates.Unresolved;
        }
        var workerClass = EmployeeManagementService.DeriveWorkerClass(employee.NationalityCode, employee.Nationality);
        if (workerClass is not null) employee.SaudiOrNonSaudi = workerClass;
    }

    /// <summary>The primary government ID type from the identity numbers the draft carries, in the order the form lists them (ID or Iqama first, passport last).</summary>
    public static string PrimaryIdType(Employee employee)
    {
        var keys = new List<string?>();
        if (!string.IsNullOrWhiteSpace(employee.IdNumber)) keys.Add("id_number");
        if (!string.IsNullOrWhiteSpace(employee.IqamaNumber)) keys.Add("iqama_number");
        if (!string.IsNullOrWhiteSpace(employee.BorderNumber)) keys.Add("border_number");
        if (!string.IsNullOrWhiteSpace(employee.CivilId)) keys.Add("civil_id");
        if (!string.IsNullOrWhiteSpace(employee.EmiratesId)) keys.Add("emirates_id");
        if (!string.IsNullOrWhiteSpace(employee.Qid)) keys.Add("qid");
        if (!string.IsNullOrWhiteSpace(employee.PassportNumber)) keys.Add("passport_number");
        return EmployeeManagementService.PrimaryIdType(keys, null) ?? string.Empty;
    }

    /// <summary>A contract starts on the start date when it has a type and no start was stated (the form's rule).</summary>
    public static DateOnly? ContractStart(string? contractType, DateOnly? stated, DateTime joiningDate) =>
        stated ?? (string.IsNullOrWhiteSpace(contractType) ? null : DateOnly.FromDateTime(joiningDate));
}
