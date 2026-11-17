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
    /// <summary>Starts at 1 and goes up on every rotation; part of the AAD and of every token-cache key. A concurrency token,
    /// so two rotations racing cannot both win.</summary>
    public int CredentialVersion { get; set; } = 1;
    /// <summary>The non-secret identifier issued with the secret (an OAuth client id). Bound into the AAD.</summary>
    public string? ClientIdentifier { get; set; }
    /// <summary>The API host the credential was issued for. Bound into the AAD; a call to another host is refused.</summary>
    public string? BaseUrlHost { get; set; }
    /// <summary>The token (authorisation) host the credential was issued for, when it differs from the API host. Bound into the AAD.</summary>
    public string? TokenUrlHost { get; set; }
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
    /// <summary>Second artifact hash for a two-file submission (a bank header and body); <see cref="ArtifactSha256"/> is the first.</summary>
    public string? ArtifactSecondarySha256 { get; set; }
    /// <summary>Keyed fingerprint of the debit account. With <see cref="BatchReference"/> it makes one open submission per batch.</summary>
    public string? AccountFingerprint { get; set; }
    /// <summary>The batch number the body sees (ANB's batchNumber). One open or landed submission per (tenant, body, account, batch).</summary>
    public string? BatchReference { get; set; }
    public decimal? TotalAmount { get; set; }
    public int? ItemCount { get; set; }
    /// <summary>Concurrency token: every state change increments it, so two writers cannot both move one attempt.</summary>
    public int StateVersion { get; set; } = 1;
    /// <summary>Who resolved an Unknown outcome (never the person who started the attempt), how and why.</summary>
    public Guid? ResolvedByUserId { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }
    public string? Resolution { get; set; }
    public string? ResolutionReason { get; set; }
    public DateTime? LastPolledAtUtc { get; set; }
    public int PollCount { get; set; }
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

public static class GovernmentExchangeResolutions
{
    public const string BankHasIt = "bank_has_it";
    public const string BankDoesNot = "bank_does_not";
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
    /// <summary>Unknown, resolved "the bank does not have it": the intent is released and the batch may be submitted again.</summary>
    public const string ReleasedAfterUnknown = "ReleasedAfterUnknown";
    public const string Initiated = "Initiated";
    public const string Validated = "Validated";
    public const string InProgress = "InProgress";
    public const string Completed = "Completed";
    /// <summary>The body refused the exchange after accepting it (distinct from <see cref="RejectedStructure"/> at submit).</summary>
    public const string Rejected = "Rejected";

    public static readonly IReadOnlyList<string> All =
    [
        Attempted, Recorded, Simulated, Refused, Failed, SubmitIntended, Accepted, RejectedStructure, Unknown, ReleasedAfterUnknown,
        Initiated, Validated, InProgress, Completed, Rejected,
    ];

    /// <summary>States that claim the body has the exchange. Only a live adapter on the api channel may write them.</summary>
    public static readonly IReadOnlyList<string> LiveOnly = [Accepted, Initiated, Validated, InProgress, Completed, Rejected];

    /// <summary>A submission in these states holds its batch: no second open submission for the same (tenant, body, account, batch).</summary>
    public static readonly IReadOnlyList<string> HoldsTheBatch = [SubmitIntended, Unknown, Accepted, Initiated, Validated, InProgress, Completed, Rejected];

    /// <summary>States an attempt may be recorded in at first.</summary>
    public static readonly IReadOnlyList<string> Initial = [Attempted, Recorded, Simulated, Refused, Failed, SubmitIntended];

    /// <summary>
    /// The monotonic transition table (architecture 3.5). Anything not listed is refused. A live state may repeat (a poll that
    /// saw nothing new). Leaving <see cref="Unknown"/> is NOT here: only the second-person resolution can do it.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Next = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
    {
        // A live, non-batch call (an employee push): the body confirms it (Completed, live only) or it failed.
        [Attempted] = new HashSet<string> { Recorded, Simulated, Refused, Failed, Completed },
        [SubmitIntended] = new HashSet<string> { Accepted, RejectedStructure, Unknown, Refused, Failed },
        [Accepted] = new HashSet<string> { Accepted, Initiated, Validated, InProgress, Completed, Rejected },
        [Initiated] = new HashSet<string> { Initiated, Validated, InProgress, Completed, Rejected },
        [Validated] = new HashSet<string> { Validated, InProgress, Completed, Rejected },
        [InProgress] = new HashSet<string> { InProgress, Completed, Rejected },
    };

    public static bool CanMove(string from, string to) => Next.TryGetValue(from, out var allowed) && allowed.Contains(to);

    /// <summary>States that announce a call the transport is about to make. Recorded only when committed; they earn the intent receipt.</summary>
    public static readonly IReadOnlyList<string> Intents = [SubmitIntended, Attempted];

    public static bool IsKnown(string? state) => state is not null && All.Contains(state, StringComparer.Ordinal);
}
