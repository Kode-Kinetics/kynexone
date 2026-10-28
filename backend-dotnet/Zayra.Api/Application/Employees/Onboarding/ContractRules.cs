namespace Zayra.Api.Application.Employees.Onboarding;

/// <summary>CD17: contract dates are data integrity. A fixed-term contract ends after it starts; an end date needs a fixed term.</summary>
public static class ContractRules
{
    public static bool IsFixedTerm(string? contractType)
    {
        if (string.IsNullOrWhiteSpace(contractType)) return false;
        var t = contractType.Replace("-", " ").Replace("_", " ").Trim().ToLowerInvariant();
        return t.StartsWith("fixed");
    }

    /// <summary>Returns the field error codes (empty when the dates are consistent).</summary>
    public static IReadOnlyList<string> Check(string? contractType, DateOnly? start, DateOnly? end)
    {
        var errors = new List<string>();
        if (end is null) return errors;
        if (!IsFixedTerm(contractType)) { errors.Add(EmployeeFieldErrors.ContractEndWithoutFixedTerm); return errors; }
        if (start is DateOnly s && end.Value <= s) errors.Add(EmployeeFieldErrors.ContractEndBeforeStart);
        return errors;
    }
}
