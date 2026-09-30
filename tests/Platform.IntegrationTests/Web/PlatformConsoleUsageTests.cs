using System.Net;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Web.Usage;

namespace Platform.IntegrationTests.Web;

/// <summary>
/// W-10 console usage page <c>/platform/usage</c> (spec 6.6; O-23, O-25, O-26): concurrent users from this web instance's
/// circuit registry and active users from the usage job's stored result, in total and per tenant, split into staff and
/// vendors; counts only. The class owns its database, so the stored counts written here are the only ones the page reads.
/// </summary>
public sealed partial class PlatformConsoleTests
{
    private const string UsagePath = "/platform/usage";

    [Fact]
    public async Task The_usage_section_shows_concurrent_and_active_users_per_tenant()
    {
        await StoreUsageAsync(DateTimeOffset.UtcNow,
        [
            ("acme", "staff", "1d", 3), ("acme", "staff", "7d", 5), ("acme", "staff", "30d", 8),
            ("acme", "vendor", "1d", 1), ("acme", "vendor", "7d", 2), ("acme", "vendor", "30d", 4),
            ("beta", "staff", "1d", 0), ("beta", "staff", "7d", 0), ("beta", "staff", "30d", 6),
            ("beta", "vendor", "1d", 0), ("beta", "vendor", "7d", 0), ("beta", "vendor", "30d", 0),
            (null, "staff", "1d", 3), (null, "staff", "7d", 5), (null, "staff", "30d", 14),
            (null, "vendor", "1d", 1), (null, "vendor", "7d", 2), (null, "vendor", "30d", 4),
        ]);
        await using var factory = Factory();
        var circuits = factory.Services.GetRequiredService<ConnectedCircuits>();
        circuits.Add(new object(), "acme", UsageKind.Staff, "staff-a", () => false);
        circuits.Add(new object(), "acme", UsageKind.Staff, "staff-a", () => false);
        circuits.Add(new object(), "acme", UsageKind.Staff, "staff-b", () => false);
        circuits.Add(new object(), "acme", UsageKind.Vendor, "vendor-a", () => false);

        var html = await GetPageAsync(factory, UsagePath, PlatformAdmin());

        var acme = Row(html, "data-tenant", "acme");
        acme.ShouldContain("Acme Contracting");
        acme.ShouldContain(UsersCell("now", "staff", "2"));
        acme.ShouldContain(UsersCell("now", "vendor", "1"));
        acme.ShouldContain(UsersCell("1d", "staff", "3"));
        acme.ShouldContain(UsersCell("7d", "staff", "5"));
        acme.ShouldContain(UsersCell("30d", "staff", "8"));
        acme.ShouldContain(UsersCell("1d", "vendor", "1"));
        acme.ShouldContain(UsersCell("30d", "vendor", "4"));
        var beta = Row(html, "data-tenant", "beta");
        beta.ShouldContain(UsersCell("now", "staff", "0"));
        beta.ShouldContain(UsersCell("1d", "staff", "0"));
        beta.ShouldContain(UsersCell("30d", "staff", "6"));

        TileValue(html, "now").ShouldBe("3");
        TileValue(html, "1d").ShouldBe("4");
        TileValue(html, "7d").ShouldBe("7");
        TileValue(html, "30d").ShouldBe("18");
        Element(html, "article", "data-tile", "30d").ShouldContain("data-value=\"14\"");
        html.ShouldContain("data-usage-state=\"fresh\"");
    }

    [Fact]
    public async Task The_usage_totals_count_a_vendor_active_on_two_tenants_once()
    {
        await StoreUsageAsync(DateTimeOffset.UtcNow,
        [
            ("acme", "vendor", "1d", 1), ("beta", "vendor", "1d", 1), (null, "vendor", "1d", 1),
            ("acme", "staff", "1d", 0), ("beta", "staff", "1d", 0), (null, "staff", "1d", 0),
        ]);
        await using var factory = Factory();
        var circuits = factory.Services.GetRequiredService<ConnectedCircuits>();
        circuits.Add(new object(), "acme", UsageKind.Vendor, "vendor-on-two", () => false);
        circuits.Add(new object(), "beta", UsageKind.Vendor, "vendor-on-two", () => false);

        var html = await GetPageAsync(factory, UsagePath, PlatformAdmin());

        Row(html, "data-tenant", "acme").ShouldContain(UsersCell("1d", "vendor", "1"));
        Row(html, "data-tenant", "beta").ShouldContain(UsersCell("1d", "vendor", "1"));
        Row(html, "data-tenant", "acme").ShouldContain(UsersCell("now", "vendor", "1"));
        Row(html, "data-tenant", "beta").ShouldContain(UsersCell("now", "vendor", "1"));
        TileValue(html, "1d").ShouldBe("1", "the vendor is one user across tenants");
        TileValue(html, "now").ShouldBe("1");
        // The console defaults to Arabic (the localization test covers English).
        html.ShouldContain("المورد النشط لدى جهتين يُحتسب في صف كل جهة، ومرة واحدة في المجموع.");
    }

