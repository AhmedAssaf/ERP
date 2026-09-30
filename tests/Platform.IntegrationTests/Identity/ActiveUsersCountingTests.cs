using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity.Contracts;
using Platform.Shared.Telemetry;

namespace Platform.IntegrationTests.Identity;

/// <summary>
/// W-10 active users (spec 6.4, 6.1; O-19, O-21), QA additions beside <see cref="ActiveUsersTests"/>: where each rolling
/// window ends ("1d" the last 24 hourly buckets, "7d" 168, "30d" 720, none after the counting time), and the tags the
/// worker's active-user gauges carry after a real job run. Each test works on a tenant of its own.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ActiveUsersCountingTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_day_week_and_month_windows_end_at_their_oldest_hourly_bucket()
    {
        // A fixed counting time half past an hour; the buckets are placed on absolute hours around it, so the result does
        // not depend on when the test runs or on the hour turning while it runs. The oldest bucket (30 days) stays younger
        // than the 35-day prune of any other test, so no other test removes it.
        var utc = DateTimeOffset.UtcNow;
        var hour = new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero).AddHours(-3);
        var now = hour.AddMinutes(30);
        var tenant = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        foreach (var (user, hoursBefore) in new (string, int)[]
        {
            ("this-hour", 0),
            ("23-hours", 23),
            ("24-hours", 24),
            ("167-hours", 167),
            ("168-hours", 168),
            ("719-hours", 719),
            ("720-hours", 720),
            ("next-hour", -1),
        })
        {
            await InsertBucketAsOwnerAsync(tenant.TenantId, user, hour.AddHours(-hoursBefore));
        }

        await using var worker = new UsageWorker(db.AppConnectionString);
        await using var scope = worker.Services.CreateAsyncScope();
        var counts = (await scope.ServiceProvider.GetRequiredService<IUserActivityCounts>().CountAsync(now, Ct))
            .Where(c => c.TenantId == tenant.TenantId && c.Kind == ActivityKind.Staff)
            .ToDictionary(c => c.Window, c => c.Users);

        counts.GetValueOrDefault(ActivityWindow.OneDay).ShouldBe(2, "this hour and 23 hours before; 24 hours before is the 25th bucket");
        counts.GetValueOrDefault(ActivityWindow.SevenDays).ShouldBe(4, "up to 167 hours before; 168 hours before is the 169th bucket");
        counts.GetValueOrDefault(ActivityWindow.ThirtyDays).ShouldBe(6, "up to 719 hours before; 720 hours before is outside, though not yet pruned");
    }

    [Fact]
    public async Task No_active_user_metric_carries_a_user_tenant_or_company_id()
    {
        var tenant = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        var staff = Guid.NewGuid().ToString();
        var vendor = Guid.NewGuid().ToString();
        var company = Guid.NewGuid();
        await using (var host = new ModuleHost(db.AppConnectionString))
        {
            await using (var staffScope = host.ScopeFor(tenant, actingUserId: staff))
            {
                await staffScope.ServiceProvider.GetRequiredService<IUserActivityRecorder>().RecordAsync(ActivityKind.Staff, Ct);
            }

            await using var vendorScope = host.ScopeFor(tenant, company, vendor);
            await vendorScope.ServiceProvider.GetRequiredService<IUserActivityRecorder>().RecordAsync(ActivityKind.Vendor, Ct);
        }

        await using var worker = new UsageWorker(db.AppConnectionString);
        using var metrics = new UsageMetrics(worker.Meters);
        await worker.RunAsync(Ct);

        var points = metrics.Collect();

        // Control: the job reported this tenant's two users, so the check below looks at real points.
        points.Where(p => p.Name == TelemetryNames.UsersActive)
            .Single(p => p.HasExactly((TelemetryNames.Tags.TenantSlug, tenant.Slug), (TelemetryNames.Tags.UserKind, TelemetryNames.UserKinds.Vendor), (TelemetryNames.Tags.Window, TelemetryNames.Windows.OneDay)))
            .Value.ShouldBe(1);
        points.Select(p => p.Name).Distinct().ShouldBe([TelemetryNames.UsersActive, TelemetryNames.UsersActiveAllTenants], ignoreOrder: true);
        string[] allowedKeys = [TelemetryNames.Tags.TenantSlug, TelemetryNames.Tags.UserKind, TelemetryNames.Tags.Window];
        points.SelectMany(p => p.Tags.Keys).Distinct().ShouldAllBe(key => allowedKeys.Contains(key));
        points.Where(p => p.Name == TelemetryNames.UsersActiveAllTenants).ShouldAllBe(p => !p.Tags.ContainsKey(TelemetryNames.Tags.TenantSlug));
        points.Select(p => p.Tags[TelemetryNames.Tags.UserKind]).Distinct().ShouldBe([TelemetryNames.UserKinds.Staff, TelemetryNames.UserKinds.Vendor], ignoreOrder: true);
        points.Select(p => p.Tags[TelemetryNames.Tags.Window]).Distinct()
            .ShouldBe([TelemetryNames.Windows.OneDay, TelemetryNames.Windows.SevenDays, TelemetryNames.Windows.ThirtyDays], ignoreOrder: true);
        var values = points.SelectMany(p => p.Tags.Values).ToList();
        foreach (var id in new[] { staff, vendor, company.ToString(), tenant.TenantId.ToString() })
        {
            values.ShouldNotContain(id);
        }
    }

    /// <summary>One staff bucket of <paramref name="user"/> on exactly <paramref name="hour"/>, as the owner (the trigger sets the database's hour on insert).</summary>
    private async Task InsertBucketAsOwnerAsync(Guid tenantId, string user, DateTimeOffset hour)
    {
        await using var connection = new NpgsqlConnection(db.OwnerConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            insert into identity.user_activity (tenant_id, user_id, kind) values (@tenant, @user, 'staff');
            update identity.user_activity set hour = @hour where tenant_id = @tenant and user_id = @user and kind = 'staff';
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("user", user);
        command.Parameters.AddWithValue("hour", hour);
        await command.ExecuteNonQueryAsync(Ct);
    }
}
