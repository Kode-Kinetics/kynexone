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
    public const string SecretConfigKey = "Proxy:ClientIpSecret";

    public static string Resolve(HttpContext context, string? configuredSecret)
    {
        var fallback = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (string.IsNullOrEmpty(configuredSecret)) return fallback;

        var presented = context.Request.Headers[SecretHeader].ToString();
        if (string.IsNullOrEmpty(presented)
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(configuredSecret)))
            return fallback;

        var asserted = context.Request.Headers[ClientIpHeader].ToString().Trim();
        return IPAddress.TryParse(asserted, out var ip) ? ip.ToString() : fallback;
    }
}
