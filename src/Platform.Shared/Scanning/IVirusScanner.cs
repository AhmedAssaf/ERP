namespace Platform.Shared.Scanning;

/// <summary>What a virus scan found (vendor spec V-10).</summary>
public enum ScanVerdict
{
    /// <summary>The scanner read the whole content and found nothing.</summary>
    Clean,

    /// <summary>The scanner found a signature; <see cref="ScanResult.Signature"/> names it.</summary>
    Infected,

    /// <summary>
    /// No verdict: the scanner could not be reached, did not answer in time, or answered with an error. The content is
    /// neither clean nor infected; the caller keeps it aside and scans it again later.
    /// </summary>
    Unavailable,

    /// <summary>
    /// No verdict for this content: the scanner was reached and answered, but with an error about the content (a size or
    /// scan limit, a file it cannot read). Unlike <see cref="Unavailable"/>, other content may still be scanned now.
    /// </summary>
    Failed,
}

/// <summary>The verdict of one scan, with the signature name when infected.</summary>
public sealed record ScanResult(ScanVerdict Verdict, string? Signature)
{
    public static ScanResult Clean { get; } = new(ScanVerdict.Clean, null);

    public static ScanResult Unavailable { get; } = new(ScanVerdict.Unavailable, null);

    public static ScanResult Failed { get; } = new(ScanVerdict.Failed, null);

    public static ScanResult Infected(string signature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signature);
        return new ScanResult(ScanVerdict.Infected, signature);
    }
}

/// <summary>
/// Scans content for viruses before anyone else can read it (F-12, V-10). An outage is a result
/// (<see cref="ScanResult.Unavailable"/>), never an exception; only the caller's own cancellation throws.
/// </summary>
public interface IVirusScanner
{
    Task<ScanResult> ScanAsync(Stream content, CancellationToken cancellationToken = default);
}
