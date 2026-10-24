using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

// The hosted service's vendor tables. Nothing in the product reads or writes them any more; the entities
// stay mapped only so the schema is unchanged until WP5 drops the tables (implementation prompt §4.1).

// ── Platform role constants ────────────────────────────────────────────────────

public static class PlatformRoles
{
    public const string Owner     = "Owner";
    public const string Admin     = "Admin";
    public const string Finance   = "Finance";
    public const string Support   = "Support";
    public const string Marketing = "Marketing";
    public const string Auditor   = "Auditor";

    public static readonly IReadOnlySet<string> All = new HashSet<string>
    {
        Owner, Admin, Finance, Support, Marketing, Auditor
    };
}

// ── Platform User (separate from tenant users) ────────────────────────────────

public class PlatformUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = null!;
    public string FullName { get; set; } = null!;
    public string PasswordHash { get; set; } = null!;
    public string Role { get; set; } = PlatformRoles.Admin; // Owner, Admin, Finance, Support, Marketing, Auditor
    public bool IsActive { get; set; } = true;
    public bool MfaEnabled { get; set; }
    // Encrypted via IDataProtector — never returned in responses.
    public string? MfaSecretEncrypted { get; set; }
    public DateTime? MfaConfiguredAtUtc { get; set; }
    /// <summary>
    /// SHA-256 hashes (hex, newline-separated) of the operator's UNUSED one-time recovery codes.
    /// Plaintext codes are shown once, at enrolment or regeneration, and never stored.
    /// </summary>
    public string? MfaRecoveryCodeHashes { get; set; }
    /// <summary>Last accepted TOTP time-step; any code at or below it is refused (replay protection).</summary>
    public long? MfaLastTotpStep { get; set; }
    public DateTime? LastLoginAtUtc { get; set; }
    public string? LastLoginIp { get; set; }
    // Brute-force lockout for platform-admin accounts (highest-privilege in the system). Mirrors the
    // tenant-user lockout in AuthService: after MaxFailed wrong passwords the account is locked for a
    // cool-off window. Without this, only the per-IP rate limiter slowed a distributed attack.
    public int FailedLoginCount { get; set; }
    public DateTime? LockoutEndUtc { get; set; }
    public const int MaxFailedLogins = 5;
    public const int LockoutMinutes = 15;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}

// ── Platform Support Sessions (break-glass access) ───────────────────────────

/// <summary>Audit-trail record for every platform-admin support access session.</summary>
public class PlatformSupportSession : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid TargetUserId { get; set; }
    public string TargetUserEmail { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string StartedByEmail { get; set; } = string.Empty;
    public string StartedByIp { get; set; } = string.Empty;
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    /// <summary>SHA-256 hash of the impersonation JWT — lets us match the token to a session on End.</summary>
    public string TokenHash { get; set; } = string.Empty;
    public bool IsActive => EndedAtUtc is null && DateTime.UtcNow < ExpiresAtUtc;
}

// ── Tenant Invoices ───────────────────────────────────────────────────────────

public class TenantInvoice : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string CurrencyCode { get; set; } = "USD";
    /// <summary>Draft | Sent | Paid | Overdue | Cancelled</summary>
    public string Status { get; set; } = "Draft";
    public string? PaymentMethod { get; set; }   // BankTransfer, Cheque, Online, etc.
    public string? PaymentReference { get; set; }
    public string? PeriodDescription { get; set; } // e.g. "June 2026"
    public DateOnly InvoiceDate { get; set; }
    public DateOnly DueDate { get; set; }
    public DateOnly? PaidDate { get; set; }
    public string? Notes { get; set; }
    public string? RecipientEmail { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? CreatedBy { get; set; }
}

public static class InvoiceStatuses
{
    public const string Draft         = "Draft";
    public const string Sent          = "Sent";
    public const string Paid          = "Paid";
    public const string PartiallyPaid = "PartiallyPaid";
    public const string Overdue       = "Overdue";
    public const string Cancelled     = "Cancelled";
}

// ── Platform Announcements ────────────────────────────────────────────────────

