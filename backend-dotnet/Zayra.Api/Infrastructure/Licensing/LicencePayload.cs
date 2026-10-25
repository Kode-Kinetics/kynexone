using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zayra.Api.Infrastructure.Licensing;

/// <summary>
/// What a vendor-signed licence grants. The signed bytes are this record serialised as JSON (camelCase);
/// the product never re-serialises before verifying — it checks the exact bytes it received.
/// </summary>
/// <remarks>
/// This file is compiled into BOTH the product and the vendor's issuing tool (tools/LicenceIssuer links it),
/// so the issuer and the verifier cannot disagree about a field or a rule. Keep it dependency-free.
/// </remarks>
public sealed record LicencePayload
{
    public const int CurrentFormatVersion = 1;

    public string LicenceId { get; init; } = string.Empty;
    public string Licensee { get; init; } = string.Empty;
    /// <summary>Binds the licence to one installation; null = any installation.</summary>
    public Guid? InstallationId { get; init; }
    public string Edition { get; init; } = string.Empty;
    public DateTime IssuedAtUtc { get; init; }
    public DateTime NotBeforeUtc { get; init; }
    /// <summary>null = perpetual.</summary>
    public DateTime? ExpiresAtUtc { get; init; }
    public int GraceDays { get; init; }
    /// <summary>Upgrades are refused after it (upgrade.sh). null = no limit.</summary>
    public DateTime? SupportExpiresAtUtc { get; init; }
    /// <summary><c>SingleCompany</c> or <c>Group</c>.</summary>
    public string AccountType { get; init; } = "SingleCompany";
    /// <summary>0 = unlimited, the same meaning the subscription columns had.</summary>
    public int MaxEmployees { get; init; }
    public int MaxUsers { get; init; }
    public int MaxCompanies { get; init; }
    public int MaxAdminUsers { get; init; }
    /// <summary>Monthly AI token allowance. 0 = AI disabled.</summary>
    public int AiMonthlyTokenLimit { get; init; }
    /// <summary>Optional module keys (ModuleCatalog) the organisation may switch on.</summary>
    public IReadOnlyList<string> Modules { get; init; } = [];
    /// <summary>Opt-in feature keys (OptInFeatures) the organisation may use.</summary>
    public IReadOnlyList<string> OptInFeatures { get; init; } = [];
    /// <summary>Allowed country packs (ISO-2); null = all.</summary>
    public IReadOnlyList<string>? Countries { get; init; }
    public int FormatVersion { get; init; } = CurrentFormatVersion;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    public static LicencePayload? Parse(byte[] utf8Json)
    {
        try { return JsonSerializer.Deserialize<LicencePayload>(utf8Json, Json); }
        catch (JsonException) { return null; }
    }

    public byte[] ToUtf8Bytes() => JsonSerializer.SerializeToUtf8Bytes(this, Json);

    public static readonly IReadOnlySet<string> AccountTypes = new HashSet<string>(StringComparer.Ordinal) { "SingleCompany", "Group" };

    /// <summary>
    /// Every structural reason the payload cannot be accepted, or an empty list. The caller supplies the key sets
    /// (the product passes its module catalog and opt-in keys; the issuer passes the same lists) and the clock.
    /// Installation binding and signature are checked by the verifier, not here.
    /// </summary>
    public IReadOnlyList<string> Problems(IReadOnlySet<string> knownModules, IReadOnlySet<string> knownOptIns, DateTime nowUtc)
    {
        var problems = new List<string>();
        if (FormatVersion != CurrentFormatVersion) problems.Add($"formatVersion {FormatVersion} is not supported (expected {CurrentFormatVersion}).");
        if (string.IsNullOrWhiteSpace(LicenceId)) problems.Add("licenceId is required.");
        if (string.IsNullOrWhiteSpace(Licensee)) problems.Add("licensee is required.");
        if (string.IsNullOrWhiteSpace(Edition)) problems.Add("edition is required.");
        if (!AccountTypes.Contains(AccountType)) problems.Add($"accountType '{AccountType}' must be SingleCompany or Group.");
        if (MaxEmployees < 0 || MaxUsers < 0 || MaxCompanies < 0 || MaxAdminUsers < 0 || AiMonthlyTokenLimit < 0)
            problems.Add("Limits must not be negative.");
        if (GraceDays < 0) problems.Add("graceDays must not be negative.");
        if (NotBeforeUtc > nowUtc) problems.Add($"The licence is not valid before {NotBeforeUtc:O}.");
        if (ExpiresAtUtc is { } expires && expires < NotBeforeUtc) problems.Add("expiresAtUtc is before notBeforeUtc.");
        foreach (var key in Modules.Where(k => !knownModules.Contains(k))) problems.Add($"Unknown module '{key}'.");
        foreach (var key in OptInFeatures.Where(k => !knownOptIns.Contains(k))) problems.Add($"Unknown opt-in feature '{key}'.");
        return problems;
    }
}

/// <summary>The licence file: <c>{ "payload": base64url, "signature": base64url, "kid": "..." }</c>.</summary>
public sealed record LicenceFile(string Payload, string Signature, string Kid)
{
    public static LicenceFile? Parse(string json)
    {
        try
        {
            var file = JsonSerializer.Deserialize<LicenceFile>(json, LicencePayload.Json);
            return file is null || string.IsNullOrWhiteSpace(file.Payload) || string.IsNullOrWhiteSpace(file.Signature)
                   || string.IsNullOrWhiteSpace(file.Kid)
                ? null
                : file;
        }
        catch (JsonException) { return null; }
    }

    public string ToJson() => JsonSerializer.Serialize(this, LicencePayload.Json);
}

public static class Base64Url
{
    public static string Encode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[]? Decode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = value.Trim().Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        try { return Convert.FromBase64String(s); }
        catch (FormatException) { return null; }
    }
}
