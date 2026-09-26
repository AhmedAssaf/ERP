using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Platform.Modules.Operations.Health;

/// <summary>Pairs a check with the component name the board and <see cref="Contracts.HealthResult"/> use.</summary>
internal sealed record NamedHealthCheck(string Component, IHealthCheck Check);
