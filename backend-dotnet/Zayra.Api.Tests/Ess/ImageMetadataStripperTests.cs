using System.Text;
using FluentAssertions;
using Zayra.Api.Infrastructure.Documents;

namespace Zayra.Api.Tests.Ess;

public class ImageMetadataStripperTests
{
    private static byte[] Seg(byte marker, byte[] payload) => [0xFF, marker, (byte)((payload.Length + 2) >> 8), (byte)((payload.Length + 2) & 0xFF), .. payload];
    private static byte[] Chunk(string type, byte[] data) => [(byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length, .. Encoding.ASCII.GetBytes(type), .. data, 0, 0, 0, 0];

    [Fact]
    public void Jpeg_DropsExifAndComments_KeepsPictureSegments()
    {
        byte[] jpeg =
        [
            0xFF, 0xD8,
            .. Seg(0xE0, Encoding.ASCII.GetBytes("JFIF\0\u0001\u0001\0\0\u0001\0\u0001\0\0")),
            .. Seg(0xE1, Encoding.ASCII.GetBytes("Exif\0\0GPS-SECRET-LAT-24.7")),
            .. Seg(0xFE, Encoding.ASCII.GetBytes("camera comment")),
            .. Seg(0xDB, [0, 1, 2, 3]),
            .. Seg(0xDA, [1, 1, 0, 0, 63, 0]), 0x11, 0x22, 0xFF, 0x00, 0x33,
            0xFF, 0xD9,
        ];
        var clean = ImageMetadataStripper.Strip(jpeg, "image/jpeg");
        var text = Encoding.Latin1.GetString(clean);
        text.Should().NotContain("Exif").And.NotContain("GPS-SECRET").And.NotContain("camera comment");
        clean[0].Should().Be(0xFF); clean[1].Should().Be(0xD8);
        text.Should().Contain("JFIF");
        clean.AsSpan().IndexOf(new byte[] { 0xFF, 0xDB }).Should().BeGreaterThan(0);
        // scan data after SOS (including the stuffed FF00) and the end marker survive untouched
        clean[^2].Should().Be(0xFF); clean[^1].Should().Be(0xD9);
        clean.AsSpan().IndexOf(new byte[] { 0x11, 0x22, 0xFF, 0x00, 0x33 }).Should().BeGreaterThan(0);
    }

    [Fact]
    public void Png_DropsTextAndExifChunks_KeepsPictureChunks()
    {
        byte[] png =
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            .. Chunk("IHDR", new byte[13]),
            .. Chunk("eXIf", Encoding.ASCII.GetBytes("GPS-SECRET")),
            .. Chunk("tEXt", Encoding.ASCII.GetBytes("Author\0Somebody")),
            .. Chunk("IDAT", [1, 2, 3, 4]),
            .. Chunk("IEND", []),
        ];
        var clean = ImageMetadataStripper.Strip(png, "image/png");
        var text = Encoding.Latin1.GetString(clean);
        text.Should().NotContain("eXIf").And.NotContain("tEXt").And.NotContain("GPS-SECRET").And.NotContain("Somebody");
        text.Should().Contain("IHDR").And.Contain("IDAT").And.EndWith("IEND\0\0\0\0");
        clean.Take(8).Should().Equal(png.Take(8));
    }

    [Fact]
    public void OtherTypes_AreReturnedUnchanged_AndGarbageNeverThrows()
    {
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.4 hello");
        ImageMetadataStripper.Strip(pdf, "application/pdf").Should().Equal(pdf);
        var broken = new byte[] { 0xFF, 0xD8, 0xFF, 0xE1, 0xFF, 0xFF, 1 };
        var act = () => ImageMetadataStripper.Strip(broken, "image/jpeg");
        act.Should().NotThrow();
        var heic = new byte[] { 0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'h', (byte)'e', (byte)'i', (byte)'c' };
        ImageMetadataStripper.Strip(heic, "image/heic").Should().Equal(heic);
    }
}
