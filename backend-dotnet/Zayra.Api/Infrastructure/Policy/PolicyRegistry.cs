namespace Zayra.Api.Infrastructure.Policy;

public enum PolicyValueType { Bool, Enum, Csv }

/// <summary>One known policy setting. Defaults are the strict/safe choice; a tenant may relax them.</summary>
public sealed record PolicyDefinition(
    string Key,
    PolicyValueType Type,
    string Default,
    IReadOnlyList<string> AllowedValues,
    string Description,
    string Category);

public sealed class UnknownPolicyKeyException : Exception
{
    public string Key { get; }
    public UnknownPolicyKeyException(string key)
        : base($"'{key}' is not a registered policy setting. Register it in PolicyRegistry before reading it.") => Key = key;
}

/// <summary>
/// Code registry of every client-configurable policy. Stored per tenant in SystemSettings
/// (Category = <see cref="StorageCategory"/>); an absent row means the registry default.
/// Isolation, audit, exactly-once posting and bank-file formats are deliberately NOT here.
/// </summary>
public static class PolicyRegistry
{
    public const string StorageCategory = "Policy";

    public const string OvertimeLateApprovalMode = "overtime.late_approval_mode";
    public const string OvertimeCompoffCapMode = "overtime.compoff_cap_mode";
    public const string PayrollAllowSkipReview = "payroll.allow_skip_review";
    public const string PayrollAllowSameApproverMultipleLevels = "payroll.allow_same_approver_multiple_levels";
    public const string EmployeesProfileChangeApprovalFields = "employees.profile_change_approval_fields";

    private static readonly PolicyDefinition[] Definitions =
    [
        new(OvertimeLateApprovalMode, PolicyValueType.Enum, "Refuse", ["Refuse", "BackPay"],
            "What happens when overtime is approved after its payroll period has closed: Refuse the approval, or pay it back in a later period.",
            "Overtime"),
        new(OvertimeCompoffCapMode, PolicyValueType.Enum, "WholeDays", ["WholeDays", "Fractional"],
            "How the compensatory-off cap is applied: in whole days only, or allowing fractional days.",
            "Overtime"),
        new(PayrollAllowSkipReview, PolicyValueType.Bool, "false", ["true", "false"],
            "Allow a payroll run to be completed without the review step.",
            "Payroll"),
        new(PayrollAllowSameApproverMultipleLevels, PolicyValueType.Bool, "false", ["true", "false"],
            "Allow one person to approve more than one level of the same approval chain.",
            "Payroll"),
        new(EmployeesProfileChangeApprovalFields, PolicyValueType.Csv,
            "bank_iban,national_id,passport_number,salary,iqama_number,sponsor_name,nationality", [],
            "Employee profile fields whose changes need approval (comma-separated, lowercase field names).",
            "Employees"),
    ];

    private static readonly Dictionary<string, PolicyDefinition> ByKey =
        Definitions.ToDictionary(d => d.Key, StringComparer.Ordinal);

    public static IReadOnlyList<PolicyDefinition> All => Definitions;

    public static bool TryGet(string key, out PolicyDefinition definition)
    {
        if (key is not null && ByKey.TryGetValue(key, out var d)) { definition = d; return true; }
        definition = null!;
        return false;
    }

    /// <summary>Fails loudly: an unregistered key is a bug in the calling code.</summary>
    public static PolicyDefinition Get(string key) =>
        TryGet(key, out var d) ? d : throw new UnknownPolicyKeyException(key);

    /// <summary>Validates <paramref name="raw"/> against the definition and returns its canonical stored form.</summary>
    public static bool TryNormalise(PolicyDefinition def, string? raw, out string normalised, out string error)
    {
        normalised = string.Empty; error = string.Empty;
        raw ??= string.Empty;
        switch (def.Type)
        {
            case PolicyValueType.Bool:
                if (bool.TryParse(raw.Trim(), out var b)) { normalised = b ? "true" : "false"; return true; }
                error = "Value must be 'true' or 'false'.";
                return false;
            case PolicyValueType.Enum:
                var member = def.AllowedValues.FirstOrDefault(a => string.Equals(a, raw.Trim(), StringComparison.OrdinalIgnoreCase));
                if (member is not null) { normalised = member; return true; }
                error = $"Value must be one of: {string.Join(", ", def.AllowedValues)}.";
                return false;
            case PolicyValueType.Csv:
                normalised = string.Join(',', ParseCsv(raw));
                return true;
            default:
                error = "Unsupported policy type.";
                return false;
        }
    }

    public static IReadOnlyList<string> ParseCsv(string? raw) =>
        (raw ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();
}
