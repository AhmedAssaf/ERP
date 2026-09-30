using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Operations.Contracts;
using Platform.Shared.Telemetry;
using Platform.Web.Usage;

namespace Platform.IntegrationTests.Identity;

/// <summary>
/// W-10 business metrics, active users (spec 6.4, 6.6 storage, 6.8; O-21, Q10 as recommended): an authenticated request
/// or circuit activity writes one hourly bucket per user, tenant and kind into <c>identity.user_activity</c> (insert only,
/// forced row-level security, the database's hour); the worker's usage job counts them through
/// <c>identity.activity_counts</c> every five minutes, stores the result in <c>ops.active_user_counts</c> and publishes
/// <c>waslabid.users.active</c>. Each test works on tenants of its own, so the other tests' activity never changes its counts.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ActiveUsersTests(DatabaseFixture db)
{
    private static readonly string[] Windows = [TelemetryNames.Windows.OneDay, TelemetryNames.Windows.SevenDays, TelemetryNames.Windows.ThirtyDays];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_user_seen_today_counts_as_active_for_the_day_week_and_month()
    {
        var (tenant, user) = await StaffTenantAsync();
        await using (var web = new PlatformWebFactory(db.AppConnectionString))
        using (var client = web.ClientFor(TenantRows.Host(tenant)))
        using (var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/").As(new TestUser(user, [tenant.Slug])), Ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await using var worker = new UsageWorker(db.AppConnectionString);
        using var metrics = new UsageMetrics(worker.Meters);
        await worker.RunAsync(Ct);

        foreach (var window in Windows)
        {
            metrics.Value(TelemetryNames.UsersActive, Slug(tenant), Kind(TelemetryNames.UserKinds.Staff), Window(window)).ShouldBe(1, window);
            metrics.Value(TelemetryNames.UsersActive, Slug(tenant), Kind(TelemetryNames.UserKinds.Vendor), Window(window)).ShouldBe(0, window);
        }

        var stored = (await worker.LatestAsync(Ct)).ShouldNotBeNull();
        foreach (var window in Windows)
        {
            stored.Counts.ShouldContain(new ActiveUserCount(tenant.Slug, TelemetryNames.UserKinds.Staff, window, 1));
        }

        (await ActivityRows.ForTenantAsync(db.OwnerConnectionString, tenant.TenantId, Ct)).ShouldHaveSingleItem().UserId.ShouldBe(user);
    }

    [Fact]
    public async Task A_user_seen_eight_days_ago_counts_for_the_month_only()
    {
        var tenant = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        await ActivityRows.InsertAsOwnerAsync(db.OwnerConnectionString, tenant.TenantId, $"old-{Guid.NewGuid():N}", "staff", hoursAgo: 8 * 24, Ct);

        await using var worker = new UsageWorker(db.AppConnectionString);
        using var metrics = new UsageMetrics(worker.Meters);
        await worker.RunAsync(Ct);

        metrics.Value(TelemetryNames.UsersActive, Slug(tenant), Kind(TelemetryNames.UserKinds.Staff), Window(TelemetryNames.Windows.OneDay)).ShouldBe(0);
        metrics.Value(TelemetryNames.UsersActive, Slug(tenant), Kind(TelemetryNames.UserKinds.Staff), Window(TelemetryNames.Windows.SevenDays)).ShouldBe(0);
        metrics.Value(TelemetryNames.UsersActive, Slug(tenant), Kind(TelemetryNames.UserKinds.Staff), Window(TelemetryNames.Windows.ThirtyDays)).ShouldBe(1);
    }

    [Fact]
    public async Task Last_seen_is_written_at_most_once_an_hour_per_user_and_tenant()
    {
        var clock = new TestClock();
        var (tenant, user) = await StaffTenantAsync();
        using var inserts = new StatementCounter(db.AppConnectionString, "insert into identity.user_activity");
        await using var host = new ModuleHost(db.AppConnectionString, clock: clock);

        for (var i = 0; i < 50; i++)
        {
            await RecordAsync(host, tenant, user, ActivityKind.Staff);
        }

        (await ActivityRows.ForTenantAsync(db.OwnerConnectionString, tenant.TenantId, Ct)).Count.ShouldBe(1);
        inserts.Count.ShouldBe(1, "the throttle lets only the first call of the hour reach the database");

        // An hour later on both clocks: the database's hour has moved on (the row is now an hour old), and so has the host's.
        await ActivityRows.AgeAsOwnerAsync(db.OwnerConnectionString, tenant.TenantId, user, hours: 1, Ct);
        clock.Advance(TimeSpan.FromHours(1));
        await RecordAsync(host, tenant, user, ActivityKind.Staff);
        await RecordAsync(host, tenant, user, ActivityKind.Staff);

        (await ActivityRows.ForTenantAsync(db.OwnerConnectionString, tenant.TenantId, Ct)).Count.ShouldBe(2);
        inserts.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Two_instances_writing_the_same_hour_leave_one_row()
    {
        var (tenant, user) = await StaffTenantAsync();
        await using var first = new ModuleHost(db.AppConnectionString);
        await using var second = new ModuleHost(db.AppConnectionString);

        await Task.WhenAll(RecordAsync(first, tenant, user, ActivityKind.Staff), RecordAsync(second, tenant, user, ActivityKind.Staff));

        (await ActivityRows.ForTenantAsync(db.OwnerConnectionString, tenant.TenantId, Ct)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Circuit_activity_counts_as_activity()
    {
        var (tenant, user) = await StaffTenantAsync();
        await using var host = new ModuleHost(db.AppConnectionString);
        await using var circuitScope = host.ScopeFor(tenant, actingUserId: user);
        var session = UsageSession.Staff(user, tenant);
        var guard = session.Guard();
        await using var meterHost = UsageMetrics.NewMeterFactoryHost();
        var registry = new ConnectedCircuits(meterHost.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>());
        var handler = new UsageCircuitHandler(
            session.Connection(), session.Tenants, session.Vendor, session.Platform, guard, registry,
            circuitScope.ServiceProvider.GetRequiredService<IUserActivityRecorder>());
        await handler.OnConnectionUpAsync(null!, Ct);
        var ran = 0;
        var inbound = handler.CreateInboundActivityHandler(_ =>
        {
            ran++;
            return Task.CompletedTask;
        });

        await inbound(null!);

        ran.ShouldBe(1, "the circuit's own activity still runs");
        (await ActivityRows.ForTenantAsync(db.OwnerConnectionString, tenant.TenantId, Ct)).ShouldHaveSingleItem().Kind.ShouldBe("staff");
    }

    [Fact]
    public async Task Health_alive_static_and_anonymous_requests_record_no_activity()
    {
        var (tenant, user) = await StaffTenantAsync();
        var staff = new TestUser(user, [tenant.Slug]);
        await using var web = new PlatformWebFactory(db.AppConnectionString);
        using var client = web.ClientFor(TenantRows.Host(tenant));

        foreach (var path in new[] { "/health", "/alive", "/_framework/blazor.web.js", "/_content/Platform.UI/js/dialog.js", "/branding/logo", "/favicon.png" })
        {
            using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, path).As(staff), Ct);
        }

        using (var anonymous = await client.GetAsync(new Uri("/", UriKind.Relative), Ct))
        {
            anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        (await ActivityRows.ForTenantAsync(db.OwnerConnectionString, tenant.TenantId, Ct)).ShouldBeEmpty();

        // The control: a page request by the same user is activity.
        using (var page = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/").As(staff), Ct))
        {
            page.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await ActivityRows.ForTenantAsync(db.OwnerConnectionString, tenant.TenantId, Ct)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Another_tenants_session_cannot_read_last_seen_rows()
    {
        var (tenant, user) = await StaffTenantAsync();
        await ActivityRows.InsertAsOwnerAsync(db.OwnerConnectionString, tenant.TenantId, user, "staff", hoursAgo: 0, Ct);

        foreach (var (label, tenantId, vendor, acting) in new (string, Guid?, Guid?, string)[]
        {
            ("staff of the same tenant", tenant.TenantId, null, user),
            ("staff of another tenant", TestTenants.Beta.TenantId, null, "beta.admin"),
            ("a vendor of the same tenant", tenant.TenantId, Guid.NewGuid(), $"vendor-{Guid.NewGuid():N}"),
            ("a vendor without a tenant", null, Guid.NewGuid(), $"vendor-{Guid.NewGuid():N}"),
        })
        {
            await using var session = await ActivityRows.AppSessionAsync(db.AppConnectionString, tenantId, vendor, acting, Ct);
            (await ActivityRows.TryAsync(session, "select count(*) from identity.user_activity", Ct)).ShouldBe("refused", label);
            (await ActivityRows.TryAsync(session, "select * from identity.activity_counts(now())", Ct)).ShouldBe("refused", label);
            (await ActivityRows.TryAsync(session, "select identity.prune_activity(now())", Ct)).ShouldBe("refused", label);
            (await ActivityRows.TryAsync(session, "update identity.user_activity set hour = now()", Ct)).ShouldBe("refused", label);
            (await ActivityRows.TryAsync(session, "delete from identity.user_activity", Ct)).ShouldBe("refused", label);
        }

        // The worker's session (neither a tenant nor a vendor context) gets counts, never rows.
        await using var worker = await ActivityRows.AppSessionAsync(db.AppConnectionString, null, null, null, Ct);
        (await ActivityRows.TryAsync(worker, "select * from identity.activity_counts(now())", Ct)).ShouldBe("ok");
        (await ActivityRows.TryAsync(worker, "select count(*) from identity.user_activity", Ct)).ShouldBe("refused");
    }

    public static TheoryData<string> ForeignRows => new(
        "another user", "another tenant", "staff from a vendor session", "vendor from a staff session", "no acting user");

    [Theory]
    [MemberData(nameof(ForeignRows))]
    public async Task A_session_writes_only_its_own_row_for_its_own_tenant_and_kind(string attempt)
    {
        var tenant = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        var user = $"user-{Guid.NewGuid():N}";
        var company = Guid.NewGuid();
        var (sessionVendor, sessionUser, rowTenant, rowUser, rowKind) = attempt switch
        {
            "another user" => ((Guid?)null, (string?)user, tenant.TenantId, $"other-{Guid.NewGuid():N}", "staff"),
            "another tenant" => (null, user, TestTenants.Beta.TenantId, user, "staff"),
            "staff from a vendor session" => (company, user, tenant.TenantId, user, "staff"),
            "vendor from a staff session" => (null, user, tenant.TenantId, user, "vendor"),
            "no acting user" => (null, null, tenant.TenantId, user, "staff"),
            _ => throw new ArgumentOutOfRangeException(nameof(attempt), attempt, null),
        };
        await using var session = await ActivityRows.AppSessionAsync(db.AppConnectionString, tenant.TenantId, sessionVendor, sessionUser, Ct);

        var outcome = await ActivityRows.TryAsync(
            session, "insert into identity.user_activity (tenant_id, user_id, kind) values (@tenant, @user, @kind)", Ct,
            ("tenant", rowTenant), ("user", rowUser), ("kind", rowKind));

        outcome.ShouldBe("refused", attempt);
    }

    [Fact]
    public async Task A_session_writes_its_own_row_in_the_kind_its_context_allows()
    {
        var tenant = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        var staff = $"staff-{Guid.NewGuid():N}";
        var vendor = $"vendor-{Guid.NewGuid():N}";
        const string insert = "insert into identity.user_activity (tenant_id, user_id, kind) values (@tenant, @user, @kind) on conflict do nothing";

        await using (var staffSession = await ActivityRows.AppSessionAsync(db.AppConnectionString, tenant.TenantId, null, staff, Ct))
        await using (var command = new NpgsqlCommand(insert, staffSession))
        {
            command.Parameters.AddWithValue("tenant", tenant.TenantId);
            command.Parameters.AddWithValue("user", staff);
            command.Parameters.AddWithValue("kind", "staff");
            (await command.ExecuteNonQueryAsync(Ct)).ShouldBe(1);
            // The same hour again: nothing, and no error.
            (await command.ExecuteNonQueryAsync(Ct)).ShouldBe(0);
        }

        await using (var vendorSession = await ActivityRows.AppSessionAsync(db.AppConnectionString, tenant.TenantId, Guid.NewGuid(), vendor, Ct))
        await using (var command = new NpgsqlCommand(insert, vendorSession))
        {
            command.Parameters.AddWithValue("tenant", tenant.TenantId);
            command.Parameters.AddWithValue("user", vendor);
            command.Parameters.AddWithValue("kind", "vendor");
            (await command.ExecuteNonQueryAsync(Ct)).ShouldBe(1);
        }

        (await ActivityRows.ForTenantAsync(db.OwnerConnectionString, tenant.TenantId, Ct)).Select(r => (r.UserId, r.Kind))
            .ShouldBe([(staff, "staff"), (vendor, "vendor")], ignoreOrder: true);
    }

    [Fact]
    public async Task The_activity_hour_is_the_database_clock_not_the_writers()
    {
        var tenant = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        var user = $"user-{Guid.NewGuid():N}";
        await using (var session = await ActivityRows.AppSessionAsync(db.AppConnectionString, tenant.TenantId, null, user, Ct))
        await using (var command = new NpgsqlCommand(
            "insert into identity.user_activity (tenant_id, user_id, kind, hour) values (@tenant, @user, 'staff', timestamptz '2020-01-01 07:00:00+00')", session))
        {
            command.Parameters.AddWithValue("tenant", tenant.TenantId);
            command.Parameters.AddWithValue("user", user);
            await command.ExecuteNonQueryAsync(Ct);
        }

        var row = (await ActivityRows.ForTenantAsync(db.OwnerConnectionString, tenant.TenantId, Ct)).ShouldHaveSingleItem();
        row.Hour.Year.ShouldBeGreaterThan(2020);
        (DateTimeOffset.UtcNow - row.Hour).ShouldBeLessThan(TimeSpan.FromHours(1.1));
        (row.Hour.Minute, row.Hour.Second).ShouldBe((0, 0), "a bucket is an hour");
    }

    [Fact]
    public async Task A_vendor_active_on_two_tenants_counts_once_across_tenants()
    {
        var first = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        var second = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        var vendor = $"vendor-{Guid.NewGuid():N}";
        var company = Guid.NewGuid();
        await using var worker = new UsageWorker(db.AppConnectionString);
        var now = DateTimeOffset.UtcNow;
        var before = await AllTenantsVendorsTodayAsync(worker, now);

        foreach (var tenant in new[] { first, second })
        {
            await using var host = new ModuleHost(db.AppConnectionString);
            await using var scope = host.ScopeFor(tenant, company, vendor);
            await scope.ServiceProvider.GetRequiredService<IUserActivityRecorder>().RecordAsync(ActivityKind.Vendor, Ct);
        }

        await using var countScope = worker.Services.CreateAsyncScope();
        var counts = await countScope.ServiceProvider.GetRequiredService<IUserActivityCounts>().CountAsync(now, Ct);
        counts.ShouldContain(new ActivityCount(first.TenantId, ActivityKind.Vendor, ActivityWindow.OneDay, 1));
        counts.ShouldContain(new ActivityCount(second.TenantId, ActivityKind.Vendor, ActivityWindow.OneDay, 1));
        (await AllTenantsVendorsTodayAsync(worker, now)).ShouldBe(before + 1, "one vendor on two tenants is one user across tenants");
    }

    [Fact]
    public async Task Activity_older_than_35_days_is_pruned()
    {
        var tenant = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        await ActivityRows.InsertAsOwnerAsync(db.OwnerConnectionString, tenant.TenantId, "gone", "staff", hoursAgo: 36 * 24, Ct);
        await ActivityRows.InsertAsOwnerAsync(db.OwnerConnectionString, tenant.TenantId, "kept", "staff", hoursAgo: 34 * 24, Ct);

        await using var worker = new UsageWorker(db.AppConnectionString);
        await worker.PruneAsync(Ct);

        (await ActivityRows.ForTenantAsync(db.OwnerConnectionString, tenant.TenantId, Ct)).Select(r => r.UserId).ShouldBe(["kept"]);
    }

    [Fact]
    public async Task A_failed_activity_write_does_not_fail_the_request()
    {
        // The table refuses the application role's insert (as a missing table or a lost grant would): the page request and
        // a circuit event of a staff member both still succeed, and the failure is logged by type only.
        var (tenant, user) = await StaffTenantAsync();
        await ExecuteAsOwnerAsync("revoke insert on identity.user_activity from erp_app");
        try
        {
            var logs = new CapturedLogs();
            await using (var web = new PlatformWebFactory(db.AppConnectionString).WithWebHostBuilder(builder =>
                builder.ConfigureServices(services => services.AddLogging(b => b.AddProvider(logs)))))
            using (var client = web.CreateClient(new() { BaseAddress = new Uri($"http://{TenantRows.Host(tenant)}"), AllowAutoRedirect = false }))
            using (var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/").As(new TestUser(user, [tenant.Slug])), Ct))
            {
                response.StatusCode.ShouldBe(HttpStatusCode.OK);
            }

            var failures = logs.Entries.Where(e => e.Level == LogLevel.Warning && e.Text.Contains("ErrorType=", StringComparison.Ordinal)).ToList();
            failures.ShouldHaveSingleItem().Text.ShouldContain("PostgresException");
            failures.ShouldAllBe(e => !e.Text.Contains(user, StringComparison.Ordinal) && !e.Text.Contains("Password", StringComparison.OrdinalIgnoreCase));

            await using var host = new ModuleHost(db.AppConnectionString);
            await using var circuitScope = host.ScopeFor(tenant, actingUserId: user);
            await using var meterHost = UsageMetrics.NewMeterFactoryHost();
            var session = UsageSession.Staff(user, tenant);
            var handler = new UsageCircuitHandler(
                session.Connection(), session.Tenants, session.Vendor, session.Platform, session.Guard(),
                new ConnectedCircuits(meterHost.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>()),
                circuitScope.ServiceProvider.GetRequiredService<IUserActivityRecorder>());
            await handler.OnConnectionUpAsync(null!, Ct);
            var ran = 0;

            await handler.CreateInboundActivityHandler(_ =>
            {
                ran++;
                return Task.CompletedTask;
            })(null!);

            ran.ShouldBe(1, "the circuit event runs although its activity could not be written");
        }
        finally
        {
            await ExecuteAsOwnerAsync("grant insert on identity.user_activity to erp_app");
        }

        (await ActivityRows.ForTenantAsync(db.OwnerConnectionString, tenant.TenantId, Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task After_a_failed_activity_write_the_next_requests_do_not_touch_the_database_for_a_minute()
    {
        // The database is down: the first write fails; every other write of the next minute, for any user, is not tried,
        // so no request waits on the connection and the log gets one warning per minute, not one per request.
        using var server = new ClosingTcpServer();
        var down = new NpgsqlConnectionStringBuilder(db.AppConnectionString) { Host = "127.0.0.1", Port = server.Port, Timeout = 2, Pooling = false }.ConnectionString;
        var clock = new TestClock();
        var logs = new CapturedLogs();
        await using var host = new ModuleHost(down, clock: clock, configure: services => services.AddLogging(b => b.AddProvider(logs)));
        var tenant = MemberRows.NewTenant();

        await RecordAsync(host, tenant, "first", ActivityKind.Staff);
        var attempts = server.Attempts;
        attempts.ShouldBeGreaterThan(0, "the first write reached the database");

        foreach (var user in new[] { "second", "third", "first" })
        {
            await RecordAsync(host, tenant, user, ActivityKind.Staff);
        }

        clock.Advance(TimeSpan.FromSeconds(59));
        await RecordAsync(host, tenant, "fourth", ActivityKind.Vendor);

        server.Attempts.ShouldBe(attempts, "nothing touches the database within the minute after a failure");
        Warnings(logs).Count.ShouldBe(1);

        clock.Advance(TimeSpan.FromSeconds(2));
        await RecordAsync(host, tenant, "second", ActivityKind.Staff);

        server.Attempts.ShouldBeGreaterThan(attempts, "after the minute the next write tries again");
        Warnings(logs).Count.ShouldBe(2);

        static List<(LogLevel Level, string Category, string Text)> Warnings(CapturedLogs logs) =>
            [.. logs.Entries.Where(e => e.Level == LogLevel.Warning && e.Text.Contains("ErrorType=", StringComparison.Ordinal))];
    }

    [Fact]
    public async Task The_platform_console_session_can_neither_count_nor_prune_activity()
    {
        // Review of W-10: the worker has no acting user; a platform console session always has one.
        await using var console = await ActivityRows.AppSessionAsync(db.AppConnectionString, null, null, "platform.admin", Ct);

        (await ActivityRows.TryAsync(console, "select * from identity.activity_counts(now())", Ct)).ShouldBe("refused");
        (await ActivityRows.TryAsync(console, "select identity.prune_activity(now())", Ct)).ShouldBe("refused");

        await using var worker = await ActivityRows.AppSessionAsync(db.AppConnectionString, null, null, null, Ct);
        (await ActivityRows.TryAsync(worker, "select identity.prune_activity(now() - interval '400 days')", Ct)).ShouldBe("ok");
    }

    [Fact]
    public async Task The_stored_counts_are_read_only_without_a_tenant_or_vendor_context()
    {
        await using (var worker = new UsageWorker(db.AppConnectionString))
        {
            await worker.RunAsync(Ct);
        }

        foreach (var (label, tenantId, vendor) in new (string, Guid?, Guid?)[]
        {
            ("tenant staff", TestTenants.Acme.TenantId, null),
            ("vendor", TestTenants.Acme.TenantId, Guid.NewGuid()),
        })
        {
            await using var session = await ActivityRows.AppSessionAsync(db.AppConnectionString, tenantId, vendor, "someone", Ct);
            await using var count = new NpgsqlCommand("select count(*)::int from ops.active_user_counts", session);
            ((int)(await count.ExecuteScalarAsync(Ct))!).ShouldBe(0, label);
            (await ActivityRows.TryAsync(session, "insert into ops.active_user_counts (tenant_slug, kind, time_window, users, computed_at) values ('acme', 'staff', '1d', 99, now())", Ct))
                .ShouldBe("refused", label);
        }

        // The platform console reads the counts but, having an acting user, neither writes nor deletes them.
        await using var console = await ActivityRows.AppSessionAsync(db.AppConnectionString, null, null, "platform.admin", Ct);
        await using var all = new NpgsqlCommand("select count(*)::int from ops.active_user_counts", console);
        var stored = (int)(await all.ExecuteScalarAsync(Ct))!;
        stored.ShouldBeGreaterThan(0);
        (await ActivityRows.TryAsync(console, "insert into ops.active_user_counts (tenant_slug, kind, time_window, users, computed_at) values ('acme', 'staff', '1d', 99, now())", Ct))
            .ShouldBe("refused");
        await using (var delete = new NpgsqlCommand("delete from ops.active_user_counts", console))
        {
            (await delete.ExecuteNonQueryAsync(Ct)).ShouldBe(0, "no delete policy admits the console");
        }

        ((int)(await all.ExecuteScalarAsync(Ct))!).ShouldBe(stored);
    }

    private async Task ExecuteAsOwnerAsync(string sql)
    {
        await using var owner = new NpgsqlConnection(db.OwnerConnectionString);
        await owner.OpenAsync(CancellationToken.None);
#pragma warning disable CA2100 // The statements are the tests' own.
        await using var command = new NpgsqlCommand(sql, owner);
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static (string, string) Slug(Platform.Shared.Tenancy.TenantContext tenant) => (TelemetryNames.Tags.TenantSlug, tenant.Slug);

    private static (string, string) Kind(string kind) => (TelemetryNames.Tags.UserKind, kind);

    private static (string, string) Window(string window) => (TelemetryNames.Tags.Window, window);

    private static async Task RecordAsync(ModuleHost host, Platform.Shared.Tenancy.TenantContext tenant, string user, ActivityKind kind)
    {
        await using var scope = host.ScopeFor(tenant, actingUserId: user);
        await scope.ServiceProvider.GetRequiredService<IUserActivityRecorder>().RecordAsync(kind, Ct);
    }

    private static async Task<int> AllTenantsVendorsTodayAsync(UsageWorker worker, DateTimeOffset now)
    {
        await using var scope = worker.Services.CreateAsyncScope();
        var counts = await scope.ServiceProvider.GetRequiredService<IUserActivityCounts>().CountAsync(now, Ct);
        return counts.SingleOrDefault(c => c is { TenantId: null, Kind: ActivityKind.Vendor, Window: ActivityWindow.OneDay })?.Users ?? 0;
    }

    /// <summary>A tenant of the test's own with one active tenant admin, who may open its pages.</summary>
    private async Task<(Platform.Shared.Tenancy.TenantContext Tenant, string User)> StaffTenantAsync()
    {
        var tenant = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        var user = $"staff-{Guid.NewGuid():N}";
        await MemberRows.EnsureActiveAdminAsync(db.OwnerConnectionString, tenant.TenantId, user, Ct);
        return (tenant, user);
    }
}
