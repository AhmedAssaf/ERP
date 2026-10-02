using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using Hangfire;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using OpenTelemetry.Metrics;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Operations;
using Platform.Modules.Operations.Health;
using Platform.Shared.Data;
using Platform.Shared.Telemetry;

namespace Platform.IntegrationTests.Telemetry;

/// <summary>
/// W-10 final fix wave (final review, item D; spec O-10, O-11): Npgsql names a data source without a name after its connection
/// string (password removed, host and user kept), and puts that name on its connection pool and command metrics
/// (<c>db.client.connection.pool.name</c>) and on every command span (<c>db.npgsql.data_source</c>). Every data source the hosts
/// build is named (<see cref="DataSourceNames"/>); the span tag never leaves; and since the module contexts' pool and
/// Hangfire's LISTEN connection (long polling) come from bare connection strings that no name reaches, the pool name never
/// leaves on a metric either.
/// </summary>
/// <remarks>
/// Npgsql's meter is process-wide, so it also reports the pools the test helpers open with plain connection strings: each host
/// here gets the test database's connection string with an <c>Application Name</c> of its own, which an unnamed pool of that
/// host carries in its name. The pool names themselves are read with a raw <see cref="MeterListener"/>, beside the hosts' own
/// exporters, which must never show one.
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class TelemetryDataSourceTests(DatabaseFixture db)
{
    private const string PoolName = "db.client.connection.pool.name";
    private const string DataSourceTag = "db.npgsql.data_source";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_web_hosts_data_sources_are_named_and_neither_spans_nor_metrics_carry_a_connection_string()
    {
        var marker = Marker();
        var telemetry = new CapturedTelemetry();
        await using var factory = new PlatformWebFactory(Marked(marker)).WithWebHostBuilder(builder => builder.ConfigureTestServices(telemetry.AddTo));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://acme.localhost"), AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.TryAddWithoutValidation("User-Agent", "WaslaBidProbe/1.0 (probe agent)").ShouldBeTrue();

        using var response = await client.SendAsync(request, Ct);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await using var context = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<OperationsDbContext>>().CreateDbContextAsync(Ct);
            await context.Database.ExecuteSqlRawAsync("select 1", Ct);
        }

        response.StatusCode.ShouldNotBe(HttpStatusCode.InternalServerError);
        var server = (await telemetry.WaitForServerSpansAsync(factory.Services, 1, Ct)).ShouldHaveSingleItem();
        server.GetTagItem("user_agent.original").ShouldBeNull("no request header value is captured (O-11)");
        foreach (var span in telemetry.AllSpans)
        {
            span.GetTagItem(DataSourceTag).ShouldBeNull($"span {span.DisplayName} carries no data source name");
            span.TagObjects.ShouldAllBe(tag => tag.Value == null || !tag.Value.ToString()!.Contains(marker, StringComparison.Ordinal));
        }

        ShouldCarryNoPoolName(telemetry.CollectMetrics(factory.Services), marker);
        var names = RawPoolNames();
        names.ShouldContain(DataSourceNames.Tenancy);
        names.ShouldContain(DataSourceNames.KeyRing);
    }

    [Fact]
    public async Task The_workers_data_sources_are_named_and_its_metrics_carry_no_pool_name()
    {
        var marker = Marker();
        var telemetry = new CapturedTelemetry();
        await using var worker = await JobServerHost.StartAsync(
            Marked(marker),
            telemetry.AddTo,
            configureHost: builder => builder.AddPlatformTelemetry(TelemetryNames.Services.Worker),
            cancellationToken: Ct);
        var markerJob = Guid.NewGuid().ToString("N");

        string jobId;
        await using (var scope = worker.ScopeFor(TestTenants.Acme))
        {
            jobId = scope.ServiceProvider.GetRequiredService<IBackgroundJobClient>().Enqueue<TelemetryProbeJob>(job => job.Run(markerJob));
        }

        await worker.WaitForSuccessAsync(jobId, Ct);

        ShouldCarryNoPoolName(telemetry.CollectMetrics(worker.Services), marker);
        RawPoolNames().ShouldContain(DataSourceNames.Jobs);
        foreach (var span in telemetry.AllSpans)
        {
            span.GetTagItem(DataSourceTag).ShouldBeNull($"span {span.DisplayName} carries no data source name");
        }
    }

    [Fact]
    public async Task The_postgresql_health_check_leaves_no_pool_named_after_its_connection_string()
    {
        var marker = Marker();

        var health = await new PostgreSqlHealthCheck(Marked(marker)).CheckHealthAsync(new HealthCheckContext(), Ct);

        health.Status.ShouldBe(HealthStatus.Healthy);
        RawPoolNames().Where(name => name.Contains(marker, StringComparison.Ordinal)).ShouldBeEmpty();
    }

    /// <summary>
    /// W-10 follow-up (2026-10-02; spec O-10): a database span is exported without events. In the otel mapping each span event
    /// is a <c>logs-*</c> document, and Npgsql records <c>received-first-response</c> on every command of the module contexts'
    /// pool, which Npgsql's own switch does not reach. A failed command keeps its exception type on the span instead of an
    /// event with the message (which can quote a value, such as the key of a unique violation).
    /// </summary>
    [Fact]
    public async Task A_database_span_is_exported_without_events_and_a_failed_one_names_its_exception_type()
    {
        var telemetry = new CapturedTelemetry();
        await using var factory = new PlatformWebFactory(db.AppConnectionString).WithWebHostBuilder(builder => builder.ConfigureTestServices(telemetry.AddTo));
        using var source = new ActivitySource("WaslaBid.Tests.DatabaseEvents");
        ActivityTraceId traceId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await using var context = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<OperationsDbContext>>().CreateDbContextAsync(Ct);
            using var parent = source.StartActivity("probe").ShouldNotBeNull("the host's tracer listens to WaslaBid.* sources");
            traceId = parent.TraceId;
            await context.Database.ExecuteSqlRawAsync("select 1", Ct);
            await Should.ThrowAsync<PostgresException>(() => context.Database.ExecuteSqlRawAsync("select 1/0", Ct));
        }

        var database = telemetry.AllSpans.Where(span => span.TraceId == traceId && span.Source.Name == TelemetryNames.Sources.Npgsql).ToList();
        database.Count.ShouldBeGreaterThanOrEqualTo(2);
        database.ShouldAllBe(span => !span.Events.Any());
        var failed = database.Where(span => span.Status == ActivityStatusCode.Error).ShouldHaveSingleItem();
        failed.GetTagItem(TelemetryNames.Attributes.ExceptionType).ShouldBe(typeof(PostgresException).FullName);
    }

    private static string Marker() => $"w10probe{Guid.NewGuid():N}";

    private string Marked(string marker) =>
        new NpgsqlConnectionStringBuilder(db.AppConnectionString) { ApplicationName = marker }.ConnectionString;

    /// <summary>The host's exported Npgsql metrics exist and carry no pool name, nor anything of a connection string.</summary>
    private static void ShouldCarryNoPoolName(IReadOnlyList<MetricSnapshot> metrics, string marker)
    {
        var npgsql = metrics.Where(m => m.MeterName == TelemetryNames.Sources.Npgsql).ToList();
        npgsql.ShouldContain(m => m.Name == "db.client.connection.count", "the host exports Npgsql's pool metrics");
        foreach (var metric in npgsql)
        {
            foreach (var point in metric.MetricPoints)
            {
                foreach (var tag in point.Tags)
                {
                    tag.Key.ShouldNotBe(PoolName, $"{metric.Name} carries no pool name");
                    var value = tag.Value?.ToString() ?? string.Empty;
                    value.ShouldNotContain("Username=", Case.Insensitive);
                    value.ShouldNotContain(marker);
                }
            }
        }
    }

    /// <summary>The name of every Npgsql pool in the process, read straight from the meter (no SDK view in between).</summary>
    private static List<string> RawPoolNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, own) =>
        {
            if (instrument.Meter.Name == TelemetryNames.Sources.Npgsql && instrument.Name == "db.client.connection.max")
            {
                own.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((_, _, tags, _) => Collect(names, tags));
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) => Collect(names, tags));
        listener.Start();
        listener.RecordObservableInstruments();
        names.ShouldNotBeEmpty("the Npgsql meter reports its pools");
        return [.. names];
    }

    private static void Collect(HashSet<string> names, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        foreach (var tag in tags)
        {
            if (tag.Key == PoolName && tag.Value is string name)
            {
                names.Add(name);
            }
        }
    }
}
