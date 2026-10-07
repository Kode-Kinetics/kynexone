using System.Text.Json;
using System.Text.Json.Nodes;
using Zayra.Api.Application.Common;
using Zayra.Api.Models;

namespace Zayra.Api.Application.Employees;

/// <summary>
/// Produces the ONLY permitted JSON snapshot of an Employee for history/audit rows.
/// Raw JsonSerializer.Serialize(employee) is forbidden for persistence — it leaks salary,
/// IBAN, Iqama, passport, national IDs and medical data into EmployeeHistory.SnapshotJson,
/// bypassing the API read-path mask (EmployeeDetailDto.Project).
/// Identity/banking numbers keep their last 4 characters, salary becomes a deterministic
/// change marker, and free-text medical/disciplinary content is redacted entirely.
/// </summary>
public static class EmployeeSafeSnapshot
{
    // Identity / banking numbers — masked to last 4 so audit can still correlate documents.
    private static readonly string[] MaskedIdFields =
    {
        nameof(Employee.BankIban), nameof(Employee.WpsBankDetails),
        nameof(Employee.PassportNumber), nameof(Employee.VisaNumber), nameof(Employee.IqamaNumber),
        nameof(Employee.MuqeemNumber), nameof(Employee.GosiReference), nameof(Employee.QiwaContractNumber),
        nameof(Employee.EmiratesId), nameof(Employee.LaborCardNumber), nameof(Employee.VisaFileNumber),
        nameof(Employee.Qid), nameof(Employee.WorkPermitNumber), nameof(Employee.CivilId),
        nameof(Employee.ResidencyNumber), nameof(Employee.IdNumber),
        nameof(Employee.ContractReference), nameof(Employee.WorkPermitReference),
        nameof(Employee.QiwaEmployeeReference)
    };

    // Free-text fields whose content is sensitive in full — redacted, not masked.
    private static readonly string[] RedactedFields =
    {
        nameof(Employee.MedicalInformation), nameof(Employee.DisciplinaryRecords),
        nameof(Employee.TerminationReason), nameof(Employee.BankName)
    };

    // Exact (normalised) names of this model's sensitive fields. Names from outside the model — the migration
    // import takes FieldName verbatim from a legacy system ("IBAN Number", "Iqama No") — are caught by
    // SensitiveFieldClassifier's name and value-shape rules instead.
    private static readonly HashSet<string> SensitiveFieldNames = new(
        MaskedIdFields.Concat(RedactedFields).Select(SensitiveFieldClassifier.Normalise),
        StringComparer.Ordinal);

    public static string Serialize(Employee employee)
    {
        var node = JsonSerializer.SerializeToNode(employee)?.AsObject()
                   ?? throw new InvalidOperationException("Employee snapshot serialization failed.");
        foreach (var field in MaskedIdFields)
        {
            if (node[field] is JsonValue value && value.TryGetValue<string>(out var raw))
                node[field] = SensitiveValueMask.MaskId(raw);
        }
        foreach (var field in RedactedFields)
        {
            if (node[field] is JsonValue value && value.TryGetValue<string>(out var raw) && !string.IsNullOrEmpty(raw))
                node[field] = SensitiveValueMask.Redacted;
        }
        node[nameof(Employee.Salary)] = employee.Salary is null
            ? null
            : SensitiveValueMask.HashMarker(employee.Salary.Value);
        node["_snapshotPolicy"] = "sensitive-masked-v1";
        return node.ToJsonString();
    }

    /// <summary>
    /// Fail-safe for the field-level OldValue/NewValue history columns: if the tracked
    /// field is sensitive, the stored before/after values are masked (salary becomes a
    /// change marker). Non-sensitive fields pass through untouched.
    /// </summary>
    public static string SanitizeFieldValue(string fieldName, string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (SensitiveFieldClassifier.IsSalaryName(fieldName))
            return SensitiveValueMask.HashMarker(value);
        if (SensitiveFieldNames.Contains(SensitiveFieldClassifier.Normalise(fieldName))
            || SensitiveFieldClassifier.IsIdentifierName(fieldName)
            || SensitiveFieldClassifier.LooksSensitive(value))
            return SensitiveValueMask.MaskId(value);
        // Whatever the field is called, an IBAN or national ID inside the text does not survive.
        return SensitiveFieldClassifier.MaskEmbedded(value);
    }
}
