using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Zayra.Api.Infrastructure.Geo;

public sealed record MapCoordinates(decimal Latitude, decimal Longitude);

/// <summary>
/// Reads the coordinates behind a Google Maps short link (maps.app.goo.gl/…, goo.gl/maps/…) so an admin can pin a
/// location by pasting what the Share button gives them. Full links are parsed in the browser; only short links,
/// which carry no coordinates, come here.
///
/// SSRF-safe by construction: the first request goes only to those two short-link hosts over https on the default
/// port; a redirect is followed only to https google.com/maps or www.google.com/maps; at most <see cref="MaxHops"/>
/// requests, a <see cref="Timeout"/> for the whole exchange, GET with no body and no cookies (the named client's
/// handler has redirects and cookies off), and response bodies are never read. The answer is only the latitude and
/// longitude parsed from a redirect target URL, or null.
/// </summary>
public sealed class MapLinkResolver
{
    public const string HttpClientName = "map-link-resolver";
    public const int MaxHops = 3;

    private static readonly HashSet<string> MapHosts = new(StringComparer.OrdinalIgnoreCase) { "google.com", "www.google.com" };
    // A redirect to maps.google.com is read for its coordinates, never requested.
    private static readonly HashSet<string> ParseOnlyHosts = new(StringComparer.OrdinalIgnoreCase) { "maps.google.com" };

    private readonly IHttpClientFactory _factory;
    public MapLinkResolver(IHttpClientFactory factory) => _factory = factory;

    /// <summary>The whole exchange, every hop included.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

    public static bool IsShortLink(Uri uri) =>
        IsPlainHttps(uri)
        && (string.Equals(uri.Host, "maps.app.goo.gl", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.Length > 1
            || string.Equals(uri.Host, "goo.gl", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.StartsWith("/maps/", StringComparison.Ordinal));

    public static bool IsGoogleMapsUrl(Uri uri) =>
        IsPlainHttps(uri) && MapHosts.Contains(uri.Host)
        && (uri.AbsolutePath == "/maps" || uri.AbsolutePath.StartsWith("/maps/", StringComparison.Ordinal));

    private static bool IsPlainHttps(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo);

    public async Task<MapCoordinates?> ResolveAsync(string? link, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(link) || link.Length > 2048) return null;
        if (!Uri.TryCreate(link.Trim(), UriKind.Absolute, out var current) || !IsShortLink(current)) return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        var client = _factory.CreateClient(HttpClientName);
        try
        {
            for (var hop = 0; hop < MaxHops; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                var status = (int)response.StatusCode;
                if (status is < 300 or > 399 || response.Headers.Location is not { } location)
                    // Landed: only a Google Maps page's own address can carry the pin (a short link's never does).
                    return IsGoogleMapsUrl(current) ? Parse(current) : null;

                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                var parseOnly = IsPlainHttps(next) && ParseOnlyHosts.Contains(next.Host);
                if (!IsGoogleMapsUrl(next) && !parseOnly) return null; // off-host: never followed
                if (Parse(next) is { } found) return found;
                if (parseOnly) return null;
                current = next;
            }
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // took longer than Timeout
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    // ── Parsing a full Google Maps URL ────────────────────────────────────────────────────────────

    private const string Number = @"[-+]?\d{1,3}(?:\.\d+)?";
    private static readonly TimeSpan RegexBudget = TimeSpan.FromMilliseconds(100);
    /// <summary>The place's own pin in the data blob; preferred over the viewport centre.</summary>
    private static readonly Regex DataPin = new($@"!3d({Number})!4d({Number})", RegexOptions.CultureInvariant, RegexBudget);
    /// <summary>"/maps/search/24.7,+46.6" and "/maps/place/24.7,46.6".</summary>
    private static readonly Regex PathPair = new($@"/maps/(?:search|place|dir)/({Number}),\s*\+?({Number})(?:[/?#]|$)", RegexOptions.CultureInvariant, RegexBudget);
    /// <summary>"/@24.7,46.6,17z": the map's centre.</summary>
    private static readonly Regex AtPair = new($@"/@({Number}),({Number})(?:,|$|/|\?)", RegexOptions.CultureInvariant, RegexBudget);
    private static readonly Regex Pair = new($@"^\s*({Number})\s*,\s*\+?({Number})\s*$", RegexOptions.CultureInvariant, RegexBudget);
    private static readonly string[] PairParameters = { "q", "query", "ll", "center", "destination", "daddr" };

    public static MapCoordinates? Parse(Uri uri)
    {
        var text = Uri.UnescapeDataString(uri.AbsoluteUri);
        if (Match(DataPin.Match(text)) is { } pin) return pin;
        if (Match(PathPair.Match(text)) is { } path) return path;
        foreach (var name in PairParameters)
            if (QueryValue(uri, name) is { } value && Match(Pair.Match(value)) is { } pair) return pair;
        return Match(AtPair.Match(text));
    }

    private static string? QueryValue(Uri uri, string name)
    {
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0 || !string.Equals(part[..eq], name, StringComparison.Ordinal)) continue;
            return Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
        }
        return null;
    }

    private static MapCoordinates? Match(Match m)
    {
        if (!m.Success) return null;
        if (!decimal.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
            || !decimal.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var lng)) return null;
        if (lat is < -90 or > 90 || lng is < -180 or > 180) return null;
        return new MapCoordinates(Math.Round(lat, 7), Math.Round(lng, 7));
    }

    /// <summary>The primary handler for <see cref="HttpClientName"/>: no redirects, no cookies, no proxy credentials.</summary>
    public static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(5),
    };
}
