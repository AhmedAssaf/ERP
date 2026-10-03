using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Operations.Contracts;

namespace Platform.IntegrationTests.Operations;

/// <summary>D-8: platform actions are not tenant events. ops.platform_audit is append-only for the app role.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class PlatformAuditTests(DatabaseFixture db) : IAsyncLifetime
{
    private const string SeedAction = "ops.platform_audit.seed";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private ModuleHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = new ModuleHost(db.AppConnectionString);
        await using var scope = _host.PlatformScope("platform-admin");
        await scope.ServiceProvider.GetRequiredService<IPlatformAudit>()
            .WriteAsync(new PlatformAuditEntry("platform-admin", SeedAction, "component", "clamav"), Ct);
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    [Fact]
    public async Task Written_entries_are_readable_back()
    {
        await using var connection = new NpgsqlConnection(db.AppConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("select count(*) from ops.platform_audit where action = @action", connection);
        command.Parameters.AddWithValue("action", SeedAction);

        var count = (long)(await command.ExecuteScalarAsync(Ct))!;

        count.ShouldBeGreaterThan(0);
    }

    [Theory]
    [InlineData("update ops.platform_audit set action = 'x' where action = 'ops.platform_audit.seed'")]
    [InlineData("delete from ops.platform_audit where action = 'ops.platform_audit.seed'")]
    public async Task The_platform_audit_is_append_only(string sql)
    {
        await using var connection = new NpgsqlConnection(db.AppConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);

        var rejected = await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct));

        rejected.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }
}
