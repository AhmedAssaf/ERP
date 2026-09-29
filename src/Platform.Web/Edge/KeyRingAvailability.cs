using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Platform.Web.Edge;

/// <summary>
/// W-24 (QA): a key ring the host cannot read fails every page with a 500 (no cookie, no antiforgery token), so it must
/// never look healthy. Two answers, each where it does least harm:
/// <list type="bullet">
/// <item><see cref="KeyRingStartupCheck"/>: an error that never heals without an operator (a wrong password, a role without
/// a login because the migrator was not re-run, missing rights, a missing table, schema or database) stops the host at
/// startup, naming <c>ConnectionStrings:KeyRing</c> and the PostgreSQL state, never a password.</item>
/// <item><see cref="KeyRingHealthCheck"/>: anything else (the database unreachable while it restarts) may heal by itself,
/// so the host starts and <c>/health</c> answers Unhealthy until the ring can be read; refusing to start there would turn
/// a short database outage into a restart loop.</item>
/// </list>
/// </summary>
internal static class KeyRingAvailability
{
    /// <summary>A cheap read that needs the login, the rights and the table, nothing more.</summary>
    public const string ProbeSql = "select 1 from platform.data_protection_keys limit 1";

    private static readonly HashSet<string> PermanentStates = new(StringComparer.Ordinal)
    {
        PostgresErrorCodes.InvalidPassword, // 28P01
        PostgresErrorCodes.InvalidAuthorizationSpecification, // 28000: e.g. a role not permitted to log in
        PostgresErrorCodes.InsufficientPrivilege, // 42501
        PostgresErrorCodes.UndefinedTable, // 42P01: platform/0007 not applied
        PostgresErrorCodes.InvalidSchemaName, // 3F000
        PostgresErrorCodes.InvalidCatalogName, // 3D000: no such database
    };

    public static bool IsPermanent(PostgresException exception) => PermanentStates.Contains(exception.SqlState);

    public static async Task ProbeAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var probe = new NpgsqlCommand(ProbeSql, connection);
        await probe.ExecuteScalarAsync(cancellationToken);
    }
}

/// <summary>Stops the host at startup when the key ring cannot be read for a reason that never heals by itself.</summary>
internal sealed partial class KeyRingStartupCheck(
    [FromKeyedServices(KeyRing.DataSourceKey)] NpgsqlDataSource dataSource, ILogger<KeyRingStartupCheck> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await KeyRingAvailability.ProbeAsync(dataSource, cancellationToken);
        }
        catch (PostgresException exception) when (KeyRingAvailability.IsPermanent(exception))
        {
            // The state and the role only: PostgreSQL's message names the user, never the password.
            throw new InvalidOperationException(
                $"The Data Protection key ring cannot be read with connection string 'ConnectionStrings:KeyRing' (PostgreSQL {exception.SqlState}). "
                + "Check its password, and re-run the migrator with the same connection string: it applies platform/0007 and gives "
                + $"{KeyRing.KeyRingRoleName} its login (docs/07 section 4).", exception);
        }
        catch (NpgsqlException exception)
        {
            LogUnreachable(logger, exception.GetType().Name);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "The key ring could not be read at startup ({ErrorType}); /health reports Unhealthy until it can.")]
    private static partial void LogUnreachable(ILogger logger, string errorType);
}

/// <summary><c>/health</c> is Unhealthy while the key ring cannot be read: every page would fail without it.</summary>
internal sealed class KeyRingHealthCheck([FromKeyedServices(KeyRing.DataSourceKey)] NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await KeyRingAvailability.ProbeAsync(dataSource, cancellationToken);
            return HealthCheckResult.Healthy("The key ring can be read.");
        }
        catch (NpgsqlException exception)
        {
            var reason = exception is PostgresException postgres ? postgres.SqlState : exception.GetType().Name;
            return HealthCheckResult.Unhealthy($"The key ring cannot be read ({reason}).");
        }
    }
}
