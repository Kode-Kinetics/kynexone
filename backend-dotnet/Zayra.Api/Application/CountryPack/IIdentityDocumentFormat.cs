namespace Zayra.Api.Application.CountryPack;

/// <summary>
/// FORMAT authority for GCC identity documents — the third leg of the field model (catalog = SHAPE,
/// floor = REQUIREDNESS, pack = FORMAT). Resolved per country via <see cref="ICountryPackResolver"/>.
/// Returns a conservative validation regex + a human hint for a readiness/catalog field key (e.g.
/// "EmiratesId", "IqamaNumber"); an unknown key returns (null, null) ⇒ no format constraint.
/// Patterns are CONSERVATIVE and require legal validation before being treated as authoritative — the
/// same disclaimer that governs the readiness floor applies (they gate write-validation as warnings).
/// </summary>
public interface IIdentityDocumentFormat
{
    /// <summary>The regex + hint for a field key, or (null, null) when the pack imposes no format.</summary>
    (string? Pattern, string? Hint) GetFormat(string fieldKey);
}
