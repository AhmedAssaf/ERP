using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Platform.Modules.Operations.Health;

/// <summary>
/// docs/05 row 19 (F-60): disk usage on the worker's own drive (<see cref="DriveInfo"/>, no platform-specific API),
/// alerted through the same open/close incident pipeline as any other check rather than shown as an F-51 board tile
/// (docs/05 row 17 lists exactly seven board tiles and disk is not one of them); "component" name is "Disk".
/// </summary>
internal sealed class DiskSpaceHealthCheck : IHealthCheck
{
    private const string Component = "Disk";
    private readonly string _driveRoot;
    private readonly int _thresholdPercent;

    public DiskSpaceHealthCheck(string path, int thresholdPercent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _driveRoot = Path.GetPathRoot(Path.GetFullPath(path)) is { Length: > 0 } root ? root : path;
        _thresholdPercent = thresholdPercent;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var drive = new DriveInfo(_driveRoot);
            var usedPercent = DiskUsage.UsedPercent(drive.TotalSize, drive.AvailableFreeSpace);

            return Task.FromResult(usedPercent >= _thresholdPercent
                ? HealthCheckResult.Unhealthy(
                    $"Disk usage on {_driveRoot} is {usedPercent}%, at or above the {_thresholdPercent}% threshold.")
                : HealthCheckResult.Healthy());
        }
        catch (Exception ex)
        {
            // Never ex.Message (N-10): a drive lookup failure could echo a UNC path or mapped-drive credential state.
            return Task.FromResult(HealthCheckResult.Unhealthy(
                HealthCheckMessages.WithExceptionType(ex, HealthCheckMessages.CouldNotReach(Component))));
        }
    }
}

/// <summary>The pure percentage decision, kept separate so it is unit-testable without a real drive.</summary>
internal static class DiskUsage
{
    public static int UsedPercent(long totalBytes, long availableFreeBytes)
    {
        if (totalBytes <= 0)
        {
            return 0;
        }

        var usedBytes = Math.Max(0, totalBytes - availableFreeBytes);
        return (int)Math.Round(usedBytes * 100.0 / totalBytes, MidpointRounding.AwayFromZero);
    }
}
