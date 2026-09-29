namespace Zayra.Api.Infrastructure.Notifications;

/// <summary>
/// W2-D (S4) — the vocabulary an employee opts in/out of, and the mapping from the event codes the
/// platform actually emits onto it.
///
/// Emitters do not declare a category. Today they send either a template code ("PAYSLIP_READY",
/// "COMPLIANCE_DOCUMENT_EXPIRY") or, through the legacy NotifyAsync surface, the derived
/// "{EntityName}.Notice" code. <see cref="Classify"/> maps both onto the categories the mobile app
/// shows. An event that maps to NO category is never blocked by a category preference — an
/// unknown event is not something the employee could have opted out of.
/// </summary>
public static class NotificationCategories
{
    public const string Approvals = "approvals";
    public const string Leave = "leave";
    public const string Overtime = "overtime";
    public const string Payslip = "payslip";
    public const string Documents = "documents";
    public const string Attendance = "attendance";
    public const string HrRequests = "hr_requests";
    public const string Announcements = "announcements";
    /// <summary>Password resets, sign-in and MFA notices. Mandatory: cannot be opted out of.</summary>
    public const string Security = "security";

    public static readonly string[] All =
        [Approvals, Leave, Overtime, Payslip, Documents, Attendance, HrRequests, Announcements, Security];

    /// <summary>
    /// Categories that ignore an opt-out on every channel. Exposed to the app as
    /// <c>{ enabled: true, locked: true }</c> so it can render them disabled.
    /// </summary>
    public static readonly IReadOnlySet<string> Mandatory = new HashSet<string>(StringComparer.Ordinal) { Security };

    /// <summary>Wire channel names the preference API accepts.</summary>
    public const string PushKey = "push";
    public const string EmailKey = "email";
    public const string SmsKey = "sms";
    public static readonly string[] ChannelKeys = [PushKey, EmailKey, SmsKey];

    public static bool IsCategory(string? value) => value is not null && All.Contains(value, StringComparer.Ordinal);
    public static bool IsChannelKey(string? value) => value is not null && ChannelKeys.Contains(value, StringComparer.Ordinal);
    public static bool IsMandatory(string? category) => category is not null && Mandatory.Contains(category);

    /// <summary>
    /// Dispatcher channel → preference channel key. In-app is the guaranteed fallback and has no
    /// key (it can never be opted out of); WhatsApp shares the SMS consent surface exactly as the
    /// channel master switch does (NotificationService.SelectChannel).
    /// </summary>
    public static string? ChannelKeyFor(string dispatcherChannel) => dispatcherChannel switch
    {
        NotificationChannels.Push => PushKey,
        NotificationChannels.Email => EmailKey,
        NotificationChannels.Sms => SmsKey,
        NotificationChannels.WhatsApp => SmsKey,
        _ => null,
    };

    /// <summary>
    /// Maps an emitted event onto a category, or null when it belongs to none. Order matters:
    /// "approval" wins over the entity it approves, because an approver's queue notice is an
    /// approvals notice even when the entity is a LeaveRequest.
    /// </summary>
    public static string? Classify(string? eventCode, string? entityName)
    {
        var key = $"{eventCode} {entityName}".ToLowerInvariant().Replace("-", "_");
        if (string.IsNullOrWhiteSpace(key)) return null;

        if (Has(key, "password", "mfa", "security", "login", "signin", "sign_in", "otp")) return Security;
        if (Has(key, "approval")) return Approvals;
        if (Has(key, "payslip", "payroll")) return Payslip;
        if (Has(key, "overtime")) return Overtime;
        if (Has(key, "leave")) return Leave;
        if (Has(key, "regulari", "attendance", "punch")) return Attendance;
        if (Has(key, "hrrequest", "hr_request")) return HrRequests;
        if (Has(key, "document", "compliance")) return Documents;
        if (Has(key, "announcement")) return Announcements;
        return null;
    }

    private static bool Has(string key, params string[] needles) =>
        needles.Any(n => key.Contains(n, StringComparison.Ordinal));
}
