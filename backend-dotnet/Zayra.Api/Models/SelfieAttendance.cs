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
/// <para><b>Lifecycle.</b> The upload inserts the row as <c>Pending</c> (its storage key derived from its id, so the
/// file can be found even if the request dies), stores the file, then flips it to <c>Active</c> with its SHA-256 and
/// size. Every attempt therefore leaves a row, which is what the hourly upload limit counts.</para>
///
/// <para><b>Retention (class E).</b> The blob is deleted by <c>SelfieEvidencePurgeJobHandler</c>: 90 days after the
/// punch's payroll month is locked, or at work date + 120 days when no run locked that month, or 24 hours after upload
/// when no punch ever used it, or 1 hour after a <c>Pending</c> upload that never completed; immediately for unused
/// selfies when the employee withdraws consent; and before a tenant is erased. The ROW is kept, with its
/// <see cref="Sha256"/>, as the record that a photo existed and was disposed of; <see cref="PurgeState"/> and
/// <see cref="PurgedAtUtc"/> say so.</para>
/// </summary>
public class AttendanceEvidence : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    /// <summary>The employee who uploaded it — always the caller's own linked employee, never a client-supplied id.</summary>
    public int EmployeeId { get; set; }

    /// <summary>Document-storage key of the sanitised JPEG. Server-only; never returned to a client.</summary>
    public string StorageKey { get; set; } = string.Empty;

    /// <summary>
    /// Lower-case hex SHA-256 of the stored (re-encoded) bytes. Null only while <c>Pending</c> (or for an attempt that
    /// was purged before it completed); an <c>Active</c> row always has it (CHECK). Kept after the blob is purged.
    /// </summary>
    public string? Sha256 { get; set; }

    public string ContentType { get; set; } = "image/jpeg";

    /// <summary>Size of the stored bytes; null while <c>Pending</c>, like <see cref="Sha256"/>.</summary>
    public int? ByteSize { get; set; }
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

    /// <summary><see cref="AttendanceEvidencePurgeStates"/>. A new row is an unfinished attempt until its file is stored.</summary>
    public string PurgeState { get; set; } = AttendanceEvidencePurgeStates.Pending;

    public DateTime? PurgedAtUtc { get; set; }

    /// <summary>
    /// Why this attempt failed, when it did. <c>Busy</c>/<c>Storage</c> (waivable, below); <c>Timeout</c>, <c>Aborted</c> and
    /// <c>DeniedFailure</c> close an attempt as failed without ever waiving anything (<see cref="SelfieUploadFailureReasons"/>).
    /// Set to a waivable reason when this upload attempt failed on the SERVER's side (<see cref="SelfieUploadFailureReasons"/>: the image
    /// slot stayed busy, or storage failed the write) while the employee had no other attempt in flight. Such an attempt
    /// is a server-failure WAIVER (review 3): where the tenant requires a selfie, it lets exactly one of the employee's own
    /// punches through without one, within 10 minutes, at most twice per employee per tenant-local day. A server failure
    /// that happened while another attempt was in flight waives nothing (<c>DeniedFailure</c>). Null while an attempt is
    /// in flight or after it succeeded.
    /// </summary>
    public string? FailedReason { get; set; }

    /// <summary>When a punch used this attempt's waiver. Set with <see cref="WaiverRawEventId"/>, in the punch's own
    /// transaction under the per-employee advisory lock (CHECK: the pair is set together; a waiver is used once). Also a
    /// concurrency token, the database backstop should that lock ever be bypassed: the UPDATE carries
    /// <c>WHERE waiver_consumed_at_utc IS NULL</c>, so a second consumer fails instead of using the waiver twice.</summary>
    public DateTime? WaiverConsumedAtUtc { get; set; }

    /// <summary>The raw punch the waiver let through (recorded <c>VerificationMethod = None</c>, <c>PhotoReference =
    /// waiver:&lt;this id&gt;</c>). Unique: one waiver, one punch.</summary>
    public Guid? WaiverRawEventId { get; set; }

    /// <summary>When a later SUCCESSFUL upload by the same employee cancelled this open waiver (they could take a selfie
    /// after all). A cancelled waiver can no longer be used.</summary>
    public DateTime? WaiverCancelledAtUtc { get; set; }

    /// <summary>
    /// Set at upload only for a selfie taken under the time-boxed demo exception (<c>SelfieDemoExceptionOptions</c>, since
    /// <c>20261008000800</c>): the blob is deleted at this instant (capture + the exception's retention days), whether a
    /// punch used it or not, overriding the payroll-lock and 120-day rules — and still after the exception has expired.
    /// The shorter of this and the normal rule wins. Null for every other selfie.
    /// </summary>
    public DateTime? PurgeDueAtUtc { get; set; }
}

