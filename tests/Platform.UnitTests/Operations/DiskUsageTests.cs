using Platform.Modules.Operations.Health;

namespace Platform.UnitTests.Operations;

/// <summary>docs/05 row 19 (F-60): the disk-alert threshold decision, tested pure (no real drive).</summary>
public sealed class DiskUsageTests
{
    [Fact]
    public void Below_the_threshold_rounds_down_correctly() =>
        DiskUsage.UsedPercent(totalBytes: 100, availableFreeBytes: 21).ShouldBe(79);

    [Fact]
    public void At_the_threshold_is_at_or_above() =>
        DiskUsage.UsedPercent(totalBytes: 100, availableFreeBytes: 20).ShouldBe(80);

    [Fact]
    public void An_empty_drive_is_zero_percent_used() =>
        DiskUsage.UsedPercent(totalBytes: 100, availableFreeBytes: 100).ShouldBe(0);

    [Fact]
    public void A_full_drive_is_a_hundred_percent_used() =>
        DiskUsage.UsedPercent(totalBytes: 100, availableFreeBytes: 0).ShouldBe(100);

    [Fact]
    public void An_unknown_total_size_is_never_reported_as_a_failure() =>
        DiskUsage.UsedPercent(totalBytes: 0, availableFreeBytes: 0).ShouldBe(0);
}
