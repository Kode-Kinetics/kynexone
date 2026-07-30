using Zayra.Api.Infrastructure.Payroll;

namespace Zayra.Api.Infrastructure.Employees;

/// <summary>
/// Immutable spec for one readiness field: how to LABEL it, where it sits, how to FIX it, and how to
/// read its PRESENCE from a snapshot. The getter is pure and takes an as-of date + alert-window so
/// date-based fields (IDs/visas) can report Expired / ExpiringSoon.
/// </summary>
public sealed record FieldSpec(
    string Key,
    string Label,
    string Category,          // identity | payroll | org | contract | document | personal
    string DefaultGate,       // "activate" | "pay"
    string FixKind,           // "field" | "document"
    string? FixTarget,        // e.g. "iqamaNumber" or "payrollProfile.iban" (field fixes)
    string? DocumentType,     // e.g. "Contract" (document fixes)
    System.Func<EmployeeReadinessSnapshot, DateOnly, int, FieldPresence> Getter);

/// <summary>
/// The single, code-owned catalog of resolvable readiness keys (§3.2). Employment-lifecycle
/// vocabulary — NOT tenant business data — so it lives in code and is fail-safe: a tenant cannot
/// invent a field the evaluator cannot read. Replaces the 8-way `GetField` switch (and its fail-open
/// `_ => "n/a"`) in CompanyGovernanceController. An unresolvable key returns FieldPresence.NotEvaluable
/// ⇒ the evaluator FAILS CLOSED. `doc:{Type}` keys are resolved dynamically. Accepts "field" as an
/// alias for "key" (handled by the JSON parser, not here — the registry is keyed by the canonical key).
/// </summary>
public static class EmployeeFieldRegistry
{
    public const string DocPrefix = "doc:";

    private static readonly IReadOnlyDictionary<string, FieldSpec> Specs = Build();

    /// <summary>All statically-known specs (excludes the dynamic doc:{Type} family).</summary>
    public static IReadOnlyDictionary<string, FieldSpec> All => Specs;

    /// <summary>True when the key is resolvable (a known static key, or any doc:{Type}).</summary>
    public static bool IsKnown(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        var k = key.Trim();
        if (k.StartsWith(DocPrefix, System.StringComparison.OrdinalIgnoreCase) && k.Length > DocPrefix.Length) return true;
        return Specs.ContainsKey(k);
    }

