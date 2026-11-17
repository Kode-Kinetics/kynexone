using Microsoft.Extensions.Configuration;

namespace Zayra.Api.Infrastructure.Government;

/// <summary>Refused before any socket was opened: the live gate is closed, or the host is not on the allow-list.</summary>
public sealed class GovernmentTransportRefusedException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Cache key for anything a body hands back per credential (an OAuth access token). Including the environment and the
/// credential version means a sandbox token is never used against production and a rotated credential never reuses a token
/// minted under the old one (security R8).
/// </summary>
public sealed record GovernmentTokenCacheKey(Guid TenantId, string Body, string Environment, string CredentialVersion);

/// <summary>
/// The ONE way KynexOne sends a request to a government or bank body (ratchet: GovernmentHttpOnlyRatchetTests).
///
/// <list type="bullet">
/// <item><b>Base URL from a per-environment allow-list only.</b> Built-in entries (today: the production Qiwa host the live
/// adapter always used) plus operator configuration <c>Government:{Section}:BaseUrls:{environment}</c>. Never from request
/// data, which also closes SSRF. A base URL must be a plain https origin, and a sandbox URL may not point at a host that is
/// listed for production.</item>
/// <item><b>The live gate first.</b> A refused <see cref="GovernmentLiveDecision"/> throws before a client is even created.</item>
/// <item><b>Exactly one send.</b> No retry of any kind, and in particular never a retried POST: a timeout after the body was
/// sent is an unknown outcome a person resolves, not something to send twice.</item>
/// <item><b>No request logging.</b> The named clients are registered with <c>RemoveAllLoggers</c> (URLs can carry ID numbers).</item>
/// </list>
/// </summary>
public sealed class GovernmentHttp
{
    public const string HostRefusedCode = "government_host_not_allowed";
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Hosts the code itself vouches for. Everything else must be configured by the operator.</summary>
    private static readonly IReadOnlyDictionary<(string Body, string Environment), string> BuiltIn =
        new Dictionary<(string, string), string>
        {
            // The host LiveQiwaApiAdapter has always targeted (unverified with Qiwa; the adapter is refused without an agreement).
            [(GovernmentBodies.Qiwa, GovernmentEnvironments.Production)] = "https://api.qiwa.tech",
        };

    private readonly IHttpClientFactory _factory;
    private readonly IConfiguration? _config;

    public GovernmentHttp(IHttpClientFactory factory, IConfiguration? config)
    {
        _factory = factory;
        _config = config;
    }

    public static string ClientName(string body) => $"gov-{body}";

    public static string BaseUrlKey(string body, string environment) =>
        $"Government:{GovernmentBodies.Section(body)}:BaseUrls:{environment}";

    /// <summary>The allow-listed base URL for a body in an environment, or a refusal.</summary>
    public Uri ResolveBaseUrl(string body, string environment)
    {
        if (!GovernmentBodies.IsKnown(body) || !GovernmentEnvironments.IsKnown(environment))
            throw Refused(body, environment, "unknown body or environment");
        var url = Candidate(body, environment)
            ?? throw Refused(body, environment, "no base URL is allow-listed for this environment");
        if (environment != GovernmentEnvironments.Production
            && Candidate(body, GovernmentEnvironments.Production) is { } production
            && string.Equals(production.Host, url.Host, StringComparison.OrdinalIgnoreCase))
            throw Refused(body, environment, "the base URL is the production host");
        return url;
    }

    private Uri? Candidate(string body, string environment)
    {
        var configured = _config?[BaseUrlKey(body, environment)];
        var raw = !string.IsNullOrWhiteSpace(configured) ? configured.Trim()
            : BuiltIn.TryGetValue((body, environment), out var builtIn) ? builtIn : null;
        if (raw is null) return null;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || uri.AbsolutePath != "/")
            throw Refused(body, environment, "the configured base URL is not a plain https origin");
        return uri;
    }

    /// <summary>
    /// Sends one request to <paramref name="relativePath"/> (must start with a single <c>/</c>) under the allow-listed base URL
    /// of the decision's body and <paramref name="environment"/>. Never retries. The caller owns the response.
    /// </summary>
    public async Task<HttpResponseMessage> ExchangeAsync(GovernmentLiveDecision decision, string environment, HttpMethod method,
        string relativePath, HttpContent? content, Action<HttpRequestMessage>? configure, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!decision.IsOpen)
            throw new GovernmentTransportRefusedException(GovernmentLivePolicy.RefusedCode,
                $"Live exchange with {decision.Body} is switched off ({decision.ReasonCode}). Nothing was sent.");
        var baseUrl = ResolveBaseUrl(decision.Body, environment);
        if (string.IsNullOrEmpty(relativePath) || relativePath[0] != '/' || relativePath.StartsWith("//", StringComparison.Ordinal)
            || relativePath.Contains('\\'))
            throw Refused(decision.Body, environment, "the request path must be relative to the allow-listed base URL");
        var target = new Uri(baseUrl, relativePath);
        if (!string.Equals(target.Host, baseUrl.Host, StringComparison.OrdinalIgnoreCase) || target.Scheme != Uri.UriSchemeHttps
            || target.Port != baseUrl.Port)
            throw Refused(decision.Body, environment, "the request path left the allow-listed host");

        using var request = new HttpRequestMessage(method, target) { Content = content };
        configure?.Invoke(request);
        var client = _factory.CreateClient(ClientName(decision.Body));
        return await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
    }

    private static GovernmentTransportRefusedException Refused(string body, string environment, string why) =>
        new(HostRefusedCode, $"Refused to contact {body} ({environment}): {why}. Nothing was sent.");
}
