using System.Collections.Concurrent;
using Hangfire;
using Hangfire.Common;
using Hangfire.PostgreSql;
using Hangfire.PostgreSql.Factories;
using Hangfire.States;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Shared;
using Platform.Shared.Jobs;
using Platform.Shared.Tenancy;

namespace Platform.IntegrationTests.Jobs;

/// <summary>
/// W-42 (ADR-0012 addendum): the worker runs a job only when its row carries a valid signature from a host holding
/// <c>Jobs:SigningKey</c>, over the job and the tenant it runs as, and only once per signature. A row the application role
/// writes itself (unsigned, signed under another key, carrying another job's signature, or with its tenant changed after
/// signing) fails without being invoked and is not retried; a signed row copied into a new job runs as one job only; the
/// web host's client, a retry, a console re-run and the worker's recurring scheduler still run.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class JobAuthenticityTests(DatabaseFixture db) : IAsyncDisposable
{
    private readonly ServiceProvider _web = WebProvider(db.AppConnectionString, TestSecrets.JobKeys);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_job_row_written_without_the_platform_client_never_runs()
    {
        await using var worker = await JobServerHost.StartAsync(db.WorkerConnectionString, cancellationToken: Ct);
        var marker = Marker();

        // A well-formed Hangfire row for an allow-listed job, as SQL running as erp_app can write one: no signature.
        var jobId = new BackgroundJobClient(AppStorage()).Enqueue(() => AuthenticityProbe.Run(marker));

        (await WaitForRefusalAsync(worker, jobId)).ShouldContain("carries no signature");
        AuthenticityProbe.Seen.ShouldNotContainKey(marker);
    }

    [Fact]
    public async Task A_job_signed_under_another_key_never_runs()
    {
        await using var worker = await JobServerHost.StartAsync(db.WorkerConnectionString, cancellationToken: Ct);
        await using var other = WebProvider(db.AppConnectionString, JobSigningKeys.FromBytes(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        var marker = Marker();

        var jobId = Client(other).Enqueue(() => AuthenticityProbe.Run(marker));

        (await WaitForRefusalAsync(worker, jobId)).ShouldContain("does not match");
        AuthenticityProbe.Seen.ShouldNotContainKey(marker);
    }

    [Fact]
    public async Task A_signature_copied_from_another_job_does_not_admit_a_different_job()
    {
        var allowed = Marker();
        var forged = Marker();
        var signedId = Client(_web).Create(Job.FromExpression(() => AuthenticityProbe.Run(allowed)), new ScheduledState(TimeSpan.FromHours(1)));
        var forgedId = Client(_web).Create(Job.FromExpression(() => AuthenticityProbe.Run(forged)), new ScheduledState(TimeSpan.FromHours(1)));
        await CopyParameterAsync(signedId, forgedId, JobAuthenticity.ParameterName);
        await using var worker = await JobServerHost.StartAsync(db.WorkerConnectionString, cancellationToken: Ct);

        Client(_web).ChangeState(forgedId, new EnqueuedState(), ScheduledState.StateName).ShouldBeTrue();
        Client(_web).ChangeState(signedId, new EnqueuedState(), ScheduledState.StateName).ShouldBeTrue();

        (await WaitForRefusalAsync(worker, forgedId)).ShouldContain("does not match");
        await worker.WaitForSuccessAsync(signedId, Ct);
        AuthenticityProbe.Seen.ShouldNotContainKey(forged);
        AuthenticityProbe.Seen[allowed].ShouldBe(1);
    }

    [Fact]
    public async Task A_tenant_changed_after_signing_never_runs_for_either_tenant()
    {
        var marker = Marker();
        string jobId;
        await using (var scope = _web.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantAccessor>().Set(TestTenants.Acme);
            jobId = scope.ServiceProvider.GetRequiredService<IBackgroundJobClient>()
                .Create(Job.FromExpression<TenantAuthenticityProbe>(p => p.Run(marker)), new ScheduledState(TimeSpan.FromHours(1)));
        }

        // The application role moves the job to another tenant, consistently, as it could with plain SQL.
        using (var app = AppStorage().GetConnection())
        {
            app.SetJobParameter(jobId, TenantJobFilter.TenantIdParameter, SerializationHelper.Serialize(TestTenants.Beta.TenantId));
            app.SetJobParameter(jobId, TenantJobFilter.TenantParameter, SerializationHelper.Serialize(TestTenants.Beta));
        }

        await using var worker = await JobServerHost.StartAsync(db.WorkerConnectionString, cancellationToken: Ct);
        Client(_web).ChangeState(jobId, new EnqueuedState(), ScheduledState.StateName).ShouldBeTrue();

        (await WaitForRefusalAsync(worker, jobId)).ShouldContain("does not match");
        TenantAuthenticityProbe.Seen.ShouldNotContainKey(marker);
    }

    [Fact]
    public async Task A_tenant_scoped_job_signed_by_the_web_host_runs_as_its_tenant()
    {
        await using var worker = await JobServerHost.StartAsync(db.WorkerConnectionString, cancellationToken: Ct);
        var marker = Marker();
        string jobId;
        await using (var scope = _web.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantAccessor>().Set(TestTenants.Acme);
            jobId = scope.ServiceProvider.GetRequiredService<IBackgroundJobClient>().Enqueue<TenantAuthenticityProbe>(p => p.Run(marker));
        }

        await worker.WaitForSuccessAsync(jobId, Ct);
        TenantAuthenticityProbe.Seen[marker].ShouldBe(TestTenants.Acme.TenantId);
    }

    [Fact]
    public async Task A_signed_row_copied_into_a_new_job_runs_once()
    {
        await using var worker = await JobServerHost.StartAsync(db.WorkerConnectionString, cancellationToken: Ct);
        var marker = Marker();
        var original = Client(_web).Enqueue(() => AuthenticityProbe.Run(marker));
        await worker.WaitForSuccessAsync(original, Ct);

        // The same job, written again by the application role with the original's parameters (its signature included).
        var copy = new BackgroundJobClient(AppStorage()).Create(Job.FromExpression(() => AuthenticityProbe.Run(marker)), new ScheduledState(TimeSpan.FromHours(1)));
        await CopyParameterAsync(original, copy, JobAuthenticity.ParameterName);
        Client(_web).ChangeState(copy, new EnqueuedState(), ScheduledState.StateName).ShouldBeTrue();

        (await WaitForRefusalAsync(worker, copy)).ShouldContain("already ran as another job");
        AuthenticityProbe.Seen[marker].ShouldBe(1);
    }

    [Fact]
    public async Task A_retry_and_a_console_re_run_of_a_signed_job_run_again()
    {
        await using var worker = await JobServerHost.StartAsync(db.WorkerConnectionString, cancellationToken: Ct);
        var marker = Marker();
        RetryProbe.FailuresLeft[marker] = 1;

        var jobId = Client(_web).Enqueue(() => RetryProbe.Run(marker));

        // Fails once, is retried at once under the same job id, and succeeds.
        await worker.WaitForSuccessAsync(jobId, Ct);
        RetryProbe.Runs[marker].ShouldBe(2);

        // The console's re-run (PlatformJobs) moves a failed job back to the queue as erp_app; it keeps its id and signature.
        var failing = Marker();
        RetryProbe.FailuresLeft[failing] = 1;
        var failedId = Client(_web).Enqueue(() => NoRetryProbe.Run(failing));
        await WaitForStateAsync(worker, failedId, FailedState.StateName);
        new BackgroundJobClient(AppStorage()).ChangeState(failedId, new EnqueuedState(), FailedState.StateName).ShouldBeTrue();

        await worker.WaitForSuccessAsync(failedId, Ct);
        RetryProbe.Runs[failing].ShouldBe(2);
    }

    [Fact]
    public async Task A_succeeded_job_moved_back_to_the_queue_is_refused_without_running_again()
    {
        await using var worker = await JobServerHost.StartAsync(db.WorkerConnectionString, cancellationToken: Ct);
        var marker = Marker();
        var jobId = Client(_web).Enqueue(() => AuthenticityProbe.Run(marker));
        await worker.WaitForSuccessAsync(jobId, Ct);

        // The application role re-queues the succeeded job: same id, same signature.
        new BackgroundJobClient(AppStorage()).ChangeState(jobId, new EnqueuedState(), SucceededState.StateName).ShouldBeTrue();

        // Processed twice: the run that succeeded, then the refusal.
        (await WaitForRefusalAsync(worker, jobId, maxProcessing: 2)).ShouldContain("already succeeded");
        AuthenticityProbe.Seen[marker].ShouldBe(1);
    }

    [Fact]
    public async Task A_culture_changed_after_signing_never_runs()
    {
        var marker = Marker();
        var jobId = Client(_web).Create(Job.FromExpression(() => AuthenticityProbe.Run(marker)), new ScheduledState(TimeSpan.FromHours(1)));
        using (var app = AppStorage().GetConnection())
        {
            app.SetJobParameter(jobId, JobBinding.CultureParameter, SerializationHelper.Serialize("ar-SA"));
        }

        await using var worker = await JobServerHost.StartAsync(db.WorkerConnectionString, cancellationToken: Ct);
        Client(_web).ChangeState(jobId, new EnqueuedState(), ScheduledState.StateName).ShouldBeTrue();

        (await WaitForRefusalAsync(worker, jobId)).ShouldContain("does not match");
        AuthenticityProbe.Seen.ShouldNotContainKey(marker);
    }

    [Fact]
    public async Task A_recurring_job_fired_by_the_worker_scheduler_is_signed_and_runs()
    {
        await using var worker = await JobServerHost.StartAsync(
            db.WorkerConnectionString,
            configureJobServer: options => options.SchedulePollingInterval = TimeSpan.FromMilliseconds(250),
            cancellationToken: Ct);
        var marker = Marker();
        var recurringId = $"w42-recurring-{marker}";
        var catalog = worker.Services.GetRequiredService<RecurringJobCatalog>();
        try
        {
            catalog.AddOrUpdate<RecurringAuthenticityProbe>(recurringId, p => p.RunAsync(marker), Cron.Yearly());
            // Due now (as the worker's role): created long ago with a yearly occurrence missed since, so the scheduler fires it
            // once on its next poll, through the job server's own factory.
            await ExecuteAsync(db.WorkerConnectionString, """
                update hangfire.hash set value = '2000-01-01T00:00:00.0000000Z' where key = @key and field in ('NextExecution', 'CreatedAt');
                insert into hangfire.set (key, value, score) values ('recurring-jobs', @id, 946684800)
                    on conflict (key, value) do update set score = excluded.score;
                """, ("key", $"recurring-job:{recurringId}"), ("id", recurringId));

            var jobId = await WaitForRecurringRunAsync(worker, recurringId);
            await worker.WaitForSuccessAsync(jobId, Ct);
            worker.Storage.GetParameter(jobId, JobAuthenticity.ParameterName).ShouldNotBeNullOrEmpty();
            RecurringAuthenticityProbe.Seen.ShouldContain(marker);
        }
        finally
        {
            worker.RecurringJobs.RemoveIfExists(recurringId);
        }
    }

    public async ValueTask DisposeAsync() => await _web.DisposeAsync();

    private static string Marker() => Guid.NewGuid().ToString("N");

    private static IBackgroundJobClient Client(IServiceProvider web) => web.CreateScope().ServiceProvider.GetRequiredService<IBackgroundJobClient>();

    /// <summary>The web host's registration: Hangfire's storage and the signing client only, as erp_app.</summary>
    private static ServiceProvider WebProvider(string appConnectionString, JobSigningKeys keys)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPlatformShared();
        services.AddJobClient(appConnectionString, keys);
        return services.BuildServiceProvider();
    }

    /// <summary>Hangfire's storage as erp_app without any platform filter: a row as plain SQL could write it.</summary>
    private PostgreSqlStorage AppStorage() =>
        new(new NpgsqlConnectionFactory(db.AppConnectionString, new PostgreSqlStorageOptions()),
            new PostgreSqlStorageOptions { SchemaName = JobsModule.SchemaName, PrepareSchemaIfNecessary = false });

    private async Task CopyParameterAsync(string fromJobId, string toJobId, string name)
    {
        using var app = AppStorage().GetConnection();
        app.SetJobParameter(toJobId, name, app.GetJobParameter(fromJobId, name).ShouldNotBeNull());
        await Task.CompletedTask;
    }

    private static async Task ExecuteAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
#pragma warning disable CA2100 // The tests' own statements.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<string> WaitForRecurringRunAsync(JobServerHost worker, string recurringId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            using (var connection = worker.Storage.GetConnection())
            {
                if (connection.GetAllEntriesFromHash($"recurring-job:{recurringId}")?.GetValueOrDefault("LastJobId") is { Length: > 0 } jobId)
                {
                    return jobId;
                }
            }

            await Task.Delay(100, Ct);
        }

        throw new TimeoutException($"Recurring job {recurringId} did not fire within 60 seconds.");
    }

    private static async Task WaitForStateAsync(JobServerHost worker, string jobId, string state)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            using (var connection = worker.Storage.GetConnection())
            {
                if (connection.GetStateData(jobId)?.Name == state)
                {
                    return;
                }
            }

            await Task.Delay(100, Ct);
        }

        throw new TimeoutException($"Job {jobId} did not reach {state} within 60 seconds.");
    }

    /// <summary>Waits for the job to fail, checks it is not retried, and returns its exception type and message.</summary>
    private static async Task<string> WaitForRefusalAsync(JobServerHost worker, string jobId, int maxProcessing = 1)
    {
        await WaitForStateAsync(worker, jobId, FailedState.StateName);
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        var details = worker.Storage.GetMonitoringApi().JobDetails(jobId);
        details.History[0].StateName.ShouldBe(FailedState.StateName, "still failed: not retried");
        details.History.Count(h => h.StateName == ProcessingState.StateName).ShouldBeLessThanOrEqualTo(maxProcessing, "not retried after the refusal");
        var failure = details.History[0].Data;
        failure.TryGetValue("ExceptionType", out var type);
        failure.TryGetValue("ExceptionMessage", out var message);
        type.ShouldBe(typeof(JobRefusedException).FullName);
        return $"{type}: {message}";
    }
}

