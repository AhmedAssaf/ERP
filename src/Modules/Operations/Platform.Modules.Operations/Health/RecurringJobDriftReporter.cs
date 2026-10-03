using Microsoft.Extensions.DependencyInjection;
using Platform.Modules.Operations.Contracts;
using Platform.Shared.Jobs;

namespace Platform.Modules.Operations.Health;

/// <summary>
/// W-42: records each pass of the worker's recurring job guard as a result of component <see cref="HealthComponents.Jobs"/>.
/// A pass that restored an entry is Unhealthy and opens an F-60 incident (its alert email goes out with the health-check
/// job's next run, which the guard has just restored if it was the missing one); the next intact pass is Healthy and closes
/// it, with the recovery email. The message names the recurring job ids only, never a job's arguments (N-10).
/// </summary>
internal sealed class RecurringJobDriftReporter(IServiceScopeFactory scopes) : IRecurringJobDriftReporter
{
    public async Task ReportAsync(IReadOnlyList<string> restoredJobIds, DateTimeOffset checkedAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(restoredJobIds);
        var result = restoredJobIds.Count == 0
            ? new HealthResult(HealthComponents.Jobs, HealthStatus.Healthy, 0, checkedAt, null)
            : new HealthResult(
                HealthComponents.Jobs,
                HealthStatus.Unhealthy,
                0,
                checkedAt,
                $"Recurring jobs were missing or altered and have been restored: {string.Join(", ", restoredJobIds)}.");

        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IHealthLog>().RecordAsync([result], cancellationToken);
    }
}
