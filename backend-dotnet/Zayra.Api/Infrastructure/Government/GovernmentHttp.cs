using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Government.Credentials;

namespace Zayra.Api.Infrastructure.Government;

/// <summary>Refused before any socket was opened (or a redirect was refused): the gate is closed, the host is not allowed, or a write lacks a write decision.</summary>
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
/// The ONE way KynexOne sends a request to a government or bank body (ratchet: GovernmentRatchetTests A).
///
/// <list type="bullet">
/// <item><b>Guarded socket.</b> Every gov client's primary handler is <see cref="CreateHandler"/>: no redirects, no proxy, no
/// cookies, no automatic decompression, and a connect-time address check (<see cref="SsrfGuard"/>) so no loopback, private,
/// link-local or metadata address is ever dialled. A 3xx answer is refused, never followed.</item>
/// <item><b>Base URL from a per-environment allow-list only.</b> Built-in entries plus operator configuration
/// <c>Government:{Section}:BaseUrls:{environment}</c>. Never from request data. A base URL must be a plain https origin, and
/// a sandbox URL may not point at a host that is listed for production.</item>
/// <item><b>The live gate first.</b> A refused <see cref="GovernmentLiveDecision"/> throws before a client is created, and any
/// method but GET/HEAD needs a live-WRITE decision (four gates plus the body's recorded legal clearance). A decision made for one
/// environment cannot be used for another.</item>
/// <item><b>Exactly one send.</b> No retry of any kind, and never a retried POST: a timeout after the body was sent is an
/// unknown outcome a person resolves, not something to send twice.</item>
/// <item><b>No request logging.</b> The named clients are registered with <c>RemoveAllLoggers</c> (URLs can carry ID numbers).</item>
/// </list>
/// </summary>
public sealed class GovernmentHttp
{
    public const string HostRefusedCode = "government_host_not_allowed";
    public const string WriteRefusedCode = "government_write_not_cleared";
    public const string RedirectRefusedCode = "government_redirect_refused";
    public const string EnvironmentMismatchCode = "government_environment_mismatch";
    public const string CredentialBindingCode = "government_credential_binding_mismatch";
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    /// <summary>Largest response body a gov client will buffer.</summary>
    public const long MaxResponseBytes = 8 * 1024 * 1024;

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

    /// <summary>
    /// The primary handler of every government and bank client. Same guarantees as the bank client's own handler factory, so a
    /// bank client can take its handler from here: no redirects, no proxy, no cookies, no decompression, connect-time address check.
    /// </summary>
    public static SocketsHttpHandler CreateHandler()
    {
        var handler = SsrfGuard.CreateGuardedClientHandler();
        handler.AllowAutoRedirect = false;
        handler.UseCookies = false;
        handler.UseProxy = false;
        handler.AutomaticDecompression = DecompressionMethods.None;
        handler.MaxResponseHeadersLength = 64; // KB
        handler.PooledConnectionLifetime = TimeSpan.FromMinutes(5);
        handler.ConnectTimeout = TimeSpan.FromSeconds(15);
        return handler;
    }

    /// <summary>True when any handler in the chain would follow a redirect (unknown handler types are trusted not to).</summary>
    public static bool FollowsRedirects(HttpMessageHandler? handler)
    {
        for (var depth = 0; handler is not null && depth < 32; depth++)
        {
            switch (handler)
            {
                case SocketsHttpHandler sockets: return sockets.AllowAutoRedirect;
                case HttpClientHandler client: return client.AllowAutoRedirect;
                case DelegatingHandler delegating: handler = delegating.InnerHandler; break;
                default: return false;
            }
        }
        return false;
    }

