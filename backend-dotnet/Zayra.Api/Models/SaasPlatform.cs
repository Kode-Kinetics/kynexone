using Zayra.Api.Domain.Entities;
namespace Zayra.Api.Models;

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
    public DateTime? LastLoginAtUtc { get; set; }
    public string? LastLoginIp { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}

// ── MFA Challenge Token ───────────────────────────────────────────────────────

/// <summary>Short-lived single-use token issued after password validates but before TOTP is checked.
/// Stored as a SHA-256 hash. Raw value is returned to the client as the MFA challenge token.</summary>
public class MfaChallengeToken : INullableTenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Set for tenant users.</summary>
    public Guid? UserId { get; set; }
    /// <summary>Set for platform admins.</summary>
    public Guid? PlatformUserId { get; set; }
    /// <summary>TenantId of the user, null for platform admins.</summary>
    public Guid? TenantId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; }
    public string CreatedByIp { get; set; } = string.Empty;
    /// <summary>Set when the token is consumed. Null = still valid.</summary>
    public DateTime? UsedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsValid => UsedAtUtc is null && DateTime.UtcNow < ExpiresAtUtc;
}

// ── Subscription status constants ─────────────────────────────────────────────

public static class SubscriptionStatuses
{
    public const string Trial          = "Trial";
    public const string Active         = "Active";
    public const string PastDue        = "PastDue";
    public const string Suspended      = "Suspended";
    public const string Cancelled      = "Cancelled";
    public const string ManualContract = "ManualContract";

    public static readonly IReadOnlySet<string> All = new HashSet<string>
    {
        Trial, Active, PastDue, Suspended, Cancelled, ManualContract
    };
}

// ── Feature key constants ─────────────────────────────────────────────────────

public static class FeatureKeys
{
    public const string Recruitment = "recruitment";
    public const string Performance = "performance";
    public const string Compliance  = "compliance";
    public const string AiAssistant = "ai_assistant";
    public const string Finance     = "finance";
    public const string Payroll     = "payroll";
    public const string Shifts      = "shifts";
    public const string Overtime    = "overtime";
    public const string MobileApp        = "mobile_app";
    public const string WpsExport        = "wps_export";
    public const string QiwaIntegration  = "qiwa_integration";
    public const string EosbCalc            = "eosb_calc";
    public const string ResumeScreening     = "resume_screening";
    public const string PayrollAiValidation = "payroll_ai_validation";
    public const string RiskScores              = "risk_scores";
    public const string HijriCalendar           = "hijri_calendar";
    public const string PayslipTemplateDesigner = "payslip_template_designer";
}

// ── Tenant Subscription ───────────────────────────────────────────────────────

public class TenantSubscription : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Plan { get; set; } = "Starter"; // Starter, Growth, Enterprise
    public string Status { get; set; } = "Active"; // see SubscriptionStatuses constants
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAtUtc { get; set; }
    public int MaxEmployees { get; set; } = 50;
    public int MaxUsers { get; set; } = 10;
    public int MaxCompanies { get; set; } = 1;
    public int MaxAdminUsers { get; set; } = 10;
    public string BillingEmail { get; set; } = string.Empty;
    public string BillingCycle { get; set; } = "Monthly"; // Monthly, Annual
    public decimal MonthlyAmount { get; set; }
    public string CurrencyCode { get; set; } = "USD";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}

// ── Tenant Feature Flags ──────────────────────────────────────────────────────

public class TenantFeatureFlag : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string FeatureKey { get; set; } = string.Empty; // ai_assistant, mobile_app, wps_export, etc.
    public bool IsEnabled { get; set; }
    public string? ConfigJson { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? UpdatedBy { get; set; }
}

// ── Tenant Localization ───────────────────────────────────────────────────────