    /// <summary>Resolves a spec for any key, synthesising a doc:{Type} spec on demand.
    /// Returns null only for a genuinely unknown key (→ caller fails closed).</summary>
    public static FieldSpec? Resolve(string? key, bool requireVerified = false)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var k = key.Trim();
        if (k.StartsWith(DocPrefix, System.StringComparison.OrdinalIgnoreCase))
        {
            var type = k[DocPrefix.Length..].Trim();
            if (type.Length == 0) return null;
            return DocSpec(type, requireVerified);
        }
        return Specs.TryGetValue(k, out var spec) ? spec : null;
    }

    /// <summary>Presence of a key against a snapshot; unknown key ⇒ NotEvaluable (fail closed).</summary>
    public static FieldPresence Presence(EmployeeReadinessSnapshot snap, string key, DateOnly asOf, int alertDays, bool requireVerified = false)
        => Resolve(key, requireVerified)?.Getter(snap, asOf, alertDays) ?? FieldPresence.NotEvaluable;

    // ── Presence helpers ─────────────────────────────────────────────────────
    private static FieldPresence Str(string? v) => string.IsNullOrWhiteSpace(v) ? FieldPresence.Missing : FieldPresence.Present;
    private static FieldPresence Bool(bool v) => v ? FieldPresence.Present : FieldPresence.Missing;

    private static FieldPresence Date(DateOnly? d) => d.HasValue ? FieldPresence.Present : FieldPresence.Missing;

    /// <summary>Expiry presence: absent ⇒ Missing; expired at as-of ⇒ Expired; within window ⇒
    /// ExpiringSoon; else Present.</summary>
    private static FieldPresence Expiry(DateOnly? d, DateOnly asOf, int alertDays)
    {
        if (d is not DateOnly e) return FieldPresence.Missing;
        if (e < asOf) return FieldPresence.Expired;
        if (alertDays > 0 && e <= asOf.AddDays(alertDays)) return FieldPresence.ExpiringSoon;
        return FieldPresence.Present;
    }

    private static FieldPresence Iban(string? iban)
    {
        if (string.IsNullOrWhiteSpace(iban)) return FieldPresence.Missing;
        return IbanValidator.IsValid(iban) ? FieldPresence.Present : FieldPresence.Invalid;
    }

    private static FieldPresence Doc(EmployeeReadinessSnapshot s, string type, bool requireVerified, DateOnly asOf, int alertDays)
    {
        var docs = s.Documents.Where(d => string.Equals(d.Type, type, System.StringComparison.OrdinalIgnoreCase)).ToList();
        if (docs.Count == 0) return FieldPresence.Missing;
        if (requireVerified && !docs.Any(d => d.Verified)) return FieldPresence.Missing;
        // If ANY matching doc carries an expiry, surface expiry state from the freshest one.
        var withExpiry = docs.Where(d => d.Expiry.HasValue).OrderByDescending(d => d.Expiry).FirstOrDefault();
        if (withExpiry is not null) return Expiry(withExpiry.Expiry, asOf, alertDays);
        return FieldPresence.Present;
    }

    private static DateOnly? Comp(EmployeeReadinessSnapshot s, string key)
        => s.ComplianceExpiries.TryGetValue(key, out var d) ? d : null;

    private static FieldSpec DocSpec(string type, bool requireVerified) => new(
        DocPrefix + type, $"{type} document", "document", "activate", "document", null, type,
        (s, asOf, alert) => Doc(s, type, requireVerified, asOf, alert));

    private static IReadOnlyDictionary<string, FieldSpec> Build()
    {
        var d = new Dictionary<string, FieldSpec>(System.StringComparer.OrdinalIgnoreCase);
        void Add(string key, string label, string category, string gate, string fixKind, string? fixTarget,
                 System.Func<EmployeeReadinessSnapshot, DateOnly, int, FieldPresence> getter)
            => d[key] = new FieldSpec(key, label, category, gate, fixKind, fixTarget, null, getter);

        // ── Personal / org scalars ───────────────────────────────────────────
        Add("EnglishName", "English name", "personal", "activate", "field", "englishName", (s, _, _) => Str(s.EnglishName));
        Add("FullName", "Full name", "personal", "activate", "field", "fullName", (s, _, _) => Str(s.FullName));
        Add("DateOfBirth", "Date of birth", "personal", "activate", "field", "dateOfBirth", (s, _, _) => Date(s.DateOfBirth));
        Add("Nationality", "Nationality", "personal", "activate", "field", "nationality", (s, _, _) => Str(s.Nationality));
        Add("Gender", "Gender", "personal", "activate", "field", "gender", (s, _, _) => Str(s.Gender));
        Add("WorkEmail", "Work email", "personal", "activate", "field", "workEmail", (s, _, _) => Str(s.WorkEmail));
        Add("Phone", "Phone", "personal", "activate", "field", "phone", (s, _, _) => Str(s.Phone));
        Add("DepartmentId", "Department", "org", "activate", "field", "departmentId", (s, _, _) => Bool(s.DepartmentId.HasValue));
        Add("DesignationId", "Designation", "org", "activate", "field", "designationId", (s, _, _) => Bool(s.DesignationId.HasValue));
        Add("JoiningDate", "Joining date", "contract", "activate", "field", "joiningDate", (s, _, _) => Bool(s.JoiningDate != default));
        Add("ContractType", "Contract type", "contract", "activate", "field", "contractType", (s, _, _) => Str(s.ContractType));
        Add("EmploymentType", "Employment type", "contract", "activate", "field", "employmentType", (s, _, _) => Str(s.EmploymentType));

        // ── Identity numbers ─────────────────────────────────────────────────
        Add("IqamaNumber", "Iqama number", "identity", "activate", "field", "iqamaNumber", (s, _, _) => Str(s.IqamaNumber));
        Add("GosiReference", "GOSI reference", "identity", "activate", "field", "gosiReference", (s, _, _) => Str(s.GosiReference));
        Add("EmiratesId", "Emirates ID", "identity", "activate", "field", "emiratesId", (s, _, _) => Str(s.EmiratesId));
        Add("Qid", "Qatar ID (QID)", "identity", "activate", "field", "qid", (s, _, _) => Str(s.Qid));
        Add("CivilId", "Civil ID", "identity", "activate", "field", "civilId", (s, _, _) => Str(s.CivilId));
        Add("IdNumber", "Government ID number", "identity", "activate", "field", "idNumber", (s, _, _) => Str(s.IdNumber));
        Add("PassportNumber", "Passport number", "identity", "activate", "field", "passportNumber", (s, _, _) => Str(s.PassportNumber));
        Add("VisaNumber", "Visa number", "identity", "activate", "field", "visaNumber", (s, _, _) => Str(s.VisaNumber));
        Add("WorkPermitNumber", "Work permit number", "identity", "activate", "field", "workPermitNumber", (s, _, _) => Str(s.WorkPermitNumber));
        Add("MuqeemNumber", "Muqeem number", "identity", "activate", "field", "muqeemNumber", (s, _, _) => Str(s.MuqeemNumber));
        Add("LaborCardNumber", "Labour card number", "identity", "activate", "field", "laborCardNumber", (s, _, _) => Str(s.LaborCardNumber));
        Add("QiwaContractNumber", "Qiwa contract number", "identity", "activate", "field", "qiwaContractNumber", (s, _, _) => Str(s.QiwaContractNumber));
        Add("SocialInsuranceReference", "Social-insurance reference", "identity", "activate", "field", "payrollProfile.socialInsuranceReference", (s, _, _) => Str(s.SocialInsuranceReference));

        // ── Expiry keys (P1) — source scalars where present, else compliance records (D3) ──
        Add("PassportExpiryDate", "Passport expiry", "identity", "pay", "field", "passportExpiryDate", (s, a, w) => Expiry(s.PassportExpiryDate, a, w));
        Add("VisaExpiryDate", "Visa expiry", "identity", "pay", "field", "visaExpiryDate", (s, a, w) => Expiry(s.VisaExpiryDate, a, w));
        Add("IqamaExpiry", "Iqama expiry", "identity", "pay", "field", "iqamaExpiry", (s, a, w) => Expiry(Comp(s, "iqama_expiry"), a, w));
        Add("EmiratesIdExpiry", "Emirates ID expiry", "identity", "pay", "field", "emiratesIdExpiry", (s, a, w) => Expiry(Comp(s, "emirates_id_expiry"), a, w));
        Add("QidExpiry", "QID expiry", "identity", "pay", "field", "qidExpiry", (s, a, w) => Expiry(Comp(s, "qid_expiry"), a, w));
        Add("CivilIdExpiry", "Civil ID expiry", "identity", "pay", "field", "civilIdExpiry", (s, a, w) => Expiry(Comp(s, "civil_id_expiry"), a, w));

        // ── Payroll ──────────────────────────────────────────────────────────
        Add("BankIban", "IBAN (WPS)", "payroll", "pay", "field", "payrollProfile.iban", (s, _, _) => Iban(s.BankIban));
        Add("SalaryStructure", "Salary", "payroll", "pay", "field", "salary", (s, _, _) => Bool(s.HasSalary));
        Add("MolId", "MOL personal ID", "payroll", "pay", "field", "payrollProfile.molId", (s, _, _) => Str(s.MolId));
        Add("BankRoutingCode", "Bank routing code", "payroll", "pay", "field", "payrollProfile.bankRoutingCode", (s, _, _) => Str(s.BankRoutingCode));
        Add("PaymentMethod", "Payment method", "payroll", "pay", "field", "payrollProfile.paymentMethod", (s, _, _) => Str(s.PaymentMethod));

        return d;
    }
}
