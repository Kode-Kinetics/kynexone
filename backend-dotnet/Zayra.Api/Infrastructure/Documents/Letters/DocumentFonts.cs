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

    public const string LicenceConfigKey = "QuestPdf:License";

    /// <summary>
    /// The QuestPDF licence documents are rendered under, set once at startup by <see cref="ConfigureLicence"/>.
    /// Community until then (tests and Development).
    /// </summary>
    public static QuestPDF.Infrastructure.LicenseType Licence { get; private set; } = QuestPDF.Infrastructure.LicenseType.Community;

    /// <summary>
    /// Reads <c>QuestPdf:License</c> (<c>QuestPdf__License</c>): <c>Professional</c> or <c>Enterprise</c>, the commercial
    /// licence the vendor holds for client installations. The Community licence does not cover redistributing the
    /// product to clients, so outside Development a missing value or <c>Community</c> refuses to start, before any
    /// document can be issued under the wrong licence. Development falls back to Community.
    /// </summary>
    public static QuestPDF.Infrastructure.LicenseType ConfigureLicence(string? configured, bool isDevelopment)
    {
        var value = configured?.Trim();
        QuestPDF.Infrastructure.LicenseType licence;
        if (string.Equals(value, "Professional", StringComparison.OrdinalIgnoreCase))
            licence = QuestPDF.Infrastructure.LicenseType.Professional;
        else if (string.Equals(value, "Enterprise", StringComparison.OrdinalIgnoreCase))
            licence = QuestPDF.Infrastructure.LicenseType.Enterprise;
        else if (isDevelopment && (string.IsNullOrEmpty(value) || string.Equals(value, "Community", StringComparison.OrdinalIgnoreCase)))
            licence = QuestPDF.Infrastructure.LicenseType.Community;
        else
            throw new InvalidOperationException(
                $"[QuestPDF] {LicenceConfigKey} is '{value}'. Set QuestPdf__License to Professional or Enterprise: the Community " +
                "licence does not cover client installations.");

        lock (Gate)
        {
            Licence = licence;
            if (_initialised) QuestPDF.Settings.License = licence;
        }
        return licence;
    }

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

            QuestPDF.Settings.License = Licence;

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
