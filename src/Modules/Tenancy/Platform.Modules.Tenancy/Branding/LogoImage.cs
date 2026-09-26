using Platform.Modules.Tenancy.Contracts;
using Platform.Shared.Results;
using SkiaSharp;

namespace Platform.Modules.Tenancy.Branding;

/// <summary>A logo ready to store: PNG bytes and their size in pixels.</summary>
internal sealed record ProcessedLogo(byte[] Png, int Width, int Height);

/// <summary>
/// Turns an uploaded logo into the PNG we store (F-02, spec 4.3 and D-10). Only PNG and JPEG, recognised by their magic
/// numbers (SVG can carry script and is never accepted). The size in pixels is read from the header before any pixel is
/// decoded, so a small file announcing a huge image (a decompression bomb) is refused cheaply. The pixels are then
/// decoded, scaled down to <see cref="MaxLongSide"/> on the long side, and encoded as a new PNG: nothing of the original
/// file but its pixels survives, so metadata and anything appended to it are dropped.
/// </summary>
internal static class LogoImage
{
    public const int MaxBytes = 512 * 1024;
    public const int MaxSide = 4096;
    public const int MaxLongSide = 1024;

    public const string TooLarge = BrandingErrors.LogoTooLarge;
    public const string NotImage = BrandingErrors.LogoNotImage;
    public const string TooManyPixels = BrandingErrors.LogoTooManyPixels;
    public const string Unreadable = BrandingErrors.LogoUnreadable;

    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] JpegMagic = [0xFF, 0xD8, 0xFF];
    private static readonly string[] AcceptedTypes = ["image/png", "image/jpeg"];

    public static Result<ProcessedLogo> Process(byte[] data, string? contentType)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length > MaxBytes)
        {
            return Fail(TooLarge, $"The logo is larger than {MaxBytes / 1024} KB.");
        }

        var declared = contentType?.Split(';')[0].Trim();
        var format = Sniff(data);
        if (format is null || (!string.IsNullOrEmpty(declared) && !AcceptedTypes.Contains(declared, StringComparer.OrdinalIgnoreCase)))
        {
            return Fail(NotImage, "The logo must be a PNG or JPEG image.");
        }

        // PNG states its size in the first chunk; read it before handing the file to the decoder at all.
        if (format == SKEncodedImageFormat.Png && PngHeaderSize(data) is { } header && (header.Width > MaxSide || header.Height > MaxSide))
        {
            return Fail(TooManyPixels, $"The logo is larger than {MaxSide} pixels on a side.");
        }

        using var skData = SKData.CreateCopy(data);
        using var codec = SKCodec.Create(skData);
        if (codec is null || codec.EncodedFormat != format)
        {
            return Fail(Unreadable, "The logo could not be read as an image.");
        }

        var (width, height) = (codec.Info.Width, codec.Info.Height);
        if (width > MaxSide || height > MaxSide)
        {
            return Fail(TooManyPixels, $"The logo is larger than {MaxSide} pixels on a side.");
        }

        if (width <= 0 || height <= 0)
        {
            return Fail(Unreadable, "The logo could not be read as an image.");
        }

        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var decoded = new SKBitmap(info);
        if (codec.GetPixels(info, decoded.GetPixels()) != SKCodecResult.Success)
        {
            return Fail(Unreadable, "The logo could not be read as an image.");
        }

        var scale = Math.Min(1.0, (double)MaxLongSide / Math.Max(width, height));
        var (outWidth, outHeight) = (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
        using var resized = scale < 1.0
            ? decoded.Resize(new SKImageInfo(outWidth, outHeight, SKColorType.Rgba8888, SKAlphaType.Premul), new SKSamplingOptions(SKCubicResampler.Mitchell))
            : null;
        var output = resized ?? decoded;
        if (output is null)
        {
            return Fail(Unreadable, "The logo could not be read as an image.");
        }

        using var image = SKImage.FromBitmap(output);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png is null
            ? Fail(Unreadable, "The logo could not be read as an image.")
            : Result.Success(new ProcessedLogo(png.ToArray(), output.Width, output.Height));
    }

    private static SKEncodedImageFormat? Sniff(byte[] data)
    {
        if (data.AsSpan().StartsWith(PngMagic))
        {
            return SKEncodedImageFormat.Png;
        }

        return data.AsSpan().StartsWith(JpegMagic) ? SKEncodedImageFormat.Jpeg : null;
    }

    // Signature (8 bytes), then the IHDR chunk: length (4), "IHDR" (4), width (4, big-endian), height (4, big-endian).
    private static (uint Width, uint Height)? PngHeaderSize(byte[] data)
    {
        if (data.Length < 24 || !data.AsSpan(12, 4).SequenceEqual("IHDR"u8))
        {
            return null;
        }

        return (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16, 4)),
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(20, 4)));
    }

    private static Result<ProcessedLogo> Fail(string code, string message) =>
        Result.Failure<ProcessedLogo>(Error.Validation(code, message));
}