/// <summary>Values of <c>attendance_evidence.failed_reason</c> (CHECK <c>ck_attendance_evidence__failed_reason</c>).</summary>
public static class SelfieUploadFailureReasons
{
    /// <summary>The process-wide image slot stayed busy for 3 s; nothing reached storage (the row is Purged at once).</summary>
    public const string Busy = "Busy";
    /// <summary>Storage refused or failed the write; a partial file may exist, so the row stays Pending for the sweeper.</summary>
    public const string Storage = "Storage";

    // ── Non-waivable: the attempt is closed as failed (so it no longer counts as "in flight" and blocks no waiver), but
    // it never lets a punch through without a required selfie. The row stays Pending, so the 1-hour sweeper strictly
    // deletes any file — including one a late write lands after the attempt was given up.

    /// <summary>The server's 45 s upload deadline fired during the storage write. The write may still land late, so the
    /// row is never removed: it stays Pending for the sweeper. Does not count toward the hourly limit.</summary>
    public const string Timeout = "Timeout";
    /// <summary>The client went away while the file was being written. Pending for the sweeper; COUNTS toward the hourly
    /// limit (the client caused it).</summary>
    public const string Aborted = "Aborted";
    /// <summary>A storage failure that was refused a waiver (another attempt of the employee was in flight). Pending for the
    /// sweeper; counts toward the hourly limit like any storage failure.</summary>
    public const string DeniedFailure = "DeniedFailure";

    public static readonly IReadOnlyList<string> All = [Busy, Storage, Timeout, Aborted, DeniedFailure];
    /// <summary>The reasons that are a server-failure waiver (review 3).</summary>
    public static readonly IReadOnlyList<string> Waivable = [Busy, Storage];
}

/// <summary>Values of <c>attendance_evidence.purge_state</c> (CHECK <c>ck_attendance_evidence__purge_state</c>).</summary>
public static class AttendanceEvidencePurgeStates
{
    /// <summary>The upload was reserved (the attempt counts) but the file is not confirmed stored yet. Never usable by a punch.</summary>
    public const string Pending = "Pending";
    /// <summary>The blob is in storage.</summary>
    public const string Active = "Active";
    /// <summary>The blob was deleted (confirmed, every version); the row and its SHA-256 remain.</summary>
    public const string Purged = "Purged";

