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

    /// <summary>Error code for a value that is malformed (wrong type, shape or vocabulary).</summary>
    public const string CodeValueInvalid = "policy_value_invalid";
    /// <summary>Error code for a value that is well-formed but removes something a policy may never relax.</summary>
    public const string CodeFloorViolated = "policy_floor_violated";

    /// <summary>
    /// Fields whose profile change is ALWAYS approval-gated, whatever list a tenant stores: pay, bank account and the two
    /// identity numbers. The list may be cleared down to these, never below. Spelled as the policy spells them.
    /// </summary>
    public static readonly IReadOnlyList<string> ProfileChangeApprovalFloor = ["bank_iban", "national_id", "passport_number", "salary"];

    /// <summary>
    /// Switches that relax a payroll control for whoever runs payroll. The person who benefits (payroll.approve, or a Payroll
    /// Manager / Officer) may not switch them on: PolicySettingsController refuses and audits the attempt.
    /// </summary>
    public static readonly IReadOnlyList<string> PayrollControlSwitches = [PayrollAllowSkipReview, PayrollAllowSameApproverMultipleLevels];

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
            "bank_iban,national_id,passport_number,salary,iqama_number,sponsor_name,nationality,preferred_name,personal_email,phone,marital_status,emergency_contact_name,emergency_contact_phone", [],
            "Employee profile fields whose changes need approval (comma-separated, lowercase field names). The personal-detail fields (preferred_name, personal_email, phone, marital_status, emergency_contact_*) govern employee self-service requests only.",
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
    public static bool TryNormalise(PolicyDefinition def, string? raw, out string normalised, out string error) =>
        TryNormalise(def, raw, out normalised, out error, out _);

    /// <summary>As above, also naming the failure: <see cref="CodeValueInvalid"/> or <see cref="CodeFloorViolated"/>.</summary>
    public static bool TryNormalise(PolicyDefinition def, string? raw, out string normalised, out string error, out string code)
    {
        normalised = string.Empty; error = string.Empty; code = CodeValueInvalid;
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
                var tokens = ParseCsv(raw);
                IReadOnlyList<string> floorMissing = def.Key == EmployeesProfileChangeApprovalFields ? MissingFloor(tokens) : [];
                normalised = string.Join(',', tokens);
                if (floorMissing.Count > 0)
                {
                    normalised = string.Empty;
                    code = CodeFloorViolated;
                    error = $"These fields always need approval and cannot be removed: {string.Join(", ", floorMissing)}.";
                    return false;
                }
                return true;
            default:
                error = "Unsupported policy type.";
                return false;
        }
    }

    /// <summary>The floor fields (as the policy spells them) that <paramref name="tokens"/> does not cover, comparing field names the way the approval rules do.</summary>
    public static IReadOnlyList<string> MissingFloor(IReadOnlyCollection<string> tokens)
    {
        var have = tokens.Select(Zayra.Api.Infrastructure.Employees.ProfileChangeApprovalRules.FieldKey).ToHashSet(StringComparer.Ordinal);
        return ProfileChangeApprovalFloor
            .Where(f => !have.Contains(Zayra.Api.Infrastructure.Employees.ProfileChangeApprovalRules.FieldKey(f)))
            .ToList();
    }

    /// <summary>
    /// The value a reader sees for a Csv policy: the stored tokens, plus (for the approval-fields policy) any floor field the
    /// stored list lacks. A list stored before the floor existed therefore reads with the floor applied.
    /// </summary>
    public static string EffectiveCsv(PolicyDefinition def, string? stored)
    {
        var tokens = ParseCsv(stored).ToList();
        if (def.Key == EmployeesProfileChangeApprovalFields) tokens.AddRange(MissingFloor(tokens));
        return string.Join(',', tokens);
    }

    public static IReadOnlyList<string> ParseCsv(string? raw) =>
        (raw ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();
}
