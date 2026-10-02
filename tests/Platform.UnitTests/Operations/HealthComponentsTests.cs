using Platform.Modules.Operations.Contracts;

namespace Platform.UnitTests.Operations;

/// <summary>W-10 (O-14): Telemetry is checked and alerted like Disk, and the F-51 board keeps its seven tiles (docs/05 row 17).</summary>
public sealed class HealthComponentsTests
{
    [Fact]
    public void Telemetry_is_not_a_board_tile()
    {
        HealthComponents.Telemetry.ShouldBe("Telemetry");
        HealthComponents.Board.ShouldNotContain(HealthComponents.Telemetry);
        HealthComponents.Board.Count.ShouldBe(7);
    }
}
