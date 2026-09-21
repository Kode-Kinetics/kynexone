using Zayra.Api.Domain.Entities;
namespace Zayra.Api.Models;

public class ESSDashboardPreference : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public int EmployeeId { get; set; }
    public string WidgetLayoutJson { get; set; } = "{}";
    public string Locale { get; set; } = "en";
    public bool RtlEnabled { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}

public class EmployeeProfileChangeRequest : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public int EmployeeId { get; set; }
    public string RequestedChangesJson { get; set; } = "{}";
    public string Reason { get; set; } = string.Empty;
    public string Status { get; set; } = "PendingHR";
    public bool ContainsSensitiveFields { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
    public Guid? DecidedBy { get; set; }
}

/// <summary>
/// An employee asking HR for a document — the salary certificate for the bank, the
/// employment-verification letter for the landlord.
///
/// <para>This entity and its DbSet already existed and had <b>zero references in any
/// controller</b>: dead scaffolding for exactly this feature. Rather than stand up a parallel
/// queue it is now the ESS request path, with the decision/issuance columns below added.</para>
///
/// <para><b>Deliberately NOT <c>ICompanyScopedOperational</c>.</b> The table pre-dates company
/// scoping and has no CompanyId; adding the operational tier would filter every existing row
/// (all of which would have CompanyId == null) out of every company-scoped user's view. The
/// employee-row scope check in the controller (<c>IDataScopeService.CanAccessEmployee</c>) is the
/// access boundary here, as it is for HRRequest next door. The letter this produces —
/// <see cref="IssuedLetter"/> — IS company-scoped operational.</para>
/// </summary>
public class EmployeeDocumentRequest : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public int EmployeeId { get; set; }
    public string RequestType { get; set; } = string.Empty;
    public string DocumentType { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }

    // ── Letter issuance (feat/hr-documents) ────────────────────────────────────────────
    // Additive columns. Every one is nullable or has a default, so rows written before this
    // feature keep their meaning.

    /// <summary>Canonical <see cref="HrLetterTypes"/> value, when the request is for a letter.</summary>
    public string LetterType { get; set; } = string.Empty;

    /// <summary>en / ar / bilingual — what the employee asked for.</summary>
    public string Language { get; set; } = HrLetterLanguages.Bilingual;

    /// <summary>"Riyad Bank", "Embassy of Italy" — printed as the addressee when supplied.</summary>
    public string AddresseeName { get; set; } = string.Empty;

    /// <summary>The SAL-CERT (or sibling) ticket this request raised, so HR works one queue.</summary>
    public Guid? HrRequestId { get; set; }

    public DateTime? DecidedAtUtc { get; set; }
    public Guid? DecidedByUserId { get; set; }

    /// <summary>Mandatory when a request is declined; the employee is told why.</summary>
    public string DecisionNote { get; set; } = string.Empty;

    /// <summary>Set once HR issues; the register row that proves the document exists.</summary>
    public Guid? IssuedLetterId { get; set; }
}

/// <summary>Closed vocabulary for <see cref="EmployeeDocumentRequest.Status"/>.</summary>
public static class EmployeeDocumentRequestStatuses
{
    public const string Pending = "Pending";
    public const string Issued = "Issued";
    public const string Declined = "Declined";
    public const string Cancelled = "Cancelled";

    public static readonly IReadOnlyList<string> All = [Pending, Issued, Declined, Cancelled];
}

public class HRRequestCategory : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public int DefaultSlaHours { get; set; } = 48;
    public bool IsActive { get; set; } = true;
}

public class HRRequest : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public int EmployeeId { get; set; }
    public Guid? CategoryId { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Priority { get; set; } = "Normal";
    public string Status { get; set; } = "Open";
    public DateTime DueAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    /// <summary>W2-D (S1) — an EmployeeDocument the requesting employee owns, attached at creation.</summary>
    public Guid? AttachmentDocumentId { get; set; }
}

public class HRRequestComment : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid HRRequestId { get; set; }
    public int EmployeeId { get; set; }
    public Guid? UserId { get; set; }
    public string Comment { get; set; } = string.Empty;
    // "Employee" when written from the self-service portal, "HR" when written from the
    // HR Request Centre. Drives the two-sided thread display and the "HR responded?" /
    // "not responded yet" SLA indicator. Defaults to "Employee" for legacy rows.
    public string AuthorType { get; set; } = "Employee";
    public string AuthorName { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class HRRequestAttachment : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid HRRequestId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string StorageUrl { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class HRRequestSLA : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CategoryId { get; set; }
    public string Priority { get; set; } = "Normal";
    public int SlaHours { get; set; } = 48;
    public bool IsActive { get; set; } = true;
}

public class EmployeePolicyAcknowledgement : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public int EmployeeId { get; set; }
    public Guid PolicyId { get; set; }
    public DateTime AcknowledgedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? UserId { get; set; }
}

public class EmployeeAnnouncement : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string Audience { get; set; } = "All";
    public DateTime PublishedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAtUtc { get; set; }
    public bool IsActive { get; set; } = true;
}

public class EmployeeNotification : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public int EmployeeId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string NotificationType { get; set; } = "Info";
    public bool IsRead { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ReadAtUtc { get; set; }
}

public class EmployeeNotificationPreference : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public int EmployeeId { get; set; }
    public bool EmailEnabled { get; set; } = true;
    public bool PushEnabled { get; set; } = true;
    public bool SmsEnabled { get; set; }
    public string QuietHoursJson { get; set; } = "{}";
}

public class EmployeePayslipAccessLog : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public int EmployeeId { get; set; }
    public Guid PayslipId { get; set; }
    public string Action { get; set; } = "View";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? UserId { get; set; }
}

public class EmployeeSelfServiceAuditLog : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public int EmployeeId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string EntityName { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? UserId { get; set; }
}

public class EmployeeAIQueryLog : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public int EmployeeId { get; set; }
    public string Question { get; set; } = string.Empty;
    public string Answer { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? UserId { get; set; }
}

public class EmployeeActionItem : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public int EmployeeId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Status { get; set; } = "Open";
    public DateTime? DueAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class EmployeeSentimentPulse : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public int EmployeeId { get; set; }
    public int Score { get; set; }
    public string Comment { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class EmployeeMobileDevice : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public int EmployeeId { get; set; }
    public string DeviceIdentifier { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string PushToken { get; set; } = string.Empty;
    public bool BiometricEnabled { get; set; }
    public DateTime RegisteredAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastSeenAtUtc { get; set; }
}
