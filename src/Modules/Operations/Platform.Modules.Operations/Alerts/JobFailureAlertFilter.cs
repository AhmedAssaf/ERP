using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.DependencyInjection;

namespace Platform.Modules.Operations.Alerts;

/// <summary>
/// F-60 "a job fails three times, one alert per job id": an <see cref="IElectStateFilter"/>, not an
/// <see cref="IApplyStateFilter"/>, because Hangfire's built-in <c>AutomaticRetryAttribute</c> (default order 20)
/// rewrites a failed attempt's candidate state from <see cref="FailedState"/> to a retry state during election
/// itself; by the time states are *applied*, only the final, retries-exhausted failure would still read as Failed.
/// This filter must therefore run before that rewrite. Hangfire only honours a job filter's <see cref="Order"/>
/// when the instance also implements <see cref="IJobFilter"/> (<c>Hangfire.Common.JobFilter</c>'s constructor checks
/// for it explicitly); <see cref="IElectStateFilter"/> alone does not extend it, so this filter implements both.
/// It keeps its own counter as a job parameter (never Hangfire's own "RetryCount", which is
/// <c>AutomaticRetryAttribute</c>'s own implementation detail, not a documented contract) and resets on success.
/// Registered per job server (<see cref="Platform.Shared.Jobs.JobsModule"/>), never via Hangfire's static
/// <c>GlobalJobFilters</c>, so it cannot leak into another Hangfire server sharing the same test process.
/// </summary>
internal sealed class JobFailureAlertFilter(IServiceScopeFactory scopeFactory, TimeProvider clock, AlertSettings settings)
    : IElectStateFilter, IJobFilter
{
    internal const string FailureCountParameter = "waslabid-consecutive-failures";
    internal const string AlertedParameter = "waslabid-failure-alerted";
    private const int AlertAtFailureCount = 3;

    // Lower than AutomaticRetryAttribute's default Order (20): this filter must see the untouched Failed candidate
    // before that filter rewrites it into a retry.
    public int Order => -100;

    // Only one instance of this filter is ever registered (a DI singleton); irrelevant either way.
    public bool AllowMultiple => false;

    public void OnStateElection(ElectStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.CandidateState is SucceededState)
        {
            // A later success starts a fresh streak; three failures after a success alert again.
            context.SetJobParameter(FailureCountParameter, 0);
            return;
        }

        if (context.CandidateState is not FailedState)
        {
            return;
        }

        var count = context.GetJobParameter<int>(FailureCountParameter) + 1;
        context.SetJobParameter(FailureCountParameter, count);

        if (count != AlertAtFailureCount || context.GetJobParameter<bool>(AlertedParameter))
        {
            return;
        }

        context.SetJobParameter(AlertedParameter, true);
        Alert(context.BackgroundJob.Id, context.BackgroundJob.Job.ToString());
    }

    private void Alert(string jobId, string jobDisplayName)
    {
        using var scope = scopeFactory.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<IAlertSender>();
        var message = AlertMessages.JobFailedThreeTimes(jobId, jobDisplayName, clock.GetUtcNow(), settings.BoardUrl);

        // IElectStateFilter is synchronous (Hangfire OSS has no async filter pipeline); alerts are rare enough that
        // blocking the worker thread for one SMTP round-trip is acceptable.
        sender.SendAsync(message, CancellationToken.None).GetAwaiter().GetResult();
    }
}
