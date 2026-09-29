using System.Security.Claims;
using System.Xml.Linq;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Migrator;
using Platform.Web.Edge;

namespace Platform.IntegrationTests.Web;

/// <summary>
/// W-24: the Data Protection key ring that signs and encrypts the login cookie (and antiforgery tokens and Blazor's
/// prerendered component state) lives in PostgreSQL, <c>platform.data_protection_keys</c>, under one application name,
/// so every web instance and every restart reads the same keys. Each test runs on a fresh database in the same container,
/// so keys written here never reach the shared database's key ring.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class DataProtectionKeyRingTests(DatabaseFixture db) : IAsyncLifetime
{
    private readonly string _database = $"key_ring_{Guid.NewGuid():N}";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string OwnerConnectionString => new NpgsqlConnectionStringBuilder(db.OwnerConnectionString) { Database = _database }.ConnectionString;

    private string AppConnectionString => new NpgsqlConnectionStringBuilder(db.AppConnectionString) { Database = _database }.ConnectionString;

    private string KeyRingConnectionString => TestSecrets.KeyRingConnectionString(OwnerConnectionString);

    public async ValueTask InitializeAsync()
    {
        await ExecuteAsync(db.OwnerConnectionString, $"create database {_database}");
        await ExecuteAsync(OwnerConnectionString, "create extension if not exists vector; create extension if not exists pgcrypto; create extension if not exists unaccent;");
        await MigrationRunner.RunAsync(OwnerConnectionString, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await ExecuteAsync(db.OwnerConnectionString, $"drop database if exists {_database} with (force)");
    }

    [Fact]
    public async Task A_login_cookie_issued_by_one_instance_is_accepted_by_another_and_after_a_restart()
    {
        string cookie;
        Guid keyId;
        await using (var first = new PlatformWebFactory(AppConnectionString))
        await using (var second = new PlatformWebFactory(AppConnectionString))
        {
            cookie = CookieFormat(first).Protect(Ticket("acme.admin"));
            keyId = first.Services.GetRequiredService<IKeyManager>().GetAllKeys().ShouldHaveSingleItem().KeyId;

            CookieFormat(second).Unprotect(cookie).ShouldNotBeNull().Principal.FindFirst("sub")!.Value.ShouldBe("acme.admin");
            foreach (var instance in new[] { first, second })
            {
                instance.Services.GetRequiredService<IOptions<DataProtectionOptions>>().Value.ApplicationDiscriminator.ShouldBe("waslabid-web");
            }
        }

        await using var restarted = new PlatformWebFactory(AppConnectionString);
        CookieFormat(restarted).Unprotect(cookie).ShouldNotBeNull().Principal.FindFirst("sub")!.Value.ShouldBe("acme.admin");

        var stored = await KeyRowsAsync();
        stored.ShouldHaveSingleItem().ShouldContain($"id=\"{keyId}\"");
    }

    [Fact]
    public async Task With_a_certificate_every_stored_key_is_encrypted_and_another_instance_still_reads_it()
    {
        string payload;
        await using (var first = WithCertificate(new PlatformWebFactory(AppConnectionString)))
        {
            payload = first.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("w-24").Protect("sealed");
        }

        var stored = (await KeyRowsAsync()).ShouldHaveSingleItem();
        stored.ShouldContain("EncryptedData");
        stored.ShouldNotContain("<masterKey");
        stored.ShouldNotContain("unencrypted form");

        await using var second = WithCertificate(new PlatformWebFactory(AppConnectionString));
        second.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("w-24").Unprotect(payload).ShouldBe("sealed");
    }

    /// <summary>
    /// Whoever can add a key can forge any session, so the table belongs to <c>erp_key_ring</c> alone (review blocker W24-P1):
    /// the application role cannot even read it, and the key ring role only reads and adds, with no context.
    /// </summary>
    [Fact]
    public async Task Only_the_key_ring_role_reads_and_adds_keys_and_the_app_role_has_no_right_at_all()
    {
        await using (var host = new PlatformWebFactory(AppConnectionString))
        {
            CookieFormat(host).Protect(Ticket("acme.admin"));
        }

        await using (var app = new NpgsqlConnection(AppConnectionString))
        {
            await app.OpenAsync(Ct);
            foreach (var sql in new[]
            {
                "select count(*) from platform.data_protection_keys",
                "insert into platform.data_protection_keys (friendly_name, xml) values ('planted', '<key/>')",
                "update platform.data_protection_keys set xml = '<key/>'",
                "delete from platform.data_protection_keys",
            })
            {
                var denied = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(app, sql));
                denied.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege, sql);
            }
        }

        (await CountAsKeyRingAsync(null)).ShouldBe(1);
        (await CountAsKeyRingAsync("select set_config('app.tenant_id', @value, false)", TestTenants.Acme.TenantId.ToString())).ShouldBe(0);
        (await CountAsKeyRingAsync("select set_config('app.vendor_company_id', @value, false)", Guid.NewGuid().ToString())).ShouldBe(0);
        (await CountAsKeyRingAsync("select set_config('app.user_id', @value, false)", "acme.admin")).ShouldBe(0);

        await using var ring = new NpgsqlConnection(KeyRingConnectionString);
        await ring.OpenAsync(Ct);
        foreach (var sql in new[] { "update platform.data_protection_keys set xml = '<key/>'", "delete from platform.data_protection_keys", "truncate platform.data_protection_keys" })
        {
            var denied = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(ring, sql));
            denied.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege, sql);
        }
    }

    /// <summary>
    /// Defence in depth behind the role: with a certificate configured, the host ignores a key stored without the
    /// certificate's encryption, even one added through the key ring role itself, including a plaintext master key wrapped
    /// in Data Protection's own "null" decryptor, which would otherwise pass as encrypted.
    /// </summary>
    [Theory]
    [InlineData("plaintext")]
    [InlineData("null-decryptor")]
    public async Task With_a_certificate_a_key_stored_without_its_encryption_is_not_trusted(string form)
    {
        var (keyXml, cookie) = ForgeSession("platform.admin");
        await using (var ring = new NpgsqlConnection(KeyRingConnectionString))
        {
            await ring.OpenAsync(Ct);
            await using var plant = new NpgsqlCommand("insert into platform.data_protection_keys (friendly_name, xml) values ('planted', @xml)", ring);
            plant.Parameters.AddWithValue("xml", form == "plaintext" ? keyXml : WrapInNullDecryptor(keyXml));
            await plant.ExecuteNonQueryAsync(Ct);
        }

        await using (var withoutCertificate = new PlatformWebFactory(AppConnectionString))
        {
            // The control: the same planted key is trusted by a host that encrypts nothing (Development and Testing).
            CookieFormat(withoutCertificate).Unprotect(cookie).ShouldNotBeNull().Principal.FindFirst("sub")!.Value.ShouldBe("platform.admin");
        }

        await using var withCertificate = WithCertificate(new PlatformWebFactory(AppConnectionString));
        CookieFormat(withCertificate).Unprotect(cookie).ShouldBeNull();
    }

    /// <summary>
    /// Two instances starting at the same moment on an empty table: both would make a key. The first stores it, the second
    /// sees it under the store lock and uses it, so there is one key and a cookie from either instance opens the other.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Two_instances_starting_together_on_an_empty_ring_share_one_key(bool withCertificate)
    {
        for (var round = 0; round < 3; round++)
        {
            await ExecuteAsync(OwnerConnectionString, "delete from platform.data_protection_keys");
            await using var first = withCertificate ? WithCertificate(new PlatformWebFactory(AppConnectionString)) : new PlatformWebFactory(AppConnectionString);
            await using var second = withCertificate ? WithCertificate(new PlatformWebFactory(AppConnectionString)) : new PlatformWebFactory(AppConnectionString);

            await Task.WhenAll(Task.Run(() => first.Server), Task.Run(() => second.Server));

            var fromFirst = CookieFormat(first).Protect(Ticket("first"));
            var fromSecond = CookieFormat(second).Protect(Ticket("second"));
            CookieFormat(second).Unprotect(fromFirst).ShouldNotBeNull().Principal.FindFirst("sub")!.Value.ShouldBe("first");
            CookieFormat(first).Unprotect(fromSecond).ShouldNotBeNull().Principal.FindFirst("sub")!.Value.ShouldBe("second");
            (await KeyRowsAsync()).Count.ShouldBe(1, $"round {round}");
        }
    }

    /// <summary>
    /// After the certificate is replaced (docs/07: every user signs in again), the old key can no longer be decrypted, so
    /// the host makes a new one. That key must be stored even though the old key's period still covers it; otherwise
    /// every instance runs on a key of its own in memory, and cookies neither cross instances nor survive a restart until
    /// the old key expires (review of W-24).
    /// </summary>
    [Fact]
    public async Task After_the_certificate_is_replaced_a_new_key_is_stored_and_cookies_cross_instances_and_restarts()
    {
        await using (var beforeReplacement = WithCertificate(new PlatformWebFactory(AppConnectionString)))
        {
            CookieFormat(beforeReplacement).Protect(Ticket("before"));
        }

        var (path, password) = TestCertificates.Create("CN=waslabid-tests-replacement-key-ring");
        string cookie;
        await using (var first = WithCertificate(new PlatformWebFactory(AppConnectionString), path, password))
        await using (var second = WithCertificate(new PlatformWebFactory(AppConnectionString), path, password))
        {
            cookie = CookieFormat(first).Protect(Ticket("after"));
            CookieFormat(second).Unprotect(cookie).ShouldNotBeNull("a cookie from one instance opens the other").Principal.FindFirst("sub")!.Value.ShouldBe("after");
        }

        await using var restarted = WithCertificate(new PlatformWebFactory(AppConnectionString), path, password);
        CookieFormat(restarted).Unprotect(cookie).ShouldNotBeNull("the cookie survives a restart").Principal.FindFirst("sub")!.Value.ShouldBe("after");
        (await KeyRowsAsync()).Count.ShouldBe(2, "the old key and the one made under the new certificate");
    }

    /// <summary>
    /// W24-P2 (N-10): Data Protection writes whole key elements at Debug and Trace. Its categories are capped at Information;
    /// a configuration that still turns them lower stops the host outside Development.
    /// </summary>
    [Fact]
    public void A_host_that_would_log_the_key_ring_below_information_does_not_start()
    {
        using var factory = new PlatformWebFactory(AppConnectionString).WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.PostConfigure<LoggerFilterOptions>(o => o.Rules.Add(new LoggerFilterRule(null, "Microsoft.AspNetCore.DataProtection", LogLevel.Debug, null)))));

        var refused = Should.Throw<InvalidOperationException>(() => factory.Server);

        refused.Message.ShouldContain("Microsoft.AspNetCore.DataProtection");
    }

    /// <summary>
    /// A wrong <c>ConnectionStrings:KeyRing</c> password never heals by itself: the host stops at startup and names the
    /// setting and the PostgreSQL state, never a password (QA of W-24: it used to start, answer /health with 200, and fail
    /// every page with 500).
    /// </summary>
    [Fact]
    public void A_key_ring_password_the_database_refuses_stops_the_host()
    {
        const string wrongPassword = "not-the-key-ring-password-5b7e";
        var wrong = new NpgsqlConnectionStringBuilder(KeyRingConnectionString) { Password = wrongPassword }.ConnectionString;
        using var factory = new PlatformWebFactory(AppConnectionString)
            .WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:KeyRing", wrong));

        var refused = Should.Throw<InvalidOperationException>(() => factory.Server);

        refused.Message.ShouldContain("ConnectionStrings:KeyRing");
        refused.Message.ShouldContain("28P01");
        Messages(refused).ShouldNotContain(wrongPassword);
        Messages(refused).ShouldNotContain(TestSecrets.KeyRingPassword);
    }

    /// <summary>A database the migrator has not brought to platform/0007 (no key table) stops the host and says to run it.</summary>
    [Fact]
    public async Task A_database_without_the_key_table_stops_the_host_and_names_the_migrator()
    {
        var empty = $"key_ring_unmigrated_{Guid.NewGuid():N}";
        await ExecuteAsync(db.OwnerConnectionString, $"create database {empty}");
        try
        {
            var app = new NpgsqlConnectionStringBuilder(db.AppConnectionString) { Database = empty }.ConnectionString;
            using var factory = new PlatformWebFactory(app);

            var refused = Should.Throw<InvalidOperationException>(() => factory.Server);

            refused.Message.ShouldContain("42P01");
            refused.Message.ShouldContain("migrator");
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAsync(db.OwnerConnectionString, $"drop database if exists {empty} with (force)");
        }
    }

    /// <summary>
    /// A key ring the host cannot reach may come back by itself (the database restarting during a deployment), so the host
    /// starts, and /health answers Unhealthy until the ring can be read, so the worker's health check and any orchestrator
    /// see it (QA of W-24: /health said Healthy while every page failed).
    /// </summary>
    [Fact]
    public async Task While_the_key_ring_cannot_be_reached_health_is_unhealthy()
    {
        var unreachable = new NpgsqlConnectionStringBuilder(KeyRingConnectionString) { Host = "127.0.0.1", Port = 1, Timeout = 3 }.ConnectionString;
        await using var factory = new PlatformWebFactory(AppConnectionString)
            .WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:KeyRing", unreachable));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldBe("Unhealthy");
    }

    /// <summary>
    /// The migrator not re-run after platform/0007: erp_key_ring exists without a login (28000). The host stops and says
    /// to run the migrator. Tests in the database collection never run in parallel, so the shared role is changed here
    /// and restored.
    /// </summary>
    [Fact]
    public async Task A_key_ring_role_without_a_login_stops_the_host_and_names_the_migrator()
    {
        await ExecuteAsync(OwnerConnectionString, "alter role erp_key_ring nologin");
        try
        {
            using var factory = new PlatformWebFactory(AppConnectionString);

            var refused = Should.Throw<InvalidOperationException>(() => factory.Server);

            refused.Message.ShouldContain("28000");
            refused.Message.ShouldContain("migrator");
        }
        finally
        {
            await ExecuteAsync(OwnerConnectionString, "alter role erp_key_ring login");
        }
    }

    /// <summary>
    /// /health is anonymous, unthrottled and skips tenant resolution, so a burst of calls must not reach the database each
    /// time: at most one check per cache window, on the ring's own pool, capped at a few connections (review of W-24).
    /// </summary>
    [Fact]
    public async Task Many_concurrent_health_calls_make_at_most_one_database_check_per_window_on_a_capped_pool()
    {
        await using var factory = new PlatformWebFactory(AppConnectionString);
        using var client = factory.CreateClient();
        var probe = factory.Services.GetRequiredService<KeyRingProbe>();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var responses = await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => client.GetAsync(new Uri("/health", UriKind.Relative), Ct)));
        clock.Stop();

        responses.ShouldAllBe(r => r.StatusCode == System.Net.HttpStatusCode.OK);
        var windows = 1 + (int)(clock.Elapsed / KeyRingAvailability.CacheFor);
        probe.DatabaseChecks.ShouldBeInRange(1, windows);

        var dataSource = factory.Services.GetRequiredKeyedService<NpgsqlDataSource>(KeyRing.DataSourceKey);
        new NpgsqlConnectionStringBuilder(dataSource.ConnectionString).MaxPoolSize.ShouldBe(KeyRing.DefaultMaxPoolSize);
        await using var owner = new NpgsqlConnection(OwnerConnectionString);
        await owner.OpenAsync(Ct);
        await using var count = new NpgsqlCommand(
            "select count(*) from pg_stat_activity where usename = 'erp_key_ring' and datname = current_database()", owner);
        ((long)(await count.ExecuteScalarAsync(Ct))!).ShouldBeLessThanOrEqualTo(KeyRing.DefaultMaxPoolSize);
    }

    /// <summary>
    /// A database that accepts connections and never answers: /health says Unhealthy inside the worker's five seconds per
    /// check (F-51), so the board shows the Web host reporting Unhealthy rather than the Web host unreachable.
    /// </summary>
    [Fact]
    public async Task A_hung_key_ring_database_makes_health_unhealthy_within_the_workers_limit()
    {
        using var hung = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        hung.Start();
        var held = new List<System.Net.Sockets.TcpClient>();
        using var stop = new CancellationTokenSource();
        var acceptor = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    held.Add(await hung.AcceptTcpClientAsync(stop.Token));
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }, Ct);
        var port = ((System.Net.IPEndPoint)hung.LocalEndpoint).Port;
        var silent = new NpgsqlConnectionStringBuilder(KeyRingConnectionString) { Host = "127.0.0.1", Port = port }.ConnectionString;
        try
        {
            await using var factory = new PlatformWebFactory(AppConnectionString)
                .WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:KeyRing", silent));
            using var client = factory.CreateClient();
            var clock = System.Diagnostics.Stopwatch.StartNew();

            using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), Ct);
            clock.Stop();

            response.StatusCode.ShouldBe(System.Net.HttpStatusCode.ServiceUnavailable);
            clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await stop.CancelAsync();
            hung.Stop();
            await acceptor;
            held.ForEach(c => c.Dispose());
        }
    }

    [Fact]
    public async Task With_a_readable_key_ring_health_is_healthy()
    {
        await using var factory = new PlatformWebFactory(AppConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldBe("Healthy");
    }

    [Fact]
    public void A_host_without_a_key_ring_connection_string_does_not_start()
    {
        using var factory = new PlatformWebFactory(AppConnectionString)
            .WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:KeyRing", string.Empty));

        var refused = Should.Throw<InvalidOperationException>(() => factory.Server);

        refused.Message.ShouldContain("'KeyRing'");
    }

    /// <summary>The migrator gives only erp_key_ring a login for the ring; a pasted owner connection string is refused too.</summary>
    [Fact]
    public void A_key_ring_connection_as_any_role_but_erp_key_ring_does_not_start()
    {
        using var factory = new PlatformWebFactory(AppConnectionString)
            .WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:KeyRing", OwnerConnectionString));

        var refused = Should.Throw<InvalidOperationException>(() => factory.Server);

        refused.Message.ShouldContain("erp_key_ring");
    }

    [Fact]
    public void A_key_ring_connection_as_the_application_role_does_not_start()
    {
        using var factory = new PlatformWebFactory(AppConnectionString)
            .WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:KeyRing", AppConnectionString));

        var refused = Should.Throw<InvalidOperationException>(() => factory.Server);

        refused.Message.ShouldContain("its own role");
    }

    [Fact]
    public void Production_host_without_a_key_ring_certificate_does_not_start()
    {
        using var factory = new PlatformWebFactory("Host=unused;Database=unused", environment: "Production")
            .WithWebHostBuilder(builder => builder.UseSetting("DataProtection:CertificatePath", string.Empty));

        var refused = Should.Throw<InvalidOperationException>(() => factory.Server);

        refused.Message.ShouldContain("DataProtection:CertificatePath");
    }

    [Fact]
    public void An_unreadable_key_ring_certificate_stops_the_host_without_naming_its_password()
    {
        const string wrongPassword = "not-the-pfx-password-8c1f";
        using var factory = new PlatformWebFactory("Host=unused;Database=unused", environment: "Production")
            .WithWebHostBuilder(builder => builder.UseSetting("DataProtection:CertificatePassword", wrongPassword));

        var refused = Should.Throw<InvalidOperationException>(() => factory.Server);

        refused.Message.ShouldContain("DataProtection:CertificatePath");
        Messages(refused).ShouldNotContain(wrongPassword);
        Messages(refused).ShouldNotContain(TestCertificates.KeyRingPassword);
    }

    [Fact]
    public void A_missing_key_ring_certificate_file_stops_the_host()
    {
        using var factory = new PlatformWebFactory("Host=unused;Database=unused")
            .WithWebHostBuilder(builder => builder.UseSetting("DataProtection:CertificatePath", Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.pfx")));

        var refused = Should.Throw<InvalidOperationException>(() => factory.Server);

        refused.Message.ShouldContain("DataProtection:CertificatePath");
    }

    private static WebApplicationFactory<Program> WithCertificate(PlatformWebFactory factory) =>
        WithCertificate(factory, TestCertificates.KeyRingPath, TestCertificates.KeyRingPassword);

    private static WebApplicationFactory<Program> WithCertificate(PlatformWebFactory factory, string path, string password) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("DataProtection:CertificatePath", path);
            builder.UseSetting("DataProtection:CertificatePassword", password);
        });

    private static ISecureDataFormat<AuthenticationTicket> CookieFormat(WebApplicationFactory<Program> factory) =>
        factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme).TicketDataFormat;

    private static AuthenticationTicket Ticket(string subject) =>
        new(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", subject)], "test")), CookieAuthenticationDefaults.AuthenticationScheme);

    private async Task<List<string>> KeyRowsAsync()
    {
        await using var connection = new NpgsqlConnection(OwnerConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("select xml from platform.data_protection_keys order by id", connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    private async Task<long> CountAsKeyRingAsync(string? contextSql, string? value = null)
    {
        await using var connection = new NpgsqlConnection(KeyRingConnectionString);
        await connection.OpenAsync(Ct);
        if (contextSql is not null)
        {
            await using var context = new NpgsqlCommand(contextSql, connection);
            context.Parameters.AddWithValue("value", value!);
            await context.ExecuteNonQueryAsync(Ct);
        }

        await using var count = new NpgsqlCommand("select count(*) from platform.data_protection_keys", connection);
        return (long)(await count.ExecuteScalarAsync(Ct))!;
    }

    /// <summary>
    /// What an attacker does offline: a Data Protection instance of their own under the host's application name makes a key
    /// (unencrypted XML) and protects a login ticket with it, as the cookie handler would.
    /// </summary>
    private static (string KeyXml, string Cookie) ForgeSession(string subject)
    {
        var store = new MemoryXmlRepository();
        var services = new ServiceCollection();
        services.AddDataProtection().SetApplicationName("waslabid-web");
        services.Configure<KeyManagementOptions>(o => o.XmlRepository = store);
        using var provider = services.BuildServiceProvider();
        var format = new TicketDataFormat(provider.GetRequiredService<IDataProtectionProvider>().CreateProtector(
            "Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationMiddleware", CookieAuthenticationDefaults.AuthenticationScheme, "v2"));
        var cookie = format.Protect(Ticket(subject));
        return (store.GetAllElements().Single().ToString(SaveOptions.DisableFormatting), cookie);
    }

    /// <summary>The plaintext master key wrapped as a secret "encrypted" with <see cref="NullXmlDecryptor"/>.</summary>
    private static string WrapInNullDecryptor(string keyXml)
    {
        XNamespace dataProtection = "http://schemas.asp.net/2015/03/dataProtection";
        var key = XElement.Parse(keyXml);
        var masterKey = key.Descendants().Single(e => e.Name.LocalName == "masterKey");
        masterKey.ReplaceWith(new XElement(
            dataProtection + "encryptedSecret",
            new XAttribute("decryptorType", typeof(NullXmlDecryptor).AssemblyQualifiedName!),
            new XElement("unencryptedKey", new XElement(masterKey))));
        return key.ToString(SaveOptions.DisableFormatting);
    }

    private static string Messages(Exception exception)
    {
        var text = new System.Text.StringBuilder();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            text.AppendLine(current.Message);
        }

        return text.ToString();
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);
        await ExecuteAsync(connection, sql);
    }

#pragma warning disable CA2100 // Test SQL built from constants and generated names only.
    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
#pragma warning restore CA2100

    private sealed class MemoryXmlRepository : IXmlRepository
    {
        private readonly List<XElement> _elements = [];

        public IReadOnlyCollection<XElement> GetAllElements() => _elements.ToList();

        public void StoreElement(XElement element, string friendlyName) => _elements.Add(new XElement(element));
    }
}
