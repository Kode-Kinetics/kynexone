using Zayra.Api.Domain.Entities;
namespace Zayra.Api.Models;

// ── Qiwa sync status constants ────────────────────────────────────────────────

public static class QiwaSyncStatuses
{
    /// <summary>Employee has no Qiwa sync attempt yet.</summary>
    public const string NotSynced = "not_synced";

    /// <summary>Sync has been enqueued but not yet processed.</summary>
    public const string Pending   = "pending";

    /// <summary>Last sync completed successfully.</summary>
    public const string Synced    = "synced";

    /// <summary>Last sync attempt failed; see QiwaSyncLog.ErrorMessage.</summary>
    public const string Error     = "error";
}

// ── Qiwa tenant connection ────────────────────────────────────────────────────

/// <summary>
/// Stores the Qiwa integration connection state for a tenant.
/// One row per tenant.  No credentials are stored here — they will be held in
/// a secrets manager (e.g. Azure Key Vault / Railway secrets) when the real
/// Qiwa API is integrated.
/// </summary>
public class QiwaTenantConnection : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    /// <summary>MOL-issued establishment ID for this tenant.</summary>
    public string EstablishmentId { get; set; } = string.Empty;

    /// <summary>Human-readable establishment name from Qiwa (filled on first successful connection check).</summary>
    public string EstablishmentName { get; set; } = string.Empty;

    /// <summary>
    /// Connection state: Disconnected | Connected | ConfigurationError | ApiError
    /// </summary>
    public string Status { get; set; } = QiwaConnectionStatuses.Disconnected;

    /// <summary>The Qiwa API environment this tenant is connected to.</summary>
    public string Environment { get; set; } = "sandbox"; // sandbox | production

    /// <summary>Qiwa unified organisation number, if available.</summary>
    public string UnifiedOrganisationNumber { get; set; } = string.Empty;

    /// <summary>Last time a successful connection ping was confirmed.</summary>
    public DateTime? LastConnectedAtUtc { get; set; }

    /// <summary>Timestamp of most recent connection status check.</summary>
    public DateTime? LastCheckedAtUtc { get; set; }

    /// <summary>Error detail from the last failed connection attempt.</summary>
    public string? LastErrorMessage { get; set; }

    public Guid? ConfiguredBy { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}

public static class QiwaConnectionStatuses
{
    public const string Disconnected        = "Disconnected";
    public const string Connected           = "Connected";
    public const string ConfigurationError  = "ConfigurationError";
    public const string ApiError            = "ApiError";
}

// ── Qiwa sync log ─────────────────────────────────────────────────────────────

/// <summary>
/// Audit trail of every Qiwa sync attempt for an employee.
/// The real Qiwa API payload will be stored here once integration goes live;
/// for now the columns exist and the placeholder service writes Pending entries.
/// </summary>
public class QiwaSyncLog : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public int EmployeeId { get; set; }

    /// <summary>
    /// Direction of sync: Push (KynexOne → Qiwa) | Pull (Qiwa → KynexOne)
    /// </summary>
    public string Direction { get; set; } = "Push";

    /// <summary>
    /// Outcome: Pending | Success | Failed | Skipped
    /// </summary>
    public string Status { get; set; } = "Pending";

    /// <summary>What triggered the sync: Manual | Automatic | Webhook</summary>
    public string TriggerSource { get; set; } = "Manual";

    /// <summary>JSON snapshot of the payload that would be sent to Qiwa (populated when integration is live).</summary>
    public string? RequestPayloadJson { get; set; }

    /// <summary>JSON response body returned by Qiwa (populated when integration is live).</summary>
    public string? ResponsePayloadJson { get; set; }

    /// <summary>HTTP status code returned by Qiwa, if applicable.</summary>
    public int? HttpStatusCode { get; set; }

    /// <summary>Human-readable error description on failure.</summary>
    public string? ErrorMessage { get; set; }

    public Guid? TriggeredBy { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }

    // ── Retry / dead-letter tracking (Track A) ────────────────────────────────

    /// <summary>Number of push attempts made so far by the background worker.</summary>
    public int RetryCount { get; set; } = 0;

    /// <summary>Maximum attempts before the record is moved to DeadLetter.</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Timestamp of the most recent retry attempt.</summary>
    public DateTime? LastRetriedAtUtc { get; set; }

    /// <summary>Reason the record was dead-lettered after exhausting retries.</summary>
    public string? DeadLetterReason { get; set; }
}

// ── Qiwa sync log lifecycle states (worker) ───────────────────────────────────

public static class QiwaSyncLogStatuses
{
    public const string Pending    = "Pending";
    public const string Processing = "Processing";
    public const string Success    = "Success";
    public const string Failed     = "Failed";
    public const string Skipped    = "Skipped";
    public const string DeadLetter = "DeadLetter";
}

// ── Qiwa API credentials (encrypted at rest) ──────────────────────────────────

/// <summary>
/// Per-tenant Qiwa OAuth2 client credentials.  The client secret is stored
/// encrypted via <see cref="Microsoft.AspNetCore.DataProtection.IDataProtector"/>
/// and is never returned to clients in plaintext.
/// </summary>
public class QiwaApiCredential : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string ClientId { get; set; } = string.Empty;

    /// <summary>AES-protected client secret. Never expose in API responses.</summary>
    public string EncryptedClientSecret { get; set; } = string.Empty;

    public string Environment { get; set; } = "sandbox"; // sandbox | production
    public DateTime? TokenExpiresAtUtc { get; set; }

    /// <summary>Short-lived access token cache (acceptable to persist).</summary>
    public string CachedAccessToken { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }
}
