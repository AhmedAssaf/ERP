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
/// <c>/health</c> is anonymous and skips tenant resolution, so it never reaches the database per request: the answer
/// comes from <see cref="KeyRingProbe"/>, at most one database check every <see cref="CacheFor"/>, on a pool of its own
/// capped at a few connections (<see cref="KeyRing"/>). Every check gives up after <see cref="ProbeTimeout"/>, well inside
/// the worker's five seconds per check (F-51), so a hung database shows as the Web host reporting Unhealthy.
/// </summary>
internal static class KeyRingAvailability
{
    /// <summary>A cheap read that needs the login, the rights and the table, nothing more.</summary>
    public const string ProbeSql = "select 1 from platform.data_protection_keys limit 1";

    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(5);

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

    /// <summary>One database check, cancelled after <see cref="ProbeTimeout"/> or with <paramref name="cancellationToken"/>.</summary>
    public static async Task ProbeAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        await using var connection = await dataSource.OpenConnectionAsync(timeout.Token);
        await using var probe = new NpgsqlCommand(ProbeSql, connection);
        await probe.ExecuteScalarAsync(timeout.Token);
    }
}

/// <summary>
/// The key ring's health, checked in the database at most once per <see cref="KeyRingAvailability.CacheFor"/>: callers
/// within that window get the last result, and callers while a check runs share it (single flight). The shared check is
/// not tied to any caller's cancellation, so one caller giving up does not cancel it for the others.
/// </summary>
internal sealed partial class KeyRingProbe(IKeyRingCheck check, TimeProvider clock, ILogger<KeyRingProbe> logger)
{
    private readonly Lock _gate = new();
    private Task<HealthCheckResult>? _running;
    private HealthCheckResult _last;
    private DateTimeOffset _lastAt = DateTimeOffset.MinValue;
    private int _databaseChecks;

    /// <summary>How many times the database was asked; for tests.</summary>
    public int DatabaseChecks => Volatile.Read(ref _databaseChecks);

    public Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        Task<HealthCheckResult> running;
        lock (_gate)
        {
            if (clock.GetUtcNow() - _lastAt < KeyRingAvailability.CacheFor)
            {
                return Task.FromResult(_last);
            }

            running = _running ??= RunAsync();
        }

        return running.WaitAsync(cancellationToken);
    }

    private async Task<HealthCheckResult> RunAsync()
    {
        // Whatever happens below, this window gets an answer and the next caller after it starts a new check: a faulted
        // task must never stay in _running, or /health would replay it until the host restarts.
        var result = HealthCheckResult.Unhealthy("The key ring check failed.");
        try
        {
            // Leave the lock before touching the database.
            await Task.Yield();
            Interlocked.Increment(ref _databaseChecks);
            await check.CheckAsync(CancellationToken.None);
            result = HealthCheckResult.Healthy("The key ring can be read.");
        }
        catch (NpgsqlException exception)
        {
            var reason = exception is PostgresException postgres ? postgres.SqlState : exception.GetType().Name;
            result = HealthCheckResult.Unhealthy($"The key ring cannot be read ({reason}).");
        }
        catch (OperationCanceledException)
        {
            result = HealthCheckResult.Unhealthy($"The key ring did not answer within {KeyRingAvailability.ProbeTimeout.TotalSeconds} seconds.");
        }
#pragma warning disable CA1031 // Any other failure of the check is reported as Unhealthy for this window, not rethrown.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            // The type only: the message of an unexpected exception is not known to be free of secrets (N-10).
            LogUnexpected(logger, exception.GetType().Name);
            result = HealthCheckResult.Unhealthy($"The key ring check failed ({exception.GetType().Name}).");
        }
        finally
        {
            lock (_gate)
            {
                _last = result;
                _lastAt = clock.GetUtcNow();
                _running = null;
            }
        }

        return result;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The key ring health check failed unexpectedly ({ErrorType}).")]
    private static partial void LogUnexpected(ILogger logger, string errorType);
}

/// <summary>One check of the key ring, behind <see cref="KeyRingProbe"/>'s cache.</summary>
internal interface IKeyRingCheck
{
    Task CheckAsync(CancellationToken cancellationToken);
}

/// <summary>The real check: <see cref="KeyRingAvailability.ProbeAsync"/> on the ring's own pool.</summary>
internal sealed class DatabaseKeyRingCheck([FromKeyedServices(KeyRing.DataSourceKey)] NpgsqlDataSource dataSource) : IKeyRingCheck
{
    public Task CheckAsync(CancellationToken cancellationToken) => KeyRingAvailability.ProbeAsync(dataSource, cancellationToken);
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
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogUnreachable(logger, "Timeout");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "The key ring could not be read at startup ({ErrorType}); /health reports Unhealthy until it can.")]
    private static partial void LogUnreachable(ILogger logger, string errorType);
}

/// <summary><c>/health</c> is Unhealthy while the key ring cannot be read: every page would fail without it.</summary>
internal sealed class KeyRingHealthCheck(KeyRingProbe probe) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        probe.CheckAsync(cancellationToken);
}
