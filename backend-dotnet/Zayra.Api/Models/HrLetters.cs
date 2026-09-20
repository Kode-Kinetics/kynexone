using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

/// <summary>
/// The closed vocabulary of HR letters the product can issue.
///
/// Closed on purpose, and for the same reason <c>EmployeeManagementService.AllowedSeparationTypes</c>
/// is closed: a letter type is a reference-number prefix, a template lookup key and a permission
/// boundary all at once. A free-text "Salary Cert" or "salary_certificate" would silently create a
/// second reference series for the same document, and a bank asking HR to confirm reference
/// SAL-CERT-2026-00042 would be told it does not exist.
/// </summary>
public static class HrLetterTypes
{
    public const string SalaryCertificate = "SalaryCertificate";
    public const string SalaryTransferLetter = "SalaryTransferLetter";
    public const string EmploymentVerification = "EmploymentVerification";
    public const string AppointmentLetter = "AppointmentLetter";
    public const string ExperienceCertificate = "ExperienceCertificate";

    // Deliberately absent: the offer letter. It is addressed to a CANDIDATE, who has no
    // Employee row to hang a register entry on, and it already has its own issuance path in
    // the recruitment module. Pulling it in here would mean either a nullable EmployeeId on
    // the register (defeating the point) or a fake employee. Left where it is.

    /// <summary>Reference-number prefix per type. Stable forever: it is printed on issued documents.</summary>
    public static readonly IReadOnlyDictionary<string, string> Prefixes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SalaryCertificate] = "SAL-CERT",
            [SalaryTransferLetter] = "BANK-LTR",
            [EmploymentVerification] = "EMP-VER",
            [AppointmentLetter] = "APPT",
            [ExperienceCertificate] = "EXP",
        };

    public static readonly IReadOnlyList<string> All = [.. Prefixes.Keys];

    /// <summary>
    /// Types an employee may request for themselves through ESS. Deliberately excludes the offer
    /// letter (it belongs to a candidate, not an employee) and the appointment letter (issued once,
    /// by HR, at hire — an employee re-requesting it is a reprint, handled as a ticket).
    /// </summary>
    public static readonly IReadOnlyList<string> EmployeeRequestable =
        [SalaryCertificate, SalaryTransferLetter, EmploymentVerification, ExperienceCertificate];

    public static bool IsKnown(string? value) =>
        value is not null && Prefixes.ContainsKey(value);

    /// <summary>
    /// Maps a caller-supplied type to the canonical spelling, or null. Accepts the canonical form
    /// and the reference prefix (so "SAL-CERT", the code the seeded HR ticket category already
    /// uses, resolves to SalaryCertificate rather than 400-ing).
    /// </summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        foreach (var (type, prefix) in Prefixes)
        {
            if (string.Equals(type, trimmed, StringComparison.OrdinalIgnoreCase)) return type;
            if (string.Equals(prefix, trimmed, StringComparison.OrdinalIgnoreCase)) return type;
        }
        return null;
    }
}

public static class HrLetterLanguages
{
    public const string English = "en";
    public const string Arabic = "ar";
    public const string Bilingual = "bilingual";

    public static readonly IReadOnlyList<string> All = [English, Arabic, Bilingual];

