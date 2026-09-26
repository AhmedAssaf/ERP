using Platform.Modules.Operations.Contracts;

namespace Platform.Modules.Operations;

/// <summary>
/// The F-60 incident open/close decision, kept pure and separate from persistence so it is unit-testable without a
/// database: open on the first Unhealthy result with no open incident, close on the first Healthy result while one
/// is open, Degraded never opens or closes one.
/// </summary>
internal static class IncidentRule
{
    internal enum Outcome
    {
        None,
        Open,
        Close,
    }

    internal static Outcome Evaluate(HealthStatus status, bool hasOpenIncident) => (status, hasOpenIncident) switch
    {
        (HealthStatus.Unhealthy, false) => Outcome.Open,
        (HealthStatus.Healthy, true) => Outcome.Close,
        _ => Outcome.None,
    };
}
