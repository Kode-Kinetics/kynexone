using System.Globalization;
using System.Text.Json;

namespace Zayra.Api.Infrastructure.Geo;

/// <summary>One candidate place for a typed address: a display label and where it is.</summary>
public sealed record AddressSuggestion(string Label, string? Name, string? City, string? CountryCode, decimal Latitude, decimal Longitude);

/// <summary>
/// Turns a typed address into up to <see cref="MaxResults"/> candidate places, so an admin can pin a site without a
/// Google Maps link. Backed by OpenStreetMap's Nominatim over a fixed https host: the caller never supplies a URL,
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
    private static readonly Uri Endpoint = new("https://nominatim.openstreetmap.org/search");

    private readonly IHttpClientFactory _factory;
    public AddressSearcher(IHttpClientFactory factory) => _factory = factory;

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

    public async Task<IReadOnlyList<AddressSuggestion>> SearchAsync(string? query, string? country, string language, CancellationToken ct)
    {
        var q = Normalise(query);
        if (q is null) return [];
        var url = $"{Endpoint}?format=jsonv2&addressdetails=1&limit={MaxResults}&accept-language={(language == "ar" ? "ar" : "en")}&q={Uri.EscapeDataString(q)}";
        if (NormaliseCountry(country) is { } cc) url += $"&countrycodes={cc}";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
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
            return Parse(buffer.ToArray());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return []; }
        catch (HttpRequestException) { return []; }
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

    /// <summary>The primary handler for <see cref="HttpClientName"/>: no redirects, no cookies; Nominatim requires an identifying User-Agent.</summary>
    public static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(5),
    };
}
