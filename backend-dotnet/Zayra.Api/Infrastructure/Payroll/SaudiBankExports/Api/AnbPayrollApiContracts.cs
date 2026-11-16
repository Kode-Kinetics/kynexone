namespace Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Api;

// Shapes for the ANB Payroll Services API client (published OpenAPI "Payroll Services APIs" v0.0.1).
// Facts used here come from that spec and ANB's overview page only. Nothing in this folder is wired into
// DI: the integration wave binds IAnbEndpointConfig and IAnbCredentialSource to the government
// foundation (credential store, HTTP helper, exchange ledger). Until then the client cannot be built
// with real values, and an empty configuration refuses (fail closed).

/// <summary>
/// Where and how to reach ANB for ONE environment. The published spec has no servers and no security
/// schemes, so every value is configuration and every value is required: an empty one refuses with
/// <see cref="AnbApiCodes.ConfigMissing"/> before any socket is opened. URLs must be absolute https.
/// </summary>
public interface IAnbEndpointConfig
{
    /// <summary>Environment these settings are for (for example "sandbox"). A call scoped to a different
    /// environment refuses, so a sandbox tenant can never reach a production host.</summary>
    string? Environment { get; }
    /// <summary>Payroll API base, for example https://host/prefix. Paths from the spec are appended.</summary>
    string? BaseUrl { get; }
    /// <summary>OAuth token endpoint. Not published by ANB.</summary>
    string? TokenUrl { get; }
    /// <summary>Only <c>client_credentials</c> is supported; any other value refuses.</summary>
    string? GrantType { get; }
    /// <summary>OAuth scope sent with the token request. Not published by ANB.</summary>
    string? Scope { get; }
    /// <summary><c>client_secret_basic</c> (RFC 6749 2.3.1 header) or <c>client_secret_post</c> (form).
    /// Not published by ANB.</summary>
    string? ClientAuthMethod { get; }
}

/// <summary>Plain options object for <see cref="IAnbEndpointConfig"/>. Every value defaults to empty.</summary>
public sealed class AnbPayrollApiOptions : IAnbEndpointConfig
{
    public const string SectionName = "Government:Anb:PayrollApi";

    public string? Environment { get; set; }
    public string? BaseUrl { get; set; }
    public string? TokenUrl { get; set; }
    public string? GrantType { get; set; }
    public string? Scope { get; set; }
    public string? ClientAuthMethod { get; set; }
}

/// <summary>Who the call is for. Credentials and cached tokens are keyed by all of it.</summary>
public sealed record AnbCredentialScope(Guid TenantId, Guid? CompanyId, string Environment);

/// <summary>The customer's ANB app credential. <see cref="ToString"/> never prints the id or the secret.</summary>
public sealed class AnbClientCredential
{
    public AnbClientCredential(string clientId, string clientSecret, string version)
    {
        ClientId = clientId;
        ClientSecret = clientSecret;
        Version = version;
    }

    public string ClientId { get; }
    public string ClientSecret { get; }
    /// <summary>Changes on every rotation. Part of the token cache key, so a rotated credential is never
    /// served a token minted under the old one.</summary>
    public string Version { get; }

    public override string ToString() => $"AnbClientCredential(version={Version})";
}

/// <summary>Seam for the government credential store. Returns null when no credential is held.</summary>
public interface IAnbCredentialSource
{
    Task<AnbClientCredential?> GetAsync(AnbCredentialScope scope, CancellationToken ct);
}

/// <summary>Stable codes. Callers and the UI switch on these, never on message text.</summary>
public static class AnbApiCodes
{
    public const string ConfigMissing = "anb_config_missing";
    public const string ConfigInvalid = "anb_config_invalid";
    public const string EnvironmentMismatch = "anb_environment_mismatch";
    public const string CredentialMissing = "anb_credential_missing";
    public const string TokenUnavailable = "anb_token_unavailable";
    public const string TokenRejected = "anb_token_rejected";
    public const string ArtifactHashMismatch = "anb_artifact_hash_mismatch";
    public const string ArtifactInconsistent = "anb_artifact_inconsistent";
    public const string SubmitHeaderInvalid = "anb_submit_header_invalid";
    public const string PayrollIdInvalid = "anb_payroll_id_invalid";

    public const string Accepted = "anb_accepted";
    public const string AcceptedWithoutId = "anb_accepted_without_id";
    public const string RejectedStructure = "anb_rejected_structure";
    public const string RejectedAuthorization = "anb_rejected_authorization";
    public const string OutcomeUnknown = "anb_outcome_unknown";

    public const string Unauthorized = "anb_unauthorized";
    public const string HttpError = "anb_http_error";
    public const string ResponseUnparsable = "anb_response_unparsable";
    public const string PagePayrollMismatch = "anb_poll_payroll_mismatch";
    public const string PageMismatch = "anb_poll_page_mismatch";
    public const string PollInconsistent = "anb_poll_inconsistent";
    public const string WpsNotReady = "anb_wps_not_ready";
    public const string WpsTooLarge = "anb_wps_too_large";
    public const string StatusUnknown = "anb_status_unknown";
}

