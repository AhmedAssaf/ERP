using Hangfire;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Platform.Modules.Operations.Health;

namespace Platform.UnitTests.Operations;

/// <summary>
/// Review fix: <see cref="WorkerHeartbeatHealthCheck"/> must not block past its caller's timeout even though
/// <c>JobStorage.GetMonitoringApi().Servers()</c> is a synchronous call with no cancellation support of its own.
/// </summary>
public sealed class WorkerHeartbeatHealthCheckTests
{
    [Fact]
    public async Task A_stuck_monitoring_api_call_returns_unhealthy_within_the_callers_timeout()
    {
        var check = new WorkerHeartbeatHealthCheck(new SlowJobStorage(TimeSpan.FromSeconds(30)), TimeProvider.System);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var started = TimeProvider.System.GetTimestamp();
        var result = await check.CheckHealthAsync(new HealthCheckContext(), timeout.Token);
        var elapsed = TimeProvider.System.GetElapsedTime(started);

        result.Status.ShouldBe(HealthStatus.Unhealthy);
        result.Description.ShouldNotBeNull();
        result.Description.ShouldContain("Worker");
        elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(15));
    }

    private sealed class SlowJobStorage(TimeSpan delay) : JobStorage
    {
        public override IMonitoringApi GetMonitoringApi() => new SlowMonitoringApi(delay);

        public override IStorageConnection GetConnection() => throw new NotSupportedException();
    }

    /// <summary>Only <see cref="Servers"/> is exercised by the check under test; every other member is unused here.</summary>
    private sealed class SlowMonitoringApi(TimeSpan delay) : IMonitoringApi
    {
        public IList<ServerDto> Servers()
        {
            Thread.Sleep(delay);
            return [];
        }

        public IList<QueueWithTopEnqueuedJobsDto> Queues() => throw new NotSupportedException();

        public JobDetailsDto? JobDetails(string jobId) => throw new NotSupportedException();

        public StatisticsDto GetStatistics() => throw new NotSupportedException();

        public JobList<EnqueuedJobDto> EnqueuedJobs(string queue, int from, int perPage) => throw new NotSupportedException();

        public JobList<FetchedJobDto> FetchedJobs(string queue, int from, int perPage) => throw new NotSupportedException();

        public JobList<ProcessingJobDto> ProcessingJobs(int from, int count) => throw new NotSupportedException();

        public JobList<ScheduledJobDto> ScheduledJobs(int from, int count) => throw new NotSupportedException();

        public JobList<SucceededJobDto> SucceededJobs(int from, int count) => throw new NotSupportedException();

        public JobList<FailedJobDto> FailedJobs(int from, int count) => throw new NotSupportedException();

        public JobList<DeletedJobDto> DeletedJobs(int from, int count) => throw new NotSupportedException();

        public long ScheduledCount() => throw new NotSupportedException();

        public long EnqueuedCount(string queue) => throw new NotSupportedException();

        public long FetchedCount(string queue) => throw new NotSupportedException();

        public long FailedCount() => throw new NotSupportedException();

        public long ProcessingCount() => throw new NotSupportedException();

        public long SucceededListCount() => throw new NotSupportedException();

        public long DeletedListCount() => throw new NotSupportedException();

        public IDictionary<DateTime, long> SucceededByDatesCount() => throw new NotSupportedException();

        public IDictionary<DateTime, long> FailedByDatesCount() => throw new NotSupportedException();

        public IDictionary<DateTime, long> HourlySucceededJobs() => throw new NotSupportedException();

        public IDictionary<DateTime, long> HourlyFailedJobs() => throw new NotSupportedException();
    }
}
