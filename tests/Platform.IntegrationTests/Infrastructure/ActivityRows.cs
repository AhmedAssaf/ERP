using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>
/// Rows of <c>identity.user_activity</c> (W-10, spec 6.4) for tests: read and arranged as the owner (the app role holds
/// INSERT only), and application sessions with a chosen tenant, vendor company and acting user, as the connection
/// interceptor sets them.
/// </summary>
internal static class ActivityRows
{
    /// <summary>Hour buckets of the tenant, optionally of one user, as the owner sees them.</summary>
    public static async Task<IReadOnlyList<(string UserId, string Kind, DateTimeOffset Hour)>> ForTenantAsync(
        string ownerConnectionString, Guid tenantId, CancellationToken cancellationToken, string? userId = null)
    {
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            select user_id, kind, hour from identity.user_activity
            where tenant_id = @tenant and (@user::text is null or user_id = @user)
            order by hour, user_id, kind
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("user", (object?)userId ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<(string, string, DateTimeOffset)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2)));
        }

        return rows;
    }

    /// <summary>
    /// A bucket <paramref name="hoursAgo"/> hours before the database's current hour, as the owner. The insert trigger sets
    /// the database's hour on every insert, so the row is inserted and then moved (the trigger is for inserts only).
    /// </summary>
    public static async Task InsertAsOwnerAsync(
        string ownerConnectionString, Guid tenantId, string userId, string kind, int hoursAgo, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            insert into identity.user_activity (tenant_id, user_id, kind) values (@tenant, @user, @kind);
            update identity.user_activity set hour = hour - make_interval(hours => @hours)
            where tenant_id = @tenant and user_id = @user and kind = @kind and hour = date_trunc('hour', now());
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.AddWithValue("hours", hoursAgo);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Moves every bucket of the user back by <paramref name="hours"/>, as if the database's clock had moved on.</summary>
    public static async Task AgeAsOwnerAsync(string ownerConnectionString, Guid tenantId, string userId, int hours, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "update identity.user_activity set hour = hour - make_interval(hours => @hours) where tenant_id = @tenant and user_id = @user", connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("hours", hours);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>An application connection with the given context, as the connection interceptor sets it.</summary>
    public static async Task<NpgsqlConnection> AppSessionAsync(
        string appConnectionString, Guid? tenantId, Guid? vendorCompanyId, string? userId, CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(appConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            select set_config('app.tenant_id', @tenant, false),
                   set_config('app.vendor_company_id', @vendor, false),
                   set_config('app.user_id', @user, false)
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId?.ToString("D", CultureInfo.InvariantCulture) ?? string.Empty);
        command.Parameters.AddWithValue("vendor", vendorCompanyId?.ToString("D", CultureInfo.InvariantCulture) ?? string.Empty);
        command.Parameters.AddWithValue("user", userId ?? string.Empty);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    /// <summary>"ok", or "refused" when PostgreSQL answers 42501 (privilege or row-level security), in a rolled-back transaction.</summary>
    public static async Task<string> TryAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
#pragma warning disable CA2100 // The statements are the tests' own.
            await using var command = new NpgsqlCommand(sql, connection, transaction);
#pragma warning restore CA2100
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            await command.ExecuteNonQueryAsync(cancellationToken);
            return "ok";
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            return "refused";
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
    }
}

/// <summary>
/// Counts the statements Npgsql runs against one PostgreSQL server (by its port) whose text names a table, through
/// Npgsql's own activity source, so a test can prove how many writes reached the database. Other test classes use their
/// own containers, so their statements never match the port.
/// </summary>
internal sealed class StatementCounter : IDisposable
{
    private readonly ActivityListener _listener;
    private int _count;

    public StatementCounter(string connectionString, string fragment)
    {
        var port = new NpgsqlConnectionStringBuilder(connectionString).Port.ToString(CultureInfo.InvariantCulture);
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Npgsql",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                var text = activity.GetTagItem("db.query.text") as string ?? activity.GetTagItem("db.statement") as string ?? string.Empty;
                var at = (activity.GetTagItem("server.port") ?? activity.GetTagItem("net.peer.port"))?.ToString();
                if (at == port && text.Contains(fragment, StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref _count);
                }
            },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public int Count => Volatile.Read(ref _count);

    public void Dispose() => _listener.Dispose();
}

/// <summary>Every log entry of a host, with its level, category, message and structured values.</summary>
internal sealed class CapturedLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<(LogLevel Level, string Category, string Text)> _entries = new();

    public IReadOnlyList<(LogLevel Level, string Category, string Text)> Entries => [.. _entries];

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<(LogLevel, string, string)> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join(' ', pairs.Select(p => $"{p.Key}={p.Value}"))
                : string.Empty;
            entries.Enqueue((logLevel, category, $"{formatter(state, exception)} {values} {exception}"));
        }
    }
}
