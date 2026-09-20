using System.Text;
using System.Text.RegularExpressions;

namespace Zayra.Api.Infrastructure.Documents;

/// <summary>
/// W2-D (S1/S3) — the gate every employee self-service upload passes before it reaches
/// <see cref="IDocumentStorage"/>.
///
/// Three independent checks, all of which must agree:
///   1. the DECLARED content type is on the allow-list;
///   2. the file EXTENSION belongs to that content type;
///   3. the BYTES start with that type's signature (magic-byte sniffing).
/// A file that claims to be a PDF but is an executable, or a .png that is really a JPEG, is refused.
/// The canonical content type and a server-built file name are returned, so nothing the client
/// declared about the file is stored verbatim.
/// </summary>
public static class EssUploadPolicy
{
    public const long MaxDocumentBytes = 10L * 1024 * 1024;
    public const long MaxPhotoBytes = 5L * 1024 * 1024;

    /// <summary>Multipart framing headroom on top of the file limit, for [RequestSizeLimit].</summary>
    public const long MultipartOverheadBytes = 64 * 1024;

    public const string Pdf = "application/pdf";
    public const string Jpeg = "image/jpeg";
    public const string Png = "image/png";
    public const string Heic = "image/heic";

    public static readonly IReadOnlySet<string> DocumentTypes = new HashSet<string>(StringComparer.Ordinal) { Pdf, Jpeg, Png, Heic };

    /// <summary>
    /// Profile photos must be DECODED to be re-encoded and stripped of EXIF, and the server-side
    /// decoder has no HEIC codec — so HEIC is not accepted for photos (the app converts to JPEG).
    /// </summary>
    public static readonly IReadOnlySet<string> PhotoTypes = new HashSet<string>(StringComparer.Ordinal) { Jpeg, Png };

    private static readonly Dictionary<string, string[]> ExtensionsByType = new(StringComparer.Ordinal)
    {
        [Pdf] = [".pdf"],
        [Jpeg] = [".jpg", ".jpeg"],
        [Png] = [".png"],
        [Heic] = [".heic", ".heif"],
    };

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["image/jpg"] = Jpeg,
        ["image/pjpeg"] = Jpeg,
        ["image/heif"] = Heic,
        ["image/x-png"] = Png,
    };

    private static readonly HashSet<string> HeicBrands = new(StringComparer.Ordinal)
        { "heic", "heix", "hevc", "hevx", "heim", "heis", "mif1", "msf1" };

    private static readonly Regex UnsafeNameChars = new(@"[^A-Za-z0-9._-]+", RegexOptions.CultureInvariant);

    public sealed record Verdict(bool Ok, string? Error, string ContentType, string FileName);

    /// <summary>Validates an upload's declared type, name and bytes against the allowed set.</summary>
    public static Verdict Check(string? declaredContentType, string? fileName, ReadOnlySpan<byte> bytes,
        long maxBytes, IReadOnlySet<string> allowedTypes)
    {
        if (bytes.Length == 0) return Fail("The file is empty.");
        if (bytes.Length > maxBytes) return Fail($"The file exceeds the {maxBytes / (1024 * 1024)} MB limit.");

        var declared = Normalize(declaredContentType);
        if (declared is null || !allowedTypes.Contains(declared))
            return Fail($"File type '{declaredContentType}' is not allowed. Allowed: {string.Join(", ", allowedTypes.OrderBy(x => x))}.");

        var ext = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        if (!ExtensionsByType[declared].Contains(ext))
            return Fail($"The file extension '{ext}' does not match the declared type {declared}.");

        var sniffed = Sniff(bytes);
        if (sniffed != declared)
            return Fail("The file content does not match its declared type.");

        return new Verdict(true, null, declared, SafeFileName(fileName, ext));
    }

    /// <summary>The content type the bytes actually are, or null when they are none of the allowed kinds.</summary>
    public static string? Sniff(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 5 && b[0] == (byte)'%' && b[1] == (byte)'P' && b[2] == (byte)'D' && b[3] == (byte)'F' && b[4] == (byte)'-')
            return Pdf;
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
            return Jpeg;
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47
            && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A)
            return Png;
        // ISO-BMFF: [size:4]["ftyp"][major brand:4]
        if (b.Length >= 12 && b[4] == (byte)'f' && b[5] == (byte)'t' && b[6] == (byte)'y' && b[7] == (byte)'p'
            && HeicBrands.Contains(Encoding.ASCII.GetString(b.Slice(8, 4))))
            return Heic;
        return null;
    }

    public static string? Normalize(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return null;
        var bare = contentType.Split(';')[0].Trim().ToLowerInvariant();
        return Aliases.TryGetValue(bare, out var canonical) ? canonical : bare;
    }

    /// <summary>
    /// A storage-safe file name: the client's base name reduced to [A-Za-z0-9._-], capped, with the
    /// VERIFIED extension. Never a path.
    /// </summary>
    public static string SafeFileName(string? fileName, string ext)
    {
        var baseName = Path.GetFileNameWithoutExtension(Path.GetFileName(fileName ?? string.Empty));
        baseName = UnsafeNameChars.Replace(baseName, "_").Trim('_', '.');
        if (baseName.Length == 0) baseName = "document";
        if (baseName.Length > 80) baseName = baseName[..80];
        return baseName + ext;
    }

    private static Verdict Fail(string error) => new(false, error, string.Empty, string.Empty);
}
