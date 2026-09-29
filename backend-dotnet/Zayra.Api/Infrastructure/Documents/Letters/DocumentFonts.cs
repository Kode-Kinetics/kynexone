using System.Reflection;
using QuestPDF.Drawing;
using QuestPDF.Helpers;

namespace Zayra.Api.Infrastructure.Documents.Letters;

/// <summary>
/// Registers the fonts every generated PDF is allowed to use.
///
/// Why this type exists at all: before it, nothing in the codebase called
/// <see cref="FontManager"/>. QuestPDF's default behaviour is to fall back to whatever
/// fonts the host operating system happens to expose. A developer Mac ships Geeza Pro
/// and SF Arabic, so Arabic labels rendered fine locally; the production container is a
/// slim Linux image with no Arabic-capable font at all, so every Arabic string on the
/// payslip rendered as a row of tofu boxes (□□□□). Nobody noticed because nobody ever
/// opened a production payslip with <c>Locale = "ar"</c>.
///
/// Two decisions follow from that:
///
/// 1. <see cref="QuestPDF.Settings.UseEnvironmentFonts"/> is turned OFF. A document must
///    render byte-comparably on a laptop and in the container; silently borrowing a host
///    font is the mechanism that hid the defect for as long as it did. With it off, a
///    missing glyph is visible everywhere instead of only in production.
/// 2. Noto Sans Arabic (SIL Open Font License 1.1 — see Assets/Fonts/OFL.txt) is embedded
///    in the assembly and registered as the fallback family, so any Arabic codepoint in
///    any document resolves to a real glyph. HarfBuzz (via SkiaSharp, already a QuestPDF
///    dependency) does the contextual shaping and bidi ordering.
/// </summary>
public static class DocumentFonts
{
    /// <summary>Family name as reported by the embedded TTF's name table.</summary>
    public const string ArabicFamily = "Noto Sans Arabic";

    /// <summary>Latin family; QuestPDF bundles Lato, so it needs no registration.</summary>
    public const string LatinFamily = Fonts.Lato;

    private static readonly object Gate = new();
    private static bool _initialised;

    /// <summary>
    /// Idempotent. Safe to call from a static constructor, from Program.cs, and from a test
    /// fixture; the first caller wins and the rest are a lock acquisition.
    /// </summary>
    public static void EnsureRegistered()
    {
        if (_initialised) return;
        lock (Gate)
        {
            if (_initialised) return;

            QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

            // Deterministic rendering: never silently borrow a host font. See the type comment.
            QuestPDF.Settings.UseEnvironmentFonts = false;

            RegisterEmbedded("Zayra.Api.Assets.Fonts.NotoSansArabic-Regular.ttf");
            RegisterEmbedded("Zayra.Api.Assets.Fonts.NotoSansArabic-Bold.ttf");

            _initialised = true;
        }
    }

    private static void RegisterEmbedded(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded font '{resourceName}' is missing from the assembly. Arabic documents " +
                "would render as tofu boxes, so this fails at startup rather than at issuance. " +
                "Check the EmbeddedResource glob in Zayra.Api.csproj.");
        FontManager.RegisterFont(stream);
    }

    /// <summary>
    /// The family chain every document should use: Latin first, Arabic as the fallback for
    /// any codepoint Lato cannot draw. Mixed "SAR 15,000 — ريال" strings resolve per-run.
    /// </summary>
    public static string[] Chain => [LatinFamily, ArabicFamily];
}
