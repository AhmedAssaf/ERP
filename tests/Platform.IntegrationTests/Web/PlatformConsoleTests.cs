using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Amazon.S3;
using Amazon.S3.Model;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Operations.Contracts;
using Platform.Shared.Tenancy;
using Platform.Web.PlatformHost;

namespace Platform.IntegrationTests.Web;

/// <summary>
/// The platform console pages (plan task 7; F-51, F-54, F-60 as narrowed), requested on the platform host with a
/// platform cookie as the web host issues it. The class owns its database and MinIO, so the health rows, incidents and
/// failed jobs it writes are the only ones the pages can show.
/// </summary>
public sealed partial class PlatformConsoleTests(DatabaseFixture db, MinioFixture minio)
    : IClassFixture<DatabaseFixture>, IClassFixture<MinioFixture>
{
    private const string PlatformCookie = "waslabid.platform";
    private const string Admin = "platform.admin";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_board_lists_every_component_with_status_latency_and_last_check()
    {
        await ResetHealthAsync();
        await using var factory = Factory();
        var now = DateTimeOffset.UtcNow;
        var failedAt = now.AddMinutes(-1);
        await RecordAsync(factory, [new HealthResult(HealthComponents.ClamAv, HealthStatus.Unhealthy, 2000, failedAt, "PING timed out")]);
        await RecordAsync(factory, [.. HealthComponents.Board.Select((c, i) => new HealthResult(c, HealthStatus.Healthy, 10 + i, now))]);

        var html = await GetPageAsync(factory, "/platform", PlatformAdmin());

        foreach (var (component, index) in HealthComponents.Board.Select((c, i) => (c, i)))
        {
            var tile = Tile(html, component);
            tile.ShouldContain("data-status=\"Healthy\"");
            tile.ShouldContain($"data-latency-ms=\"{10 + index}\"");
            tile.ShouldContain($"datetime=\"{Iso(now)}\"");
        }

        var clamAv = Tile(html, HealthComponents.ClamAv);
        clamAv.ShouldContain($"datetime=\"{Iso(failedAt)}\"");
        clamAv.ShouldContain("PING timed out");
    }

    [Fact]
    public async Task A_stale_result_shows_unknown()
    {
        await ResetHealthAsync();
        await using var factory = Factory();
        var now = DateTimeOffset.UtcNow;
        var stale = now - HealthComponents.StaleAfter - TimeSpan.FromSeconds(5);
        await RecordAsync(factory, [new HealthResult(HealthComponents.Keycloak, HealthStatus.Healthy, 5, stale)]);
        await RecordAsync(factory, [new HealthResult(HealthComponents.Web, HealthStatus.Healthy, 5, now)]);

        var html = await GetPageAsync(factory, "/platform", PlatformAdmin());

        var keycloak = Tile(html, HealthComponents.Keycloak);
        keycloak.ShouldContain("data-status=\"Unknown\"");
        // The stale check's own time stays visible, so the admin sees how old the last word is.
        keycloak.ShouldContain($"datetime=\"{Iso(stale)}\"");
        Tile(html, HealthComponents.Web).ShouldContain("data-status=\"Healthy\"");
    }

    [Fact]
    public async Task A_component_that_never_reported_shows_unknown()
    {
        await ResetHealthAsync();
        await using var factory = Factory();

        var html = await GetPageAsync(factory, "/platform", PlatformAdmin());

        foreach (var component in HealthComponents.Board)
        {
            Tile(html, component).ShouldContain("data-status=\"Unknown\"");
        }
    }

    [Fact]
    public async Task The_incident_list_shows_the_last_30_days()
    {
        await ResetHealthAsync();
        var now = DateTimeOffset.UtcNow;
        await using var factory = Factory();
        var old = now.AddDays(-40);
        var recent = now.AddDays(-3);
        foreach (var openedAt in new[] { old, recent })
        {
            await RecordAsync(factory, [new HealthResult(HealthComponents.Email, HealthStatus.Unhealthy, 1, openedAt, "NOOP refused")]);
            await RecordAsync(factory, [new HealthResult(HealthComponents.Email, HealthStatus.Healthy, 1, openedAt.AddMinutes(7))]);
        }

        var incidents = await IncidentsSinceAsync(factory, now.AddDays(-60));
        var oldId = incidents.Single(i => i.OpenedAt == Truncate(old)).Id;
        var recentId = incidents.Single(i => i.OpenedAt == Truncate(recent)).Id;

        var html = await GetPageAsync(factory, "/platform", PlatformAdmin());

        var row = Row(html, "data-incident", recentId.ToString());
        row.ShouldContain(HealthComponents.Email);
        row.ShouldContain($"datetime=\"{Iso(recent)}\"");
        row.ShouldContain($"datetime=\"{Iso(recent.AddMinutes(7))}\"");
        html.ShouldNotContain(oldId.ToString());
    }

    [Fact]
    public async Task The_tenant_list_shows_user_count_storage_and_failing_jobs()
    {
        await using var factory = Factory(
            members: new FixedMembers(new Dictionary<string, int> { ["acme"] = 4, ["beta"] = 2 }), objectStorage: true);
        await PutObjectAsync($"tenants/{TestTenants.Acme.TenantId}/offers/a.pdf", 1500);
        await PutObjectAsync($"tenants/{TestTenants.Acme.TenantId}/branding/logo.png", 500);
        await PutObjectAsync($"tenants/{TestTenants.Beta.TenantId}/offers/b.pdf", 300);
        var acmeFailures = await FailedJobCountAsync(factory, TestTenants.Acme.TenantId);
        var betaFailures = await FailedJobCountAsync(factory, TestTenants.Beta.TenantId);
        await CreateFailedJobAsync(factory, TestTenants.Acme);
        await CreateFailedJobAsync(factory, TestTenants.Acme);
        await CreateFailedJobAsync(factory, null);

        var html = await GetPageAsync(factory, "/platform/tenants", PlatformAdmin());

        var acme = Row(html, "data-tenant", "acme");
        acme.ShouldContain("Acme Contracting");
        acme.ShouldContain("data-status=\"active\"");
        acme.ShouldContain("data-users=\"4\"");
        acme.ShouldContain("data-tenders=\"\"");
        acme.ShouldContain("data-storage-bytes=\"2000\"");
        acme.ShouldContain($"data-failed-jobs=\"{acmeFailures + 2}\"");
        var beta = Row(html, "data-tenant", "beta");
        beta.ShouldContain("data-users=\"2\"");
        beta.ShouldContain("data-storage-bytes=\"300\"");
        beta.ShouldContain($"data-failed-jobs=\"{betaFailures}\"");
    }

    [Fact]
    public async Task Without_Keycloak_admin_and_object_storage_settings_the_counts_are_a_dash()
    {
        await using var factory = Factory();

        var html = await GetPageAsync(factory, "/platform/tenants", PlatformAdmin());

        var acme = Row(html, "data-tenant", "acme");
        acme.ShouldContain("data-users=\"\"");
        acme.ShouldContain("data-storage-bytes=\"\"");
    }

    [Fact]
    public async Task Each_failed_job_is_listed_with_a_rerun_action_naming_the_job_and_tenant()
    {
        await using var factory = Factory();
        var jobId = await CreateFailedJobAsync(factory, TestTenants.Beta);

        var html = await GetPageAsync(factory, "/platform/tenants", PlatformAdmin());

        var row = Row(html, "data-job", jobId);
        row.ShouldContain($"{nameof(ConsoleProbeJob)}.{nameof(ConsoleProbeJob.Run)}");
        row.ShouldContain("Beta Industries");
        row.ShouldContain(nameof(InvalidOperationException));
        row.ShouldContain("data-rerun");
        // N-10: the exception message and the job's arguments never reach the page.
        html.ShouldNotContain(ConsoleProbeJob.SecretMessage);
        html.ShouldNotContain(ConsoleProbeJob.SecretArgument);
    }

    [Fact]
    public async Task Rerunning_a_failed_job_requeues_it_and_audits_the_admin_and_tenant()
    {
        await using var factory = Factory();
        var jobId = await CreateFailedJobAsync(factory, TestTenants.Acme);

        RequeueOutcome outcome;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            outcome = await scope.ServiceProvider.GetRequiredService<IPlatformJobs>().RequeueAsync(jobId, Admin, Ct);
        }

        outcome.ShouldBe(RequeueOutcome.Requeued);
        var storage = factory.Services.GetRequiredService<JobStorage>();
        using (var connection = storage.GetConnection())
        {
            connection.GetStateData(jobId).Name.ShouldBe(EnqueuedState.StateName);
        }

        var audit = await AuditRowsAsync(jobId);
        audit.ShouldHaveSingleItem();
        audit[0].Actor.ShouldBe(Admin);
        audit[0].Action.ShouldBe("job.requeued");
        audit[0].Data.ShouldContain(TestTenants.Acme.TenantId.ToString());
        audit[0].Data.ShouldContain($"{nameof(ConsoleProbeJob)}.{nameof(ConsoleProbeJob.Run)}");
        audit[0].Data.ShouldNotContain(ConsoleProbeJob.SecretArgument);

        // A second request finds the job no longer failed: nothing changes and nothing more is audited.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<IPlatformJobs>().RequeueAsync(jobId, Admin, Ct)).ShouldBe(RequeueOutcome.NotFailed);
        }

        (await AuditRowsAsync(jobId)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task The_console_header_names_the_admin_and_signs_out_on_the_platform_host()
    {
        await using var factory = Factory();

        var html = await GetPageAsync(factory, "/platform", PlatformAdmin());

        Regex.IsMatch(html, $@"<summary[^>]*>\s*{Regex.Escape(Admin)}\s*<", RegexOptions.None, TimeSpan.FromSeconds(1))
            .ShouldBeTrue("the user menu names the signed-in admin");
        Element(html, "form", "action", "/platform/sign-out").ShouldContain("__RequestVerificationToken");
        html.ShouldNotContain("/account/sign-out");
    }

    [Fact]
    public async Task The_jobs_dashboard_is_platform_only()
    {
        await using var factory = Factory();

        // Tenant host, tenant admin: the console's URL does not exist there.
        using (var tenantClient = factory.CreateClient(new() { BaseAddress = new Uri("http://acme.localhost"), AllowAutoRedirect = false }))
        using (var onTenantHost = await tenantClient.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/platform/jobs").As(TestUser.AcmeAdmin), Ct))
        {
            onTenantHost.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        // Platform host, a session without the platform-admin role (a tenant admin's claims).
        using (var tenantAdmin = await GetAsync(factory, "/platform/jobs", TenantAdminOnPlatformCookie()))
        {
            tenantAdmin.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        // Platform host, a password-only platform admin (acr 1).
        using (var passwordOnly = await GetAsync(factory, "/platform/jobs", PlatformAdmin(acr: "1")))
        {
            passwordOnly.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using var admin = await GetAsync(factory, "/platform/jobs", PlatformAdmin());
        admin.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.Content.ReadAsStringAsync(Ct)).ShouldContain("Hangfire");
    }

    [Theory]
    [InlineData(false, "2", false)]
    [InlineData(true, "1", false)]
    [InlineData(true, "2", true)]
    public async Task The_dashboard_filter_evaluates_PlatformAdmin_itself(bool withRole, string acr, bool expected)
    {
        // Independently of the endpoint's own authorization: the filter alone must refuse anything but a platform admin.
        await using var factory = Factory();
        var claims = new List<Claim> { new("sub", Admin), new("preferred_username", Admin), new("acr", acr) };
        if (withRole)
        {
            claims.Add(new Claim("roles", "platform-admin"));
        }

        var cookie = AuthCookies.Protect(factory.Services, PlatformAuthentication.CookieScheme, AuthCookies.Principal(claims));
        await using var scope = factory.Services.CreateAsyncScope();
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Host = new Microsoft.AspNetCore.Http.HostString(PlatformWebFactory.PlatformHost);
        context.Request.Headers.Cookie = $"{PlatformCookie}={cookie}";
        PlatformRequest.Mark(context);

        var allowed = await JobsDashboardAuthorizationFilter.IsPlatformAdminAsync(context);

        allowed.ShouldBe(expected);
    }

    [Fact]
    public async Task The_dashboard_filter_refuses_a_request_not_on_the_platform_host()
    {
        await using var factory = Factory();
        var cookie = AuthCookies.Protect(factory.Services, PlatformAuthentication.CookieScheme, AuthCookies.Principal(PlatformAdminClaims("2")));
        await using var scope = factory.Services.CreateAsyncScope();
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Headers.Cookie = $"{PlatformCookie}={cookie}";

        (await JobsDashboardAuthorizationFilter.IsPlatformAdminAsync(context)).ShouldBeFalse();
    }

    private WebApplicationFactory<Program> Factory(IOrganizationMembers? members = null, bool objectStorage = false) =>
        new PlatformWebFactory(db.AppConnectionString).WithWebHostBuilder(builder =>
        {
            if (objectStorage)
            {
                builder.UseSetting("ObjectStorage:ServiceUrl", minio.ServiceUrl);
                builder.UseSetting("ObjectStorage:BucketName", MinioFixture.BucketName);
                builder.UseSetting("ObjectStorage:AccessKey", MinioFixture.AccessKey);
                builder.UseSetting("ObjectStorage:SecretKey", MinioFixture.SecretKey);
            }

            builder.ConfigureTestServices(services =>
            {
                if (members is not null)
                {
                    services.Replace(ServiceDescriptor.Singleton(members));
                }
            });
        });

    private static IEnumerable<Claim> PlatformAdminClaims(string acr) =>
    [
        new("sub", Admin),
        new("preferred_username", Admin),
        new("acr", acr),
        new("roles", "platform-admin"),
    ];

    private static ClaimsPrincipal PlatformAdmin(string acr = "2") => AuthCookies.Principal(PlatformAdminClaims(acr));

    private static ClaimsPrincipal TenantAdminOnPlatformCookie() => AuthCookies.Principal(
    [
        new Claim("sub", "acme.admin"),
        new Claim("preferred_username", "acme.admin"),
        new Claim("organization", "acme"),
        new Claim("acr", "2"),
    ]);

    private static async Task<HttpResponseMessage> GetAsync(WebApplicationFactory<Program> factory, string path, ClaimsPrincipal user)
    {
        var cookie = AuthCookies.Protect(factory.Services, PlatformAuthentication.CookieScheme, user);
        using var client = factory.CreateClient(new() { BaseAddress = new Uri($"http://{PlatformWebFactory.PlatformHost}"), AllowAutoRedirect = false, HandleCookies = false });
        return await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, path).WithCookie(PlatformCookie, cookie), Ct);
    }

    private static async Task<string> GetPageAsync(WebApplicationFactory<Program> factory, string path, ClaimsPrincipal user)
    {
        using var response = await GetAsync(factory, path, user);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Ct));
    }

    private static string Tile(string html, string component) => Element(html, "article", "data-component", component);

    /// <summary>The table row holding a cell marked with the attribute (QuickGrid puts no attributes on its rows).</summary>
    private static string Row(string html, string attribute, string value)
    {
        var match = Regex.Match(
            html,
            $"<tr[^>]*>(?:(?!</tr>).)*{attribute}=\"{Regex.Escape(value)}\"(?:(?!</tr>).)*</tr>",
            RegexOptions.Singleline,
            TimeSpan.FromSeconds(1));
        match.Success.ShouldBeTrue($"no row with {attribute}=\"{value}\" on the page");
        return match.Value;
    }

    private static string Element(string html, string tag, string attribute, string value)
    {
        var match = Regex.Match(
            html, $"<{tag}[^>]*{attribute}=\"{Regex.Escape(value)}\"[^>]*>.*?</{tag}>", RegexOptions.Singleline, TimeSpan.FromSeconds(1));
        match.Success.ShouldBeTrue($"no <{tag} {attribute}=\"{value}\"> on the page");
        return match.Value;
    }

    // PostgreSQL keeps microseconds, .NET keeps 100 ns ticks.
    private static DateTimeOffset Truncate(DateTimeOffset value) => new(value.Ticks - (value.Ticks % 10), value.Offset);

    // Pages show Riyadh time (UTC+3) and put the instant, with that offset, in the datetime attribute, as AuditList does.
    private static string Iso(DateTimeOffset value) =>
        value.ToOffset(TimeSpan.FromHours(3)).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    /// <summary>This class owns its database, so each board test starts from no health rows and no incidents.</summary>
    private async Task ResetHealthAsync()
    {
        await using var connection = new NpgsqlConnection(db.OwnerConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("truncate ops.health_results, ops.incidents", connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task RecordAsync(WebApplicationFactory<Program> factory, IReadOnlyList<HealthResult> results)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IHealthLog>().RecordAsync(results, Ct);
    }

    private static async Task<IReadOnlyList<Incident>> IncidentsSinceAsync(WebApplicationFactory<Program> factory, DateTimeOffset since)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IHealthLog>().IncidentsAsync(since, Ct);
    }

    private static async Task<string> CreateFailedJobAsync(WebApplicationFactory<Program> factory, TenantContext? tenant)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        if (tenant is not null)
        {
            scope.ServiceProvider.GetRequiredService<TenantAccessor>().Set(tenant);
        }

        var client = scope.ServiceProvider.GetRequiredService<IBackgroundJobClient>();
        return client.Create(
            Job.FromExpression(() => ConsoleProbeJob.Run(ConsoleProbeJob.SecretArgument)),
            new FailedState(new InvalidOperationException(ConsoleProbeJob.SecretMessage)));
    }

    private static async Task<int> FailedJobCountAsync(WebApplicationFactory<Program> factory, Guid tenantId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var failed = await scope.ServiceProvider.GetRequiredService<IPlatformJobs>().FailedAsync(cancellationToken: Ct);
        return failed.Count(j => j.TenantId == tenantId);
    }

    private async Task PutObjectAsync(string key, int size)
    {
        using var client = new AmazonS3Client(
            MinioFixture.AccessKey, MinioFixture.SecretKey, new AmazonS3Config { ServiceURL = minio.ServiceUrl, ForcePathStyle = true });
        using var body = new MemoryStream(new byte[size]);
        await client.PutObjectAsync(new PutObjectRequest { BucketName = MinioFixture.BucketName, Key = key, InputStream = body }, Ct);
    }

    private async Task<List<(string? Actor, string Action, string Data)>> AuditRowsAsync(string jobId)
    {
        await using var connection = new NpgsqlConnection(db.AppConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "select actor_id, action, data::text from ops.platform_audit where subject_id = @job", connection);
        command.Parameters.AddWithValue("job", jobId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<(string?, string, string)>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add((reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return rows;
    }

    private sealed class FixedMembers(IReadOnlyDictionary<string, int> counts) : IOrganizationMembers
    {
        public Task<int?> CountAsync(string organizationAlias, CancellationToken cancellationToken = default) =>
            Task.FromResult(counts.TryGetValue(organizationAlias, out var count) ? count : (int?)null);
    }
}

/// <summary>A job the console tests put straight into the Failed state; it never runs.</summary>
public static class ConsoleProbeJob
{
    // No retries: otherwise Hangfire's global AutomaticRetry filter turns the created Failed state into a scheduled retry.
    public const string SecretArgument = "argument-that-must-not-show";
    public const string SecretMessage = "message-that-must-not-show";

    [AutomaticRetry(Attempts = 0)]
    public static void Run(string argument) => ArgumentException.ThrowIfNullOrEmpty(argument);
}