    public static bool IsKnown(string? value) =>
        value is not null && All.Contains(value, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// A tenant-configurable letter template: the body text, with <c>{{token}}</c> merge fields,
/// in English and Arabic.
///
/// <para><b>Company scope:</b> <see cref="ICompanyScoped"/>, not <see cref="ICompanyScopedOperational"/>.
/// A template is configuration, and a <c>CompanyId == null</c> row is the deliberate
/// "every legal entity in this tenant uses this wording" case — the seeded defaults are exactly
/// that. Under the operational tier a company-scoped HR officer would see no template at all and
/// could issue nothing.</para>
/// </summary>
public class HrLetterTemplate : ITenantOwned, ICompanyScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    /// <summary>Null = tenant-wide default. Set = an override for one legal entity.</summary>
    public Guid? CompanyId { get; set; }

    /// <summary>One of <see cref="HrLetterTypes"/>.</summary>
    public string LetterType { get; set; } = string.Empty;

    public string NameEn { get; set; } = string.Empty;
    public string NameAr { get; set; } = string.Empty;

    /// <summary>en / ar / bilingual — what this template is able to render.</summary>
    public string Language { get; set; } = HrLetterLanguages.Bilingual;

    /// <summary>Document heading, e.g. "SALARY CERTIFICATE".</summary>
    public string TitleEn { get; set; } = string.Empty;
    public string TitleAr { get; set; } = string.Empty;

    /// <summary>Body paragraphs, blank-line separated, containing {{merge_tokens}}.</summary>
    public string BodyEn { get; set; } = string.Empty;
    public string BodyAr { get; set; } = string.Empty;

    /// <summary>Closing line above the signature block.</summary>
    public string ClosingEn { get; set; } = string.Empty;
    public string ClosingAr { get; set; } = string.Empty;

    /// <summary>
    /// True for the rows AuthSeeder plants in a new tenant. A system default may be edited
    /// (that is the point of the feature) but never hard-deleted, so a tenant that ruins a
    /// template can always be reset.
    /// </summary>
    public bool IsSystemDefault { get; set; }

    public bool IsActive { get; set; } = true;
    public int Version { get; set; } = 1;

    public Guid? CreatedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? UpdatedByUserId { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public bool IsDeleted { get; set; }
}

/// <summary>
/// The register of issued letters — one row per PDF that left the building.
///
/// Before this table the only trace of an issued letter was an audit row saying
/// "employee.letter.experience happened", and the reference number printed on the document
/// (<c>EXP-{code}-{yyyyMM}</c>) was recomputed inline and stored nowhere, so two experience
/// letters for the same person in the same month carried the same reference. A bank or an
/// embassy that phones HR quoting a reference needs an answer; that is what this is for.
/// </summary>
public class IssuedLetter : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    /// <summary>The employing legal entity, copied from the employee at issuance.</summary>
    public Guid? CompanyId { get; set; }

    public int EmployeeId { get; set; }

    /// <summary>Denormalised so the register still reads correctly after a rename or a leaver purge.</summary>
    public string EmployeeCode { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty;

    /// <summary>One of <see cref="HrLetterTypes"/>.</summary>
    public string LetterType { get; set; } = string.Empty;

    /// <summary>
    /// The stored, human-quotable reference, e.g. <c>SAL-CERT-2026-00042</c>. Unique per tenant —
    /// enforced by a database unique index, not by hope.
    /// </summary>
    public string ReferenceNumber { get; set; } = string.Empty;

    /// <summary>The ordinal within (tenant, letter type, year) that produced the reference.</summary>
    public int SequenceNumber { get; set; }
    public int SequenceYear { get; set; }

    public Guid? TemplateId { get; set; }
    public string Language { get; set; } = HrLetterLanguages.Bilingual;

    /// <summary>Free text from the requester: "bank loan", "Schengen visa", "school admission".</summary>
    public string Purpose { get; set; } = string.Empty;

    /// <summary>"To whom it may concern" unless the requester named a bank or an embassy.</summary>
    public string AddresseeName { get; set; } = string.Empty;

    public Guid? IssuedByUserId { get; set; }

    /// <summary>The named human in the signature block — never the hard-coded "HR Department".</summary>
    public string IssuedByName { get; set; } = string.Empty;
    public string IssuedByTitle { get; set; } = string.Empty;
    public DateTime IssuedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>SHA-256 of the exact PDF bytes, so a produced document can be proven unaltered.</summary>
    public string FileHash { get; set; } = string.Empty;
    public int FileSizeBytes { get; set; }

    /// <summary>
    /// The merged field values as rendered, so the register can answer "what salary did the
    /// certificate we issued in March actually state?" after the salary has since changed.
    /// </summary>
    public string MergedValuesJson { get; set; } = "{}";

    /// <summary>
    /// The rendered title/paragraphs/closing per language, exactly as they went onto the page.
    /// Kept so HR can reprint the document a bank has lost without the reprint silently picking
    /// up a template edit made since — a reprint that says something different from the original
    /// under the same reference number is worse than no reprint.
    /// </summary>
    public string RenderedContentJson { get; set; } = "{}";

    /// <summary>Set when the issuance answered an ESS request rather than starting with HR.</summary>
    public Guid? DocumentRequestId { get; set; }

    /// <summary>Set when the issuance closed an HR Request Centre ticket (the SAL-CERT queue).</summary>
    public Guid? HrRequestId { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; }
}
