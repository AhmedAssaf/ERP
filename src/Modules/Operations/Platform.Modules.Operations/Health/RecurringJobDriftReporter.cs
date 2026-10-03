using Microsoft.Extensions.DependencyInjection;
using Platform.Modules.Operations.Contracts;
using Platform.Shared.Jobs;

namespace Platform.Modules.Operations.Health;

/// <summary>
/// W-42: records each pass of the worker's recurring job guard as a result of component <see cref="HealthComponents.Jobs"/>.
/// A pass that restored an entry or removed an unknown one is Unhealthy and opens an F-60 incident (its alert email goes out with the health-check
/// job's next run, which the guard has just restored if it was the missing one); the next intact pass is Healthy and closes
/// it, with the recovery email. The message names the recurring job ids only, never a job's arguments (N-10).
/// </summary>
internal sealed class RecurringJobDriftReporter(IServiceScopeFactory scopes) : IRecurringJobDriftReporter
{
    public async Task ReportAsync(
        IReadOnlyList<string> restoredJobIds, IReadOnlyList<string> removedJobIds, DateTimeOffset checkedAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(restoredJobIds);
        ArgumentNullException.ThrowIfNull(removedJobIds);
        var parts = new List<string>();
        if (restoredJobIds.Count > 0)
        {
            parts.Add($"Recurring jobs were missing or altered and have been restored: {string.Join(", ", restoredJobIds)}.");
        }

        if (removedJobIds.Count > 0)
        {
            parts.Add($"Recurring jobs the worker does not define have been removed: {string.Join(", ", removedJobIds)}.");
        }

        var result = parts.Count == 0
            ? new HealthResult(HealthComponents.Jobs, HealthStatus.Healthy, 0, checkedAt, null)
            : new HealthResult(HealthComponents.Jobs, HealthStatus.Unhealthy, 0, checkedAt, string.Join(" ", parts));

        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IHealthLog>().RecordAsync([result], cancellationToken);
    }
}
