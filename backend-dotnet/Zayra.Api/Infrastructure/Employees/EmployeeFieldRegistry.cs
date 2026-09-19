using Zayra.Api.Application.Common;
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

        // ── Expiry keys (P1) — first-class Employee scalars (parity with Passport/Visa expiry), with a
        // compliance-record fallback for legacy data written before the scalars existed (§D3/Δ20). ──
        Add("PassportExpiryDate", "Passport expiry", "identity", "pay", "field", "passportExpiryDate", (s, a, w) => Expiry(s.PassportExpiryDate, a, w));
        Add("VisaExpiryDate", "Visa expiry", "identity", "pay", "field", "visaExpiryDate", (s, a, w) => Expiry(s.VisaExpiryDate, a, w));
        Add("IqamaExpiry", "Iqama expiry", "identity", "pay", "field", "iqamaExpiry", (s, a, w) => Expiry(s.IqamaExpiryDate ?? Comp(s, "iqama_expiry"), a, w));
        Add("EmiratesIdExpiry", "Emirates ID expiry", "identity", "pay", "field", "emiratesIdExpiry", (s, a, w) => Expiry(s.EmiratesIdExpiryDate ?? Comp(s, "emirates_id_expiry"), a, w));
        Add("QidExpiry", "QID expiry", "identity", "pay", "field", "qidExpiry", (s, a, w) => Expiry(s.QidExpiryDate ?? Comp(s, "qid_expiry"), a, w));
        Add("CivilIdExpiry", "Civil ID expiry", "identity", "pay", "field", "civilIdExpiry", (s, a, w) => Expiry(s.CivilIdExpiryDate ?? Comp(s, "civil_id_expiry"), a, w));

        // ── Payroll ──────────────────────────────────────────────────────────
        Add("BankIban", "IBAN (WPS)", "payroll", "pay", "field", "payrollProfile.iban", (s, _, _) => Iban(s.BankIban));
        Add("SalaryStructure", "Salary", "payroll", "pay", "field", "salary", (s, _, _) => Bool(s.HasSalary));
        Add("MolId", "MOL personal ID", "payroll", "pay", "field", "payrollProfile.molId", (s, _, _) => Str(s.MolId));
        Add("BankRoutingCode", "Bank routing code", "payroll", "pay", "field", "payrollProfile.bankRoutingCode", (s, _, _) => Str(s.BankRoutingCode));
        Add("PaymentMethod", "Payment method", "payroll", "pay", "field", "payrollProfile.paymentMethod", (s, _, _) => Str(s.PaymentMethod));

        return d;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  EMPLOYEE FIELD CATALOG (§3.1) — the authoritative superset of every employee field, the SINGLE
    //  SOURCE OF TRUTH the three surfaces derive from: the create/edit modal (via GET field-catalog),
    //  the CSV template/importer/export (via CsvHeaders), and the DB entity. Readiness stays a *view*
    //  over this catalog (ActivationRelevant descriptors mirror the readiness Specs above 1:1 — enforced
    //  by AssertCatalogIntegrity). The catalog describes SHAPE and BINDING only; it NEVER declares
    //  requiredness — that comes exclusively from the policy resolver / GET {id}/readiness, so tenants
    //  stay configurable and no field-required list is duplicated across layers.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>NATIONALITY axis for a field's modal visibility (the second axis alongside Countries).
    /// Mirrors the floor's three-way split so the two surfaces cannot drift: All (every resident),
    /// HostNationalOnly (nationality == the jurisdiction — e.g. SA National ID), ExpatOnly (non-GCC
    /// expat — work permit / residence visa / Iqama, which GCC common-market reciprocity waives for host
    /// and other-GCC nationals). Visibility is UX only; requiredness stays the policy resolver's job.</summary>
    public enum FieldApplicability { All, HostNationalOnly, ExpatOnly }

    /// <summary>Binding metadata for one employee field across all three surfaces.</summary>
    public sealed record EmployeeFieldDescriptor(
        string Key,                 // canonical key; for ActivationRelevant rows this EQUALS the readiness FieldSpec key
        string Label,
        string Section,             // identity | personal | employment | organization | payroll | salary | qiwa
        string? CsvHeader,          // canonical CSV column; null ⇒ not an independent import/export column
        string InputType,           // text | email | date | number | select | lookup | toggle
        bool Sensitive,
        bool ActivationRelevant,    // true ⇒ has a readiness FieldSpec (requiredness decided by policy, not here)
        IReadOnlyList<string>? Countries, // null ⇒ all jurisdictions; else GCC-conditional (ISO-2 list)
        string Binding,             // descriptive storage target (emp.*, payrollProfile.*, salary:*, org:*, compliance)
        FieldApplicability Applicability = FieldApplicability.All, // NATIONALITY axis (§ two-axis resolver)
        IReadOnlyDictionary<string, string>? LabelByCountry = null, // ISO-2 → local English label (KW/OM/BH CivilId, social-insurance scheme)
        string? ComplianceFieldKey = null, // snake_case mirror key (EmployeeComplianceRecord.FieldKey) for statutory rows
        string? ExpiryKey = null);         // the PAIRED expiry descriptor key, so the modal binds value+expiry as one control

    /// <summary>Resolve the local-English label for a field in a jurisdiction (per-country override wins,
    /// e.g. OM Civil ID → "Oman Resident Card"; else the base label).</summary>
    public static string LabelFor(EmployeeFieldDescriptor d, string? iso2)
    {
        var c = (iso2 ?? string.Empty).Trim().ToUpperInvariant();
        if (c.Length > 0 && d.LabelByCountry is not null && d.LabelByCountry.TryGetValue(c, out var loc) && !string.IsNullOrWhiteSpace(loc))
            return loc;
        return d.Label;
    }

    /// <summary>Whether a descriptor's NATIONALITY axis makes it visible for a (country, nationality).
    /// The catalog twin of <see cref="GccReadinessFloor.NationalityApplies"/>.</summary>
    public static bool AppliesToNationality(FieldApplicability applicability, string countryIso2, string normalizedNationality)
        => applicability switch
        {
            FieldApplicability.HostNationalOnly => countryIso2.Length > 0
                && string.Equals(countryIso2, normalizedNationality, System.StringComparison.OrdinalIgnoreCase),
            // Non-GCC expat: any nationality that is not a GCC state (blank/unknown ⇒ expat, fail-safe visible).
            FieldApplicability.ExpatOnly => !Application.CountryPack.CountryTier.IsGcc(normalizedNationality),
            _ => true,
        };

    private static readonly IReadOnlyList<EmployeeFieldDescriptor> CatalogList = BuildCatalog();

    /// <summary>The full field catalog, in canonical CSV/modal display order.</summary>
    public static IReadOnlyList<EmployeeFieldDescriptor> Catalog => CatalogList;

    /// <summary>The canonical, ordered CSV column list — the ONE source the template, export header, and
    /// importer header-validation all read (replaces the hand-maintained EmployeeCsvHeaders array).</summary>
    public static IReadOnlyList<string> CsvHeaders =>
        CatalogList.Where(d => d.CsvHeader is not null).Select(d => d.CsvHeader!).ToList();

    /// <summary>The example row for the downloadable CSV template — one neutral, obviously-a-placeholder
    /// cell per column, DERIVED from the same ordered catalog <see cref="CsvHeaders"/> is derived from and
    /// shaped by each field's declared <c>InputType</c>. Never a hand-written row: a literal example row is
    /// positional, so adding one catalog column silently shifts every later value into the wrong column
    /// (exactly the drift the hand-maintained header array was removed to prevent). Adding a field to the
    /// catalog extends the header AND its placeholder in the same step, so the two cannot disagree.</summary>
    public static IReadOnlyList<string> CsvExampleRow =>
        CatalogList.Where(d => d.CsvHeader is not null).Select(CsvPlaceholder).ToList();

    /// <summary>Neutral placeholder for one column: a format hint where the shape is unambiguous
    /// (dates/numbers/emails/toggles), otherwise a literal that can never be mistaken for real data
    /// or for another tenant's name.</summary>
    private static string CsvPlaceholder(EmployeeFieldDescriptor d) => d.InputType switch
    {
        "date" => "YYYY-MM-DD",
        "number" => "0",
        "email" => "name@example.com",
        "toggle" => "false",
        _ => "EXAMPLE",   // text | select | lookup
    };

    /// <summary>Descriptors filtered to a resolved jurisdiction for the modal (null country ⇒ universal
    /// rows only; a GCC country ⇒ universal + that country's conditional rows). COUNTRY axis only — the
    /// offline-fallback / country-only special case of <see cref="CatalogFor"/>.</summary>
    public static IReadOnlyList<EmployeeFieldDescriptor> CatalogForCountry(string? iso2)
    {
        var c = (iso2 ?? string.Empty).Trim().ToUpperInvariant();
        return CatalogList.Where(d => d.Countries is null
            || (c.Length > 0 && d.Countries.Contains(c, System.StringComparer.OrdinalIgnoreCase))).ToList();
    }

    /// <summary>Descriptors resolved on BOTH axes — COUNTRY (employing entity) × NATIONALITY (person) —
    /// the modal/CSV visibility source of truth. A Saudi national never gets an Iqama descriptor; a
    /// non-GCC expat gets work-permit/visa; host social-insurance shows for host nationals. Blank
    /// nationality is treated as a non-GCC expat (fail-safe: shows work-authorization, hides
    /// host-national-only rows).</summary>
    public static IReadOnlyList<EmployeeFieldDescriptor> CatalogFor(string? iso2, string? nationality)
    {
        var c = (CountryCodeStandard.NormalizeToIso2(iso2) ?? iso2 ?? string.Empty).Trim().ToUpperInvariant();
        var nat = GccReadinessFloor.NormalizeNationality(nationality);
        return CatalogList.Where(d =>
            (d.Countries is null || (c.Length > 0 && d.Countries.Contains(c, System.StringComparer.OrdinalIgnoreCase)))
            && AppliesToNationality(d.Applicability, c, nat)).ToList();
    }

    /// <summary>Self-check (§3.1 enforcement): every readiness spec key has exactly one ActivationRelevant
    /// descriptor and vice-versa; CSV headers are unique. Throws on drift. Called by a startup/test guard
    /// so a future edit that desyncs the catalog from the registry fails fast instead of silently.</summary>
    public static void AssertCatalogIntegrity()
    {
        var specKeys = Specs.Keys.ToHashSet(System.StringComparer.OrdinalIgnoreCase);
        var arKeys = CatalogList.Where(d => d.ActivationRelevant).Select(d => d.Key).ToList();
        var arSet = arKeys.ToHashSet(System.StringComparer.OrdinalIgnoreCase);

        var missing = specKeys.Where(k => !arSet.Contains(k)).ToList();
        if (missing.Count > 0)
            throw new System.InvalidOperationException(
                $"EmployeeFieldRegistry drift: readiness key(s) with no ActivationRelevant catalog descriptor: {string.Join(", ", missing)}.");

        var extra = arKeys.Where(k => !specKeys.Contains(k)).ToList();
        if (extra.Count > 0)
            throw new System.InvalidOperationException(
                $"EmployeeFieldRegistry drift: ActivationRelevant catalog descriptor(s) with no readiness spec: {string.Join(", ", extra)}.");

        var dupAr = arKeys.GroupBy(k => k, System.StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dupAr.Count > 0)
            throw new System.InvalidOperationException($"EmployeeFieldRegistry drift: duplicate ActivationRelevant key(s): {string.Join(", ", dupAr)}.");

        var headers = CatalogList.Where(d => d.CsvHeader is not null).Select(d => d.CsvHeader!).ToList();
        var dupHeaders = headers.GroupBy(h => h, System.StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dupHeaders.Count > 0)
            throw new System.InvalidOperationException($"EmployeeFieldRegistry drift: duplicate CSV header(s): {string.Join(", ", dupHeaders)}.");

        AssertFloorNationalityParity();
    }

    /// <summary>The two-axis guarantee (§ owner mandate) — FAILS THE BUILD on drift between the readiness
    /// FLOOR (requiredness authority) and the field CATALOG (modal/CSV shape) across every
    /// {country × nationality-class}. Two directions:
    ///   (1) every hard (FailClosed) floor requirement resolves to a VISIBLE ActivationRelevant descriptor
    ///       for that (country, nationality) — so no floor requirement is un-enterable in the modal (this is
    ///       the mechanical guard against the SA-national / UAE-expat "un-activatable" traps); and
    ///   (2) the mutually-exclusive nationality-split identity docs are HIDDEN where the floor waives them —
    ///       a Saudi national never sees an Iqama; a GCC/host national never sees a work permit or visa.
    /// </summary>
    private static void AssertFloorNationalityParity()
    {
        string[] gcc = { "SA", "AE", "QA", "KW", "OM", "BH" };
        // Representative nationalities per country: host national, a different GCC national, a non-GCC expat.
        foreach (var country in gcc)
        {
            var otherGcc = gcc.First(g => !string.Equals(g, country, System.StringComparison.OrdinalIgnoreCase));
            foreach (var nat in new[] { country, otherGcc, "IN" /* non-GCC expat */ })
            {
                var normNat = GccReadinessFloor.NormalizeNationality(nat);
                var visible = CatalogFor(country, nat)
                    .Where(d => d.ActivationRelevant)
                    .Select(d => d.Key)
                    .ToHashSet(System.StringComparer.OrdinalIgnoreCase);

                // Direction 1: every hard floor requirement must be a visible activation-relevant field.
                foreach (var req in GccReadinessFloor.Resolve(country))
                {
                    if (!req.FailClosed) continue;                                   // recommended → not a hard gate
                    if (req.Key.StartsWith(DocPrefix, System.StringComparison.OrdinalIgnoreCase)) continue; // documents aren't modal fields
                    if (!GccReadinessFloor.NationalityApplies(req.AppliesWhen, normNat)) continue;
                    if (!visible.Contains(req.Key))
                        throw new System.InvalidOperationException(
                            $"Catalog↔floor drift ({country}/{nat}): floor requires '{req.Key}' but no visible catalog field offers it — this employee would be un-activatable.");
                }
            }
        }

        // Direction 2: the trap guards — the identity docs that MUST NOT show for a nationality the floor waives.
        void MustHide(string country, string nationality, params string[] keys)
        {
            var visible = CatalogFor(country, nationality).Select(d => d.Key).ToHashSet(System.StringComparer.OrdinalIgnoreCase);
            foreach (var k in keys)
                if (visible.Contains(k))
                    throw new System.InvalidOperationException(
                        $"Catalog↔floor drift ({country}/{nationality}): '{k}' must be hidden for this nationality but the catalog shows it.");
        }
        MustHide("SA", "SA", "IqamaNumber", "IqamaExpiry", "WorkPermitNumber", "VisaNumber", "VisaExpiryDate"); // a Saudi national never sees an Iqama/visa
        MustHide("AE", "AE", "WorkPermitNumber", "VisaNumber", "VisaExpiryDate");                               // an Emirati never needs a work permit/visa
        MustHide("QA", "SA", "WorkPermitNumber", "VisaNumber");                                                 // a GCC national (Saudi in Qatar) is work-permit exempt
        MustHide("SA", "AE", "IqamaNumber");                                                                    // a GCC national (Emirati in KSA) holds no Iqama
    }

    private static IReadOnlyList<EmployeeFieldDescriptor> BuildCatalog()
    {
        var list = new List<EmployeeFieldDescriptor>();
        // Country groupings for GCC-conditional rows.
        string[] Sa = { "SA" }; string[] Ae = { "AE" }; string[] Qa = { "QA" };
        string[] Kw = { "KW" }; string[] Bh = { "BH" };
        string[] KwOmBh = { "KW", "OM", "BH" };
        string[] GccAll = { "SA", "AE", "QA", "KW", "OM", "BH" };
        string[] SocInsCountries = { "AE", "QA", "KW", "OM", "BH" }; // SA uses GosiReference, not the generic key
        const FieldApplicability All = FieldApplicability.All;
        const FieldApplicability HostNational = FieldApplicability.HostNationalOnly;
        const FieldApplicability Expat = FieldApplicability.ExpatOnly;

        void D(string key, string label, string section, string? csv, string input,
               bool sensitive = false, bool ar = false, IReadOnlyList<string>? countries = null, string binding = "",
               FieldApplicability applicability = FieldApplicability.All,
               IReadOnlyDictionary<string, string>? labelByCountry = null,
               string? complianceFieldKey = null, string? expiryKey = null)
            => list.Add(new EmployeeFieldDescriptor(key, label, section, csv, input, sensitive, ar, countries,
                string.IsNullOrEmpty(binding) ? "emp." + key : binding,
                applicability, labelByCountry, complianceFieldKey, expiryKey));

        // ── Identity / master ────────────────────────────────────────────────
        D("EmployeeCode", "Employee code", "identity", "EmployeeCode", "text", binding: "emp.EmployeeCode");
        D("CompanyLegalName", "Company", "organization", "CompanyLegalName", "lookup", binding: "org:companyId");
        D("BranchCode", "Branch", "organization", "BranchCode", "lookup", binding: "org:branchId");
        D("CostCenterCode", "Cost center", "organization", "CostCenterCode", "lookup", binding: "org:costCenterId");
        D("WorkLocation", "Work location", "organization", "WorkLocation", "text", binding: "emp.WorkLocation");
        D("FullName", "Full name", "personal", "FullName", "text", ar: true, binding: "emp.FullName");
        D("EnglishName", "English name", "personal", null, "text", ar: true, binding: "emp.EnglishName(fromFullName)");
        D("ArabicName", "Arabic name", "personal", "ArabicName", "text", binding: "emp.ArabicName");
        D("PreferredName", "Preferred name", "personal", "PreferredName", "text", binding: "emp.PreferredName");
        D("WorkEmail", "Work email", "personal", "WorkEmail", "email", ar: true, binding: "emp.WorkEmail");
        D("PersonalEmail", "Personal email", "personal", "PersonalEmail", "email", binding: "emp.PersonalEmail");
        D("Phone", "Mobile number", "personal", "Phone", "text", ar: true, binding: "emp.Phone");
        D("Gender", "Gender", "personal", "Gender", "select", ar: true, binding: "emp.Gender");
        D("DateOfBirth", "Date of birth", "personal", "DateOfBirth", "date", ar: true, binding: "emp.DateOfBirth");
        D("Nationality", "Nationality", "personal", "Nationality", "select", ar: true, binding: "emp.Nationality");
        D("MaritalStatus", "Marital status", "personal", "MaritalStatus", "select", binding: "emp.MaritalStatus");
        D("CountryCode", "Country", "personal", "CountryCode", "select", binding: "emp.CountryCode");
        D("EmergencyContactName", "Emergency contact name", "personal", "EmergencyContactName", "text", binding: "emp.EmergencyContactName");
        D("EmergencyContactPhone", "Emergency contact phone", "personal", "EmergencyContactPhone", "text", binding: "emp.EmergencyContactPhone");

        // ── Organisation / employment ────────────────────────────────────────
        D("Department", "Department", "organization", "Department", "lookup", ar: false, binding: "org:department(name)");
        D("DepartmentCode", "Department code", "organization", "DepartmentCode", "lookup", binding: "org:departmentId(code)");
        D("DepartmentId", "Department", "organization", null, "lookup", ar: true, binding: "org:departmentId");
        D("Designation", "Designation", "organization", "Designation", "lookup", binding: "org:designation(title)");
        D("DesignationId", "Designation", "organization", null, "lookup", ar: true, binding: "org:designationId");
        D("JobTitle", "Job title", "employment", "JobTitle", "text", binding: "emp.JobTitle");
        D("EmploymentType", "Employment type", "employment", "EmploymentType", "select", ar: true, binding: "emp.EmploymentType");
        D("ContractType", "Contract type", "employment", "ContractType", "select", ar: true, binding: "emp.ContractType");
        D("Grade", "Grade", "organization", "Grade", "lookup", binding: "org:gradeId");
        D("PositionCode", "Position", "organization", "PositionCode", "lookup", binding: "org:positionId");
        D("ManagerEmployeeCode", "Line manager", "organization", "ManagerEmployeeCode", "lookup", binding: "emp.ManagerEmployeeId(code)");
        D("ManagerEmail", "Line manager email", "organization", "ManagerEmail", "text", binding: "emp.ManagerEmployeeId(email)");
        D("SupervisorEmployeeCode", "Supervisor", "organization", "SupervisorEmployeeCode", "lookup", binding: "emp.SupervisorEmployeeId(code)");
        D("SupervisorEmail", "Supervisor email", "organization", "SupervisorEmail", "text", binding: "emp.SupervisorEmployeeId(email)");
        D("Status", "Status", "employment", "Status", "select", binding: "emp.Status");
        D("JoiningDate", "Joining date", "employment", "JoiningDate", "date", ar: true, binding: "emp.JoiningDate");
        D("ConfirmationDate", "Confirmation date", "employment", "ConfirmationDate", "date", binding: "emp.ConfirmationDate");
        D("ProbationStartDate", "Probation start", "employment", "ProbationStartDate", "date", binding: "emp.ProbationStartDate");
        D("ProbationEndDate", "Probation end", "employment", "ProbationEndDate", "date", binding: "emp.ProbationEndDate");
        D("ContractStartDate", "Contract start", "employment", "ContractStartDate", "date", binding: "emp.ContractStartDate");
        D("ContractEndDate", "Contract end", "employment", "ContractEndDate", "date", binding: "emp.ContractEndDate");
        D("NoticePeriodDays", "Notice period (days)", "employment", "NoticePeriodDays", "number", binding: "emp.NoticePeriodDays");
        D("ShiftPolicyCode", "Shift policy", "employment", "ShiftPolicyCode", "text", binding: "emp.ShiftPolicyCode");
        D("LeavePolicyCode", "Leave policy", "employment", "LeavePolicyCode", "text", binding: "emp.LeavePolicyCode");
        D("AttendancePolicyCode", "Attendance policy", "employment", "AttendancePolicyCode", "text", binding: "emp.AttendancePolicyCode");

        // ── Salary / payroll ─────────────────────────────────────────────────
        D("SalaryStructure", "Salary", "salary", null, "number", sensitive: true, ar: true, binding: "salary");
        D("SalaryStructureCode", "Salary structure code", "salary", "SalaryStructureCode", "text", binding: "salary:structureCode");
        D("BasicSalary", "Basic salary", "salary", "BasicSalary", "number", sensitive: true, binding: "salary:basicSalary");
        D("HousingAllowance", "Housing allowance", "salary", "HousingAllowance", "number", sensitive: true, binding: "salary:housingAllowance");
        D("TransportAllowance", "Transport allowance", "salary", "TransportAllowance", "number", sensitive: true, binding: "salary:transportAllowance");
        D("FoodAllowance", "Food allowance", "salary", "FoodAllowance", "number", sensitive: true, binding: "salary:foodAllowance");
        D("MobileAllowance", "Mobile allowance", "salary", "MobileAllowance", "number", sensitive: true, binding: "salary:mobileAllowance");
        D("OtherAllowance", "Other allowance", "salary", "OtherAllowance", "number", sensitive: true, binding: "salary:otherAllowance");
        D("FixedDeduction", "Fixed deduction", "salary", "FixedDeduction", "number", sensitive: true, binding: "salary:fixedDeduction");
        D("Currency", "Currency", "salary", "Currency", "select", binding: "payrollProfile.salaryCurrency");
        D("PayrollGroup", "Payroll group", "payroll", "PayrollGroup", "text", binding: "payrollProfile.payrollGroup");
        D("PaymentMethod", "Payment method", "payroll", "PaymentMethod", "select", ar: true, binding: "payrollProfile.paymentMethod");
        D("BankIban", "IBAN (WPS)", "payroll", "IBAN", "text", sensitive: true, ar: true, binding: "payrollProfile.iban");
        D("AccountNumber", "Bank account number", "payroll", "AccountNumber", "text", sensitive: true, binding: "payrollProfile.accountNumber");
        D("BankName", "Bank name", "payroll", "BankName", "text", binding: "payrollProfile.bankName");
        D("BankRoutingCode", "Bank routing code", "payroll", "BankRoutingCode", "text", sensitive: true, ar: true, binding: "payrollProfile.bankRoutingCode");
        D("MolId", "MOL personal ID", "payroll", "MolId", "text", sensitive: true, ar: true, binding: "payrollProfile.molId");
        // Social-insurance reference binds to ONE typed column but carries the correct national-scheme name
        // per country (GPSSA/GRSIA/PIFSS/SPF/SIO). SA uses the dedicated GosiReference field, so this row is
        // scoped to the other five. Visible for all residents (requiredness by nationality comes from the floor).
        D("SocialInsuranceReference", "Social-insurance reference", "payroll", "SocialInsuranceReference", "text", ar: true,
            countries: SocInsCountries, binding: "payrollProfile.socialInsuranceReference", applicability: All,
            labelByCountry: new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
            {
                ["AE"] = "GPSSA reference", ["QA"] = "GRSIA reference", ["KW"] = "PIFSS reference",
                ["OM"] = "SPF reference (PASI)", ["BH"] = "SIO reference",
            });

        // ── GCC statutory identity — numbers + expiries (COUNTRY × NATIONALITY axes) ──────────
        D("PassportNumber", "Passport number", "identity", "PassportNumber", "text", sensitive: true, ar: true, binding: "emp.PassportNumber",
            complianceFieldKey: "passport_number", expiryKey: "PassportExpiryDate");
        D("PassportIssueDate", "Passport issue date", "identity", "PassportIssueDate", "date", binding: "emp.PassportIssueDate");
        D("PassportExpiryDate", "Passport expiry", "identity", "PassportExpiryDate", "date", sensitive: true, ar: true, binding: "emp.PassportExpiryDate");
        // Residence visa (non-GCC expats only — GCC common-market reciprocity).
        D("VisaNumber", "Visa number", "identity", "VisaNumber", "text", ar: true, binding: "emp.VisaNumber", applicability: Expat,
            complianceFieldKey: "visa_number", expiryKey: "VisaExpiryDate");
        D("VisaIssueDate", "Visa issue date", "identity", "VisaIssueDate", "date", binding: "emp.VisaIssueDate", applicability: Expat);
        D("VisaExpiryDate", "Visa expiry", "identity", "VisaExpiryDate", "date", sensitive: true, ar: true, binding: "emp.VisaExpiryDate", applicability: Expat);
        D("VisaFileNumber", "Visa file number", "identity", "VisaFileNumber", "text", binding: "emp.VisaFileNumber", applicability: Expat,
            complianceFieldKey: "visa_file_number");
        // SA: Iqama (residence permit) is the non-GCC-expat identity; IdNumber (Hawiyya) is the host national's.
        D("IqamaNumber", "Iqama (residence permit) no.", "identity", "IqamaNumber", "text", sensitive: true, ar: true, countries: Sa,
            binding: "emp.IqamaNumber", applicability: Expat, complianceFieldKey: "iqama_number", expiryKey: "IqamaExpiry");
        D("IqamaExpiry", "Iqama expiry", "identity", "IqamaExpiry", "date", ar: true, countries: Sa, binding: "emp.IqamaExpiryDate", applicability: Expat);
        D("MuqeemNumber", "Muqeem number", "identity", "MuqeemNumber", "text", ar: true, countries: Sa, binding: "emp.MuqeemNumber",
            complianceFieldKey: "muqeem_reference");
        D("GosiReference", "GOSI reference", "identity", "GosiReference", "text", ar: true, countries: Sa, binding: "emp.GosiReference",
            complianceFieldKey: "gosi_reference");
        D("QiwaContractNumber", "Qiwa contract number", "identity", "QiwaContractNumber", "text", ar: true, countries: Sa, binding: "emp.QiwaContractNumber",
            complianceFieldKey: "qiwa_contract_reference");
        D("EmiratesId", "Emirates ID", "identity", "EmiratesId", "text", sensitive: true, ar: true, countries: Ae, binding: "emp.EmiratesId",
            complianceFieldKey: "emirates_id", expiryKey: "EmiratesIdExpiry");
        D("EmiratesIdExpiry", "Emirates ID expiry", "identity", "EmiratesIdExpiry", "date", ar: true, countries: Ae, binding: "emp.EmiratesIdExpiryDate");
        D("LaborCardNumber", "Labour card number", "identity", "LaborCardNumber", "text", ar: true, countries: Ae, binding: "emp.LaborCardNumber",
            applicability: Expat, complianceFieldKey: "labor_card_number");
        D("Qid", "Qatar ID (QID)", "identity", "Qid", "text", ar: true, countries: Qa, binding: "emp.Qid",
            complianceFieldKey: "qid", expiryKey: "QidExpiry");
        D("QidExpiry", "QID expiry", "identity", "QidExpiry", "date", ar: true, countries: Qa, binding: "emp.QidExpiryDate");
        // KW/OM/BH share the typed CivilId column but each carries its own local English name.
        D("CivilId", "Civil ID", "identity", "CivilId", "text", ar: true, countries: KwOmBh, binding: "emp.CivilId",
            complianceFieldKey: "civil_id", expiryKey: "CivilIdExpiry",
            labelByCountry: new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
            {
                ["KW"] = "Kuwait Civil ID", ["OM"] = "Oman Resident Card / Civil ID no.", ["BH"] = "Bahrain CPR (Personal no.)",
            });
        D("CivilIdExpiry", "Civil ID expiry", "identity", "CivilIdExpiry", "date", ar: true, countries: KwOmBh, binding: "emp.CivilIdExpiryDate",
            labelByCountry: new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
            {
                ["KW"] = "Kuwait Civil ID expiry", ["OM"] = "Resident Card expiry", ["BH"] = "CPR expiry",
            });
        // Work permit — non-GCC expats across all GCC states (floor gates it for AE; recommended elsewhere).
        D("WorkPermitNumber", "Work permit number", "identity", "WorkPermitNumber", "text", ar: true, countries: GccAll, binding: "emp.WorkPermitNumber",
            applicability: Expat, complianceFieldKey: "work_permit",
            labelByCountry: new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
            {
                ["AE"] = "MOHRE work permit / labour card no.", ["BH"] = "LMRA work permit no.",
            });
        D("WorkPermitIssueDate", "Work permit issue date", "identity", "WorkPermitIssueDate", "date", binding: "emp.WorkPermitIssueDate", applicability: Expat);
        D("ResidencyNumber", "Residency number", "identity", "ResidencyNumber", "text", countries: KwOmBh, binding: "emp.ResidencyNumber",
            applicability: Expat, complianceFieldKey: "residency_number");
        D("ResidencyIssueDate", "Residency issue date", "identity", "ResidencyIssueDate", "date", countries: KwOmBh, binding: "emp.ResidencyIssueDate", applicability: Expat);
        D("IdNumber", "National ID (Hawiyya)", "identity", "IdNumber", "text", sensitive: true, ar: true, countries: Sa, binding: "emp.IdNumber",
            applicability: HostNational, complianceFieldKey: "id_number");
        D("SponsorName", "Sponsor name", "identity", "SponsorName", "text", binding: "emp.SponsorName", applicability: Expat, complianceFieldKey: "sponsor");

        // ── Qiwa integration block ───────────────────────────────────────────
        D("SaudiOrNonSaudi", "Saudi / non-Saudi", "qiwa", "SaudiOrNonSaudi", "select", countries: Sa, binding: "emp.SaudiOrNonSaudi");
        D("IdType", "ID type", "qiwa", "IdType", "select", countries: Sa, binding: "emp.IdType");
        D("OccupationCode", "Occupation code", "qiwa", "OccupationCode", "text", countries: Sa, binding: "emp.OccupationCode");
        D("EstablishmentId", "Establishment ID", "qiwa", "EstablishmentId", "text", countries: Sa, binding: "emp.EstablishmentId");
        D("WorkLocationId", "Work location ID", "qiwa", "WorkLocationId", "text", countries: Sa, binding: "emp.WorkLocationId");
        D("ContractReference", "Contract reference", "qiwa", "ContractReference", "text", countries: Sa, binding: "emp.ContractReference");
        D("WorkPermitReference", "Work permit reference", "qiwa", "WorkPermitReference", "text", countries: Sa, binding: "emp.WorkPermitReference");
        D("QiwaEmployeeReference", "Qiwa employee reference", "qiwa", "QiwaEmployeeReference", "text", countries: Sa, binding: "emp.QiwaEmployeeReference");
        D("QiwaSyncStatus", "Qiwa sync status", "qiwa", "QiwaSyncStatus", "text", countries: Sa, binding: "emp.QiwaSyncStatus");

        _ = Kw; _ = Bh; // reserved groupings (kept for readability of jurisdiction intent)
        return list;
    }
}
