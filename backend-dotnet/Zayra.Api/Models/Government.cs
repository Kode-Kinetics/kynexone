using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

/// <summary>
/// One customer (establishment) secret for a government or bank body: a GOSI client secret or private key, a bank app
/// credential. Envelope-encrypted under the tenant's field-encryption key (<c>FieldKeyService</c>), with AAD binding it to
/// tenant + company + body + environment + version, so a ciphertext copied to another row, tenant or environment does not
/// open. NEVER returned by any API: <c>GovernmentCredentialStore</c> hands out metadata only, and the secret is opened only
/// inside a transport. Deployment (vendor) secrets never live here; they stay in configuration (architecture 3.4).
/// </summary>
public class GovernmentCredential : ITenantOwned, ICompanyScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    /// <summary>Null for a tenant-wide credential; set for one company's own (looked up company first, then tenant).</summary>
    public Guid? CompanyId { get; set; }
    public string Body { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    /// <summary>The field-encryption envelope of the secret. Never logged, never serialised.</summary>
    public byte[] Ciphertext { get; set; } = [];
    /// <summary>The tenant DEK version that sealed <see cref="Ciphertext"/> (the key id).</summary>
    public int DekVersion { get; set; }
    /// <summary>Starts at 1 and goes up on every rotation; part of the AAD and of every token-cache key.</summary>
    public int CredentialVersion { get; set; } = 1;
    /// <summary>A keyed (blind-index) fingerprint so a person can tell two credentials apart without seeing either.</summary>
    public string Fingerprint { get; set; } = string.Empty;
    public DateTime? ExpiresAtUtc { get; set; }
    public DateTime? LastUsedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? RotatedAtUtc { get; set; }
    public Guid? RotatedBy { get; set; }
}

public static class GovernmentCredentialKinds
{
    public const string OAuthClient = "oauth_client";
    public const string ApiKey = "api_key";
    public const string PrivateKey = "private_key";
    public const string Certificate = "certificate";

    public static readonly IReadOnlyList<string> All = [OAuthClient, ApiKey, PrivateKey, Certificate];

    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind, StringComparer.Ordinal);
}

/// <summary>
/// One ATTEMPT to exchange something with a government or bank body, on any channel (architecture 3.1): a file we produced,
/// a file we imported, a filing a person recorded, or an API call. Written only through <c>IGovernmentExchangeRecorder</c>,
/// which stages its audit row in the same save. Holds hashes, a masked preview and ids, NEVER a request or response body
/// (bodies carry IBANs and ID numbers; security R6). Unique per (tenant, body, operation, idempotency key), so two people
/// pressing Submit produce one row.
/// </summary>
public class GovernmentExchange : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public string Body { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string Channel { get; set; } = string.Empty;
    public string? Environment { get; set; }
    public string? SubjectType { get; set; }
    public string? SubjectId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string? RequestSha256 { get; set; }
    public string? ResponseSha256 { get; set; }
    /// <summary>SHA-256 of the frozen artifact sent (for a bank submit: the approved export's bytes).</summary>
    public string? ArtifactSha256 { get; set; }
    /// <summary>A short preview with IBAN- and ID-shaped text masked (GovernmentLogScrubber). Never the body.</summary>
    public string? MaskedPreview { get; set; }
    public int? HttpStatus { get; set; }
    public string? ErrorCode { get; set; }
    /// <summary>The body's own reference for the exchange (a bank payroll id), once it gives one.</summary>
    public string? ExternalReference { get; set; }
    public string Adapter { get; set; } = string.Empty;
    /// <summary>True only when the attempt went through an adapter that declared itself live, on the api channel.</summary>
    public bool IsLive { get; set; }
    public Guid? ActorUserId { get; set; }
    public Guid? ApprovalRequestId { get; set; }
    public Guid? JobId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
}

public static class GovernmentExchangeChannels
{
    public const string FileOut = "file_out";
    public const string FileIn = "file_in";
    public const string Manual = "manual";
    public const string Api = "api";

    public static readonly IReadOnlyList<string> All = [FileOut, FileIn, Manual, Api];
}

/// <summary>
/// Attempt states. The bank submit states (architecture 3.5) are here so the ledger can hold them; their transitions belong
/// to the bank submit service, not to this vocabulary.
/// </summary>
public static class GovernmentExchangeStates
{
    public const string Attempted = "Attempted";
    public const string Recorded = "Recorded";
    public const string Simulated = "Simulated";
    public const string Refused = "Refused";
    public const string Failed = "Failed";
    public const string SubmitIntended = "SubmitIntended";
    public const string Accepted = "Accepted";
    public const string RejectedStructure = "RejectedStructure";
    public const string Unknown = "Unknown";
    public const string Validated = "Validated";
    public const string InProgress = "InProgress";
    public const string Completed = "Completed";

    public static readonly IReadOnlyList<string> All =
        [Attempted, Recorded, Simulated, Refused, Failed, SubmitIntended, Accepted, RejectedStructure, Unknown, Validated, InProgress, Completed];

    /// <summary>States that claim the body has the exchange. Only a live adapter on the api channel may write them.</summary>
    public static readonly IReadOnlyList<string> LiveOnly = [Accepted, Validated, InProgress, Completed];

    public static bool IsKnown(string? state) => state is not null && All.Contains(state, StringComparer.Ordinal);
}
