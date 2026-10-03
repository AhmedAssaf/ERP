using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Platform.Shared.Caching;

/// <summary>
/// W-34: the one Redis connection of a host (docs/02 stack row "Cache and locks"), from <c>ConnectionStrings:Redis</c>.
/// Development uses <c>localhost:6379</c> (the Compose service <c>erp-redis</c>, no password); the pilot's value, with
/// its password, lives in the secret store, never in the repository (N-10). Without the setting nothing is registered,
/// and each user falls back to what it did before (the duplicate-CR throttle counts in process memory; the worker does
/// not check Redis). <c>abortConnect=false</c> is always applied, so the host starts while Redis is down and the
/// multiplexer keeps reconnecting in the background; with the fail-fast backlog policy a call made meanwhile fails at
/// once (the default policy would hold it for the five-second timeout) and its caller falls back. A Redis that accepted
/// the connection and then stops answering costs each call the operation timeout, which is lowered here to
/// <see cref="OperationTimeoutMilliseconds"/> (the library's default is five seconds), and the first connection, which
/// blocks the request that first needs Redis, gives up after one attempt of <see cref="ConnectTimeoutMilliseconds"/>
/// (the default is three attempts of five seconds; the multiplexer keeps reconnecting in the background either way); a
/// connection string that sets <c>asyncTimeout</c>, <c>syncTimeout</c>, <c>connectTimeout</c> or <c>connectRetry</c>
/// keeps its own value.
/// </summary>
public static class RedisConnection
{
    public const string ConnectionStringName = "Redis";

    /// <summary>The full setting name, for messages; never its value (N-10).</summary>
    public const string Setting = "ConnectionStrings:" + ConnectionStringName;

    /// <summary>Async and sync operation timeout unless the connection string sets one: a hung Redis costs a call about a second.</summary>
    public const int OperationTimeoutMilliseconds = 1000;

    /// <summary>Connect timeout unless the connection string sets one.</summary>
    public const int ConnectTimeoutMilliseconds = 2000;

    /// <summary>True when the host has <c>ConnectionStrings:Redis</c>.</summary>
    public static bool IsConfigured(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return !string.IsNullOrWhiteSpace(configuration.GetConnectionString(ConnectionStringName));
    }

    /// <summary>
    /// The options for <paramref name="connectionString"/> with <see cref="ConfigurationOptions.AbortOnConnectFail"/> off,
    /// <see cref="BacklogPolicy.FailFast"/>, and the lower timeouts where the connection string sets none.
    /// The connection string is never put into an exception message or a log record here (N-10).
    /// </summary>
    public static ConfigurationOptions Options(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var options = ConfigurationOptions.Parse(connectionString);
        options.AbortOnConnectFail = false;
        options.BacklogPolicy = BacklogPolicy.FailFast;
        var set = SetOptionNames(connectionString);
        if (!set.Contains("asyncTimeout"))
        {
            options.AsyncTimeout = OperationTimeoutMilliseconds;
        }

        if (!set.Contains("syncTimeout"))
        {
            options.SyncTimeout = OperationTimeoutMilliseconds;
        }

        if (!set.Contains("connectTimeout"))
        {
            options.ConnectTimeout = ConnectTimeoutMilliseconds;
        }

        if (!set.Contains("connectRetry"))
        {
            options.ConnectRetry = 1;
        }

        return options;
    }

    /// <summary>The <c>name=value</c> option names in a connection string (endpoints have no <c>=</c>).</summary>
    private static HashSet<string> SetOptionNames(string connectionString) =>
        connectionString.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => part.IndexOf('=', StringComparison.Ordinal) > 0)
            .Select(part => part[..part.IndexOf('=', StringComparison.Ordinal)].Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers one <see cref="IConnectionMultiplexer"/> for the host when <c>ConnectionStrings:Redis</c> is set; idempotent,
    /// so every module that needs Redis may call it. The multiplexer connects when first resolved.
    /// </summary>
    public static IServiceCollection AddRedis(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        if (!IsConfigured(configuration))
        {
            return services;
        }

        var options = Options(configuration.GetConnectionString(ConnectionStringName)!);
        services.TryAddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(options));
        return services;
    }
}
