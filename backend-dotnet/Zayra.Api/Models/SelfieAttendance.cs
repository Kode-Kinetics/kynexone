using Microsoft.EntityFrameworkCore;
using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

/// <summary>
/// One selfie an employee uploaded for an attendance punch (selfie attendance v2, table <c>attendance_evidence</c>).
///
/// <para><b>What it is.</b> The envelope of a re-encoded, EXIF-free JPEG held in document storage: who it belongs to,
/// its SHA-256, and whether a punch has used it. The id is the opaque, single-use token the app sends with the punch;
/// the storage key never leaves the server. No face matching is performed, so this row proves only that a photo was
/// taken by the signed-in employee's app at upload time — it is not a biometric verification.</para>
///
/// <para><b>Retention (class E).</b> The blob is deleted by <c>SelfieEvidencePurgeJobHandler</c>: 90 days after the
/// punch's payroll month is locked, or at work date + 120 days when no run locked that month, or 24 hours after upload
/// when no punch ever used it. The ROW is kept, with its <see cref="Sha256"/>, as the record that a photo existed and
/// was disposed of; <see cref="PurgeState"/> and <see cref="PurgedAtUtc"/> say so.</para>
/// </summary>
public class AttendanceEvidence : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    /// <summary>The employee who uploaded it — always the caller's own linked employee, never a client-supplied id.</summary>
    public int EmployeeId { get; set; }

    /// <summary>Document-storage key of the sanitised JPEG. Server-only; never returned to a client.</summary>
    public string StorageKey { get; set; } = string.Empty;

    /// <summary>Lower-case hex SHA-256 of the stored (re-encoded) bytes. Kept after the blob is purged.</summary>
    public string Sha256 { get; set; } = string.Empty;

    public string ContentType { get; set; } = "image/jpeg";
    public int ByteSize { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>The evidence id may be used by a punch until this instant (upload + 10 minutes).</summary>
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>
    /// When a punch consumed it. A concurrency token: the UPDATE that marks it used carries
    /// <c>WHERE used_at_utc IS NULL</c>, so two punches racing on one id cannot both succeed.
    /// </summary>
    public DateTime? UsedAtUtc { get; set; }

    /// <summary>The raw punch that used it. Set together with <see cref="UsedAtUtc"/> (CHECK).</summary>
    public Guid? UsedByRawEventId { get; set; }

    /// <summary><see cref="AttendanceEvidencePurgeStates"/>.</summary>
    public string PurgeState { get; set; } = AttendanceEvidencePurgeStates.Active;

    public DateTime? PurgedAtUtc { get; set; }
}

/// <summary>Values of <c>attendance_evidence.purge_state</c> (CHECK <c>ck_attendance_evidence__purge_state</c>).</summary>
public static class AttendanceEvidencePurgeStates
{
    /// <summary>The blob is in storage.</summary>
    public const string Active = "Active";
    /// <summary>The blob was deleted by the retention job; the row and its SHA-256 remain.</summary>
    public const string Purged = "Purged";

    public static readonly IReadOnlyList<string> All = [Active, Purged];
}

/// <summary>
/// An employee's consent to selfie attendance, recorded per policy version (table <c>biometric_consents</c>).
///
/// <para>Consent is ACTIVE while <see cref="WithdrawnAtUtc"/> is null and <see cref="PolicyVersion"/> equals the
/// tenant's current consent policy version. Withdrawing is always possible and never blocks a punch: without active
/// consent the employee punches without a selfie (the non-biometric alternative). At most one row per employee is
/// open at a time (partial unique index).</para>
/// </summary>
public class BiometricConsent : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public int EmployeeId { get; set; }

    /// <summary>The consent text version the employee agreed to, e.g. <c>"1"</c>.</summary>
    public string PolicyVersion { get; set; } = string.Empty;

    public DateTime GivenAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? WithdrawnAtUtc { get; set; }

    /// <summary>Where consent was given: <see cref="BiometricConsentChannels"/>.</summary>
    public string Channel { get; set; } = BiometricConsentChannels.Mobile;
}

/// <summary>Values of <c>biometric_consents.channel</c> (CHECK <c>ck_biometric_consents__channel</c>).</summary>
public static class BiometricConsentChannels
{
    public const string Mobile = "Mobile";
    public const string Web = "Web";

    public static readonly IReadOnlyList<string> All = [Mobile, Web];

