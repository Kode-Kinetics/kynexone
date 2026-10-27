using System.Security.Claims;
using Zayra.Api.Application.Employees.Onboarding;

namespace Zayra.Api.Application.Employees.Ess;

/// <summary>Section keys of the ESS "Complete your profile" flow (contract C-1; P3/P4 never look inside a section).</summary>
public static class EssSectionKeys
{
    public const string Person = "person";
    public const string Emergency = "emergency";
    public const string ArabicName = "arabicName";
    public const string Id = "id";
    public const string Bank = "bank";
    public const string Documents = "documents";

    public static readonly IReadOnlyList<string> All = [Person, Emergency, ArabicName, Id, Bank, Documents];
    public static bool IsKnown(string? key) => key is not null && All.Contains(key, StringComparer.Ordinal);
}

public enum EssFieldKind { Text, Email, Phone, Enum, NationalId, Iqama, Border, Iban, BankName }

/// <param name="LabelKey">i18n key for the field label (EN + AR in onboardingP2.ts).</param>
public sealed record EssFieldSpec(string Key, string LabelKey, int MaxLength, bool Required, EssFieldKind Kind,
    bool Sensitive = false, IReadOnlyList<string>? Options = null);

public sealed record EssSectionSpec(string Key, bool RequiresStepUp, IReadOnlyList<EssFieldSpec> Fields);

/// <summary>Facts about an employee that are not columns on <c>Employee</c> itself.</summary>
public sealed record EssEmployeeFacts(NationalityClass? NationalityClass, string? CompanyCountry, bool HasIdDocument);

/// <summary>Stable constants of the flow.</summary>
public static class EssCompletion
{
    /// <summary>Version of the privacy notice the employee is shown. A submit must echo it (PDPL: notice before collection).</summary>
    public const string NoticeVersion = "ess-1";
    public const int MaxSubmissionsPerDay = 5;
    public const int MaxAttachmentsPerSection = 3;
    public const long MaxFileBytes = 5L * 1024 * 1024;
    public const long MaxRequestBytes = 15L * 1024 * 1024;
    public const int RejectedRetentionDays = 30;
    public const string StepUpHeader = "X-Step-Up";

    // Request states reported to the client.
    public const string Missing = "Missing";
    public const string Confirm = "Confirm";
    public const string Pending = "Pending";
    public const string ChangesRequested = "ChangesRequested";
    public const string Accepted = "Accepted";
    public const string AwaitingFinance = "AwaitingFinance";
    /// <summary>Nothing missing, nothing asked for, nothing sent: not listed, but the employee may still send a correction.</summary>
    public const string Idle = "Idle";

    /// <summary>The legacy (pre-section) request reason; maps to person + emergency.</summary>
    public const string LegacyReason = "Employee profile completion";
}

public enum ScanVerdict { Clean, PendingScan, Quarantined }

/// <summary>Malware scan hook for ESS uploads. The default is a no-op; a real engine plugs in here without touching the flow.</summary>
public interface IMalwareScanner
{
    Task<ScanVerdict> ScanAsync(ReadOnlyMemory<byte> bytes, string fileName, CancellationToken ct);
}

public sealed class NoopMalwareScanner : IMalwareScanner
{
    public Task<ScanVerdict> ScanAsync(ReadOnlyMemory<byte> bytes, string fileName, CancellationToken ct) =>
        Task.FromResult(ScanVerdict.Clean);
}

/// <summary>Contract C-1: the only thing P3 calls. Asks the employee to complete the named sections.</summary>
public interface IEssSectionRequester
{
    Task RequestAsync(Guid tenantId, int employeeId, IReadOnlyList<string> sections, CancellationToken ct);
}

// ── Step-up (contract C-2, FROZEN — P4 consumes it) ─────────────────────────────────────────────────

public static class StepUpPurposes
{
    public const string EssSensitive = "ess.sensitive";
    public const string Esign = "esign";
    public static readonly TimeSpan EssSensitiveMaxAge = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan EsignMaxAge = TimeSpan.FromMinutes(5);
    public static bool IsKnown(string? purpose) => purpose is EssSensitive or Esign;
}

public sealed record StepUpCredential(string? Password, string? MfaCode);

/// <param name="Error">invalid_credentials | mfa_required | locked | unknown_purpose when <paramref name="Ok"/> is false.</param>
public sealed record StepUpResult(bool Ok, string? Token, DateTime? ExpiresAtUtc, string? Error);

/// <summary>
/// Proof that the signed-in person re-entered their password (and second factor, when enrolled) moments ago.
/// Stateless: the token is HMAC-signed and carries <c>{sub, tenant, purpose, iat, jti, bind}</c>; it is sent as
/// <c>X-Step-Up</c>. A missing or stale token is answered 401 <c>step_up_required</c> by the caller.
/// </summary>
public interface IStepUpService
{
    Task<StepUpResult> VerifyAsync(Guid userId, string purpose, StepUpCredential credential, CancellationToken ct);

    /// <summary>True when <paramref name="token"/> was issued to this principal (user + tenant) for <paramref name="purpose"/> no longer than <paramref name="max"/> ago.</summary>
    bool IsFresh(string? token, ClaimsPrincipal principal, string purpose, TimeSpan max);

    bool IsFresh(string? token, Guid userId, Guid tenantId, string purpose, TimeSpan max);

    /// <summary>The <c>jti</c> of a valid token (for P4's single-use ledger), or null when the token is not valid for this user/tenant/purpose.</summary>
    string? ReadJti(string? token, Guid userId, Guid tenantId, string purpose);
}

/// <summary>Pre-persistence checks shared by the controller and its tests.</summary>
public static class EssSubmitGuard
{
    /// <summary>"notice_required" unless the client echoed the current notice version.</summary>
    public static string? NoticeError(string? echoed) =>
        string.Equals(echoed, EssCompletion.NoticeVersion, StringComparison.Ordinal) ? null : EssProblemCodes.NoticeRequired;
}

/// <summary>Problem codes of the flow. Each has <c>ess.completion.error.&lt;code&gt;</c> in EN and AR (onboardingP2.ts).</summary>
public static class EssProblemCodes
{
    public const string StepUpRequired = "step_up_required";
    public const string UnknownField = "unknown_field";
    public const string ValidationFailed = "validation_failed";
    public const string SectionPending = "section_pending";
    public const string SectionLocked = "section_locked";
    public const string NoticeRequired = "notice_required";
    public const string RateLimited = "rate_limited";
    public const string UploadRejected = "upload_rejected";
    public const string EssNotLinked = "ess_not_linked";
    public const string InvalidCredentials = "invalid_credentials";
    public const string Locked = "locked";
    public const string MfaRequired = "mfa_required";
    public const string OverrideNotPermitted = "override_not_permitted";
    public const string OverrideReasonRequired = "override_reason_required";
    public const string DocumentQuarantined = "document_quarantined";
    public const string NoteRequired = "note_required";
    public const string NotFound = "not_found";
    public const string FinancePending = "finance_pending";

    public static readonly IReadOnlyList<string> All =
    [
        StepUpRequired, UnknownField, ValidationFailed, SectionPending, SectionLocked, NoticeRequired, RateLimited, UploadRejected,
        EssNotLinked, InvalidCredentials, Locked, MfaRequired, OverrideNotPermitted, OverrideReasonRequired, DocumentQuarantined, NoteRequired, NotFound, FinancePending,
    ];
}
