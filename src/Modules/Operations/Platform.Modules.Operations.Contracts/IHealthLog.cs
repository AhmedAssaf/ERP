namespace Platform.Modules.Operations.Contracts;

/// <summary>
/// Platform-level health results and incidents (F-51, F-60 data). No tenant_id: these are platform infrastructure
/// facts, never a tenant event.
/// Rules: a result opens an incident on the first Unhealthy result for a component with no open incident; closes the
/// open incident on the first Healthy result while one is open; Degraded never opens or closes one.
/// </summary>
public interface IHealthLog
{
    /// <summary>Records one health-check cycle's results and returns the incident transitions it caused.</summary>
    Task<IReadOnlyList<IncidentTransition>> RecordAsync(IReadOnlyList<HealthResult> results, CancellationToken cancellationToken = default);

    /// <summary>The latest recorded result per component.</summary>
    Task<IReadOnlyList<HealthResult>> LatestAsync(CancellationToken cancellationToken = default);

    /// <summary>Incidents opened at or after <paramref name="since"/>, most recent first.</summary>
    Task<IReadOnlyList<Incident>> IncidentsAsync(DateTimeOffset since, CancellationToken cancellationToken = default);
}