    public static string? Normalize(string? value) =>
        All.FirstOrDefault(v => string.Equals(v, value?.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// What the SERVER verified for a self-punch, stored on <c>attendance_raw_events.verification_method</c>. The client's
/// own claims (VerificationMethod, ConfidenceScore, ClientBiometricVerified) never reach this value.
/// </summary>
public static class AttendanceVerificationMethods
{
    public const string None = "None";
    public const string Geofence = "Geofence";
    public const string Selfie = "Selfie";
    public const string SelfieAndGeofence = "Selfie+Geofence";

    public static string From(bool selfie, bool geofence) => (selfie, geofence) switch
    {
        (true, true) => SelfieAndGeofence,
        (true, false) => Selfie,
        (false, true) => Geofence,
        _ => None,
    };
}

/// <summary>EF mapping for the two selfie-attendance tables; called once from <c>ZayraDbContext.OnModelCreating</c>.</summary>
public static class SelfieAttendanceModelConfiguration
{
    public const string PurgeStatesIn = "('Active','Purged')";
    public const string ChannelsIn = "('Mobile','Web')";

    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AttendanceEvidence>(entity =>
        {
            entity.ToTable("attendance_evidence", t =>
            {
                t.HasCheckConstraint("ck_attendance_evidence__purge_state", "purge_state IN " + PurgeStatesIn);
                t.HasCheckConstraint("ck_attendance_evidence__purged_pair", "(purge_state = 'Purged') = (purged_at_utc IS NOT NULL)");
                t.HasCheckConstraint("ck_attendance_evidence__used_pair", "(used_at_utc IS NULL) = (used_by_raw_event_id IS NULL)");
                t.HasCheckConstraint("ck_attendance_evidence__byte_size", "byte_size > 0");
                t.HasCheckConstraint("ck_attendance_evidence__expiry", "expires_at_utc > created_at_utc");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.StorageKey).HasMaxLength(500).IsRequired();
            entity.Property(x => x.Sha256).HasColumnType("character(64)").IsRequired();
            entity.Property(x => x.ContentType).HasMaxLength(64).IsRequired();
            entity.Property(x => x.PurgeState).HasMaxLength(16).HasDefaultValue(AttendanceEvidencePurgeStates.Active);
            entity.Property(x => x.UsedAtUtc).IsConcurrencyToken();
            entity.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AttendanceRawEvent>().WithMany().HasForeignKey(x => x.UsedByRawEventId).OnDelete(DeleteBehavior.Restrict);
            // Serves the upload rate limit: COUNT(*) of one employee's uploads in the last hour.
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.CreatedAtUtc })
                .HasDatabaseName("ix_attendance_evidence__employee_created");
            // Serves the purge scheduler and job: Active rows ordered by age.
            entity.HasIndex(x => new { x.PurgeState, x.CreatedAtUtc })
                .HasDatabaseName("ix_attendance_evidence__purge_due")
                .HasFilter("purge_state = 'Active'");
            // One evidence row per punch; serves punch -> selfie lookups and backs the FK.
            entity.HasIndex(x => x.UsedByRawEventId)
                .HasDatabaseName("ux_attendance_evidence__used_by_raw_event")
                .IsUnique()
                .HasFilter("used_by_raw_event_id IS NOT NULL");
        });

        modelBuilder.Entity<BiometricConsent>(entity =>
        {
            entity.ToTable("biometric_consents", t =>
            {
                t.HasCheckConstraint("ck_biometric_consents__channel", "channel IN " + ChannelsIn);
                t.HasCheckConstraint("ck_biometric_consents__withdrawn_after_given", "withdrawn_at_utc IS NULL OR withdrawn_at_utc >= given_at_utc");
                t.HasCheckConstraint("ck_biometric_consents__policy_version", "policy_version <> ''");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.PolicyVersion).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Channel).HasMaxLength(16).IsRequired();
            entity.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
            // Serves "does this employee have open consent" (upload, punch, ESS read) and enforces one open row.
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId })
                .HasDatabaseName("ux_biometric_consents__one_open_per_employee")
                .IsUnique()
                .HasFilter("withdrawn_at_utc IS NULL");
            // Serves the consent history of one employee (audit / DSAR), newest first.
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.GivenAtUtc })
                .HasDatabaseName("ix_biometric_consents__employee_history");
        });
    }
}
