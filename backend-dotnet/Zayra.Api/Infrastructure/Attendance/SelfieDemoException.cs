using System.Globalization;

namespace Zayra.Api.Infrastructure.Attendance;

/// <summary>
/// A narrow, time-boxed DEMO EXCEPTION to the selfie-attendance sign-off gate (owner decision 2026-10-08), configuration
/// section <c>SelfieDemoException</c>. For a tenant whose slug is listed in <see cref="TenantSlugs"/>, while
/// <c>now &lt; </c><see cref="ExpiresUtc"/>, <see cref="AttendanceVerificationService.GetPolicyAsync"/> treats
/// <c>selfie_attendance</c> as on WITHOUT the DPIA sign-off and the KSA storage-residency check — and only those two:
/// consent, the upload limits, single-use evidence, the HR view permission, the audit and the purge all stay as they are.
/// Every selfie taken under it is stamped at upload with <c>attendance_evidence.purge_due_at_utc</c> = capture +
/// <see cref="EvidenceRetentionDays"/>, so the purge deletes it then, used or not, even after the exception has expired.
/// <para>Fails closed: no slugs, no expiry, an expiry in the past, no <see cref="ApprovedBy"/>, or a retention outside
/// 1–<see cref="MaxRetentionDays"/> days means the exception does nothing.</para>
/// </summary>
public sealed class SelfieDemoExceptionOptions
{
    public const string SectionName = "SelfieDemoException";
    public const int DefaultRetentionDays = 7;
    /// <summary>A demo is short: a longer retention is a configuration mistake, and the exception refuses to apply.</summary>
    public const int MaxRetentionDays = 30;

    /// <summary>Tenant slugs the exception covers (case-insensitive). Empty by default: nobody.</summary>
    public string[] TenantSlugs { get; set; } = [];

    /// <summary>Required. The exception applies only while now is before this instant (UTC).</summary>
    public DateTime? ExpiresUtc { get; set; }

    /// <summary>A selfie taken under the exception is deleted this many days after capture, used or not.</summary>
    public int EvidenceRetentionDays { get; set; } = DefaultRetentionDays;

    /// <summary>Required free text naming who approved the exception and when; recorded in every audit row.</summary>
    public string? ApprovedBy { get; set; }

    /// <summary>Why the configuration could not be read (a malformed value), or null. The exception is then OFF.</summary>
    public string? ConfigError { get; private set; }

    /// <summary>
    /// Reads the section by hand, never with the binder: <c>Get&lt;T&gt;()</c> THROWS on a malformed date or number, and this
    /// runs at startup, so a typo in a manual "end it early" edit would take the whole API down. A value that does not
    /// parse switches the exception OFF and is reported through <see cref="ConfigError"/> instead.
    /// </summary>
    public static SelfieDemoExceptionOptions From(Microsoft.Extensions.Configuration.IConfiguration? configuration)
    {
        var options = new SelfieDemoExceptionOptions();
        var section = configuration?.GetSection(SectionName);
        if (section is null || !section.Exists()) return options;
        options.TenantSlugs = section.GetSection(nameof(TenantSlugs)).GetChildren()
            .Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()).ToArray();
        options.ApprovedBy = section[nameof(ApprovedBy)];
        var errors = new List<string>();
        var rawExpiry = section[nameof(ExpiresUtc)];
        if (!string.IsNullOrWhiteSpace(rawExpiry))
        {
            if (DateTime.TryParse(rawExpiry, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var expiry))
                options.ExpiresUtc = DateTime.SpecifyKind(expiry, DateTimeKind.Utc);
            else errors.Add($"ExpiresUtc '{rawExpiry}' is not a date (use e.g. 2026-10-22T23:59:59Z)");
        }
        var rawDays = section[nameof(EvidenceRetentionDays)];
        if (!string.IsNullOrWhiteSpace(rawDays))
        {
            if (int.TryParse(rawDays, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days)) options.EvidenceRetentionDays = days;
            else { options.EvidenceRetentionDays = 0; errors.Add($"EvidenceRetentionDays '{rawDays}' is not a whole number of days"); }
        }
        if (errors.Count > 0)
        {
            options.ExpiresUtc = null; // fail closed
            options.ConfigError = string.Join("; ", errors);
        }
        return options;
    }

    /// <summary>The expiry as UTC (a value bound from configuration may arrive as Local or Unspecified).</summary>
    public DateTime? ExpiresAtUtc => ExpiresUtc is not { } e ? null
        : e.Kind switch { DateTimeKind.Utc => e, DateTimeKind.Local => e.ToUniversalTime(), _ => DateTime.SpecifyKind(e, DateTimeKind.Utc) };

    /// <summary>Whether the exception can apply to anyone at <paramref name="nowUtc"/> (lets callers skip the slug lookup).</summary>
    public bool IsActive(DateTime nowUtc) =>
        TenantSlugs is { Length: > 0 } && TenantSlugs.Any(s => !string.IsNullOrWhiteSpace(s))
        && ExpiresAtUtc is { } expires && nowUtc < expires
        && !string.IsNullOrWhiteSpace(ApprovedBy)
        && EvidenceRetentionDays is >= 1 and <= MaxRetentionDays;

    /// <summary>The grant for a tenant with <paramref name="tenantSlug"/> at <paramref name="nowUtc"/>, or null.</summary>
    public SelfieDemoExceptionGrant? GrantFor(string? tenantSlug, DateTime nowUtc)
    {
        if (!IsActive(nowUtc) || string.IsNullOrWhiteSpace(tenantSlug)) return null;
        var slug = tenantSlug.Trim();
        return TenantSlugs.Any(s => string.Equals(s?.Trim(), slug, StringComparison.OrdinalIgnoreCase))
            ? new SelfieDemoExceptionGrant(slug, ExpiresAtUtc!.Value, EvidenceRetentionDays, ApprovedBy!.Trim())
            : null;
    }
}

/// <summary>The demo exception as it applies to one tenant right now.</summary>
public sealed record SelfieDemoExceptionGrant(string TenantSlug, DateTime ExpiresUtc, int EvidenceRetentionDays, string ApprovedBy)
{
    /// <summary>Audit action of the first policy read per tenant per process while the exception is active.</summary>
    public const string ActiveAction = "attendance.selfie.demo_exception_active";

    public TimeSpan EvidenceRetention => TimeSpan.FromDays(EvidenceRetentionDays);

    /// <summary>What the employee is told before consenting and when taking the selfie (selfie.demoNotice).</summary>
    public string NoticeEn =>
        $"Demo: photos are stored outside Saudi Arabia and deleted automatically {EvidenceRetentionDays} days after they are taken.";

    public string NoticeAr =>
        $"عرض تجريبي: تُحفظ الصور خارج المملكة العربية السعودية وتُحذف تلقائيًا بعد {DaysAr(EvidenceRetentionDays)} من التقاطها.";

    /// <summary>The day count in grammatical Arabic: يوم واحد، يومين، 3–10 أيام، 11+ يومًا.</summary>
    public static string DaysAr(int days) => days switch
    {
        1 => "يوم واحد",
        2 => "يومين",
        >= 3 and <= 10 => $"{days.ToString(CultureInfo.InvariantCulture)} أيام",
        _ => $"{days.ToString(CultureInfo.InvariantCulture)} يومًا",
    };

    public object AuditFields => new
    {
        tenantSlug = TenantSlug,
        expiresUtc = ExpiresUtc,
        evidenceRetentionDays = EvidenceRetentionDays,
        approvedBy = ApprovedBy,
        skippedGates = new[] { "dpia", "dataResidency" },
    };
}
