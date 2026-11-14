using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Employees;

public enum WageFileIdOutcome { Resolved, Conflict, BorderNumberUsed, BorderNumberNotAllowed, Missing }

/// <summary>The employee ID a Saudi wage / bank file carries, or null with the reason.</summary>
public sealed record WageFileId(string? Value, WageFileIdOutcome Outcome);

/// <summary>
/// The ONE answer to "which government ID does the wage file carry for this person", shared by the WPS validator
/// (<c>WpsSifValidator</c>), the WPS register writer (<c>PayrollController.KsaWageFileEmployeeId</c>) and the Saudi bank export, so
/// what is validated is what is written.
/// <list type="number">
/// <item>The National ID / Iqama on file: <c>IdNumber</c>, unless its type is recorded as a passport or a border number (neither
/// is a wage-file government ID; the type is read in any spelling through <see cref="GovernmentIdTypes"/>), and <c>IqamaNumber</c>.
/// Two different values are a conflict, and nothing is written.</item>
/// <item>With neither: the Border Number, for a person inside the company's border-number window
/// (<see cref="BorderNumberRule.ResolveWageFileIdsAsync"/>), only when the company uses it as the wage-file ID.</item>
/// </list>
/// </summary>
public static class WageFileGovernmentId
{
    /// <summary>The distinct, trimmed National ID / Iqama values on the record.</summary>
    public static IReadOnlyList<string> Candidates(Employee employee)
    {
        var idNumberIsGovernmentId =
            GovernmentIdTypes.Canonical(employee.IdType) is not (GovernmentIdTypes.Passport or GovernmentIdTypes.BorderNumber);
        return new[] { idNumberIsGovernmentId ? employee.IdNumber : null, employee.IqamaNumber }
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public static WageFileId Resolve(Employee employee, IReadOnlyDictionary<int, BorderNumberRule.WageFileBorderId>? borderIds)
    {
        var candidates = Candidates(employee);
        if (candidates.Count > 1) return new(null, WageFileIdOutcome.Conflict);
        if (candidates.Count == 1) return new(candidates[0], WageFileIdOutcome.Resolved);
        if (borderIds is not null && borderIds.TryGetValue(employee.Id, out var border))
            return border.Allowed
                ? new(border.BorderNumber, WageFileIdOutcome.BorderNumberUsed)
                : new(null, WageFileIdOutcome.BorderNumberNotAllowed);
        return new(null, WageFileIdOutcome.Missing);
    }
}
