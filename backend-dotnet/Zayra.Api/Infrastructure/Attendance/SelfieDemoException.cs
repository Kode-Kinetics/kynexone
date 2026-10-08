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

    public static SelfieDemoExceptionOptions From(Microsoft.Extensions.Configuration.IConfiguration? configuration) =>
        configuration?.GetSection(SectionName).Get<SelfieDemoExceptionOptions>() ?? new SelfieDemoExceptionOptions();

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
        $"عرض تجريبي: تُحفظ الصور خارج المملكة العربية السعودية وتُحذف تلقائيًا بعد {EvidenceRetentionDays.ToString(CultureInfo.InvariantCulture)} أيام من التقاطها.";

    public object AuditFields => new
    {
        tenantSlug = TenantSlug,
        expiresUtc = ExpiresUtc,
        evidenceRetentionDays = EvidenceRetentionDays,
        approvedBy = ApprovedBy,
        skippedGates = new[] { "dpia", "dataResidency" },
    };
}
