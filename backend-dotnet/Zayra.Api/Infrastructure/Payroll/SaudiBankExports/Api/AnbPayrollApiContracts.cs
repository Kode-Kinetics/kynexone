using Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Channels;

namespace Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Api;

// ANB-only shapes for the ANB Payroll Services API client (published OpenAPI "Payroll Services APIs" v0.0.1).
// Callers outside the Anb* files use IBankPayrollChannel and the neutral types in Channels/ instead.
// Nothing here is wired into DI: the integration wave binds IAnbEndpointConfig and IAnbCredentialSource to
// the government foundation. Until then an empty configuration refuses (fail closed).

/// <summary>
/// Where and how to reach ANB for ONE environment. The published spec has no servers and no security
/// schemes, so every value is configuration and every value is required: an empty one refuses with
/// <see cref="BankChannelCodes.ConfigMissing"/> before any socket is opened.
/// </summary>
public interface IAnbEndpointConfig
{
    /// <summary>Environment these settings are for (for example "sandbox").</summary>
    string? Environment { get; }
    /// <summary>Payroll API base, absolute https. Paths from the spec are appended.</summary>
    string? BaseUrl { get; }
    /// <summary>OAuth token endpoint, absolute https. Not published by ANB.</summary>
    string? TokenUrl { get; }
    /// <summary>Only <c>client_credentials</c> is supported.</summary>
    string? GrantType { get; }
    /// <summary>OAuth scope. Not published by ANB.</summary>
    string? Scope { get; }
    /// <summary><c>client_secret_basic</c> or <c>client_secret_post</c>. Not published by ANB.</summary>
    string? ClientAuthMethod { get; }
    /// <summary>Comma-separated host suffixes both URLs must sit under (for example "anb.com.sa"). IP
    /// literals and single-label hosts are always refused.</summary>
    string? AllowedHostSuffixes { get; }
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
    public string? AllowedHostSuffixes { get; set; }
}

/// <summary>Time limits. Defaults suit production; tests shorten them.</summary>
public sealed record AnbClientLimits(TimeSpan RequestTimeout, TimeSpan StatusReadDeadline)
{
    public static readonly AnbClientLimits Default = new(TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(2));
}

/// <summary>
/// The customer's ANB app credential, bound to the environment and hosts it was issued for. A call whose
/// configured environment, base-URL host or token-URL host differs is refused. <see cref="ToString"/>
/// never prints the id or the secret.
/// </summary>
public sealed class AnbClientCredential
{
    public AnbClientCredential(string clientId, string clientSecret, string version,
        string environment, string baseUrlHost, string tokenUrlHost)
    {
        ClientId = clientId;
        ClientSecret = clientSecret;
        Version = version;
        Environment = environment;
        BaseUrlHost = baseUrlHost;
        TokenUrlHost = tokenUrlHost;
    }

    public string ClientId { get; }
    public string ClientSecret { get; }
    /// <summary>Changes on every rotation. Part of the token cache key (with a hash of the secret).</summary>
    public string Version { get; }
    public string Environment { get; }
    public string BaseUrlHost { get; }
    public string TokenUrlHost { get; }

    public override string ToString() => $"AnbClientCredential(version={Version}, env={Environment})";
}

/// <summary>Seam for the government credential store. Returns null when no credential is held.</summary>
public interface IAnbCredentialSource
{
    Task<AnbClientCredential?> GetAsync(BankChannelScope scope, CancellationToken ct);
}

/// <summary>A status string outside ANB's published vocabulary. Never guessed into a known one.</summary>
public sealed class AnbUnknownStatusException : BankChannelException
{
    public AnbUnknownStatusException(string kind, string? token)
        : base(BankChannelCodes.StatusUnknown, $"ANB {kind} status '{AnbStatusWords.Printable(token)}' is not in the published vocabulary; refusing to guess.")
    {
        Kind = kind;
        Token = AnbStatusWords.Printable(token);
    }

    public string Kind { get; }
    /// <summary>The token if it looks like a status word, else a placeholder (a crafted value is never echoed).</summary>
    public string Token { get; }
}

internal static class AnbStatusWords
{
    public static string Printable(string? token)
    {
        if (token is null) return "<null>";
        if (token.Length == 0) return "<empty>";
        return IsWord(token) ? token : "<withheld>";
    }

    /// <summary>ASCII letters, space, underscore, hyphen, at most 32. Anything else (digits, look-alike
    /// letters such as a dotless i) is not a status word.</summary>
    public static bool IsWord(string token) =>
        token.Length <= 32 && token.All(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or ' ' or '_' or '-');
}

/// <summary>Optional request headers from the spec. Fee semantics are not published, so the client only
/// passes them through (after rejecting control characters); it gives them no meaning.</summary>
public sealed record AnbSubmitHeaders(
    string? ProfileId = null,
    string? FeesCategoryCode = null,
    string? FeesAndVatAccount = null,
    string? FeesFrequency = null,
    bool AutoWpsFileUpload = false,
    string? NationalUnifiedNo = null)
{
    public override string ToString() => $"AnbSubmitHeaders(autoWps={AutoWpsFileUpload})";
}

public sealed record AnbSubmitRequest(
    BankChannelScope Scope, BankArtifactFile Header, BankArtifactFile Body, AnbSubmitHeaders Headers, bool PriorOutcomeUnknown = false);

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
    public override string ToString() => $"AnbStatusLine(status={PaymentStatus})";
}

/// <summary>All pages of one payroll, merged. <see cref="Complete"/> is true only when the distinct lines
/// equal the bank's <c>itemCount</c> and the count is not zero. Lines with an unpublished status word are
/// kept verbatim; the mapper holds them.</summary>
public sealed record AnbPayrollStatusSnapshot(
    string BankPayrollId,
    string PayrollStatus,
    string? Reason,
    IReadOnlyList<AnbStatusLine> Lines,
    int ItemCount,
    int PagesFetched,
    int DuplicateLinesCollapsed,
    bool Complete,
    DateTimeOffset? PayrollCreatedAt = null);
