namespace Zayra.Api.Application.Localization;

/// <summary>
/// Offline, deterministic Latin→Arabic phonetic transliteration.
///
/// P0-6: replaces the previous frontend keystroke call to the public MyMemory translation API,
/// which exfiltrated employee and company legal names to an uncontrolled third party (PDPL Art.29/6,
/// GDPR Art.44/6). This service runs entirely in-process — no outbound network, no third party —
/// so personal data never leaves our infrastructure. Results are advisory suggestions the user
/// previews and can edit before accepting (opt-in beside the existing manual Arabic field).
/// </summary>
public interface ITransliterationService
{
    /// <summary>Best-effort phonetic Arabic rendering of Latin-script text. Returns "" for empty input.</summary>
    string ToArabic(string text);
}