/// <summary>A platform job without a tenant that counts its runs per marker.</summary>
[PlatformJob]
public static class AuthenticityProbe
{
    public static ConcurrentDictionary<string, int> Seen { get; } = new();

    public static void Run(string marker) => Seen.AddOrUpdate(marker, 1, (_, runs) => runs + 1);
}

/// <summary>A tenant-scoped platform job that records the tenant it ran as.</summary>
[PlatformJob(TenantScoped = true)]
public sealed class TenantAuthenticityProbe(ITenantAccessor tenants)
{
    public static ConcurrentDictionary<string, Guid?> Seen { get; } = new();

    public void Run(string marker) => Seen[marker] = tenants.Current?.TenantId;
}

/// <summary>A recurring platform job (instance, async, as the worker's own).</summary>
[PlatformJob]
public sealed class RecurringAuthenticityProbe
{
    public static ConcurrentBag<string> Seen { get; } = [];

#pragma warning disable CA1822 // An instance job, as the worker's recurring jobs are.
    public Task RunAsync(string marker)
    {
        Seen.Add(marker);
        return Task.CompletedTask;
    }
#pragma warning restore CA1822
}

/// <summary>Fails as many times as asked for its marker, then succeeds; retried at once.</summary>
[PlatformJob]
[AutomaticRetry(Attempts = 3, DelaysInSeconds = [0])]
public static class RetryProbe
{
    public static ConcurrentDictionary<string, int> FailuresLeft { get; } = new();

    public static ConcurrentDictionary<string, int> Runs { get; } = new();

    public static void Run(string marker)
    {
        Runs.AddOrUpdate(marker, 1, (_, runs) => runs + 1);
        if (FailuresLeft.TryGetValue(marker, out var left) && left > 0)
        {
            FailuresLeft[marker] = left - 1;
            throw new InvalidOperationException("Planned failure of the retry probe.");
        }
    }
}

/// <summary>As <see cref="RetryProbe"/> without retries: it stays failed until re-run.</summary>
[PlatformJob]
[AutomaticRetry(Attempts = 0)]
public static class NoRetryProbe
{
    public static void Run(string marker) => RetryProbe.Run(marker);
}
