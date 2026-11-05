using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Zayra.Api.Infrastructure.Geo;

/// <summary>One candidate place for a typed address: a display label and where it is.</summary>
public sealed record AddressSuggestion(string Label, string? Name, string? City, string? CountryCode, decimal Latitude, decimal Longitude);

/// <summary>
/// Turns a typed address into up to <see cref="MaxResults"/> candidate places, so an admin can pin a site without a
/// Google Maps link. Backed by a chain of free providers (OpenStreetMap Nominatim, then Photon; Geo:Providers) over fixed https hosts: the caller never supplies a URL,
/// the query goes out as a single encoded parameter, the response is size-capped and parsed into the few fields
/// above, and any failure (timeout, 4xx/5xx, bad JSON) is an empty list, never an error. Searches are explicit
/// (a Search button, never per keystroke) to stay inside Nominatim's usage policy; the controller rate-limits them.
/// </summary>
public sealed class AddressSearcher
{
    public const string HttpClientName = "address-searcher";
    public const int MaxResults = 5;
    public const int MaxQueryLength = 200;
    private const int MaxBodyBytes = 256 * 1024;
    private const int OverallSeconds = 10;
    private const int MaxLabelLength = 300;
    private static readonly Uri NominatimEndpoint = new("https://nominatim.openstreetmap.org/search");
    private static readonly Uri PhotonEndpoint = new("https://photon.komoot.io/api/");
    /// <summary>The only hosts the address search may ever contact. A keyed provider added later extends this list.</summary>
    private static readonly string[] AllowedHosts = ["nominatim.openstreetmap.org", "photon.komoot.io"];

    /// <summary>One free provider in the chain: builds its fixed-host request and parses its answer.</summary>
    private interface IProvider
    {
        string Name { get; }
        Uri BuildRequest(string query, string? country, string language);
        IReadOnlyList<AddressSuggestion> Parse(ReadOnlySpan<byte> json, string? country);
    }

    private sealed class NominatimProvider : IProvider
    {
        public string Name => "nominatim";
        public Uri BuildRequest(string q, string? country, string language)
        {
            var url = $"{NominatimEndpoint}?format=jsonv2&addressdetails=1&limit={MaxResults}&accept-language={(language == "ar" ? "ar" : "en")}&q={Uri.EscapeDataString(q)}";
            if (country is not null) url += $"&countrycodes={country}";
            return new Uri(url);
        }
        public IReadOnlyList<AddressSuggestion> Parse(ReadOnlySpan<byte> json, string? country) => AddressSearcher.Parse(json);
    }

    /// <summary>Photon (komoot): keyless, GeoJSON. It has no country parameter, so results are filtered here; it only knows default/en/de/fr/it, so Arabic omits lang.</summary>
    private sealed class PhotonProvider : IProvider
    {
        public string Name => "photon";
        public Uri BuildRequest(string q, string? country, string language)
        {
            var url = $"{PhotonEndpoint}?limit={(country is null ? MaxResults : MaxResults * 3)}&q={Uri.EscapeDataString(q)}";
            if (language == "en") url += "&lang=en";
            return new Uri(url);
        }
        public IReadOnlyList<AddressSuggestion> Parse(ReadOnlySpan<byte> json, string? country)
        {
            var results = new List<AddressSuggestion>();
            try
            {
                var reader = new Utf8JsonReader(json);
                using var doc = JsonDocument.ParseValue(ref reader);
                if (doc.RootElement.ValueKind != JsonValueKind.Object
                    || !doc.RootElement.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array) return [];
                foreach (var f in features.EnumerateArray())
                {
                    if (results.Count == MaxResults) break;
                    if (f.ValueKind != JsonValueKind.Object
                        || !f.TryGetProperty("geometry", out var g) || g.ValueKind != JsonValueKind.Object
                        || !g.TryGetProperty("coordinates", out var c) || c.ValueKind != JsonValueKind.Array || c.GetArrayLength() < 2
                        || !c[0].TryGetDecimal(out var lng) || !c[1].TryGetDecimal(out var lat)
                        || lat is < -90 or > 90 || lng is < -180 or > 180) continue;
                    if (!f.TryGetProperty("properties", out var p) || p.ValueKind != JsonValueKind.Object) continue;
                    var cc = Text(p, "countrycode")?.ToUpperInvariant();
                    if (country is not null && !string.Equals(cc, country, StringComparison.OrdinalIgnoreCase)) continue;
                    var street = Text(p, "street");
                    var streetLine = street is null ? null : Text(p, "housenumber") is { } n ? $"{street} {n}" : street;
                    var parts = new List<string>();
                    foreach (var part in new[] { Text(p, "name"), streetLine, Text(p, "district"), Text(p, "city"), Text(p, "state"), Text(p, "postcode"), Text(p, "country") })
                        if (part is not null && !parts.Contains(part, StringComparer.OrdinalIgnoreCase)) parts.Add(part);
                    if (parts.Count == 0) continue;
                    var label = string.Join(", ", parts);
                    if (label.Length > MaxLabelLength) label = label[..MaxLabelLength];
                    var city = Text(p, "city") ?? Text(p, "district") ?? Text(p, "state");
                    results.Add(new AddressSuggestion(label, Text(p, "name"), city, cc, Math.Round(lat, 6), Math.Round(lng, 6)));
                }
            }
            catch (JsonException) { return []; }
            return results;
        }
    }

