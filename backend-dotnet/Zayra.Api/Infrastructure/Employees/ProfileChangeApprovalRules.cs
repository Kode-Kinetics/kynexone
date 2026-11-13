namespace Zayra.Api.Infrastructure.Employees;

/// <summary>
/// PEO-03 — decides whether a change to one employee profile field goes through the approval flow.
/// The tenant's csv policy <c>employees.profile_change_approval_fields</c> names the fields (snake_case or
/// camelCase, case-insensitive). Fields in the policy's vocabulary (the registry defaults) are governed purely by
/// the list; fields outside that vocabulary which were always approval-gated (GOSI reference, medical, salary package…)
/// stay gated regardless: they are structural, not a tenant choice. Any other field becomes gated when listed.
/// Salary, bank IBAN, national ID and passport number are a floor: always gated, whatever list is stored.
/// </summary>
public static class ProfileChangeApprovalRules
{
    /// <summary>Every field that has always been approval-gated on the employee PATCH route.</summary>
    public static readonly HashSet<string> LegacySensitiveFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "salary", "bankName", "bankIban", "wpsBankDetails", "passportNumber", "passportExpiryDate", "visaNumber",
        "dateOfBirth", "salary", "bankName", "bankIban", "wpsBankDetails", "passportNumber", "passportIssueDate",
        "passportExpiryDate", "visaNumber", "visaIssueDate", "visaExpiryDate", "iqamaNumber", "muqeemNumber",
        // KSA Border Number — the pre-Iqama national-identity number; same approval route as the Iqama.
        "borderNumber",
        // F02 — the GOSI first-registration date decides which contribution schedule a Saudi national is on,
        // so it takes the same maker-checker route as the GOSI reference beside it.
        "gosiFirstRegisteredOn",
        "gosiReference", "qiwaContractNumber", "emiratesId", "laborCardNumber", "visaFileNumber", "qid", "civilId", "residencyNumber",
        "residencyIssueDate", "workPermitNumber", "workPermitIssueDate", "medicalInformation", "disciplinaryRecords",
        "terminationReason",
        // The GPSSA/GRSIA/PIFSS/SPF/SIO counterpart of gosiReference (Saudi GOSI). Same class of value — the
        // social-insurance enrolment every contribution is filed against — so it takes the same maker-checker
        // route. It became writable through PUT when EmployeeChangeApplier gained its payroll-profile key;
        // without this entry that write would have skipped the approval gosiReference always required.
        "socialInsuranceReference",
        // Where the WPS/SIF line pays: the bank's routing code and the account number (payroll profile).
        "bankRoutingCode", "accountNumber",
        "molId", "salaryCurrency", "payrollGroup", "salaryStructureReference", "paymentMethod", "salaryBreakdown",
        // The national ID and the four identity-card expiries take the same route as the passport and visa beside
        // them: they identify the person and gate activation/payroll, so a change is approved before it applies.
        // Creating an employee is not a change and still sets them directly.
        "idNumber", "iqamaExpiryDate", "emiratesIdExpiryDate", "qidExpiryDate", "civilIdExpiryDate",
        // The Qiwa block references, writable through PUT since they gained an applier case: the same route as the Qiwa
        // contract number beside them. EstablishmentId decides whether the Saudi bank file accepts the person.
        "establishmentId", "occupationCode", "workLocationId", "contractReference", "workPermitReference"
    };

    // Policy names whose camelCase counterpart differs by more than underscores.
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["nationalid"] = "idnumber",
    };

    /// <summary>The fields the policy list governs outright (the registry's vocabulary): removing one relaxes it.</summary>
    private static readonly HashSet<string> Governed = new(StringComparer.Ordinal)
    {
        "salary", "bankiban", "passportnumber", "idnumber", "iqamanumber", "sponsorname", "nationality",
    };

    /// <summary>The comparison key for a field name: snake_case, kebab-case and camelCase spellings, and the policy's
    /// <c>national_id</c> alias, all collapse to one.</summary>
    public static string FieldKey(string s) => Norm(s);

    /// <summary>The fields no stored list can relax (see <see cref="Zayra.Api.Infrastructure.Policy.PolicyRegistry.ProfileChangeApprovalFloor"/>).</summary>
    private static readonly HashSet<string> Floor =
        new(Zayra.Api.Infrastructure.Policy.PolicyRegistry.ProfileChangeApprovalFloor.Select(f => Norm(f)), StringComparer.Ordinal);

    private static string Norm(string s)
    {
        var n = s.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).Trim().ToLowerInvariant();
        return Aliases.TryGetValue(n, out var a) ? a : n;
    }

    /// <summary>The personal details an employee can request to change about themselves. They are in the default list
    /// so self-service stays approval-gated as it always was, but the list governs only that door: an authorised HR user
    /// editing the same field on the employee record was never put through approval and still is not.</summary>
    private static readonly HashSet<string> SelfServiceOnly = new(StringComparer.Ordinal)
    {
        "preferredname", "personalemail", "phone", "maritalstatus", "emergencycontactname", "emergencycontactphone",
    };

    /// <summary>The HR-edit door (PATCH on the employee record).</summary>
    public static bool RequiresApprovalForHrEdit(string field, IReadOnlyList<string> policyFields) =>
        !SelfServiceOnly.Contains(Norm(field)) && RequiresApproval(field, policyFields);

    /// <summary>The self-service door, and the policy itself.</summary>
    public static bool RequiresApproval(string field, IReadOnlyList<string> policyFields)
    {
        var key = Norm(field);
        if (Floor.Contains(key)) return true;
        if (policyFields.Any(p => Norm(p) == key)) return true;
        return LegacySensitiveFields.Contains(field) && !Governed.Contains(key);
    }
}
