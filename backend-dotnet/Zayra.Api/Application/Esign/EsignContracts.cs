using System.Security.Cryptography;
using System.Text;

namespace Zayra.Api.Application.Esign;

/// <summary>
/// P4 — built-in click-to-sign ACKNOWLEDGEMENT of a stored document. This is an electronic acknowledgement, never a
/// "qualified electronic signature" (CD13/CD50); its legal weight is an owner / KSA-counsel item.
/// </summary>
public static class EsignConstants
{
    public const string DocumentType = "Signed acknowledgement";
    public const string MethodLegacy = "legacy_click";
    public const string MethodEss = "esign_ess";
    public const string MethodHrDevice = "esign_hr_device";
    public const string AuthPassword = "password";
    public const string AuthPasswordMfa = "password+totp";
    public const string StepUpHeader = "X-Step-Up";
    public const string IdempotencyHeader = "Idempotency-Key";
    public const int MaxSignsPerHour = 10;
    /// <summary>CD52: acknowledgement evidence is kept for the life of employment plus this many years (to be confirmed with KSA counsel).</summary>
    public const int RetentionYearsAfterEmployment = 10;
    /// <summary>The first record of a tenant's chain points at this.</summary>
    public static readonly string ZeroHash = new('0', 64);
    public const string ProblemKeyPrefix = "esign.error.";
}

/// <summary>Problem codes of the flow. Each has <c>esign.error.&lt;code&gt;</c> in EN and AR (onboardingP4.ts).</summary>
public static class EsignProblemCodes
{
    public const string StepUpRequired = "step_up_required";
    public const string TypedNameMismatch = "typed_name_mismatch";
    public const string DocumentChanged = "document_changed";
    public const string DocumentTampered = "document_tampered";
    public const string AlreadySigned = "already_signed";
    public const string ConsentVersionStale = "consent_version_stale";
    public const string SubjectCannotSignForEmployee = "subject_cannot_sign_for_employee";
    public const string EmployeeDeviceAuthFailed = "employee_device_auth_failed";
    public const string RateLimited = "rate_limited";
    public const string DocumentNotSignable = "document_not_signable";
    public const string SignatureRequired = "signature_required";
    public const string NotFound = "not_found";
    public const string EssNotLinked = "ess_not_linked";
    public const string ValidationFailed = "validation_failed";

    public static readonly IReadOnlyList<string> All =
    [
        StepUpRequired, TypedNameMismatch, DocumentChanged, DocumentTampered, AlreadySigned, ConsentVersionStale, SubjectCannotSignForEmployee,
        EmployeeDeviceAuthFailed, RateLimited, DocumentNotSignable, SignatureRequired, NotFound, EssNotLinked, ValidationFailed,
    ];
}

/// <summary>Body of a sign request. There is deliberately no time field: the server's clock is the only clock.</summary>
public sealed record EsignSignBody(string? DocumentSha256, string? ConsentVersion, string? TypedName, string? Language, string? DeviceId);

/// <summary>The consent statement the signer sees, in both languages, versioned. Changing the wording means a new version.</summary>
public static class ConsentCatalog
{
    public const string Current = "ack-2";

    // ack-1 is kept word for word so that a record that carries it can still be shown with what it agreed to. It can no longer be signed.
    private const string EnV1 =
        "I confirm that I have read and understood the document above in full. By typing my full name and confirming with my password, " +
        "I electronically acknowledge it. This is an electronic acknowledgement, not a qualified electronic signature.";
    private const string ArV1 =
        "أقرّ بأنني قرأت المستند أعلاه وفهمته بالكامل. بكتابة اسمي الكامل وتأكيد كلمة المرور، أُقرّ به إلكترونياً. " +
        "هذا إقرار إلكتروني وليس توقيعاً إلكترونياً مؤهلاً.";

    // ack-2 (CD64): says what is recorded (the evidence stores the IP address and the browser/device string, which are personal data) and
    // uses «معتمدًا», the Saudi term, instead of the EU borrowing «مؤهلًا».
    private const string EnV2 =
        "I confirm that I have read and understood the document above in full. By typing my full name and confirming with my password, " +
        "I electronically acknowledge it. This is an electronic acknowledgement, not a qualified electronic signature. " +
        "We record your typed name, the time, your IP address and your device details (browser and device type) as evidence of this acknowledgement " +
        "and keep them for the period set by your company.";
    private const string ArV2 =
        "أقرّ بأنني قرأت المستند أعلاه وفهمته بالكامل. بكتابة اسمي الكامل وتأكيد كلمة المرور، أُقرّ به إلكترونيًا. " +
        "هذا إقرار إلكتروني وليس توقيعًا إلكترونيًا معتمدًا. " +
        "نسجّل اسمك المكتوب والوقت وعنوان IP وبيانات جهازك (المتصفح ونوع الجهاز) كدليل على هذا الإقرار، ونحتفظ بها للمدة التي تحددها شركتك.";

    /// <summary>Only the current version can be signed.</summary>
    public static bool IsKnown(string? version) => string.Equals(version, Current, StringComparison.Ordinal);

    public static string Text(string version, string language)
    {
        var arabic = string.Equals(language, "ar", StringComparison.OrdinalIgnoreCase);
        return version switch
        {
            "ack-1" => arabic ? ArV1 : EnV1,
            "ack-2" => arabic ? ArV2 : EnV2,
            _ => throw new ArgumentOutOfRangeException(nameof(version)),
        };
    }

    /// <summary>The order the two texts are shown in: the reader's language first, so an Arabic session reads Arabic first.</summary>
    public static IReadOnlyList<string> Order(string? language) =>
        string.Equals(language, "ar", StringComparison.OrdinalIgnoreCase) ? ["ar", "en"] : ["en", "ar"];

    /// <summary>SHA-256 over the exact text of BOTH languages (EN then AR, newline separated): what the signer agreed to is fixed by one hash.</summary>
    public static string TextSha256(string version) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Text(version, "en") + "\n" + Text(version, "ar")))).ToLowerInvariant();
}

/// <summary>Normalised comparison of the name the signer typed with the names on the employee record (EN or AR).</summary>
public static class TypedNameMatcher
{
    public static string Normalise(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var decomposed = value.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed.Normalize(NormalizationForm.FormD))
        {
            var category = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category is System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.Format) continue;   // diacritics, harakat
            if (ch == 'ـ') continue;                                                                                                   // tatweel
            sb.Append(ch switch { 'أ' or 'إ' or 'آ' or 'ٱ' => 'ا', 'ى' => 'ي', 'ة' => 'ه', _ => ch });
        }
        var folded = sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
        return string.Join(' ', folded.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    public static bool Matches(string? typed, params string?[] recordNames)
    {
        var t = Normalise(typed);
        return t.Length > 0 && recordNames.Any(n => Normalise(n) is { Length: > 0 } r && r == t);
    }
}

/// <summary>The short code printed on the certificate, derived from the record hash so it can be checked against the chain.</summary>
public static class VerificationCode
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string From(string recordHash)
    {
        var bytes = Convert.FromHexString(recordHash);
        var sb = new StringBuilder(14);
        for (var i = 0; i < 12; i++)
        {
            if (i > 0 && i % 4 == 0) sb.Append('-');
            sb.Append(Alphabet[bytes[i] % Alphabet.Length]);
        }
        return sb.ToString();
    }
}