    private readonly IHttpClientFactory _factory;
    private readonly IReadOnlyList<IProvider> _providers;

    /// <param name="config">Optional. <c>Geo:Providers</c> is a comma list (default <c>nominatim,photon</c>) giving the order, and which providers are on.</param>
    public AddressSearcher(IHttpClientFactory factory, IConfiguration? config = null)
    {
        _factory = factory;
        IProvider[] known = [new NominatimProvider(), new PhotonProvider()];
        var setting = config?["Geo:Providers"];
        if (string.IsNullOrWhiteSpace(setting)) { _providers = known; return; }
        _providers = setting.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => known.FirstOrDefault(k => string.Equals(k.Name, n, StringComparison.OrdinalIgnoreCase)))
            .OfType<IProvider>().Distinct().ToList();
    }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(6);

    /// <summary>The query as the provider should see it, or null when it is too short or too long to search.</summary>
    public static string? Normalise(string? query)
    {
        var q = string.Join(' ', (query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return q.Length is < 3 or > MaxQueryLength ? null : q;
    }

    /// <summary>Two-letter country code, or null: anything else is ignored rather than passed on.</summary>
    public static string? NormaliseCountry(string? country) =>
        country is { Length: 2 } && char.IsAsciiLetter(country[0]) && char.IsAsciiLetter(country[1]) ? country.ToLowerInvariant() : null;

    /// <summary>Tries each provider in order and returns the first non-empty answer; a provider that fails counts as empty. Never throws.</summary>
    public async Task<IReadOnlyList<AddressSuggestion>> SearchAsync(string? query, string? country, string language, CancellationToken ct)
    {
        var q = Normalise(query);
        if (q is null) return [];
        var cc = NormaliseCountry(country);
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overall.CancelAfter(TimeSpan.FromSeconds(OverallSeconds));
        foreach (var provider in _providers)
        {
            if (overall.IsCancellationRequested) break;
            try
            {
                var found = await TryAsync(provider, q, cc, language, overall.Token);
                if (found.Count > 0) return found;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { /* any provider failure is just "no answer from this one" */ }
        }
        return [];
    }

    private async Task<IReadOnlyList<AddressSuggestion>> TryAsync(IProvider provider, string q, string? cc, string language, CancellationToken overall)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(overall);
        timeout.CancelAfter(Timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, provider.BuildRequest(q, cc, language));
        using var response = await _factory.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode) return [];
        if (response.Content.Headers.ContentLength > MaxBodyBytes) return [];
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, timeout.Token)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxBodyBytes) return [];
        }
        return provider.Parse(buffer.ToArray(), cc);
    }

    public static IReadOnlyList<AddressSuggestion> Parse(ReadOnlySpan<byte> json)
    {
        var results = new List<AddressSuggestion>();
        try
        {
            var reader = new Utf8JsonReader(json);
            using var doc = JsonDocument.ParseValue(ref reader);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (results.Count == MaxResults) break;
                if (item.ValueKind != JsonValueKind.Object
                    || !TryDecimal(item, "lat", out var lat) || !TryDecimal(item, "lon", out var lng)
                    || lat is < -90 or > 90 || lng is < -180 or > 180) continue;
                var label = Text(item, "display_name");
                if (label is null) continue;
                string? city = null, cc = null;
                if (item.TryGetProperty("address", out var a) && a.ValueKind == JsonValueKind.Object)
                {
                    city = Text(a, "city") ?? Text(a, "town") ?? Text(a, "village") ?? Text(a, "suburb") ?? Text(a, "state");
                    cc = Text(a, "country_code")?.ToUpperInvariant();
                }
                results.Add(new AddressSuggestion(label, Text(item, "name"), city, cc, Math.Round(lat, 6), Math.Round(lng, 6)));
            }
        }
        catch (JsonException) { return []; }
        return results;
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? (s.Length > 300 ? s[..300] : s) : null;

    private static bool TryDecimal(JsonElement e, string name, out decimal value)
    {
        value = 0;
        return e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            && decimal.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>The primary handler for <see cref="HttpClientName"/>: no redirects, no cookies, only the allow-listed https hosts; the providers require an identifying User-Agent.</summary>
    public static HttpMessageHandler CreateHandler() => WithHostAllowList(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(5),
    });

    /// <summary>Wraps <paramref name="inner"/> so that any request that is not https to an allow-listed host fails before it leaves the process.</summary>
    public static DelegatingHandler WithHostAllowList(HttpMessageHandler inner) => new HostAllowListHandler(inner);

    private sealed class HostAllowListHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri;
            if (uri is null || uri.Scheme != Uri.UriSchemeHttps || !AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
                throw new HttpRequestException("Address search may only contact its fixed https providers.");
            return base.SendAsync(request, ct);
        }
    }
}
