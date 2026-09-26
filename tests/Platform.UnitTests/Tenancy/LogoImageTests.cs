using System.Text;
using Platform.Modules.Tenancy.Branding;
using SkiaSharp;

namespace Platform.UnitTests.Tenancy;

/// <summary>
/// F-02 logo handling (spec 4.3, D-10): PNG or JPEG only by magic number, never more than 4096 px on a side (checked from
/// the header, before any pixel is decoded), re-encoded to PNG at most 1024 px on the long side, which drops metadata.
/// </summary>
public sealed class LogoImageTests
{
    [Fact]
    public void A_png_is_reencoded_as_png_at_its_own_size()
    {
        var result = LogoImage.Process(Image(300, 120, SKEncodedImageFormat.Png), "image/png");

        result.IsSuccess.ShouldBeTrue();
        result.Value.Width.ShouldBe(300);
        result.Value.Height.ShouldBe(120);
        IsPng(result.Value.Png).ShouldBeTrue();
    }

    [Fact]
    public void A_large_jpeg_is_resized_to_1024_on_the_long_side()
    {
        var result = LogoImage.Process(Image(2048, 1000, SKEncodedImageFormat.Jpeg), "image/jpeg");

        result.IsSuccess.ShouldBeTrue();
        result.Value.Width.ShouldBe(1024);
        result.Value.Height.ShouldBe(500);
        using var decoded = SKBitmap.Decode(result.Value.Png);
        (decoded.Width, decoded.Height).ShouldBe((1024, 500));
        IsPng(result.Value.Png).ShouldBeTrue();
    }

    [Fact]
    public void Metadata_in_the_upload_is_not_kept()
    {
        // A PNG with a text chunk after the header: the re-encoded file is built from pixels only.
        var png = Image(64, 64, SKEncodedImageFormat.Png);
        var withText = InsertChunk(png, "tEXt", Encoding.ASCII.GetBytes("Comment\0secret-marker"));
        Encoding.ASCII.GetString(withText).ShouldContain("secret-marker");

        var result = LogoImage.Process(withText, "image/png");

        result.IsSuccess.ShouldBeTrue();
        Encoding.ASCII.GetString(result.Value.Png).ShouldNotContain("secret-marker");
    }

    [Fact]
    public void The_same_image_gives_the_same_bytes()
    {
        var input = Image(200, 100, SKEncodedImageFormat.Png);

        LogoImage.Process(input, "image/png").Value.Png.ShouldBe(LogoImage.Process(input, "image/png").Value.Png);
    }

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>", "image/svg+xml")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>", "image/png")]
    [InlineData("GIF89a\u0001\u0000\u0001\u0000", "image/gif")]
    [InlineData("just some text", "text/plain")]
    public void Anything_but_png_or_jpeg_is_refused(string content, string contentType)
    {
        var result = LogoImage.Process(Encoding.UTF8.GetBytes(content), contentType);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(LogoImage.NotImage);
    }

    [Fact]
    public void A_png_declared_as_another_type_is_refused()
    {
        var result = LogoImage.Process(Image(10, 10, SKEncodedImageFormat.Png), "image/svg+xml");

        result.Error!.Code.ShouldBe(LogoImage.NotImage);
    }

    [Fact]
    public void A_truncated_png_is_refused_as_unreadable()
    {
        var png = Image(100, 100, SKEncodedImageFormat.Png);

        var result = LogoImage.Process(png[..40], "image/png");

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(LogoImage.Unreadable);
    }

    [Theory]
    [InlineData(4097, 10)]
    [InlineData(10, 4097)]
    public void An_image_over_4096_pixels_on_a_side_is_refused_before_decoding(int width, int height)
    {
        var result = LogoImage.Process(Image(width, height, SKEncodedImageFormat.Png), "image/png");

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(LogoImage.TooManyPixels);
    }

    [Fact]
    public void A_decompression_bomb_header_is_refused_without_decoding_it()
    {
        // A valid PNG header announcing 60000 x 60000 pixels (about 14 GB decoded) followed by no image data.
        var result = LogoImage.Process(PngHeaderOnly(60_000, 60_000), "image/png");

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(LogoImage.TooManyPixels);
    }

    [Fact]
    public void More_than_512_KB_is_refused()
    {
        var result = LogoImage.Process(new byte[LogoImage.MaxBytes + 1], "image/png");

        result.Error!.Code.ShouldBe(LogoImage.TooLarge);
    }

    private static byte[] Image(int width, int height, SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(0x0F, 0x76, 0x6E));
            using var paint = new SKPaint { Color = SKColors.White };
            canvas.DrawRect(width / 4f, height / 4f, width / 2f, height / 2f, paint);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    private static bool IsPng(byte[] bytes) =>
        bytes.Length > 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

    private static byte[] PngHeaderOnly(int width, int height)
    {
        var signature = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var ihdr = new byte[13];
        WriteBigEndian(ihdr, 0, width);
        WriteBigEndian(ihdr, 4, height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 6; // RGBA
        return [.. signature, .. Chunk("IHDR", ihdr), .. Chunk("IEND", [])];
    }

    private static byte[] InsertChunk(byte[] png, string type, byte[] data)
    {
        // After the signature (8 bytes) and IHDR (4 length + 4 type + 13 data + 4 CRC = 25 bytes).
        const int afterHeader = 8 + 25;
        return [.. png[..afterHeader], .. Chunk(type, data), .. png[afterHeader..]];
    }

    private static byte[] Chunk(string type, byte[] data)
    {
        var typeBytes = Encoding.ASCII.GetBytes(type);
        var chunk = new byte[12 + data.Length];
        WriteBigEndian(chunk, 0, data.Length);
        typeBytes.CopyTo(chunk, 4);
        data.CopyTo(chunk, 8);
        WriteBigEndian(chunk, 8 + data.Length, unchecked((int)Crc32([.. typeBytes, .. data])));
        return chunk;
    }

    private static void WriteBigEndian(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