public class TenantLocalizationSetting : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string DefaultLanguage { get; set; } = "en"; // en, ar
    public bool RtlEnabled { get; set; }
    public string CalendarSystem { get; set; } = "Gregorian"; // Gregorian, Hijri
    public string DefaultTimezone { get; set; } = "America/New_York";
    public string DateFormat { get; set; } = "MM/DD/YYYY";
    public string CurrencyCode { get; set; } = "USD";
    public string CountryCode { get; set; } = "US";
    public string WeekStartDay { get; set; } = "Monday";
    public string WorkWeek { get; set; } = "Mon-Fri"; // Sun-Thu, Mon-Fri, Mon-Sat
    public bool HijriDatesEnabled { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

// ── Tenant Branding ───────────────────────────────────────────────────────────

public class TenantBranding : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string LogoUrl { get; set; } = string.Empty;
    public string PrimaryColor { get; set; } = "#2563EB";
    public string AccentColor { get; set; } = "#7C3AED";
    public string CompanyNameEn { get; set; } = string.Empty;
    public string CompanyNameAr { get; set; } = string.Empty;
    public string PortalTitle { get; set; } = string.Empty;
    public string FaviconUrl { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

// ── Tenant Field Help Text (admin-customizable tooltips) ─────────────────────

public class TenantFieldHelpText : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    /// <summary>Stable key identifying the form field, e.g. "employees.joining_date".</summary>
    public string FieldKey { get; set; } = string.Empty;
    /// <summary>Tenant-specific tooltip text shown instead of the built-in default.</summary>
    public string Text { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? UpdatedBy { get; set; }
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

// ── Country Payroll Rules (Configurable per country) ─────────────────────────

public class CountryPayrollRule : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string CountryCode { get; set; } = string.Empty;
    public string RuleKey { get; set; } = string.Empty; // eosb_rate, overtime_multiplier_holiday, gratuity_threshold_years, etc.
    public string RuleValue { get; set; } = string.Empty;
    public string DataType { get; set; } = "string"; // string, decimal, int, bool, json
    public string Description { get; set; } = string.Empty;
    public bool IsOverride { get; set; }
    public DateTime EffectiveFrom { get; set; } = DateTime.UtcNow;
    public DateTime? EffectiveTo { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
}

// ── Tenant AI Usage ───────────────────────────────────────────────────────────

public class TenantAiUsage : ITenantOwned
{
    public Guid TenantId { get; set; }
    public int YearMonth { get; set; } // YYYYMM
    public long TokensUsed { get; set; }
    public int RequestCount { get; set; }
    public int BlockedCount { get; set; }
    public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;
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

public static class AiPlanLimits
{
    // Monthly token limits per plan. 0 = unlimited.
    public static long GetMonthlyTokenLimit(string plan) => plan switch
    {
        "Starter"    => 50_000,
        "Growth"     => 200_000,
        "Enterprise" => 0,
        _            => 50_000
    };
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

// ── Login Activity ────────────────────────────────────────────────────────────

public class LoginActivity : INullableTenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    public Guid? UserId { get; set; }
    /// <summary>Email used in the login attempt. NEVER store passwords or tokens.</summary>
    public string? EmailAttempted { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string? FailureReason { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
}

public static class LoginEventTypes
{
    public const string LoginSuccess             = "login_success";
    public const string LoginFailed              = "login_failed";
    public const string LoginBlockedLockout      = "login_blocked_lockout";
    public const string AccountLocked            = "account_locked";
    public const string PasswordResetRequested   = "password_reset_requested";
    public const string PasswordResetCompleted   = "password_reset_completed";
    public const string MfaReset                 = "mfa_reset";
    public const string SessionRevoked           = "session_revoked";
    public const string PlatformLoginSuccess     = "platform_login_success";
    public const string PlatformLoginFailed      = "platform_login_failed";
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

// ── Platform Configuration Entries ──────────────────────────────────────────────
// Persistent key-value store for mutable platform settings (e.g. maintenance mode).
// Keys are simple ASCII strings; values are free-form strings (booleans as "true"/"false").

public class PlatformConfigEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? UpdatedByPlatformUserId { get; set; }
}

public static class PlatformConfigKeys
{
    public const string MaintenanceMode    = "maintenance_mode";
    public const string MaintenanceMessage = "maintenance_message";
}