    public static readonly IReadOnlyList<string> All = [Pending, Active, Purged];
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
    public const string PurgeStatesIn = "('Pending','Active','Purged')";
    public const string FailureReasonsIn = "('Busy','Storage','Timeout','Aborted','DeniedFailure')";
    public const string WaivableReasonsIn = "('Busy','Storage')";
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
                t.HasCheckConstraint("ck_attendance_evidence__byte_size", "byte_size IS NULL OR byte_size > 0");
                // A usable (Active) selfie always carries its hash and size; only an unfinished or abandoned attempt lacks them.
                t.HasCheckConstraint("ck_attendance_evidence__active_payload", "purge_state <> 'Active' OR (sha256 IS NOT NULL AND byte_size IS NOT NULL)");
                // Only an Active selfie can have been used by a punch.
                t.HasCheckConstraint("ck_attendance_evidence__used_was_active", "used_at_utc IS NULL OR purge_state <> 'Pending'");
                t.HasCheckConstraint("ck_attendance_evidence__expiry", "expires_at_utc > created_at_utc");
                // Review 3: the server-failure waiver is one specific failed attempt.
                t.HasCheckConstraint("ck_attendance_evidence__failed_reason", "failed_reason IS NULL OR failed_reason IN " + FailureReasonsIn);
                // A failed attempt never became a usable selfie.
                t.HasCheckConstraint("ck_attendance_evidence__failed_never_active", "failed_reason IS NULL OR (purge_state <> 'Active' AND used_at_utc IS NULL)");
                // Used by one punch: the time and the punch are set together.
                t.HasCheckConstraint("ck_attendance_evidence__waiver_pair", "(waiver_consumed_at_utc IS NULL) = (waiver_raw_event_id IS NULL)");
                // Only a WAIVABLE failed attempt (Busy, Storage) carries a waiver, and a waiver is either used or cancelled, never both.
                // (The IS NOT NULL matters: NULL IN (...) is NULL, which a CHECK would let through.)
                t.HasCheckConstraint("ck_attendance_evidence__waiver_needs_failure", "(failed_reason IS NOT NULL AND failed_reason IN " + WaivableReasonsIn + ") OR (waiver_consumed_at_utc IS NULL AND waiver_cancelled_at_utc IS NULL)");
                t.HasCheckConstraint("ck_attendance_evidence__waiver_once", "waiver_consumed_at_utc IS NULL OR waiver_cancelled_at_utc IS NULL");
                // A demo-exception selfie is due after it was taken, never before.
                t.HasCheckConstraint("ck_attendance_evidence__purge_due_after_capture", "purge_due_at_utc IS NULL OR purge_due_at_utc > created_at_utc");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.StorageKey).HasMaxLength(500).IsRequired();
            entity.Property(x => x.Sha256).HasColumnType("character(64)");
            entity.Property(x => x.ContentType).HasMaxLength(64).IsRequired();
            entity.Property(x => x.PurgeState).HasMaxLength(16).HasDefaultValue(AttendanceEvidencePurgeStates.Pending);
            entity.Property(x => x.UsedAtUtc).IsConcurrencyToken();
            // The database backstop for "one waiver, one punch": the UPDATE that uses a waiver carries
            // WHERE waiver_consumed_at_utc IS NULL, so if the per-employee advisory lock is ever bypassed, a second punch
            // fails (DbUpdateConcurrencyException, refused as selfie_required) instead of consuming the same waiver again.
            entity.Property(x => x.WaiverConsumedAtUtc).IsConcurrencyToken();
            entity.Property(x => x.FailedReason).HasMaxLength(16);
            entity.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AttendanceRawEvent>().WithMany().HasForeignKey(x => x.UsedByRawEventId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AttendanceRawEvent>().WithMany().HasForeignKey(x => x.WaiverRawEventId).OnDelete(DeleteBehavior.Restrict);
            // Serves the upload rate limit (COUNT(*) of one employee's upload attempts in the last hour), the one-upload-in-
            // flight check (a Pending row of the employee younger than 60 s), and the waiver lookups (the employee's open
            // waiver from the last 10 minutes; the waivers used today, bounded by created_at_utc).
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.CreatedAtUtc })
                .HasDatabaseName("ix_attendance_evidence__employee_created");
            // Serves the purge: the scheduler's SELECT DISTINCT tenant_id over not-yet-purged rows that can be due, and
            // one tenant's Pending rows past an hour and unused rows past 24 hours, oldest first. Partial (purged rows
            // never enter it), so no constant leading column; used_at_utc is included so the due predicate is answered
            // from the index alone.
            entity.HasIndex(x => new { x.TenantId, x.CreatedAtUtc })
                .HasDatabaseName("ix_attendance_evidence__purge_due")
                .HasFilter("purge_state IN ('Pending','Active')")
                .IncludeProperties(x => x.UsedAtUtc);
            // Serves the purge's USED-selfie queries (review 2, item 2; review 3, item 5): one tenant's used, not-yet-purged
            // rows ordered by used_at_utc, due by the 120-day fallback (months locked < 90 days ago excluded) or inside one
            // payroll month locked 90+ days ago. Its own index so those rows are read without sorting every unpurged row.
            entity.HasIndex(x => new { x.TenantId, x.UsedAtUtc })
                .HasDatabaseName("ix_attendance_evidence__used_purge_due")
                .HasFilter("purge_state = 'Active' AND used_at_utc IS NOT NULL");
            // Serves the purge of demo-exception selfies (20261008000800): one tenant's not-yet-purged rows whose stamped
            // purge_due_at_utc has passed (SelfieEvidencePurger.FindDueItemsAsync, query d), and the scheduler's tenant scan.
            // Partial: only stamped, unpurged rows (a handful), so it costs nothing for every other tenant.
            entity.HasIndex(x => new { x.TenantId, x.PurgeDueAtUtc })
                .HasDatabaseName("ix_attendance_evidence__purge_due_override")
                .HasFilter("purge_due_at_utc IS NOT NULL AND purge_state IN ('Pending','Active')");
            // Serves HR's waived-punches report (review 3): one tenant's used waivers in a date range, newest first.
            entity.HasIndex(x => new { x.TenantId, x.WaiverConsumedAtUtc })
                .HasDatabaseName("ix_attendance_evidence__waived_punches")
                .HasFilter("waiver_consumed_at_utc IS NOT NULL");
            // One waiver per punch; serves punch -> waiver lookups and backs the FK.
            entity.HasIndex(x => x.WaiverRawEventId)
                .HasDatabaseName("ux_attendance_evidence__waiver_raw_event")
                .IsUnique()
                .HasFilter("waiver_raw_event_id IS NOT NULL");
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
