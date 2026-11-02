using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

/// <summary>
/// One payroll rule a company's HR has set for its own legal entity (Setup -> Payroll rules). A row exists only
/// where HR chose a value; no row means "the platform default", so a company that never opens the screen is
/// paid exactly as before. <c>acknowledged_*</c> records who accepted a value that is below the statutory
/// minimum, and when; it is cleared when the value is moved back to or above the minimum.
/// </summary>
public class CompanyPayrollRule : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public string RuleKey { get; set; } = string.Empty;
    /// <summary>The rule's value from <see cref="EffectiveFrom"/> on. An empty string is a "cleared" marker: from that date
    /// the platform default applies again (history is kept, so a past period is never repriced by a later change).</summary>
    public string RuleValue { get; set; } = string.Empty;
    /// <summary>First day this value applies. <see cref="DateOnly.MinValue"/> for a rule saved before dating existed.</summary>
    public DateOnly EffectiveFrom { get; set; } = DateOnly.MinValue;
    public Guid? AcknowledgedBy { get; set; }
    public DateTime? AcknowledgedAtUtc { get; set; }
    public string? AcknowledgementNote { get; set; }
    public Guid? UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A change to a company's payroll rules that lowers pay (or goes below a statutory minimum) waits here until a
/// DIFFERENT person holding <c>payroll.approve</c> approves it. HR makes the change, Finance approves it; the maker's
/// acknowledgement and reason travel with it, and the approver is recorded separately. Changes that cannot lower pay
/// are applied directly and never appear here.
/// </summary>
public class PayrollRuleChangeRequest : ITenantOwned
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    /// <summary>JSON array of { key, from, to } (to = null clears the rule back to the platform default).</summary>
    public string ChangesJson { get; set; } = "[]";
    /// <summary>JSON array of "key:CODE" warnings the maker acknowledged.</summary>
    public string WarningCodesJson { get; set; } = "[]";
    public string? Reason { get; set; }
    /// <summary>The day the approved values start to apply (never earlier than the day of approval).</summary>
    public DateOnly EffectiveFrom { get; set; }
    public string Status { get; set; } = Pending;
    public Guid? RequestedBy { get; set; }
    public DateTime RequestedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? DecidedBy { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
    public string? DecisionNote { get; set; }
}
