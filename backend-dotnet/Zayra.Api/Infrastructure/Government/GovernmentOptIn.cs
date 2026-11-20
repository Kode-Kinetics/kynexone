using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Government;

/// <summary>
/// The per-tenant opt-in for sending a customer's data to a government or bank body (G2 of the live gate). Each key is an
/// opt-in feature (absent row = OFF) that only a platform OWNER, signed in with a named account, can switch on through
/// <c>PUT /api/platform/tenants/{id}/features/{key}</c>, and only with a consent record in the request:
/// <code>{"consent":{"agreementReference":"...","dataCategories":["payroll","bank_account"]}}</code>
/// The server stamps who recorded it and when (<c>recordedBy</c>, <c>recordedByEmail</c>, <c>recordedAtUtc</c>); anything else
/// the caller typed (a "who" or a "when" included) is dropped. Switching it off is always allowed (an Admin may), keeps the
/// record and stamps <c>withdrawnAtUtc</c>; switching it back on needs a fresh consent in that request, so a withdrawn
/// record is never reused. Never switched on in bulk.
/// </summary>
public static class GovernmentOptIn
{
    public const string ConsentMissingCode = "gov_consent_missing";
    public const string OwnerOnlyCode = "gov_consent_owner_only";
    public const string BulkEnableRefusedCode = "gov_bulk_enable_refused";

    public const int MaxAgreementReferenceLength = 120;

    /// <summary>The opt-in key of each body that has one. Takamol has none yet.</summary>
    public static readonly IReadOnlyDictionary<string, string> KeyByBody = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [GovernmentBodies.Qiwa] = FeatureKeys.GovQiwaApi,
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

    /// <summary>The fields a REQUEST must supply that are missing or invalid, by JSON path.</summary>
    public static IReadOnlyList<string> RequestProblems(string? configJson)
    {
        var consent = ConsentOf(configJson);
        var problems = new List<string>();
        if (Text(consent, "agreementReference") is not { } reference || reference.Length < 3 || reference.Length > MaxAgreementReferenceLength
            || reference.Any(char.IsControl))
            problems.Add("consent.agreementReference");
        if (Categories(consent) is not { Count: > 0 } categories || categories.Any(c => c is null || !DataCategories.Contains(c)))
            problems.Add("consent.dataCategories");
        return problems;
    }

    /// <summary>Everything that must hold for a STORED record to count: the request fields, the server stamps, no withdrawal.</summary>
    public static IReadOnlyList<string> StoredProblems(string? configJson)
    {
        var consent = ConsentOf(configJson);
        var problems = RequestProblems(configJson).ToList();
        if (Text(consent, "recordedBy") is not { } by || !Guid.TryParse(by, out var id) || id == Guid.Empty) problems.Add("consent.recordedBy");
        if (!DateTime.TryParse(Text(consent, "recordedAtUtc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)) problems.Add("consent.recordedAtUtc");
        if (Text(consent, "withdrawnAtUtc") is not null) problems.Add("consent.withdrawnAtUtc");
        return problems;
    }

    /// <summary>The record to store: only the known request fields, plus the server's stamps. Typed who/when are dropped.</summary>
    public static string Stamp(string? requestConfigJson, Guid recordedBy, string recordedByEmail, DateTime nowUtc)
    {
        var consent = ConsentOf(requestConfigJson);
        var record = new JsonObject
        {
            ["agreementReference"] = Text(consent, "agreementReference"),
            ["dataCategories"] = new JsonArray(Categories(consent)!.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
            ["recordedBy"] = recordedBy.ToString(),
            ["recordedByEmail"] = recordedByEmail,
            ["recordedAtUtc"] = nowUtc.ToString("O", CultureInfo.InvariantCulture),
        };
        return new JsonObject { ["consent"] = record }.ToJsonString();
    }

    /// <summary>Switching off keeps the record as evidence and marks it withdrawn, so it can never open the gate again.</summary>
    public static string? Withdraw(string? storedConfigJson, DateTime nowUtc)
    {
        if (ParseObject(storedConfigJson) is not { } root || root["consent"] is not JsonObject consent) return storedConfigJson;
        consent["withdrawnAtUtc"] ??= nowUtc.ToString("O", CultureInfo.InvariantCulture);
        return root.ToJsonString();
    }

    /// <summary>The consent as recorded, for the audit row: the known keys only.</summary>
    public static object? ConsentForAudit(string? configJson)
    {
        var consent = ConsentOf(configJson);
        if (consent is null) return null;
        return new
        {
            agreementReference = Text(consent, "agreementReference"),
            dataCategories = Categories(consent),
            recordedBy = Text(consent, "recordedBy"),
            recordedAtUtc = Text(consent, "recordedAtUtc"),
            withdrawnAtUtc = Text(consent, "withdrawnAtUtc"),
        };
    }

    /// <summary>G2: the tenant's flag for this body is on AND its stored, server-stamped, unwithdrawn consent record is complete.</summary>
    public static async Task<bool> HasConsentAsync(ZayraDbContext db, Guid tenantId, string body, CancellationToken ct)
    {
        if (!KeyByBody.TryGetValue(body, out var key)) return false;
        var flag = await db.TenantFeatureFlags.AsNoTracking()
            .Where(f => f.TenantId == tenantId && f.FeatureKey == key)
            .Select(f => new { f.IsEnabled, f.ConfigJson })
            .FirstOrDefaultAsync(ct);
        return flag is { IsEnabled: true } && StoredProblems(flag.ConfigJson).Count == 0;
    }

    private static JsonObject? ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static JsonObject? ConsentOf(string? json) => ParseObject(json)?["consent"] as JsonObject;

    private static List<string?>? Categories(JsonObject? consent) =>
        (consent?["dataCategories"] as JsonArray)?.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).ToList();

    private static string? Text(JsonObject? o, string key) =>
        o?[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
}
