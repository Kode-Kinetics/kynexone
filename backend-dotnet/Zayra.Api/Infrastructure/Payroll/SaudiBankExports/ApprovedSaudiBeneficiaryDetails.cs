using System.Text.Json;
namespace Zayra.Api.Infrastructure.Payroll.SaudiBankExports;

/// <summary>Customer-provided beneficiary facts saved in Employee.WpsBankDetails through the existing
/// sensitive employee-change approval workflow. No branch-address substitution or BIC inference.</summary>
public sealed record ApprovedSaudiBeneficiaryDetails(string? BicCode, string? Address1, string? Address2, string? Address3)
{
    public const string Schema = "saudi-bank-beneficiary-v1";
    public static ApprovedSaudiBeneficiaryDetails? Read(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            using var json = JsonDocument.Parse(value);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "schema") != Schema) return null;
            return new(Text(root, "bicCode"), Text(root, "employeeAddress1"), Text(root, "employeeAddress2"), Text(root, "employeeAddress3"));
        }
        catch (JsonException) { return null; }
    }
    private static string? Text(JsonElement root, string key) =>
        root.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
}
