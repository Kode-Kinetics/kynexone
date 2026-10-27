namespace Zayra.Api.Infrastructure.Documents;

/// <summary>
/// Removes metadata that can carry a person's location or device from an uploaded image, without re-encoding it (the picture
/// must stay as legible as the original: it is an identity document). JPEG: Exif/XMP (APP1), IPTC (APP13) and comment segments.
/// PNG: text and Exif chunks. Anything else is returned as it came; a file it cannot parse is returned as it came, never thrown on.
/// </summary>
public static class ImageMetadataStripper
{
    public static byte[] Strip(byte[] bytes, string contentType)
    {
        try
        {
            return contentType switch
            {
                EssUploadPolicy.Jpeg => StripJpeg(bytes),
                EssUploadPolicy.Png => StripPng(bytes),
                _ => bytes,
            };
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException) { return bytes; }
    }

    private static byte[] StripJpeg(byte[] b)
    {
        if (b.Length < 4 || b[0] != 0xFF || b[1] != 0xD8) return b;
        var o = new MemoryStream(b.Length);
        o.Write(b, 0, 2);
        var i = 2;
        while (i + 4 <= b.Length)
        {
            if (b[i] != 0xFF) return b;                       // not a segment boundary: leave the file alone
            var marker = b[i + 1];
            if (marker == 0xFF) { i++; continue; }            // fill byte
            if (marker == 0xDA)                               // start of scan: the rest is entropy-coded data, copied verbatim
            {
                o.Write(b, i, b.Length - i);
                return o.ToArray();
            }
            if (marker is 0x01 or (>= 0xD0 and <= 0xD8)) { o.Write(b, i, 2); i += 2; continue; }   // standalone markers
            var len = (b[i + 2] << 8) | b[i + 3];
            if (len < 2 || i + 2 + len > b.Length) return b;
            var drop = marker is 0xE1 or 0xED or 0xFE;       // APP1 (Exif, XMP), APP13 (IPTC), COM
            if (!drop) o.Write(b, i, 2 + len);
            i += 2 + len;
        }
        o.Write(b, i, b.Length - i);
        return o.ToArray();
    }

    private static readonly HashSet<string> DroppedPngChunks = new(StringComparer.Ordinal) { "eXIf", "tEXt", "iTXt", "zTXt", "tIME" };

    private static byte[] StripPng(byte[] b)
    {
        if (b.Length < 8) return b;
        var o = new MemoryStream(b.Length);
        o.Write(b, 0, 8);
        var i = 8;
        while (i + 12 <= b.Length)
        {
            var len = (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];
            if (len < 0 || i + 12L + len > b.Length) return b;
            var type = System.Text.Encoding.ASCII.GetString(b, i + 4, 4);
            if (!DroppedPngChunks.Contains(type)) o.Write(b, i, 12 + len);
            i += 12 + len;
        }
        o.Write(b, i, b.Length - i);
        return o.ToArray();
    }
}