    [Fact]
    public async Task A_stale_usage_result_shows_unknown()
    {
        await StoreUsageAsync(DateTimeOffset.UtcNow.AddMinutes(-16), [("acme", "staff", "1d", 3), (null, "staff", "1d", 3)]);
        await using var factory = Factory();

        var html = await GetPageAsync(factory, UsagePath, PlatformAdmin());

        var acme = Row(html, "data-tenant", "acme");
        acme.ShouldContain(UsersCell("1d", "staff", string.Empty));
        acme.ShouldContain(UsersCell("30d", "vendor", string.Empty));
        // Concurrent users come from memory and are always known.
        acme.ShouldContain(UsersCell("now", "staff", "0"));
        TileValue(html, "1d").ShouldBe(string.Empty);
        Element(html, "article", "data-tile", "1d").ShouldContain("aria-label=\"غير معروف\"");
        TileValue(html, "now").ShouldBe("0");
        html.ShouldContain("data-usage-state=\"stale\"");
    }

    [Fact]
    public async Task Before_the_first_count_active_users_show_unknown()
    {
        await StoreUsageAsync(DateTimeOffset.UtcNow, []);
        await using var factory = Factory();

        var html = await GetPageAsync(factory, UsagePath, PlatformAdmin());

        html.ShouldContain("data-usage-state=\"not-counted\"");
        TileValue(html, "7d").ShouldBe(string.Empty);
        Row(html, "data-tenant", "beta").ShouldContain(UsersCell("7d", "vendor", string.Empty));
    }

