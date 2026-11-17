using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Government;

/// <summary>
/// The per-tenant opt-in for sending a customer's data to a government or bank body (G2 of the live gate). Each key is an
/// opt-in feature (absent row = OFF) that only the platform team can switch on, through
/// <c>PUT /api/platform/tenants/{id}/features/{key}</c>, and only with a consent record in the flag's ConfigJson:
/// <code>{"consent":{"authorisedBy":"name of the customer's signatory","authorisedOn":"2026-11-16","agreementReference":"...","dataCategories":["payroll","bank_account"]}}</code>
/// Switching it off is always allowed and keeps the record. Never switched on in bulk.
/// </summary>
public static class GovernmentOptIn
{
    public const string ConsentMissingCode = "gov_consent_missing";
    public const string BulkEnableRefusedCode = "gov_bulk_enable_refused";

    /// <summary>The opt-in key of each body that has one. Qiwa keeps its existing module flag; Takamol has none yet.</summary>
    public static readonly IReadOnlyDictionary<string, string> KeyByBody = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [GovernmentBodies.Gosi] = FeatureKeys.GovGosiApi,
        [GovernmentBodies.Muqeem] = FeatureKeys.GovMuqeem,
        [GovernmentBodies.BankAnb] = FeatureKeys.BankApiAnb,
    };

    public static IReadOnlyCollection<string> Keys => KeyByBody.Values.ToArray();

    public static bool IsGovernmentKey(string? key) => key is not null && KeyByBody.Values.Contains(key, StringComparer.Ordinal);

    /// <summary>The data categories a consent may name. Anything else is refused, so the record means something.</summary>
    public static readonly IReadOnlySet<string> DataCategories = new HashSet<string>(StringComparer.Ordinal)
    {
        "identity", "employment", "payroll", "bank_account", "contributions", "residency",
    };

    /// <summary>The consent fields that are missing or invalid, by JSON path. Empty when the record is complete.</summary>
    public static IReadOnlyList<string> ConsentProblems(string? configJson, DateTime nowUtc)
    {
        var problems = new List<string>();
        JsonObject? consent = null;
        try { consent = (JsonNode.Parse(string.IsNullOrWhiteSpace(configJson) ? "{}" : configJson) as JsonObject)?["consent"] as JsonObject; }
        catch (JsonException) { }

        if (Text(consent, "authorisedBy") is not { Length: >= 2 and <= 200 }) problems.Add("consent.authorisedBy");
        if (!DateOnly.TryParseExact(Text(consent, "authorisedOn"), "yyyy-MM-dd", out var on)
            || on > DateOnly.FromDateTime(nowUtc) || on < new DateOnly(2026, 1, 1))
            problems.Add("consent.authorisedOn");
        if (Text(consent, "agreementReference") is not { Length: >= 3 and <= 120 }) problems.Add("consent.agreementReference");
        var categories = (consent?["dataCategories"] as JsonArray)?.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).ToList();
        if (categories is not { Count: > 0 } || categories.Any(c => c is null || !DataCategories.Contains(c))) problems.Add("consent.dataCategories");
        return problems;
    }

    /// <summary>The consent as recorded, for the audit row: who, when, which agreement and categories (no customer data).</summary>
    public static object? ConsentForAudit(string? configJson)
    {
        try { return (JsonNode.Parse(configJson ?? "{}") as JsonObject)?["consent"]?.DeepClone(); }
        catch (JsonException) { return null; }
    }

    /// <summary>G2: the tenant's flag for this body is on AND its stored consent record is complete.</summary>
    public static async Task<bool> HasConsentAsync(ZayraDbContext db, Guid tenantId, string body, CancellationToken ct)
    {
        if (!KeyByBody.TryGetValue(body, out var key)) return false;
        var flag = await db.TenantFeatureFlags.AsNoTracking()
            .Where(f => f.TenantId == tenantId && f.FeatureKey == key)
            .Select(f => new { f.IsEnabled, f.ConfigJson })
            .FirstOrDefaultAsync(ct);
        return flag is { IsEnabled: true } && ConsentProblems(flag.ConfigJson, DateTime.UtcNow).Count == 0;
    }

    private static string? Text(JsonObject? o, string key) =>
        o?[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
}
