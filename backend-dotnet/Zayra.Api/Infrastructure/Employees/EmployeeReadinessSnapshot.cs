namespace Zayra.Api.Infrastructure.Employees;

/// <summary>
/// The materialized, read-only projection of everything the readiness evaluator needs about ONE
/// employee, ASSEMBLED AT THE CALL-SITE (never DB-loaded-by-id inside the guard). This is load-bearing:
/// at ApproveDraft the Employee is in-memory with Id==0, its documents are keyed by DraftId, and no
/// EmployeePayrollProfile row exists yet (IBAN lives on the draft/employee scalar). The call-site
/// builds the snapshot from the in-memory Employee + the explicitly-supplied document set + IBAN string.
/// </summary>
public sealed record EmployeeReadinessSnapshot
{
    public int EmployeeId { get; init; }
    public string CountryCode { get; init; } = string.Empty;
    public string Nationality { get; init; } = string.Empty;

    // ── Employee scalars ─────────────────────────────────────────────────────
    public string EnglishName { get; init; } = string.Empty;
    public string FullName { get; init; } = string.Empty;
    public string Gender { get; init; } = string.Empty;
    public DateOnly? DateOfBirth { get; init; }
    public string WorkEmail { get; init; } = string.Empty;
    public string Phone { get; init; } = string.Empty;
    public System.Guid? DepartmentId { get; init; }
    public System.Guid? DesignationId { get; init; }
    public System.DateTime JoiningDate { get; init; }
    public string ContractType { get; init; } = string.Empty;
    public string EmploymentType { get; init; } = string.Empty;
    public string PassportNumber { get; init; } = string.Empty;
    public DateOnly? PassportExpiryDate { get; init; }
    public string IqamaNumber { get; init; } = string.Empty;
    // GCC-ID expiry scalars (first-class columns, parity with Passport/Visa expiry — §D3/Δ20). The
    // registry expiry getters read these; a compliance-record fallback is retained for legacy data.
    public DateOnly? IqamaExpiryDate { get; init; }
    public DateOnly? EmiratesIdExpiryDate { get; init; }
    public DateOnly? QidExpiryDate { get; init; }
    public DateOnly? CivilIdExpiryDate { get; init; }
    public string GosiReference { get; init; } = string.Empty;
    public string EmiratesId { get; init; } = string.Empty;
    public string Qid { get; init; } = string.Empty;
    public string CivilId { get; init; } = string.Empty;
    public string IdNumber { get; init; } = string.Empty;
    public string VisaNumber { get; init; } = string.Empty;
    public DateOnly? VisaExpiryDate { get; init; }
    public string WorkPermitNumber { get; init; } = string.Empty;
    public string MuqeemNumber { get; init; } = string.Empty;
    public string LaborCardNumber { get; init; } = string.Empty;
    public string QiwaContractNumber { get; init; } = string.Empty;

    // ── Payroll profile ──────────────────────────────────────────────────────
    public string BankIban { get; init; } = string.Empty;
    public string PaymentMethod { get; init; } = string.Empty;
    public string MolId { get; init; } = string.Empty;
    public string BankRoutingCode { get; init; } = string.Empty;
    public string SocialInsuranceReference { get; init; } = string.Empty;
    public bool HasSalary { get; init; }
    /// <summary>Honours the exit-cascade flag (EmployeeManagementService sets false on separation).
    /// A false value is a hard pay blocker regardless of any other data.</summary>
    public bool WpsEligible { get; init; } = true;

    // ── Documents (present set) ──────────────────────────────────────────────
    public IReadOnlyList<DocumentPresence> Documents { get; init; } = System.Array.Empty<DocumentPresence>();

    /// <summary>Compliance-record expiries keyed by lower_snake FieldKey (mirror map in
    /// EmployeeManagementService.ApplyKnownComplianceMirror), e.g. "iqama_expiry" → DateOnly.
    /// Sources expiry getters for Iqama/EID/QID/CivilId which have NO Employee scalar column (D3).</summary>
    public IReadOnlyDictionary<string, DateOnly?> ComplianceExpiries { get; init; }
        = new Dictionary<string, DateOnly?>(System.StringComparer.OrdinalIgnoreCase);
}

/// <summary>A present, non-deleted document of a given type; Verified flag + optional expiry.</summary>
public sealed record DocumentPresence(string Type, bool Verified, DateOnly? Expiry);

/// <summary>
/// Presence outcome of a single required field against a snapshot. Ordered by severity so the
/// evaluator can pick the strongest signal. NotEvaluable is the fail-CLOSED sentinel for a key the
/// registry cannot resolve — it is treated as a blocker, never as present (reversing the old
/// CompanyGovernanceController `_ => "n/a"` fail-open behaviour).
/// </summary>
public enum FieldPresence
{
    Present,        // satisfied
    ExpiringSoon,   // present + valid, but within the alert window (amber)
    Missing,        // absent
    Invalid,        // present but structurally invalid (e.g. IBAN fails mod-97)
    Expired,        // present but expired at the as-of date
    NotEvaluable,   // registry cannot resolve the key → FAIL CLOSED
}
