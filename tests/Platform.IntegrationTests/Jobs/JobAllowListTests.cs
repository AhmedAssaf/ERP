using System.Collections.Concurrent;
using System.Diagnostics;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Shared;
using Platform.Shared.Jobs;
using Platform.Shared.Tenancy;

namespace Platform.IntegrationTests.Jobs;

/// <summary>
/// W-36 fix round 1 (review 2026-10-03): a Hangfire job row is untrusted input, because the application role (the web host,
/// or whatever SQL runs as it) can write Hangfire's tables and the worker runs what a row names as erp_worker. The worker
/// runs only platform jobs: a row naming a framework method (<c>Process.Start</c>, <c>Environment.SetEnvironmentVariable</c>),
/// a Hangfire method, an unmarked class, a disallowed parameter type, or a tenant for a job that runs without one fails
/// without being invoked and is not retried; a <c>$type</c> inside an argument is never instantiated; a platform job runs.
/// Every row here is written through the application role, as the web host's client writes it.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class JobAllowListTests(DatabaseFixture db) : IAsyncDisposable
{
    private readonly ServiceProvider _web = WebProvider(db.AppConnectionString);
    private IServiceScope? _request;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_row_naming_process_start_fails_without_being_invoked_and_is_not_retried()
    {
        await using var worker = await JobServerHost.StartAsync(db.WorkerConnectionString, cancellationToken: Ct);
        var program = $"w36-refused-probe-{Guid.NewGuid():N}";

        var jobId = WebClient().Create(Job.FromExpression(() => Process.Start(program)), new EnqueuedState());

        var failure = await WaitForRefusalAsync(worker, jobId);
        failure.ShouldNotContain("Win32Exception", Case.Sensitive, "Process.Start was never called");
    }

    [Fact]
    public async Task A_row_naming_a_framework_method_that_would_leave_a_trace_never_runs()
    {
        await using var worker = await JobServerHost.StartAsync(db.WorkerConnectionString, cancellationToken: Ct);
        var variable = $"W36_PROBE_{Guid.NewGuid():N}";

        var jobId = WebClient().Create(Job.FromExpression(() => Environment.SetEnvironmentVariable(variable, "invoked")), new EnqueuedState());

        await WaitForRefusalAsync(worker, jobId);
        Environment.GetEnvironmentVariable(variable).ShouldBeNull();
    }

    [Fact]
    public async Task A_row_naming_a_hangfire_method_or_an_unmarked_platform_class_is_refused()
    {
        await using var worker = await JobServerHost.StartAsync(db.WorkerConnectionString, cancellationToken: Ct);
        var client = WebClient();
        var marker = Guid.NewGuid().ToString("N");

        var hangfire = client.Create(Job.FromExpression(() => TypeHelper.DefaultTypeResolver(marker)), new EnqueuedState());
        var unmarked = client.Create(Job.FromExpression(() => UnmarkedProbe.Run(marker)), new EnqueuedState());

        (await WaitForRefusalAsync(worker, hangfire)).ShouldContain("is not a platform job");
        (await WaitForRefusalAsync(worker, unmarked)).ShouldContain("is not a platform job");
        UnmarkedProbe.Seen.ShouldNotContain(marker);
    }

    [Fact]
    public async Task A_job_that_runs_without_a_tenant_is_refused_when_its_row_carries_one()
    {
        await using var worker = await JobServerHost.StartAsync(db.WorkerConnectionString, cancellationToken: Ct);
        var marker = Guid.NewGuid().ToString("N");

        string jobId;
        await using (var scope = _web.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantAccessor>().Set(TestTenants.Acme);
            jobId = scope.ServiceProvider.GetRequiredService<IBackgroundJobClient>().Enqueue(() => AllowedProbe.Run(marker));
        }

        (await WaitForRefusalAsync(worker, jobId)).ShouldContain("runs without a tenant");
        AllowedProbe.Seen.ShouldNotContain(marker);
    }

    [Fact]
    public async Task A_refused_row_of_a_job_retried_without_delay_fails_once_and_is_not_retried()
    {
        // AutomaticRetry with a zero delay elects Enqueued, not Scheduled (review of fix round 1).
        await using var worker = await JobServerHost.StartAsync(db.WorkerConnectionString, cancellationToken: Ct);
        var marker = Guid.NewGuid().ToString("N");

        string jobId;
        await using (var scope = _web.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantAccessor>().Set(TestTenants.Acme);
            jobId = scope.ServiceProvider.GetRequiredService<IBackgroundJobClient>().Enqueue(() => ZeroDelayProbe.Run(marker));
        }

        (await WaitForRefusalAsync(worker, jobId)).ShouldContain("runs without a tenant");
        // The retry AutomaticRetry elected shows in the history only as a traversed candidate; the job was processed once
        // (checked above) and ended in the allow-list's own final failure.
        worker.Storage.GetMonitoringApi().JobDetails(jobId).History[0].Reason.ShouldContain("not retried");
        ZeroDelayProbe.Seen.ShouldNotContain(marker);
    }

    [Fact]
    public async Task The_activator_refuses_a_tenant_written_after_the_filter_for_a_job_that_runs_without_one()
    {
        // The row passes the allow-list without a tenant; the application role then writes a consistent tenant pair, as it
        // could between the filter's read and the activation. The activator checks the values it uses (W-42: through the
        // job gate, which also finds the signature no longer matching).
        var jobId = WebClient().Create(Job.FromExpression<InstanceProbe>(p => p.Run()), new ScheduledState(TimeSpan.FromHours(1)));
        await using var worker = await JobServerHost.StartAsync(db.WorkerConnectionString, cancellationToken: Ct);
        using var connection = worker.Storage.GetConnection();
        var job = connection.GetJobData(jobId).Job;
        var gate = worker.Services.GetRequiredService<JobGate>();
        gate.Refusal(connection, new BackgroundJob(jobId, job, DateTime.UtcNow)).ShouldBeNull("the filter would let it through");

        using (var app = _web.GetRequiredService<JobStorage>().GetConnection())
        {
            app.SetJobParameter(jobId, TenantJobFilter.TenantIdParameter, SerializationHelper.Serialize(TestTenants.Acme.TenantId));
            app.SetJobParameter(jobId, TenantJobFilter.TenantParameter, SerializationHelper.Serialize(TestTenants.Acme));
        }

        var activator = new TenantJobActivator(worker.Services.GetRequiredService<IServiceScopeFactory>(), gate);
        var context = new JobActivatorContext(connection, new BackgroundJob(jobId, job, DateTime.UtcNow), new JobCancellationToken(false));

        Should.Throw<JobRefusedException>(() => activator.BeginScope(context)).Message.ShouldContain("runs without a tenant");
    }

    [Fact]
    public async Task A_platform_job_enqueued_by_the_web_host_still_runs()
    {
        await using var worker = await JobServerHost.StartAsync(db.WorkerConnectionString, cancellationToken: Ct);
        var marker = Guid.NewGuid().ToString("N");

        var jobId = WebClient().Enqueue(() => AllowedProbe.Run(marker));

        await worker.WaitForSuccessAsync(jobId, Ct);
        AllowedProbe.Seen.ShouldContain(marker);
    }

    [Fact]
    public async Task A_row_whose_parameter_type_is_not_allowed_is_refused()
    {
        // Queued before any worker runs; the application role then rewrites the row: same class and method name, a
        // parameter type outside the allow-list.
        var jobId = WebClient().Create(Job.FromExpression(() => AllowedProbe.Run("benign")), new EnqueuedState());
        await ForgeAsync(jobId, """["System.IO.FileInfo, System.Private.CoreLib"]""", """["\"w36\""]""");
        await using var worker = await JobServerHost.StartAsync(db.WorkerConnectionString, cancellationToken: Ct);

        var failure = await WaitForRefusalAsync(worker, jobId);
        (failure.Contains("JobLoadException", StringComparison.Ordinal) || failure.Contains("could not be loaded", StringComparison.Ordinal))
            .ShouldBeTrue(failure);
    }

    [Fact]
    public async Task A_type_name_inside_an_argument_is_never_instantiated()
    {
        // W-42: the forged row is signed again with the run's key, standing for a holder of the key (a compromised web
        // process): the argument rule holds behind the signature, not only because of it.
        await using var worker = await JobServerHost.StartAsync(db.WorkerConnectionString, cancellationToken: Ct);
        var client = WebClient();
        var marker = Guid.NewGuid().ToString("N");
        var jobId = client.Create(Job.FromExpression(() => PayloadProbe.Run(new ProbePayload { Marker = marker })), new ScheduledState(TimeSpan.FromHours(1)));

        // A $type naming a framework type inside an object-typed member of an allowed argument type.
        var payload = JsonConvert.SerializeObject(new Dictionary<string, object>
        {
            ["Marker"] = marker,
            ["Value"] = new Dictionary<string, string> { ["$type"] = "System.IO.FileInfo, System.Private.CoreLib", ["fileName"] = "w36" },
        });
        var parameterTypes = JsonConvert.SerializeObject(new[] { typeof(ProbePayload).AssemblyQualifiedName });
        await ForgeAsync(jobId, parameterTypes, JsonConvert.SerializeObject(new[] { payload }));
        using (var app = _web.GetRequiredService<JobStorage>().GetConnection())
        {
            var forged = app.GetJobData(jobId).Job;
            var token = new JobAuthenticity(TestSecrets.JobKeys, TimeProvider.System).Sign(forged, JobBinding.Read(app, jobId).Binding);
            app.SetJobParameter(jobId, JobAuthenticity.ParameterName, SerializationHelper.Serialize(token));
        }

        client.ChangeState(jobId, new EnqueuedState(), ScheduledState.StateName).ShouldBeTrue();

        await worker.WaitForSuccessAsync(jobId, Ct);
        PayloadProbe.Seen[marker].ShouldNotBe(typeof(FileInfo).FullName);
        PayloadProbe.Seen[marker].ShouldStartWith("Newtonsoft.Json.Linq.");
    }

    public async ValueTask DisposeAsync()
    {
        _request?.Dispose();
        await _web.DisposeAsync();
    }

    /// <summary>The web host's client in one request without a tenant.</summary>
    private IBackgroundJobClient WebClient()
    {
        _request ??= _web.CreateScope();
        return _request.ServiceProvider.GetRequiredService<IBackgroundJobClient>();
    }

    /// <summary>The web host's registration: Hangfire's storage and client only, as erp_app.</summary>
    private static ServiceProvider WebProvider(string appConnectionString)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPlatformShared();
        services.AddJobClient(appConnectionString, TestSecrets.JobKeys);
        return services.BuildServiceProvider();
    }

    private async Task ForgeAsync(string jobId, string parameterTypes, string arguments)
    {
        await using var app = new NpgsqlConnection(db.AppConnectionString);
        await app.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            update hangfire.job
            set invocationdata = jsonb_set(jsonb_set(invocationdata, '{ParameterTypes}', to_jsonb(@types::text)), '{Arguments}', to_jsonb(@arguments::text)),
                arguments = @arguments::jsonb
            where id = @id
            """, app);
        command.Parameters.AddWithValue("types", parameterTypes);
        command.Parameters.AddWithValue("arguments", arguments);
        command.Parameters.AddWithValue("id", long.Parse(jobId, System.Globalization.CultureInfo.InvariantCulture));
        (await command.ExecuteNonQueryAsync(Ct)).ShouldBe(1);
    }

    /// <summary>
    /// Waits for the job to fail, then checks it stays failed (no retry is scheduled) and returns the failure's exception
    /// type and message.
    /// </summary>
    private static async Task<string> WaitForRefusalAsync(JobServerHost worker, string jobId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            using (var connection = worker.Storage.GetConnection())
            {
                var state = connection.GetStateData(jobId);
                if (state?.Name == FailedState.StateName)
                {
                    break;
                }

                state?.Name.ShouldNotBe(SucceededState.StateName, $"job {jobId} must not succeed");
                state?.Name.ShouldNotBe(ScheduledState.StateName, $"job {jobId} must not be retried");
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Job {jobId} did not fail within 60 seconds.");
            }

            await Task.Delay(100, Ct);
        }

        await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        var details = worker.Storage.GetMonitoringApi().JobDetails(jobId);
        details.History[0].StateName.ShouldBe(FailedState.StateName, "still failed: not retried");
        details.History.Count(h => h.StateName == ProcessingState.StateName).ShouldBeLessThanOrEqualTo(1, "processed once at most");
        var failure = details.History[0].Data;
        failure.TryGetValue("ExceptionType", out var type);
        failure.TryGetValue("ExceptionMessage", out var message);
        return $"{type}: {message}";
    }
}

/// <summary>A platform class without <see cref="PlatformJobAttribute"/>: never run by the worker.</summary>
public static class UnmarkedProbe
{
    public static ConcurrentBag<string> Seen { get; } = [];

    public static void Run(string marker) => Seen.Add(marker);
}

/// <summary>A platform job that runs without a tenant.</summary>
[PlatformJob]
public static class AllowedProbe
{
    public static ConcurrentBag<string> Seen { get; } = [];

    public static void Run(string marker) => Seen.Add(marker);
}

/// <summary>A platform job without a tenant whose retries have no delay, so AutomaticRetry elects Enqueued.</summary>
[PlatformJob]
[AutomaticRetry(Attempts = 3, DelaysInSeconds = [0])]
public static class ZeroDelayProbe
{
    public static ConcurrentBag<string> Seen { get; } = [];

    public static void Run(string marker) => Seen.Add(marker);
}

/// <summary>A platform job without a tenant run through the activator (an instance method).</summary>
[PlatformJob]
public sealed class InstanceProbe
{
    public static ConcurrentBag<string> Seen { get; } = [];

    public void Run() => Seen.Add(GetHashCode().ToString(System.Globalization.CultureInfo.InvariantCulture));
}

/// <summary>An argument type with an object-typed member, the shape a <c>$type</c> attack needs.</summary>
public sealed class ProbePayload
{
    public string Marker { get; set; } = string.Empty;

    public object? Value { get; set; }
}

/// <summary>Records the runtime type the worker's deserializer gave the payload's object-typed member.</summary>
[PlatformJob]
public static class PayloadProbe
{
    public static ConcurrentDictionary<string, string> Seen { get; } = new();

    public static void Run(ProbePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        Seen[payload.Marker] = payload.Value?.GetType().FullName ?? "null";
    }
}
