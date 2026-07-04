using System.Net;
using System.Net.Sockets;

namespace Zayra.Api.Infrastructure.Common;

/// <summary>
/// SSRF (Server-Side Request Forgery, CWE-918) protection for outbound HTTP/TCP calls whose
/// target host is supplied by a (tenant) user — e.g. attendance-device "Pull" endpoints.
///
/// A tenant admin is untrusted relative to our cloud infrastructure, so before the server makes a
/// request to a user-supplied URL we must ensure it does not point at:
///   - a non-http(s) scheme (file:, gopher:, ftp: …),
///   - loopback / link-local / private / unique-local / CGNAT ranges (internal services, lateral movement),
///   - the cloud instance metadata service (169.254.169.254 / fd00:ec2::254 / metadata.google.internal).
///
/// The host is resolved to its actual IP addresses and EVERY resolved address is checked (defends
/// against DNS names that resolve to internal IPs). Callers should additionally disable HTTP redirects
/// and re-validate, since a 3xx can redirect to an internal address after this check
/// (see <see cref="CreateGuardedClientHandler"/>).
/// </summary>
public static class SsrfGuard
{
    /// <summary>
    /// Validates a user-supplied absolute URL for outbound use. Returns (ok, reason). When ok is false,
    /// reason is a short, non-sensitive explanation safe to log. Performs DNS resolution.
    /// </summary>
    public static async Task<(bool Ok, string Reason)> ValidateOutboundUrlAsync(string? url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
            return (false, "Endpoint URL is empty.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return (false, "Endpoint URL is not a valid absolute URL.");
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return (false, $"Only http/https endpoints are allowed (got '{uri.Scheme}').");

        IPAddress[] addresses;
        try
        {
            // If the host is already a literal IP, Dns.GetHostAddresses returns it directly.
            addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, ct);
        }
        catch (Exception)
        {
            return (false, "Endpoint host could not be resolved.");
        }
        if (addresses.Length == 0)
            return (false, "Endpoint host did not resolve to any address.");

        foreach (var ip in addresses)
            if (IsBlockedAddress(ip))
                return (false, "Endpoint resolves to a disallowed internal/loopback/metadata address.");

        return (true, "ok");
    }

    /// <summary>Convenience host:port validator for raw TCP connects (biometric SDK devices).</summary>
    public static async Task<(bool Ok, string Reason)> ValidateOutboundHostAsync(string? host, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(host))
            return (false, "Host is empty.");
        // Strip any scheme/path a user may have pasted into an "IP address" field.
        var bare = host.Trim();
        var slash = bare.IndexOf("://", StringComparison.Ordinal);
        if (slash >= 0) bare = bare[(slash + 3)..];
        bare = bare.Split('/')[0].Split(':')[0];

        IPAddress[] addresses;
        try { addresses = await Dns.GetHostAddressesAsync(bare, ct); }
        catch (Exception) { return (false, "Host could not be resolved."); }
        if (addresses.Length == 0) return (false, "Host did not resolve to any address.");
        foreach (var ip in addresses)
            if (IsBlockedAddress(ip)) return (false, "Host resolves to a disallowed internal/loopback/metadata address.");
        return (true, "ok");
    }

    /// <summary>
    /// An HttpClientHandler that does NOT auto-follow redirects — mandatory for guarded outbound calls
    /// so a validated host cannot 3xx-redirect the request to an internal address post-validation.
    /// </summary>
    public static HttpClientHandler CreateGuardedClientHandler() =>
        new() { AllowAutoRedirect = false };

    private static bool IsBlockedAddress(IPAddress ip)
    {
        // Normalize IPv4-mapped IPv6 (::ffff:a.b.c.d) to IPv4 so the v4 rules apply.
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

        if (IPAddress.IsLoopback(ip)) return true;                 // 127.0.0.0/8, ::1
        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return true; // 0.0.0.0, ::

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            // 10.0.0.0/8
            if (b[0] == 10) return true;
            // 172.16.0.0/12
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            // 192.168.0.0/16
            if (b[0] == 192 && b[1] == 168) return true;
            // 169.254.0.0/16 link-local (incl. 169.254.169.254 cloud metadata)
            if (b[0] == 169 && b[1] == 254) return true;
            // 100.64.0.0/10 CGNAT
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;
            // 0.0.0.0/8 "this network"
            if (b[0] == 0) return true;
            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal) return true; // fe80::/10, fec0::/10, fc00::/7
            // fd00:ec2::254 (AWS IMDS over IPv6) is inside fc00::/7 → already covered by IsIPv6UniqueLocal.
            return false;
        }

        return false; // unknown families: fail open only for families we don't make requests over
    }
}