    [Fact]
    public async Task Usage_counts_are_read_from_the_stored_job_results_not_from_prometheus()
    {
        await StoreUsageAsync(DateTimeOffset.UtcNow, [("acme", "staff", "1d", 7), (null, "staff", "1d", 7)]);
        var outbound = new OutboundRequests();
        // No telemetry settings at all, and the Prometheus, Loki and Grafana addresses pointing at a port nothing serves.
        await using var factory = Factory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Observability:GrafanaUrl", "http://127.0.0.1:1");
            builder.UseSetting("Observability:PrometheusUrl", "http://127.0.0.1:1");
            builder.UseSetting("Observability:LokiUrl", "http://127.0.0.1:1");
            builder.ConfigureTestServices(services => services.AddSingleton<IHttpMessageHandlerBuilderFilter>(outbound));
        });

        var html = await GetPageAsync(factory, UsagePath, PlatformAdmin());

        Row(html, "data-tenant", "acme").ShouldContain(UsersCell("1d", "staff", "7"));
        outbound.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_grafana_link_appears_only_when_configured()
    {
        await using (var without = Factory())
        {
            (await GetPageAsync(without, UsagePath, PlatformAdmin())).ShouldNotContain("data-grafana");
        }

        await using var with = Factory().WithWebHostBuilder(builder => builder.UseSetting("Observability:GrafanaUrl", "http://127.0.0.1:3000/"));
        var html = await GetPageAsync(with, UsagePath, PlatformAdmin());

        Element(html, "a", "data-grafana", "dashboard").ShouldContain("href=\"http://127.0.0.1:3000/d/waslabid-usage\"");
    }

    [Fact]
    public async Task The_usage_page_lists_no_user()
    {
        await StoreUsageAsync(DateTimeOffset.UtcNow, [("acme", "staff", "1d", 1), (null, "staff", "1d", 1)]);
        await using var factory = Factory();
        var circuits = factory.Services.GetRequiredService<ConnectedCircuits>();
        const string staffSub = "9b2f0c1e-usage-page-staff";
        const string vendorSub = "4d1e8a7b-usage-page-vendor";
        circuits.Add(new object(), "acme", UsageKind.Staff, staffSub, () => false);
        circuits.Add(new object(), "acme", UsageKind.Vendor, vendorSub, () => false);

        var html = await GetPageAsync(factory, UsagePath, PlatformAdmin());

        html.ShouldNotContain(staffSub);
        html.ShouldNotContain(vendorSub);
        html.ShouldNotContain("@acme");
        html.ShouldNotContain("acme.admin");
    }

    [Fact]
    public async Task A_tenant_session_cannot_open_the_usage_page()
    {
        await using var factory = Factory();

        // A tenant admin's claims under the platform cookie: no platform-admin role.
        using var response = await GetAsync(factory, UsagePath, TenantAdminOnPlatformCookie());

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldNotContain("data-tile");
    }

    [Fact]
    public async Task A_platform_admin_without_otp_cannot_open_the_usage_page()
    {
        await using var factory = Factory();

        using var response = await GetAsync(factory, UsagePath, PlatformAdmin(acr: "1"));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldNotContain("data-tile");
    }

    [Fact]
    public async Task The_console_navigation_lists_usage_between_tenants_and_jobs()
    {
        await using var factory = Factory();

        var html = await GetPageAsync(factory, "/platform", PlatformAdmin());

        var tenants = html.IndexOf("href=\"platform/tenants\"", StringComparison.Ordinal);
        var usage = html.IndexOf("href=\"platform/usage\"", StringComparison.Ordinal);
        var jobs = html.IndexOf("href=\"platform/jobs\"", StringComparison.Ordinal);
        tenants.ShouldBeGreaterThan(0);
        usage.ShouldBeGreaterThan(tenants);
        jobs.ShouldBeGreaterThan(usage);
    }

    [Fact]
    public async Task The_web_host_registers_one_registry_and_the_usage_circuit_handler()
    {
        await using var factory = Factory();
        await using var first = factory.Services.CreateAsyncScope();
        await using var second = factory.Services.CreateAsyncScope();

        first.ServiceProvider.GetServices<CircuitHandler>().OfType<UsageCircuitHandler>().ShouldHaveSingleItem();
        first.ServiceProvider.GetRequiredService<ConnectedCircuits>().ShouldBeSameAs(second.ServiceProvider.GetRequiredService<ConnectedCircuits>());
    }

    private static string UsersCell(string window, string kind, string users) =>
        $"data-window=\"{window}\" data-kind=\"{kind}\" data-users=\"{users}\"";

    /// <summary>The unformatted number of a total tile (<c>data-tile</c>), empty when unknown.</summary>
    private static string TileValue(string html, string tile)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            Element(html, "article", "data-tile", tile), "<p[^>]*data-value=\"([^\"]*)\"", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
        match.Success.ShouldBeTrue($"the {tile} tile has no value");
        return match.Groups[1].Value;
    }

    /// <summary>Replaces the usage job's stored result, as the owner, with the given rows computed at <paramref name="computedAt"/>.</summary>
    private async Task StoreUsageAsync(DateTimeOffset computedAt, IReadOnlyList<(string? Slug, string Kind, string Window, int Users)> rows)
    {
        await using var connection = new NpgsqlConnection(db.OwnerConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            truncate ops.active_user_counts;
            insert into ops.active_user_counts (tenant_slug, kind, time_window, users, computed_at)
            select t.slug, t.kind, t.time_window, t.users, @at
            from unnest(@slugs::text[], @kinds::text[], @windows::text[], @users::integer[]) as t(slug, kind, time_window, users);
            """, connection);
        command.Parameters.AddWithValue("at", computedAt.ToUniversalTime());
        command.Parameters.AddWithValue("slugs", rows.Select(r => r.Slug).ToArray());
        command.Parameters.AddWithValue("kinds", rows.Select(r => r.Kind).ToArray());
        command.Parameters.AddWithValue("windows", rows.Select(r => r.Window).ToArray());
        command.Parameters.AddWithValue("users", rows.Select(r => r.Users).ToArray());
        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>Records every request sent through a client of the host's HTTP client factory.</summary>
    private sealed class OutboundRequests : IHttpMessageHandlerBuilderFilter
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Uri?> _requests = new();

        public IReadOnlyList<Uri?> Requests => [.. _requests];

        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
        {
            next(builder);
            builder.AdditionalHandlers.Add(new Recorder(_requests));
        };

        private sealed class Recorder(System.Collections.Concurrent.ConcurrentQueue<Uri?> requests) : DelegatingHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                requests.Enqueue(request.RequestUri);
                return base.SendAsync(request, cancellationToken);
            }
        }
    }
}
