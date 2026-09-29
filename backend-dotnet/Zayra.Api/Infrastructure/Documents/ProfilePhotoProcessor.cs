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

    /// <exception cref="InvalidDataException">The bytes are not a decodable image, or are too large.</exception>
    public static byte[] ToSanitisedJpeg(byte[] source)
    {
        using var data = SKData.CreateCopy(source);
        using var codec = SKCodec.Create(data) ?? throw new InvalidDataException("The image could not be read.");
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > MaxSourcePixels)
            throw new InvalidDataException("The image dimensions are not supported.");

        using var decoded = SKBitmap.Decode(codec) ?? throw new InvalidDataException("The image could not be decoded.");
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