    /// <summary>Registers one guarded named client per body. The only registration of government clients (Program.cs calls it).</summary>
    public static IServiceCollection AddGovernmentHttpClients(IServiceCollection services)
    {
        foreach (var body in GovernmentBodies.All)
            services.AddHttpClient(ClientName(body))
                .ConfigurePrimaryHttpMessageHandler(CreateHandler)
                .ConfigureHttpClient(c =>
                {
                    c.Timeout = DefaultTimeout;
                    c.MaxResponseContentBufferSize = MaxResponseBytes;
                })
                .RemoveAllLoggers();
        return services;
    }

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
            || uri.AbsolutePath != "/" || uri.HostNameType != UriHostNameType.Dns)
            throw Refused(body, environment, "the configured base URL is not a plain https origin with a host name");
        return uri;
    }

    /// <summary>
    /// Refuses a credential that was not issued for this body and environment, or whose recorded host is not the host this
    /// environment's allow-list names (a credential is bound to the base URL it was issued for).
    /// </summary>
    public void RequireBoundTo(UnsealedGovernmentCredential credential, string body, string environment)
    {
        if (credential.Body != body || credential.Environment != environment)
            throw new GovernmentTransportRefusedException(CredentialBindingCode,
                $"The credential was issued for {credential.Body} ({credential.Environment}), not {body} ({environment}). Nothing was sent.");
        if (credential.BaseUrlHost is { } host
            && !string.Equals(host, ResolveBaseUrl(body, environment).Host, StringComparison.OrdinalIgnoreCase))
            throw new GovernmentTransportRefusedException(CredentialBindingCode,
                $"The credential is bound to another host than the one allow-listed for {body} ({environment}). Nothing was sent.");
    }

    /// <summary>
    /// Sends one request to <paramref name="relativePath"/> (must start with a single <c>/</c>, no dot segments) under the
    /// allow-listed base URL of the decision's body and <paramref name="environment"/>. Never retries; a 3xx is refused. The
    /// caller owns the response.
    /// </summary>
    public async Task<HttpResponseMessage> ExchangeAsync(GovernmentLiveDecision decision, string environment, HttpMethod method,
        string relativePath, HttpContent? content, Action<HttpRequestMessage>? configure, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!decision.IsOpen)
            throw new GovernmentTransportRefusedException(GovernmentLivePolicy.RefusedCode,
                $"Live exchange with {decision.Body} is switched off ({decision.ReasonCode}). Nothing was sent.");
        if (decision.Environment is { } bound && bound != environment)
            throw new GovernmentTransportRefusedException(EnvironmentMismatchCode,
                $"The decision for {decision.Body} was made for {bound}, not {environment}. Nothing was sent.");
        if (!IsRead(method) && !decision.AllowsWrites)
            throw new GovernmentTransportRefusedException(WriteRefusedCode,
                $"A {method} to {decision.Body} needs a live-write decision with the recorded legal clearance. Nothing was sent.");

        var baseUrl = ResolveBaseUrl(decision.Body, environment);
        if (string.IsNullOrEmpty(relativePath) || relativePath[0] != '/' || relativePath.StartsWith("//", StringComparison.Ordinal)
            || relativePath.Contains('\\') || relativePath.Split('?')[0].Split('/').Any(segment => segment is "." or ".."))
            throw Refused(decision.Body, environment, "the request path must be relative to the allow-listed base URL");
        var target = new Uri(baseUrl, relativePath);
        RequireSameOrigin(decision.Body, environment, baseUrl, target);

        using var request = new HttpRequestMessage(method, target) { Content = content };
        configure?.Invoke(request);
        // The configure hook sets headers; it may not move the request.
        if (request.RequestUri is null || request.Method != method) throw Refused(decision.Body, environment, "the request was changed after the host check");
        RequireSameOrigin(decision.Body, environment, baseUrl, request.RequestUri);

        var client = _factory.CreateClient(ClientName(decision.Body));
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            response.Dispose();
            throw new GovernmentTransportRefusedException(RedirectRefusedCode,
                $"{decision.Body} answered with a redirect, which is never followed. The request was sent once; treat the outcome as unknown.");
        }
        return response;
    }

    /// <summary>Sends with a credential: the credential must be bound to this body, environment and host first.</summary>
    public Task<HttpResponseMessage> ExchangeAsync(GovernmentLiveDecision decision, UnsealedGovernmentCredential credential, HttpMethod method,
        string relativePath, HttpContent? content, Action<HttpRequestMessage>? configure, CancellationToken ct)
    {
        RequireBoundTo(credential, decision.Body, credential.Environment);
        return ExchangeAsync(decision, credential.Environment, method, relativePath, content, configure, ct);
    }

    public static bool IsRead(HttpMethod method) => method == HttpMethod.Get || method == HttpMethod.Head;

    private static void RequireSameOrigin(string body, string environment, Uri baseUrl, Uri target)
    {
        if (!target.IsAbsoluteUri || !string.Equals(target.Host, baseUrl.Host, StringComparison.OrdinalIgnoreCase)
            || target.Scheme != Uri.UriSchemeHttps || target.Port != baseUrl.Port)
            throw Refused(body, environment, "the request left the allow-listed host");
    }

    private static GovernmentTransportRefusedException Refused(string body, string environment, string why) =>
        new(HostRefusedCode, $"Refused to contact {body} ({environment}): {why}. Nothing was sent.");
}
