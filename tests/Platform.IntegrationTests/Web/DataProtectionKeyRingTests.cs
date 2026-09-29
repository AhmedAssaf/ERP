using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Migrator;

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

    [Fact]
    public async Task A_session_with_a_context_sees_no_key_and_the_app_role_can_only_add_keys_without_one()
    {
        await using (var host = new PlatformWebFactory(AppConnectionString))
        {
            CookieFormat(host).Protect(Ticket("acme.admin"));
        }

        (await CountAsAppAsync(null)).ShouldBe(1);
        (await CountAsAppAsync("select set_config('app.tenant_id', @value, false)", TestTenants.Acme.TenantId.ToString())).ShouldBe(0);
        (await CountAsAppAsync("select set_config('app.vendor_company_id', @value, false)", Guid.NewGuid().ToString())).ShouldBe(0);
        (await CountAsAppAsync("select set_config('app.user_id', @value, false)", "acme.admin")).ShouldBe(0);

        await using var app = new NpgsqlConnection(AppConnectionString);
        await app.OpenAsync(Ct);
        foreach (var sql in new[] { "update platform.data_protection_keys set xml = '<key/>'", "delete from platform.data_protection_keys" })
        {
            var denied = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(app, sql));
            denied.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }

        await ExecuteAsync(app, "select set_config('app.tenant_id', '" + TestTenants.Acme.TenantId + "', false)");
        var refused = await Should.ThrowAsync<PostgresException>(() =>
            ExecuteAsync(app, "insert into platform.data_protection_keys (friendly_name, xml) values ('planted', '<key/>')"));
        refused.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
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
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("DataProtection:CertificatePath", TestCertificates.KeyRingPath);
            builder.UseSetting("DataProtection:CertificatePassword", TestCertificates.KeyRingPassword);
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

    private async Task<long> CountAsAppAsync(string? contextSql, string? value = null)
    {
        await using var connection = new NpgsqlConnection(AppConnectionString);
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
}
