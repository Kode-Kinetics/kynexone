using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Zayra.Api.Infrastructure.Http;

/// <summary>
/// The client IP that rate limits and the login failure budget partition on.
///
/// <para>Browser traffic reaches the API through the Vercel-hosted Next.js proxy, so
/// <c>RemoteIpAddress</c> is often the proxy's egress address and every user shares one bucket. When
/// <c>Proxy:ClientIpSecret</c> is configured AND a request carries <see cref="SecretHeader"/> equal to
/// it, the proxy-asserted <see cref="ClientIpHeader"/> is trusted instead. Anyone can send the IP
/// header; only the proxy knows the secret, so a spoofed IP without the secret is ignored.</para>
///
/// <para>OFF by default: with no secret configured the header is never read. The frontend middleware
/// sends both headers only when <c>PROXY_CLIENT_IP_SECRET</c> is set (docs/MFA_ENFORCEMENT.md,
/// "Real client IP").</para>
/// </summary>
public static class ClientIpResolver
{
    public const string ClientIpHeader = "X-KynexOne-Client-IP";
    public const string SecretHeader = "X-KynexOne-Proxy-Secret";
    /// <summary>Set by the Next.js middleware on every proxied /api request, secret or not.</summary>
    public const string ViaProxyHeader = "X-KynexOne-Via-Proxy";
    public const string SecretConfigKey = "Proxy:ClientIpSecret";

    public static string Resolve(HttpContext context, string? configuredSecret)
        => ResolveAddress(context, configuredSecret).Ip;

    /// <summary>
    /// The client address and how much it can be trusted to identify ONE client:
    /// <list type="bullet">
    /// <item><see cref="ClientIpSource.AuthenticatedProxy"/> — the proxy asserted it with the secret.</item>
    /// <item><see cref="ClientIpSource.Direct"/> — the request did not come through the web proxy, so
    /// <c>RemoteIpAddress</c> is the caller's (or the trusted load balancer's resolution of it).</item>
    /// <item><see cref="ClientIpSource.UnverifiedProxy"/> — it came through the proxy without the
    /// secret: <c>RemoteIpAddress</c> is the proxy's, shared by every web user.</item>
    /// </list>
    /// The proxy markers are not authenticated, so a direct caller can claim to be proxied; the only
    /// thing that buys is exemption from per-IP budgets, never from per-account limits.
    /// </summary>
    public static ClientAddress ResolveAddress(HttpContext context, string? configuredSecret)
    {
        var remote = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!string.IsNullOrEmpty(configuredSecret))
        {
            var presented = context.Request.Headers[SecretHeader].ToString();
            if (!string.IsNullOrEmpty(presented)
                && CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(configuredSecret))
                && IPAddress.TryParse(context.Request.Headers[ClientIpHeader].ToString().Trim(), out var ip))
                return new ClientAddress(ip.ToString(), ClientIpSource.AuthenticatedProxy);
        }

        var proxied = context.Request.Headers.ContainsKey(ViaProxyHeader)
                      || context.Request.Headers.ContainsKey("x-vercel-id")
                      || context.Request.Headers.ContainsKey("x-vercel-forwarded-for");
        return new ClientAddress(remote, proxied ? ClientIpSource.UnverifiedProxy : ClientIpSource.Direct);
    }
}

public enum ClientIpSource { Direct, AuthenticatedProxy, UnverifiedProxy }

/// <param name="Ip">The address to partition on.</param>
/// <param name="Source">Whether that address identifies one client (see <see cref="ClientIpResolver.ResolveAddress"/>).</param>
public readonly record struct ClientAddress(string Ip, ClientIpSource Source)
{
    /// <summary>False when the address is the shared proxy's: per-IP budgets would punish everyone.</summary>
    public bool IdentifiesOneClient => Source != ClientIpSource.UnverifiedProxy;
}
