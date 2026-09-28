using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Platform.Shared.Scanning;

/// <summary>
/// ClamAV settings (section <c>ClamAv</c>, the same keys the worker's health check reads): <c>Host</c>, <c>Port</c>
/// (3310), <c>TimeoutSeconds</c> for one whole scan (30), and <c>MaxStreamBytes</c>, which must not exceed clamd's
/// <c>StreamMaxLength</c> (25 MB by default; vendor documents are at most 10 MB). Without a host the scanner is not
/// configured and every scan is unavailable, so nothing is ever listed unscanned.
/// </summary>
public sealed record ClamAvSettings(string? Host, int Port, TimeSpan Timeout, long MaxStreamBytes = ClamAvSettings.DefaultMaxStreamBytes)
{
    public const string Section = "ClamAv";
    public const int DefaultPort = 3310;
    public const long DefaultMaxStreamBytes = 25L * 1024 * 1024;

    public static TimeSpan DefaultTimeout { get; } = TimeSpan.FromSeconds(30);

    /// <summary>Not configured: every scan is unavailable.</summary>
    public static ClamAvSettings None { get; } = new(null, DefaultPort, DefaultTimeout);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && Port is > 0 and <= 65535;

    public static ClamAvSettings FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(Section);
        return new ClamAvSettings(
            section["Host"],
            int.TryParse(section["Port"], NumberStyles.None, CultureInfo.InvariantCulture, out var port) ? port : DefaultPort,
            int.TryParse(section["TimeoutSeconds"], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
                ? TimeSpan.FromSeconds(seconds)
                : DefaultTimeout,
            long.TryParse(section["MaxStreamBytes"], NumberStyles.None, CultureInfo.InvariantCulture, out var max) && max > 0
                ? max
                : DefaultMaxStreamBytes);
    }
}
