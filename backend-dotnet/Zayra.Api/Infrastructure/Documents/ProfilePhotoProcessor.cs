using SkiaSharp;

namespace Zayra.Api.Infrastructure.Documents;

/// <summary>
/// W2-D (S3) — turns an uploaded profile photo into a small, metadata-free JPEG.
///
/// Phone cameras write EXIF, including GPS coordinates of where the photo was taken. The original
/// bytes are therefore NEVER stored: the image is decoded, rotated upright according to its EXIF
/// orientation (so stripping the tag does not leave the face sideways), scaled to fit
/// <see cref="MaxEdge"/>×<see cref="MaxEdge"/>, flattened onto white (PNG transparency) and
/// re-encoded. Skia's JPEG encoder writes no APPn metadata, so the output carries no EXIF, XMP or
/// ICC blocks from the source.
/// </summary>
public static class ProfilePhotoProcessor
{
    public const int MaxEdge = 512;
    public const int JpegQuality = 85;

    /// <summary>Refuse to decode anything larger — a small file can still declare a huge canvas.</summary>
    public const long MaxSourcePixels = 50_000_000;

    /// <summary>Profile photos: up to <see cref="MaxSourcePixels"/>, decoded toward <see cref="DefaultDecodeLongEdge"/>.</summary>
    public const int DefaultDecodeLongEdge = 1080;

    /// <exception cref="InvalidDataException">The bytes are not a decodable image, or are too large.</exception>
    public static byte[] ToSanitisedJpeg(byte[] source)
    {
        try { return ToSanitisedJpeg(source, MaxSourcePixels, DefaultDecodeLongEdge); }
        catch (ImageTooLargeException ex) { throw new InvalidDataException("The image dimensions are not supported.", ex); }
    }

    /// <summary>
    /// As <see cref="ToSanitisedJpeg(byte[])"/>, with a caller's pixel cap. The header is read first and anything over
    /// <paramref name="maxSourcePixels"/> is refused before a single pixel is decoded. Formats that can (JPEG) are then
    /// DECODED DOWNSAMPLED — Skia's codec scales by 1/2, 1/4 or 1/8 while decoding — toward
    /// <paramref name="decodeLongEdge"/>, so a 12 MP phone photo is never held at full size in memory.
    /// </summary>
    /// <exception cref="ImageTooLargeException">The declared canvas exceeds <paramref name="maxSourcePixels"/>.</exception>
    /// <exception cref="InvalidDataException">The bytes are not a decodable image.</exception>
    public static byte[] ToSanitisedJpeg(byte[] source, long maxSourcePixels, int decodeLongEdge)
    {
        using var data = SKData.CreateCopy(source);
        using var codec = SKCodec.Create(data) ?? throw new InvalidDataException("The image could not be read.");
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0) throw new InvalidDataException("The image dimensions are not supported.");
        if ((long)info.Width * info.Height > maxSourcePixels)
            throw new ImageTooLargeException(info.Width, info.Height, maxSourcePixels);

        using var decoded = DecodeDownsampled(codec, Math.Max(decodeLongEdge, MaxEdge));
        using var upright = ApplyOrigin(decoded, codec.EncodedOrigin);

        var scale = Math.Min(1.0, (double)MaxEdge / Math.Max(upright.Width, upright.Height));
        var width = Math.Max(1, (int)Math.Round(upright.Width * scale));
        var height = Math.Max(1, (int)Math.Round(upright.Height * scale));

        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new InvalidDataException("The image could not be processed.");
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);
        using (var image = SKImage.FromBitmap(upright))
        {
            canvas.DrawImage(image, new SKRect(0, 0, width, height),
                new SKSamplingOptions(SKCubicResampler.Mitchell));
        }
        canvas.Flush();

        using var snapshot = surface.Snapshot();
        using var encoded = snapshot.Encode(SKEncodedImageFormat.Jpeg, JpegQuality)
            ?? throw new InvalidDataException("The image could not be encoded.");
        return encoded.ToArray();
    }

    /// <summary>
    /// The dimensions the codec will decode at for a long edge of <paramref name="targetLongEdge"/>: the smallest size
    /// the codec supports that is still at least the target (JPEG: 1/2, 1/4, 1/8), or the full size.
    /// </summary>
    public static SKSizeI DecodeSize(SKCodec codec, int targetLongEdge)
    {
        var info = codec.Info;
        var longEdge = Math.Max(info.Width, info.Height);
        if (longEdge <= targetLongEdge) return info.Size;
        // JPEG decodes at k/8 of full size. Take the smallest scale the codec supports that still reaches the target, so
        // the output is never upscaled from too small a decode; codecs that cannot scale return the full size here.
        for (var k = 1; k < 8; k++)
        {
            var scaled = codec.GetScaledDimensions(k / 8f);
            if (scaled.Width > 0 && scaled.Height > 0 && Math.Max(scaled.Width, scaled.Height) >= targetLongEdge) return scaled;
        }
        return info.Size;
    }

    private static SKBitmap DecodeDownsampled(SKCodec codec, int targetLongEdge)
    {
        var size = DecodeSize(codec, targetLongEdge);
        var target = new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(target);
        var result = codec.GetPixels(target, bitmap.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            bitmap.Dispose();
            throw new InvalidDataException("The image could not be decoded.");
        }
        return bitmap;
    }

    /// <summary>Bakes the EXIF orientation into the pixels (the tag itself is discarded).</summary>
    private static SKBitmap ApplyOrigin(SKBitmap src, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft) return src.Copy();

        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var w = swap ? src.Height : src.Width;
        var h = swap ? src.Width : src.Height;
        var dst = new SKBitmap(new SKImageInfo(w, h, src.ColorType, src.AlphaType));
        using var canvas = new SKCanvas(dst);
        switch (origin)
        {
            case SKEncodedOrigin.TopRight:     // mirror horizontal
                canvas.Scale(-1, 1); canvas.Translate(-w, 0); break;
            case SKEncodedOrigin.BottomRight:  // rotate 180
                canvas.RotateDegrees(180); canvas.Translate(-w, -h); break;
            case SKEncodedOrigin.BottomLeft:   // mirror vertical
                canvas.Scale(1, -1); canvas.Translate(0, -h); break;
            case SKEncodedOrigin.LeftTop:      // transpose
                canvas.RotateDegrees(90); canvas.Scale(1, -1); break;
            case SKEncodedOrigin.RightTop:     // rotate 90 CW
                canvas.Translate(w, 0); canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.RightBottom:  // transverse
                canvas.Translate(w, h); canvas.RotateDegrees(90); canvas.Scale(-1, 1); break;
            case SKEncodedOrigin.LeftBottom:   // rotate 90 CCW
                canvas.Translate(0, h); canvas.RotateDegrees(270); break;
        }
        canvas.DrawBitmap(src, 0, 0);
        canvas.Flush();
        return dst;
    }
}

/// <summary>An image whose declared canvas is larger than allowed. Refused from the header, before any decode.</summary>
public sealed class ImageTooLargeException(int width, int height, long maxPixels)
    : Exception($"The image is {width}×{height} ({(long)width * height:N0} pixels); at most {maxPixels:N0} are accepted.")
{
    public int Width { get; } = width;
    public int Height { get; } = height;
}
