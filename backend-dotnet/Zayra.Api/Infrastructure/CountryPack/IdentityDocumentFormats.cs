using Zayra.Api.Application.CountryPack;

namespace Zayra.Api.Infrastructure.CountryPack;

// ── GCC identity-document FORMAT packs ─────────────────────────────────────────────────────────────
// Conservative, legally-VERIFY-before-live regexes for the statutory identity numbers of each GCC state.
// Registered keyed by ISO-3 (resolver maps ISO-2 → ISO-3); DefaultIdentityDocumentFormat is the non-keyed
// fallback that imposes no constraint. Keys match the readiness/catalog field keys exactly.

/// <summary>No format constraint — the fallback for non-GCC / unmodelled jurisdictions.</summary>
public sealed class DefaultIdentityDocumentFormat : IIdentityDocumentFormat
{
    public (string? Pattern, string? Hint) GetFormat(string fieldKey) => (null, null);
}

/// <summary>KSA: National ID (Hawiyya) 10 digits starting 1; Iqama 10 digits starting 2.</summary>
public sealed class KsaIdentityDocumentFormat : IIdentityDocumentFormat
{
    public (string? Pattern, string? Hint) GetFormat(string fieldKey) => fieldKey switch
    {
        "IdNumber" => (@"^1\d{9}$", "Saudi National ID: 10 digits starting with 1"),
        "IqamaNumber" => (@"^2\d{9}$", "Iqama: 10 digits starting with 2"),
        _ => (null, null),
    };
}

/// <summary>UAE: Emirates ID 15 digits, canonical form 784-YYYY-NNNNNNN-C (dashes optional).</summary>
public sealed class UaeIdentityDocumentFormat : IIdentityDocumentFormat
{
    public (string? Pattern, string? Hint) GetFormat(string fieldKey) => fieldKey switch
    {
        "EmiratesId" => (@"^784-?\d{4}-?\d{7}-?\d$", "Emirates ID: 15 digits (784-YYYY-NNNNNNN-C)"),
        _ => (null, null),
    };
}

/// <summary>Qatar: QID 11 digits.</summary>
public sealed class QatarIdentityDocumentFormat : IIdentityDocumentFormat
{
    public (string? Pattern, string? Hint) GetFormat(string fieldKey) => fieldKey switch
    {
        "Qid" => (@"^\d{11}$", "Qatar ID (QID): 11 digits"),
        _ => (null, null),
    };
}

/// <summary>Kuwait: Civil ID 12 digits (century-prefixed).</summary>
public sealed class KuwaitIdentityDocumentFormat : IIdentityDocumentFormat
{
    public (string? Pattern, string? Hint) GetFormat(string fieldKey) => fieldKey switch
    {
        "CivilId" => (@"^\d{12}$", "Kuwait Civil ID: 12 digits"),
        _ => (null, null),
    };
}

/// <summary>Oman: Civil / Resident-Card number 8 digits.</summary>
public sealed class OmanIdentityDocumentFormat : IIdentityDocumentFormat
{
    public (string? Pattern, string? Hint) GetFormat(string fieldKey) => fieldKey switch
    {
        "CivilId" => (@"^\d{8}$", "Oman Civil / Resident Card number: 8 digits"),
        _ => (null, null),
    };
}

/// <summary>Bahrain: CPR (Personal number) 9 digits.</summary>
public sealed class BahrainIdentityDocumentFormat : IIdentityDocumentFormat
{
    public (string? Pattern, string? Hint) GetFormat(string fieldKey) => fieldKey switch
    {
        "CivilId" => (@"^\d{9}$", "Bahrain CPR: 9 digits"),
        _ => (null, null),
    };
}