/// <summary>Thrown when the client refuses to be built or to call. No socket was opened. The message
/// names settings by key only, never by value.</summary>
public sealed class AnbPayrollApiRefusedException : Exception
{
    public AnbPayrollApiRefusedException(string code, IReadOnlyList<string>? settings = null)
        : base(settings is { Count: > 0 }
            ? $"ANB payroll API refused ({code}): {string.Join(", ", settings)}."
            : $"ANB payroll API refused ({code}).")
    {
        Code = code;
        Settings = settings ?? Array.Empty<string>();
    }

    public string Code { get; }
    public IReadOnlyList<string> Settings { get; }
}

/// <summary>A read call (status, WPS) failed. Carries a stable code and the HTTP status only: never the
/// response body, which can echo an IBAN, an ID or a token.</summary>
public sealed class AnbPayrollApiException : Exception
{
    public AnbPayrollApiException(string code, int? httpStatus = null, Exception? inner = null)
        : base(httpStatus is null ? $"ANB payroll API error ({code})." : $"ANB payroll API error ({code}, HTTP {httpStatus}).", inner)
    {
        Code = code;
        HttpStatus = httpStatus;
    }

    public string Code { get; }
    public int? HttpStatus { get; }
}

/// <summary>A status string outside ANB's published vocabulary. Never guessed into a known one.</summary>
public sealed class AnbUnknownStatusException : Exception
{
    public AnbUnknownStatusException(string kind, string? token)
        : base($"ANB {kind} status '{Printable(token)}' is not in the published vocabulary; refusing to guess.")
    {
        Kind = kind;
        Token = Printable(token);
    }

    public string Code => AnbApiCodes.StatusUnknown;
    public string Kind { get; }
    /// <summary>The token if it looks like a status word, else a placeholder (a crafted value is never echoed).</summary>
    public string Token { get; }

    private static string Printable(string? token)
    {
        if (token is null) return "<null>";
        if (token.Length == 0) return "<empty>";
        return token.Length <= 32 && token.All(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or ' ' or '_' or '-')
            ? token
            : "<withheld>";
    }
}

/// <summary>One frozen file exactly as the approver saw it, with the SHA-256 recorded when it was frozen.</summary>
public sealed record AnbArtifactFile(string FileName, byte[] Content, string ExpectedSha256)
{
    public override string ToString() => $"AnbArtifactFile({FileName}, {Content.Length} bytes, sha256={ExpectedSha256})";
}

/// <summary>Optional request headers from the spec. Fee semantics are not published, so the client only
/// passes them through (after rejecting control characters); it gives them no meaning.</summary>
public sealed record AnbSubmitHeaders(
    string? ProfileId = null,
    string? FeesCategoryCode = null,
    string? FeesAndVatAccount = null,
    string? FeesFrequency = null,
    bool AutoWpsFileUpload = false,
    string? NationalUnifiedNo = null);

public sealed record AnbSubmitRequest(AnbCredentialScope Scope, AnbArtifactFile Header, AnbArtifactFile Body, AnbSubmitHeaders Headers);

public enum AnbSubmitOutcomeKind
{
    /// <summary>2xx with a payroll id: ANB accepted the structure.</summary>
    Accepted,
    /// <summary>4xx: ANB refused the files. Regenerate through the existing re-export path.</summary>
    RejectedStructure,
    /// <summary>401/403 on the POST: ANB refused our token, so it did not take the payroll.</summary>
    RejectedAuthorization,
    /// <summary>The body may have reached ANB but we do not know what it did. NEVER resubmit automatically.</summary>
    Unknown,
    /// <summary>Refused before the POST: no socket was opened for it.</summary>
    Refused,
}

/// <summary>Result of one submit. <see cref="Message"/> is masked (IBAN and ID shapes) and truncated.</summary>
public sealed record AnbSubmitOutcome(
    AnbSubmitOutcomeKind Kind,
    string Code,
    bool RequestSent,
    int? HttpStatus = null,
    string? BankPayrollId = null,
    string? BankStatus = null,
    string? Message = null);

/// <summary>One per-employee line from <c>GET /payroll/v2/{id}</c>. Carries personal data:
/// <see cref="ToString"/> prints none of it.</summary>
public sealed record AnbStatusLine(
    string EmployeeId,
    string EmployeeAccountNumber,
    decimal SalaryAmount,
    string? BicCode,
    string? EmployeeName,
    string? PaymentId,
    string PaymentStatus,
    string? PaymentStatusDescription,
    string? PaymentTransactionReferenceNumber,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt)
{
    public override string ToString() => $"AnbStatusLine(status={PaymentStatus}, amount={SalaryAmount})";
}

/// <summary>All pages of one payroll, merged. <see cref="Complete"/> is true only when the distinct lines
/// equal the bank's <c>itemCount</c>; an incomplete snapshot must never be read as "nothing else changed".</summary>
public sealed record AnbPayrollStatusSnapshot(
    string BankPayrollId,
    string PayrollStatus,
    string? Reason,
    IReadOnlyList<AnbStatusLine> Lines,
    int ItemCount,
    int PagesFetched,
    int DuplicateLinesCollapsed,
    bool Complete);

public sealed record AnbWpsFile(byte[] Content, string Sha256, string? ContentType, string? FileName)
{
    public override string ToString() => $"AnbWpsFile({Content.Length} bytes, sha256={Sha256})";
}