public class PlatformAnnouncement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = null!;
    public string Body { get; set; } = null!;
    /// <summary>"All", "Trial", "Starter", "Growth", "Enterprise"</summary>
    public string TargetPlan { get; set; } = "All";
    /// <summary>Draft | Published | Archived</summary>
    public string Status { get; set; } = "Draft";
    public DateTime? PublishedAtUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public string CreatedByEmail { get; set; } = null!;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}

// ── Platform Leads ────────────────────────────────────────────────────────────

public class PlatformLead
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string CompanyName { get; set; } = null!;
    public string ContactName { get; set; } = null!;
    public string ContactEmail { get; set; } = null!;
    public string? Phone { get; set; }
    public string? Message { get; set; }
    /// <summary>New | Contacted | DemoScheduled | Converted | Lost</summary>
    public string Status { get; set; } = "New";
    public string? Notes { get; set; }
    /// <summary>Platform user email assigned to this lead.</summary>
    public string? AssignedTo { get; set; }
    /// <summary>Manual | WebForm | Import</summary>
    public string Source { get; set; } = "Manual";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? ConvertedToTenantId { get; set; }
}

// ── Invoice Line Items ────────────────────────────────────────────────────────

public class TenantInvoiceLine : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InvoiceId { get; set; }
    public Guid TenantId { get; set; }
    public string Description { get; set; } = string.Empty;
    public int Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal TaxRate { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal LineTotal { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

// ── Tenant Payments ───────────────────────────────────────────────────────────

public class TenantPayment : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? InvoiceId { get; set; }
    public decimal Amount { get; set; }
    public string CurrencyCode { get; set; } = "USD";
    /// <summary>BankTransfer, Cheque, Online, Cash, etc.</summary>
    public string Method { get; set; } = string.Empty;
    public string? Reference { get; set; }
    /// <summary>Pending, Completed, Failed, Refunded</summary>
    public string Status { get; set; } = PaymentStatuses.Pending;
    public DateTime? PaidAt { get; set; }
    public Guid? ReceivedByPlatformUserId { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
}

public static class PaymentStatuses
{
    public const string Pending   = "Pending";
    public const string Completed = "Completed";
    public const string Failed    = "Failed";
    public const string Refunded  = "Refunded";
}

// ── Platform Compliance Controls ────────────────────────────────────────────────

/// <summary>
/// Tracks a single SOC 2 / security control for the KynexOne platform itself.
/// Covers all SOC 2 TSCs: Security (CC), Availability (A), Confidentiality (C),
/// Processing Integrity (PI), Privacy (P).
/// </summary>
public class PlatformComplianceControl
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>e.g. "CC6 — Logical & Physical Access"</summary>
    public string Category { get; set; } = string.Empty;
    /// <summary>Control ID from the TSC framework, e.g. "CC6.1"</summary>
    public string ControlId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    /// <summary>NotStarted | InProgress | Implemented | Waived | NotApplicable</summary>
    public string Status { get; set; } = ComplianceControlStatus.NotStarted;
    public string? Owner { get; set; }
    public string? EvidenceNote { get; set; }
    public string? EvidenceUrl { get; set; }
    public DateTime? ReviewedAtUtc { get; set; }
    public Guid? ReviewedByPlatformUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}

public static class ComplianceControlStatus
{
    public const string NotStarted    = "NotStarted";
    public const string InProgress    = "InProgress";
    public const string Implemented   = "Implemented";
    public const string Waived        = "Waived";
    public const string NotApplicable = "NotApplicable";
}

// ── Platform Security Incidents ──────────────────────────────────────────────────

public class PlatformSecurityIncident
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    /// <summary>Low | Medium | High | Critical</summary>
    public string Severity { get; set; } = "Low";
    /// <summary>Open | Investigating | Resolved | Closed</summary>
    public string Status { get; set; } = "Open";
    public string? Reporter { get; set; }
    public string? AffectedSystems { get; set; }
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAtUtc { get; set; }
    public string? Resolution { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? CreatedByPlatformUserId { get; set; }
}
