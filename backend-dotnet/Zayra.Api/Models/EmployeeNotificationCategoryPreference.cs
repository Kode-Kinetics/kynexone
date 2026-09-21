using Zayra.Api.Domain.Entities;
namespace Zayra.Api.Models;

/// <summary>
/// W2-D (S4) — an employee's per-CATEGORY, per-CHANNEL notification choice.
///
/// Layered UNDER <see cref="EmployeeNotificationPreference"/> (the one-row-per-employee channel
/// master switch that already owns the table name <c>employee_notification_preferences</c>): the
/// master row decides whether a channel is on at all, this table narrows it by topic. An ABSENT
/// row means the category is ENABLED — only an explicit <c>Enabled = false</c> blocks a send, and
/// only for categories that are not mandatory (see NotificationCategories.Mandatory).
/// </summary>
public class EmployeeNotificationCategoryPreference : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public int EmployeeId { get; set; }
    /// <summary>push | email | sms (lower-case wire names; WhatsApp shares the sms consent surface).</summary>
    public string Channel { get; set; } = string.Empty;
    /// <summary>One of NotificationCategories.All.</summary>
    public string Category { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? UpdatedBy { get; set; }
}
