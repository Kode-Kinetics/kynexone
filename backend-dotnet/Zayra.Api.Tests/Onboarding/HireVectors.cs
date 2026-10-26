using System.Text.Json;

namespace Zayra.Api.Tests.Onboarding;

/// <summary>Loader for the shared Fixtures/hire-vectors.json (also read by the frontend unit tests).</summary>
internal static class HireVectors
{
    private static readonly Lazy<JsonDocument> Doc = new(() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "hire-vectors.json"))));

    public static string[] Strings(string section, string key) =>
        Doc.Value.RootElement.GetProperty(section).GetProperty(key).EnumerateArray().Select(e => e.GetString()!).ToArray();

    public static IEnumerable<(string Input, string? Expected)> Pairs(string section, string key, string input, string expected) =>
        Doc.Value.RootElement.GetProperty(section).GetProperty(key).EnumerateArray()
            .Select(e => (e.GetProperty(input).GetString()!, e.GetProperty(expected).ValueKind == JsonValueKind.Null ? null : e.GetProperty(expected).GetString()))
            .ToArray();

    public static IEnumerable<(string Input, string Expected)> DigitNormalisation() =>
        Doc.Value.RootElement.GetProperty("digitNormalisation").EnumerateArray()
            .Select(e => (e.GetProperty("input").GetString()!, e.GetProperty("expected").GetString()!)).ToArray();
}
